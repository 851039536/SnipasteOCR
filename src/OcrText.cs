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
        var (left, top, right, bottom) = OcrBox.AxisAligned(src.Box);
        return new Line(src.Text ?? string.Empty, left, top, right, bottom);
    }

    /// <summary>
    /// 按纵向位置把文本块聚类成行, 行内按左边界排序。
    ///
    /// 这是「阅读顺序排序」与「表格/CSV 重建」共用的同一个分词行步骤 —— 两处若各写一份,
    /// 一旦容差公式漂移, 同一批结果会在列表里是一行、到表格里变成两行。
    /// 分行容差取该行平均高度的一半 (下限 2px), 避免同一行的文字因基线微差被拆开。
    /// </summary>
    private static List<List<Line>> ClusterRows(IReadOnlyList<Line> lines)
    {
        var rows = new List<List<Line>>();

        foreach (var line in lines.OrderBy(l => l.CenterY))
        {
            List<Line>? target = null;
            foreach (var row in rows)
            {
                // 用 Average (double 累加) 而非 Sum/Count: 两者浮点结果并不逐位相同,
                // 这里保持与重构前完全一致的数值行为, 不夹带未验证的语义变更。
                // (旧 ToCsv 在比较里重复调用 Average, 现已与 SortReadingOrder 统一为每行算一次。)
                float refCenter = (float)row.Average(x => x.CenterY);
                float refHeight = (float)row.Average(x => x.Height);
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

        foreach (var row in rows)
            row.Sort((a, b) => a.Left.CompareTo(b.Left));

        return rows;
    }

    /// <summary>
    /// 按阅读顺序 (先上→下分行, 再左→右) 重排。
    /// PaddleOCR 输出的顺序大体是检测顺序, 但多列/多栏时可能交叉, 这里显式排序。
    /// </summary>
    public static List<Line> SortReadingOrder(IReadOnlyList<Line> lines)
    {
        var valid = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (valid.Count <= 1)
            return valid;

        var result = new List<Line>(valid.Count);
        foreach (var row in ClusterRows(valid))
            result.AddRange(row);
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

        // 1) 纵向分行 (与阅读顺序排序共用同一实现)
        var sortedRows = ClusterRows(valid);

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

    /// <summary>
    /// 合并为连贯段落: 同一行内的文本块用空格接续, 行与行之间按需换行。
    ///
    /// 与 <see cref="ToCsv"/> 的区别: 这里面向"整段话"而非"表格单元格",
    /// 目的是把排版造成的硬换行去掉 (屏幕上换行往往只是排版, 不是语义分段)。
    /// 行内接续规则: 中文之间不插空格 (中文排版无词间空格), 中英/数字之间插一个空格,
    /// 否则会出现 "识别 引擎" 这种把词切开的怪结果。
    /// </summary>
    /// <param name="lines">文本块</param>
    /// <param name="joinAll">true = 全部合成一段; false = 每个聚类行一段 (保留原始分段)</param>
    public static string ToPlainText(IReadOnlyList<Line> lines, bool joinAll = false)
    {
        var paragraphs = BuildParagraphs(lines);
        if (paragraphs.Count == 0)
            return string.Empty;

        return joinAll
            ? string.Join(" ", paragraphs)
            : string.Join(Environment.NewLine, paragraphs);
    }

    /// <summary>
    /// 导出为 Markdown。段落之间空行分隔, 便于直接粘进文档/Issue。
    /// 与 <see cref="ToPlainText"/> 同源 (同一份段落构造), 只是行间分隔不同。
    /// </summary>
    public static string ToMarkdown(IReadOnlyList<Line> lines)
    {
        var paragraphs = BuildParagraphs(lines);
        if (paragraphs.Count == 0)
            return string.Empty;

        // Markdown 中单换行会被渲染成空格, 因此段落之间用空行 (即 \n\n) 才真正分段
        return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
    }

    /// <summary>
    /// 把文本块按"行"合并成段落列表 (唯一实现)。
    /// 纯文本导出与 Markdown 导出共用 —— 两处若各写一份, 行内接续规则一旦漂移,
    /// 同样一屏文字用两种格式导出会得到不同的断句。
    /// </summary>
    private static List<string> BuildParagraphs(IReadOnlyList<Line> lines)
    {
        var valid = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (valid.Count == 0)
            return [];

        var paragraphs = new List<string>();
        foreach (var row in ClusterRows(valid))
        {
            var sb = new StringBuilder();
            foreach (var cell in row)
            {
                string t = cell.Text.Trim();
                if (t.Length == 0)
                    continue;

                if (sb.Length > 0 && NeedsSpace(sb[^1], t[0]))
                    sb.Append(' ');
                sb.Append(t);
            }
            if (sb.Length > 0)
                paragraphs.Add(sb.ToString());
        }
        return paragraphs;
    }

    /// <summary>
    /// 相邻两字符之间是否需要补空格。
    /// 规则: 两侧都是 CJK 时不补 (中文没有词间空格); 其余情况补一个,
    /// 避免中英混排被粘成 "OCR识别" 或英文单词被并到数字上。
    /// </summary>
    private static bool NeedsSpace(char left, char right)
    {
        // 已有空白/标点则不再补, 避免 "，" 前多出空格
        if (char.IsWhiteSpace(left) || char.IsWhiteSpace(right))
            return false;
        if (IsCjk(left) && IsCjk(right))
            return false;
        if (IsNoSpacePunctuation(right) || IsNoSpacePunctuation(left))
            return false;
        return true;
    }

    /// <summary>是否 CJK (中日韩) 字符: 这些字符之间不插空格</summary>
    private static bool IsCjk(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||     // 基本汉字
        (c >= 0x3400 && c <= 0x4DBF) ||     // 扩展 A
        (c >= 0x3000 && c <= 0x303F) ||     // CJK 标点
        (c >= 0xFF00 && c <= 0xFFEF) ||     // 全角字符
        (c >= 0x3040 && c <= 0x30FF);       // 日文假名

    /// <summary>右侧为闭口标点时不该在其前插空格 (如 "。" "，" ")" ":")</summary>
    private static bool IsNoSpacePunctuation(char c) =>
        c is '。' or '，' or '、' or '；' or '：' or '？' or '！' or '）' or '》' or '」'
          or '.' or ',' or ';' or ':' or '?' or '!' or ')' or ']' or '}' or '%';
}
