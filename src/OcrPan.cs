namespace SnipasteOcr;

/// <summary>
/// 结果窗口的平移几何 (纯函数): 偏移相对"图片居中"位置, 单位与 ImageRect 一致 (物理像素)。
///
/// 必须夹紧: 高倍缩放时窗口只显示图片的一部分, 但平移不能让图片边缘进入视口内侧,
/// 否则会露出背景空白; 图片比视口小时偏移归零, 保持居中。
/// </summary>
internal static class OcrPan
{
    /// <summary>图片是否大于视口 (任一轴), 决定中键/空格拖动能否平移</summary>
    public static bool CanPan(SizeF image, SizeF viewport) =>
        image.Width > viewport.Width || image.Height > viewport.Height;

    /// <summary>把偏移夹紧到「图片始终覆盖视口」的范围; 图片比视口小时返回 (0,0)</summary>
    public static PointF Clamp(PointF offset, SizeF image, SizeF viewport) => new(
        ClampAxis(offset.X, image.Width, viewport.Width),
        ClampAxis(offset.Y, image.Height, viewport.Height));

    /// <summary>单轴夹紧: 居中位置两侧各可移动 (图片尺寸 - 视口尺寸) / 2</summary>
    private static float ClampAxis(float offset, float imageSize, float viewportSize)
    {
        float slack = (imageSize - viewportSize) / 2f;
        return slack <= 0 ? 0 : Math.Clamp(offset, -slack, slack);
    }
}
