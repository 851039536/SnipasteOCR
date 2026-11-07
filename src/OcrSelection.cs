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
