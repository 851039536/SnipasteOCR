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

    // 纵向分行已迁至 OcrTableBuilder.ClusterRows —— 那是阅读顺序、表格重建、段落导出
    // 三处共用的唯一实现, 不得在本类里再写第二份。

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
        foreach (var row in OcrTableBuilder.ClusterRows(valid))
            result.AddRange(row);
        return result;
    }

    /// <summary>
    /// 按坐标重建为 CSV (制表符分隔的表格文本)。
    /// 网格本身由 <see cref="OcrTableBuilder.Build"/> 构建 —— 与 XLSX / Markdown 表格同源,
    /// 三种导出格式不得各自重算分行分列。这里只负责按分隔符渲染, 并去掉行尾多余空列。
    /// </summary>
    public static string ToCsv(IReadOnlyList<Line> lines, char delimiter = '\t')
    {
        var table = OcrTableBuilder.Build(lines);
        if (table.RowCount == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (int r = 0; r < table.RowCount; r++)
        {
            var cells = new string[table.ColumnCount];
            for (int c = 0; c < table.ColumnCount; c++)
                cells[c] = Escape(table.Get(r, c), delimiter);

            // 去掉行尾多余空列
            int last = cells.Length - 1;
            while (last >= 0 && cells[last].Length == 0) last--;
            if (last < 0) continue;
            sb.AppendLine(string.Join(delimiter, cells.Take(last + 1)));
        }
        return sb.ToString().TrimEnd();
    }

    // 列带推导 (原 BuildColumnBands / SpanOf) 已迁至 OcrTableBuilder —— 与纵向分行一起
    // 构成表格网格的唯一来源, 供 CSV / XLSX / Markdown 三种导出共用。

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
        foreach (var row in OcrTableBuilder.ClusterRows(valid))
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
