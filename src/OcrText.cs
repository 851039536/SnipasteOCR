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
    /// 思路: 行由纵向聚类得到, 列由横向「列带」得到, 缺失单元格留空。
    /// 适合从截图里的表格快速取数。
    /// </summary>
    public static string ToCsv(IReadOnlyList<Line> lines, char delimiter = '\t')
    {
        var valid = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (valid.Count == 0)
            return string.Empty;

        // 1) 纵向分行 (与阅读顺序排序共用同一实现)
        var sortedRows = ClusterRows(valid);

        // 2) 横向分列: 取「单元格数最多的行」作为参考行, 把单元格的 [Left, Right]
        //    投影到 x 轴, 用区间空隙切列带。
        //    不能只按左边界聚类: 同一列里居中表头与右对齐数字的 Left 差别很大,
        //    会被判成多个列, 导致 CSV 列数膨胀、值错位。
        var bands = BuildColumnBands(sortedRows);
        if (bands.Count == 0)
            return string.Empty;

        // 3) 填充网格: 按与列带的重叠长度归入 (并列取最左), 缺失格留空
        var grid = new string[sortedRows.Count, bands.Count];
        for (int r = 0; r < sortedRows.Count; r++)
        {
            foreach (var cell in sortedRows[r])
            {
                int best = 0;
                float bestOverlap = float.MinValue;
                for (int c = 0; c < bands.Count; c++)
                {
                    float overlap = Math.Min(bands[c].Right, cell.Right) - Math.Max(bands[c].Left, cell.Left);
                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        best = c;
                    }
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
            var cells = new string[bands.Count];
            for (int c = 0; c < bands.Count; c++)
                cells[c] = Escape(grid[r, c] ?? string.Empty, delimiter);
            // 去掉行尾多余空列
            int last = cells.Length - 1;
            while (last >= 0 && cells[last].Length == 0) last--;
            if (last < 0) continue;
            sb.AppendLine(string.Join(delimiter, cells.Take(last + 1)));
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>一个列带在 x 轴上的覆盖范围</summary>
    private readonly record struct ColumnBand(float Left, float Right);

    /// <summary>
    /// 从各行单元格的横向区间推导列带 (唯一实现)。
    ///
    /// 参考行取「单元格数最多的行」: 跨列表头通常只占一个单元格, 若把它算进去,
    /// 它的宽区间会桥接两列、让列分隔消失。参考行之间仍用区间并集求候选空隙,
    /// 再按「空隙两侧的单元格数量 + 空隙宽度」选真正的列分隔 ——
    /// 列内因对齐产生的窄空隙两侧单元格少, 列间的稳定空白两侧单元格多。
    /// 选出的分隔把并集切段合并成列带; 无法确定时宁少拆不多拆。
    /// </summary>
    private static List<ColumnBand> BuildColumnBands(List<List<Line>> rows)
    {
        if (rows.Count == 0)
            return [];

        int maxCells = rows.Max(r => r.Count);
        // 单列表 (每行最多一个单元格): 不同行的居中/缩进不应被误拆成多列
        if (maxCells <= 1)
            return [SpanOf(rows.SelectMany(r => r))];

        var reference = rows.Where(r => r.Count == maxCells).ToList();
        if (reference.Count == 0)
            reference = rows;

        var intervals = reference
            .SelectMany(r => r)
            .Where(c => c.Right > c.Left)
            .Select(c => (c.Left, c.Right))
            .OrderBy(x => x.Left)
            .ToList();
        if (intervals.Count == 0)
            return [SpanOf(rows.SelectMany(r => r))];

        var segments = new List<(float Left, float Right)> { intervals[0] };
        for (int i = 1; i < intervals.Count; i++)
        {
            var (left, right) = intervals[i];
            // 只合并真正重叠的区间; 仅"相接"的单元格保留为 0 宽空隙,
            // 让同一行里两个紧邻的块仍能按列分开 (旧左边界聚类行为不回归)。
            if (left < segments[^1].Right)
                segments[^1] = (segments[^1].Left, Math.Max(segments[^1].Right, right));
            else
                segments.Add((left, right));
        }

        // 所有参考单元格连成一个重叠簇: 没有可用的列间空白, 只能算一列
        if (segments.Count <= 1)
            return [new ColumnBand(segments[0].Left, segments[0].Right)];

        var gaps = new List<(int Index, float Width, int Score)>();
        for (int i = 0; i < segments.Count - 1; i++)
        {
            var (segLeft, segRight) = segments[i];
            var (nextLeft, nextRight) = segments[i + 1];
            int leftCells = reference.Sum(row => row.Count(c => c.Left < segRight && c.Right > segLeft));
            int rightCells = reference.Sum(row => row.Count(c => c.Left < nextRight && c.Right > nextLeft));
            gaps.Add((i, nextLeft - segRight, leftCells + rightCells));
        }

        int splitCount = Math.Min(maxCells - 1, gaps.Count);
        var chosen = gaps
            .OrderByDescending(g => g.Score)
            .ThenByDescending(g => g.Width)
            .Take(splitCount)
            .Select(g => g.Index)
            .ToHashSet();

        var bands = new List<ColumnBand>();
        float bandLeft = segments[0].Left, bandRight = segments[0].Right;
        for (int i = 0; i < segments.Count - 1; i++)
        {
            if (chosen.Contains(i))
            {
                bands.Add(new ColumnBand(bandLeft, bandRight));
                bandLeft = segments[i + 1].Left;
                bandRight = segments[i + 1].Right;
            }
            else
            {
                bandRight = Math.Max(bandRight, segments[i + 1].Right);
            }
        }
        bands.Add(new ColumnBand(bandLeft, bandRight));
        return bands;
    }

    /// <summary>一组单元格的横向总覆盖范围</summary>
    private static ColumnBand SpanOf(IEnumerable<Line> cells)
    {
        float left = float.MaxValue, right = float.MinValue;
        foreach (var cell in cells)
        {
            left = Math.Min(left, cell.Left);
            right = Math.Max(right, cell.Right);
        }
        return new ColumnBand(left, right);
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
    /// 开括号/连字符/斜杠两侧也不补 —— 旧实现只把闭标点列为例外, 结果 "(中文" 变成 "( 中文",
    /// "well-known" / "A/B" 被拆成 "well - known" / "A / B"。
    /// </summary>
    private static bool NeedsSpace(char left, char right)
    {
        // 已有空白则不再补
        if (char.IsWhiteSpace(left) || char.IsWhiteSpace(right))
            return false;
        if (IsCjk(left) && IsCjk(right))
            return false;
        // 不在闭标点/连字符前加空格; 不在开括号/连字符/闭标点后加空格。
        // 闭标点后同样不补, 保持 "。ABC" 这类原有行为不变。
        if (IsNoSpaceBefore(right) || IsNoSpaceAfter(left))
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

    /// <summary>连字符/斜杠/破折号: 两侧都不插空格 ("well-known"、"A/B"、"2024-01")</summary>
    private static bool IsTightPunctuation(char c) =>
        c is '-' or '/' or '\\' or '\u2013' or '\u2014';

    /// <summary>右侧为闭口标点时不该在其前插空格 (如 "。" "，" ")" ":")</summary>
    private static bool IsNoSpaceBefore(char c) =>
        IsTightPunctuation(c) ||
        c is '。' or '，' or '、' or '；' or '：' or '？' or '！' or '）' or '》' or '」'
          or '.' or ',' or ';' or ':' or '?' or '!' or ')' or ']' or '}' or '%';

    /// <summary>左侧为开括号/连字符/闭标点时, 其后不该插空格</summary>
    private static bool IsNoSpaceAfter(char c) =>
        IsTightPunctuation(c) ||
        c is '(' or '[' or '{' or '（' or '【' or '《' or '「' or '『'
          or '。' or '，' or '、' or '；' or '：' or '？' or '！' or '）' or '》' or '」'
          or '.' or ',' or ';' or ':' or '?' or '!' or ')' or ']' or '}' or '%';
}
