using Sdcb.SimdPaddleOCR;

namespace SnipasteOcr;

/// <summary>
/// OCR 结果窗口的选词辅助 (纯函数)。
/// 独立于窗体便于离线自检直接链接; 窗体只负责把鼠标坐标换算成行内归一化位置。
/// </summary>
public static class OcrSelection
{
    /// <summary>
    /// 按字符中线把行内归一化位置 u (0=行首, 1=行尾) 反解为字符索引 (0..n)。
    ///
    /// 返回值 n 是合法的"行尾光标位置", 表示选区可以包含最后一个字符 ——
    /// 旧实现把索引限制在 0..n-1, 于是拖到行尾永远少选最后一个字符。
    /// 命中字符左半格返回该字符索引, 右半格返回下一个索引 (文本编辑器的光标语义)。
    /// </summary>
    public static int CharIndexFromU(ReadOnlySpan<float> cum, float u, int rotationDegrees = 0)
    {
        if (cum.Length < 2)
            return 0;

        u = Math.Clamp(u, 0f, 1f);
        if (rotationDegrees == 180)
            u = 1f - u; // 180 度旋转时文字方向与框顶边相反

        float target = u * cum[^1];
        for (int k = 0; k < cum.Length - 1; k++)
        {
            if (target < (cum[k] + cum[k + 1]) * 0.5f)
                return k;
        }
        return cum.Length - 1;
    }

    /// <summary>
    /// 双击选词的字符范围 [Start, End)。按「同类连续段」切分:
    /// ASCII 字母/数字/下划线、CJK 文字、空白各自成段; 其它标点只选当前字符。
    /// 中文没有分词词典, 连续汉字整段选中 —— 比只选一个字更接近用户预期, 也不会把
    /// 中英混排的 "OCR识别" 拆成两段。
    /// </summary>
    /// <param name="text">行文本</param>
    /// <param name="index">双击落点反解出的字符索引 (0..n)</param>
    /// <returns>无文本时返回 null</returns>
    public static (int Start, int End)? WordRange(string text, int index)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        int i = Math.Clamp(index, 0, text.Length - 1);
        // 代理对 (扩展 B 汉字等): 不能把高低代理拆开
        if (char.IsLowSurrogate(text[i]) && i > 0 && char.IsHighSurrogate(text[i - 1]))
            i--;
        if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            return (i, i + 2);

        CharClass cls = ClassOf(text[i]);
        if (cls == CharClass.Other)
            return (i, i + 1);

        int start = i, end = i + 1;
        while (start > 0 && ClassOf(text[start - 1]) == cls)
            start--;
        while (end < text.Length && ClassOf(text[end]) == cls)
            end++;
        return (start, end);
    }

    private enum CharClass
    {
        Word,
        Cjk,
        Space,
        Other,
    }

    private static CharClass ClassOf(char c)
    {
        if (char.IsWhiteSpace(c))
            return CharClass.Space;
        if (IsCjkLetter(c))
            return CharClass.Cjk;
        if (char.IsLetterOrDigit(c) || c == '_')
            return CharClass.Word;
        return CharClass.Other;
    }

    /// <summary>仅 CJK 文字 (不含全角标点): 标点应作为单字边界, 不能被吞进词里</summary>
    private static bool IsCjkLetter(char c) =>
        (c >= 0x4E00 && c <= 0x9FFF) ||     // 基本汉字
        (c >= 0x3400 && c <= 0x4DBF) ||     // 扩展 A
        (c >= 0x3040 && c <= 0x30FF) ||     // 日文假名
        (c >= 0xAC00 && c <= 0xD7AF);       // 韩文

    /// <summary>
    /// 为每个检测行建立「_lines 索引 -> 阅读顺序名次」映射 (空白行排到最后)。
    /// 多选/全选复制按名次排序, 才能与「复制全部」「复制段落」使用同一阅读顺序;
    /// 直接按检测顺序索引排序时, 多栏截图会先复制右栏再复制左栏。
    /// </summary>
    public static int[] BuildReadingRanks(IReadOnlyList<PaddleOcrLine> lines, bool sortReadingOrder)
    {
        var ranks = new int[lines.Count];
        Array.Fill(ranks, int.MaxValue);

        var entries = new List<(int Index, OcrText.Line Line)>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i].Text))
                continue;
            entries.Add((i, OcrText.ToLine(lines[i])));
        }

        if (!sortReadingOrder)
        {
            for (int r = 0; r < entries.Count; r++)
                ranks[entries[r].Index] = r;
            return ranks;
        }

        // 排序结果只带值, 这里按值回填到原索引。Line 是值类型, 内容完全相同的两块
        // 谁先谁后不影响复制结果, 因此按"未占用"顺序匹配即可。
        var sorted = OcrText.SortReadingOrder([.. entries.Select(e => e.Line)]);
        var used = new bool[entries.Count];
        for (int rank = 0; rank < sorted.Count; rank++)
        {
            for (int e = 0; e < entries.Count; e++)
            {
                if (used[e] || !entries[e].Line.Equals(sorted[rank]))
                    continue;
                used[e] = true;
                ranks[entries[e].Index] = rank;
                break;
            }
        }
        return ranks;
    }
}
