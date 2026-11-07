using System.Text;
using Sdcb.SimdPaddleOCR;

namespace SnipasteOcr;

/// <summary>
/// 一行识别结果的真实字符框布局, 来源是引擎的 CTC 对齐 (PaddleOcrLine.EstimateCharacterBoxes)。
///
/// 为什么需要这层: PaddleOCR 只输出整行文本与检测框, 旧实现按字符宽度权重
/// (汉字 1.0 / ASCII 0.6 / 空格 0.35) 把字符投影到行轴上, 字符边界与真实框有 1~2px 偏差,
/// 且不区分同字号下的实际字宽 (i / W 差别很大)。CTC 对齐能给出逐 token 的四边形,
/// 这里把它归一化成「文本轴光标边界 + 每字符四边形」, 供选词命中与高亮直接使用。
///
/// 任何不确定的情况 (没有 CTC、字符数对不上、竖排、几何退化) 都返回 null,
/// 由调用方回退到旧的权重估算 —— 宁可精度差一点, 也不能让整行选词失效。
/// </summary>
public sealed class OcrGlyphLayout
{
    /// <summary>该行的识别文本 (与边界、字符框一一对应)</summary>
    public string Text { get; }

    /// <summary>
    /// 文本轴上的光标边界, 长度 = Text.Length + 1, 范围 0..1。
    /// <c>Boundaries[i]</c> 是第 i 个字符的左边界 (0 为行首, 1 为行尾, 含末尾留白)。
    /// </summary>
    public float[] Boundaries { get; }

    /// <summary>每个 UTF-16 字符对应的真实四边形 (多字符 token 的字符共享同一框)</summary>
    public PaddleOcrCharacterBox[] Boxes { get; }

    /// <summary>该行被引擎旋转的角度 (0 或 180), 用于把源图方向的位置换算到文本轴</summary>
    public int RotationDegrees { get; }

    private OcrGlyphLayout(string text, float[] boundaries, PaddleOcrCharacterBox[] boxes, int rotationDegrees)
    {
        Text = text;
        Boundaries = boundaries;
        Boxes = boxes;
        RotationDegrees = rotationDegrees;
    }

    /// <summary>
    /// 源图行轴上的归一化位置 u (0=检测框顶边起点, 1=终点) -> 行内光标索引 (0..n)。
    /// 180 度旋转时文字方向与顶边相反, 先翻转 u 再按文本轴边界判定。
    /// </summary>
    public int IndexAtSourceU(float u)
        => OcrSelection.CharIndexFromU(Boundaries, RotationDegrees == 180 ? 1f - u : u);

    /// <summary>为每个检测行建立布局; 不支持的行返回 null (调用方回退权重)</summary>
    public static OcrGlyphLayout?[] BuildAll(IReadOnlyList<PaddleOcrLine> lines)
    {
        var layouts = new OcrGlyphLayout?[lines.Count];
        for (int i = 0; i < lines.Count; i++)
            layouts[i] = TryCreate(lines[i]);
        return layouts;
    }

    /// <summary>尝试从 CTC 对齐构造布局; 任何缺失/不一致都返回 null</summary>
    public static OcrGlyphLayout? TryCreate(PaddleOcrLine line)
    {
        if (line is null)
            return null;

        string text = line.Text;
        if (string.IsNullOrEmpty(text))
            return null;
        if (line.CtcSpans is not { Length: > 0 })
            return null;
        if (line.AppliedRotationDegrees is not (0 or 180))
            return null;

        // 竖排/90° 裁剪的字符框沿检测框长边排列, 当前选词只沿顶边投影, 不支持;
        // 这里主动回退, 与引擎判断「竖排」的阈值保持一致 (PPOCRCrop.TryComputePerspective)。
        if (line.Box.Height >= line.Box.Width * 1.5f)
            return null;

        PaddleOcrCharacterBox[] tokens;
        try
        {
            tokens = line.EstimateCharacterBoxes();
        }
        catch
        {
            // 几何退化 / span 非法 / 未捕获对齐: 回退权重估算
            return null;
        }
        if (tokens.Length == 0)
            return null;

        // EstimateCharacterBoxes 每个 token 一个框, 而 line.Text 可能由多字符 token 组成;
        // 先确认二者逐字符一致, 否则整行回退。
        var tokenText = new StringBuilder(text.Length);
        foreach (var token in tokens)
        {
            if (string.IsNullOrEmpty(token.Text))
                return null;
            tokenText.Append(token.Text);
        }
        if (tokenText.Length != text.Length || tokenText.ToString() != text)
            return null;

        var (topA, topB) = OcrBox.TopEdge(line.Box);
        float dx = topB.X - topA.X, dy = topB.Y - topA.Y;
        float len2 = dx * dx + dy * dy;
        if (len2 < 1e-6f)
            return null;

        var boxes = new List<PaddleOcrCharacterBox>(text.Length);
        var boundaries = new float[text.Length + 1];
        boundaries[0] = 0f;
        float previous = 0f;

        foreach (var token in tokens)
        {
            var (uMin, uMax) = ProjectRange(token, topA.X, topA.Y, dx, dy, len2);
            // 先换算到文本轴: 180 度时文字从框的右端开始
            float textStart = line.AppliedRotationDegrees == 180 ? 1f - uMax : uMin;
            float textEnd = line.AppliedRotationDegrees == 180 ? 1f - uMin : uMax;

            int charCount = token.Text.Length;
            for (int k = 0; k < charCount; k++)
            {
                // 多字符 token 的框覆盖整个 token: 光标边界按字符数均分, 框本身共享
                float end = textStart + (textEnd - textStart) * (k + 1) / charCount;
                end = Math.Clamp(end, 0f, 1f);
                if (end <= previous + 1e-5f)
                    return null; // 字符框退化/重叠, 不冒险
                previous = end;
                boundaries[boxes.Count + 1] = end;

                boxes.Add(new PaddleOcrCharacterBox(
                    token.Text[k].ToString(), token.Score,
                    token.X1, token.Y1, token.X2, token.Y2,
                    token.X3, token.Y3, token.X4, token.Y4));
            }
        }

        // 最后一个字符右侧通常还有检测框留白; 把它并入最后一个字符的选区,
        // 这样拖到行尾能得到完整的 n 个字符。
        boundaries[^1] = 1f;
        if (boundaries.Length > 2 && boundaries[^1] <= boundaries[^2])
            return null;

        return new OcrGlyphLayout(text, boundaries, [.. boxes], line.AppliedRotationDegrees);
    }

    /// <summary>把字符框四角投影到行轴, 返回归一化参数范围</summary>
    private static (float Min, float Max) ProjectRange(
        in PaddleOcrCharacterBox box, float originX, float originY, float dx, float dy, float len2)
    {
        float u1 = ((box.X1 - originX) * dx + (box.Y1 - originY) * dy) / len2;
        float u2 = ((box.X2 - originX) * dx + (box.Y2 - originY) * dy) / len2;
        float u3 = ((box.X3 - originX) * dx + (box.Y3 - originY) * dy) / len2;
        float u4 = ((box.X4 - originX) * dx + (box.Y4 - originY) * dy) / len2;
        return (Math.Min(Math.Min(u1, u2), Math.Min(u3, u4)),
                Math.Max(Math.Max(u1, u2), Math.Max(u3, u4)));
    }
}
