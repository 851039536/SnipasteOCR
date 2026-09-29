using System.Text;
using Sdcb.SimdPaddleOCR;

namespace SnipasteOcr;

/// <summary>
/// OCR 结果的后处理: 阅读顺序排序与表格/CSV 导出。
/// 独立成类便于单独验证 (纯函数, 不依赖 UI)。
/// </summary>
public static class OcrText
{
    /// <summary>一行文本及其在图中包围盒 (用于按位置排序/分列)</summary>
    public readonly record struct Line(string Text, float Left, float Top, float Right, float Bottom)
    {
        public float CenterY => (Top + Bottom) / 2f;
        public float CenterX => (Left + Right) / 2f;
        public float Height => Bottom - Top;
    }

    /// <summary>把检测框转成轴对齐包围盒 (检测框是四边形, 可能带旋转)</summary>
    public static Line ToLine(PaddleOcrLine src)
    {
        var b = src.Box;
        float left = Math.Min(Math.Min(b.X1, b.X2), Math.Min(b.X3, b.X4));
        float right = Math.Max(Math.Max(b.X1, b.X2), Math.Max(b.X3, b.X4));
        float top = Math.Min(Math.Min(b.Y1, b.Y2), Math.Min(b.Y3, b.Y4));
        float bottom = Math.Max(Math.Max(b.Y1, b.Y2), Math.Max(b.Y3, b.Y4));
        return new Line(src.Text ?? string.Empty, left, top, right, bottom);
    }

    /// <summary>
    /// 按阅读顺序 (先上→下分行, 再左→右) 重排。
    /// PaddleOCR 输出的顺序大体是检测顺序, 但多列/多栏时可能交叉, 这里显式排序。
    /// 分行容差取该行高度的一半, 避免同一行的文字因基线微差被拆成两行。
    /// </summary>
    public static List<Line> SortReadingOrder(IReadOnlyList<Line> lines)
    {
        var valid = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (valid.Count <= 1)
            return valid;

        // 按纵向位置粗排
        var byTop = valid.OrderBy(l => l.CenterY).ToList();
        var rows = new List<List<Line>>();

        foreach (var line in byTop)
        {
            // 找到纵向重叠足够大的已有行
            List<Line>? target = null;
            foreach (var row in rows)
            {
                float refCenter = row.Average(x => x.CenterY);
                float refHeight = row.Average(x => x.Height);
                float tolerance = Math.Max(2f, refHeight * 0.5f);
                if (Math.Abs(line.CenterY - refCenter) <= tolerance)
                {
                    target = row;
                    break;
                }
            }
            if (target is null)
                rows.Add([line]);
            else
                target.Add(line);
        }

        // 行内按横坐标排序
        var result = new List<Line>(valid.Count);
        foreach (var row in rows)
            result.AddRange(row.OrderBy(l => l.Left));
        return result;
    }

    /// <summary>
    /// 按坐标重建为 CSV (制表符分隔的表格文本)。
    /// 思路: 行由纵向聚类得到, 列由各行起始 x 的聚类得到, 缺失单元格留空。
    /// 适合从截图里的表格快速取数。
    /// </summary>
    public static string ToCsv(IReadOnlyList<Line> lines, char delimiter = '\t')
    {
        var valid = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (valid.Count == 0)
            return string.Empty;

        // 1) 纵向分行
        var sortedRows = new List<List<Line>>();
        foreach (var line in valid.OrderBy(l => l.CenterY).ToList())
        {
            List<Line>? target = null;
            foreach (var row in sortedRows)
            {
                float tolerance = Math.Max(2f, row.Average(x => x.Height) * 0.5f);
                if (Math.Abs(line.CenterY - row.Average(x => x.CenterY)) <= tolerance)
                {
                    target = row;
                    break;
                }
            }
            if (target is null) sortedRows.Add([line]);
            else target.Add(line);
        }
        foreach (var row in sortedRows)
            row.Sort((a, b) => a.Left.CompareTo(b.Left));

        // 2) 横向分列: 收集所有"文本块左边界"作为列锚点 (容差取中位块高)
        float avgHeight = valid.Average(l => l.Height);
        float colTolerance = Math.Max(6f, avgHeight * 0.6f);
        var colAnchors = new List<float>();
        foreach (var row in sortedRows)
        {
            foreach (var cell in row)
            {
                if (!colAnchors.Any(a => Math.Abs(a - cell.Left) <= colTolerance))
                    colAnchors.Add(cell.Left);
            }
        }
        colAnchors.Sort();

        // 3) 填充网格
        var grid = new string[sortedRows.Count, colAnchors.Count];
        for (int r = 0; r < sortedRows.Count; r++)
        {
            foreach (var cell in sortedRows[r])
            {
                int best = 0;
                float bestDist = float.MaxValue;
                for (int c = 0; c < colAnchors.Count; c++)
                {
                    float d = Math.Abs(colAnchors[c] - cell.Left);
                    if (d < bestDist) { bestDist = d; best = c; }
                }
                // 同一格已有内容 (极少数重叠) 则追加
                grid[r, best] = string.IsNullOrEmpty(grid[r, best])
                    ? cell.Text
                    : grid[r, best] + " " + cell.Text;
            }
        }

        var sb = new StringBuilder();
        for (int r = 0; r < sortedRows.Count; r++)
        {
            var cells = new string[colAnchors.Count];
            for (int c = 0; c < colAnchors.Count; c++)
                cells[c] = Escape(grid[r, c] ?? string.Empty, delimiter);
            // 去掉行尾多余空列
            int last = cells.Length - 1;
            while (last >= 0 && cells[last].Length == 0) last--;
            if (last < 0) continue;
            sb.AppendLine(string.Join(delimiter, cells.Take(last + 1)));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>CSV 字段转义 (含分隔符/引号/换行时加引号)</summary>
    private static string Escape(string value, char delimiter)
    {
        if (value.IndexOf(delimiter) < 0 && value.IndexOf('"') < 0 &&
            value.IndexOf('\n') < 0 && value.IndexOf('\r') < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
