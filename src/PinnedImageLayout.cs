using System.Drawing;

namespace SnipasteOcr;

/// <summary>
/// 贴图窗口的尺寸 / 缩放 / 透明度 / 缩放锚点 (纯函数)。
///
/// 抽出来的理由和 SnipSizeBadge 一样: 这些公式要同时被"窗口实际尺寸""滚轮缩放"
/// "DPI 变化"三处使用, 各写一份必然在高 DPI 下分叉 (贴图被放大 1.25 倍或只画出左上角)。
/// </summary>
public static class PinnedImageLayout
{
    public const float MinZoom = 0.1f;
    public const float MaxZoom = 8f;
    public const float ZoomStep = 1.1f;

    public const double MinOpacity = 0.15;
    public const double MaxOpacity = 1.0;
    public const double OpacityStep = 0.1;

    /// <summary>把缩放夹紧到允许范围</summary>
    public static float NormalizeZoom(float zoom)
    {
        if (float.IsNaN(zoom) || zoom <= 0)
            return 1f;
        return Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    /// <summary>
    /// 物理像素图片在给定 DPI 与缩放下的客户区尺寸 (逻辑像素)。
    ///
    /// WinForms 的 ClientSize 是逻辑像素, 而截图是物理像素 —— 必须除以 DPI 因子,
    /// 否则 PerMonitorV2 下 150% 缩放会把贴图放大 1.5 倍。
    /// </summary>
    public static Size ClientSize(int physicalWidth, int physicalHeight, float dpiFactor, float zoom)
    {
        float dpi = dpiFactor <= 0 ? 1f : dpiFactor;
        float z = NormalizeZoom(zoom);
        return new Size(
            Math.Max(1, (int)Math.Round(physicalWidth * z / dpi)),
            Math.Max(1, (int)Math.Round(physicalHeight * z / dpi)));
    }

    /// <summary>滚轮增量 → 新缩放 (向上放大, 向下缩小, 结果夹紧)</summary>
    public static float StepZoom(float zoom, int wheelDelta)
    {
        if (wheelDelta == 0)
            return NormalizeZoom(zoom);

        float next = wheelDelta > 0 ? zoom * ZoomStep : zoom / ZoomStep;
        return NormalizeZoom(next);
    }

    /// <summary>方向 → 新透明度 (1 = 更不透明, -1 = 更透明)</summary>
    public static double StepOpacity(double opacity, int direction)
    {
        if (direction == 0)
            return Math.Clamp(opacity, MinOpacity, MaxOpacity);

        double next = opacity + (direction > 0 ? OpacityStep : -OpacityStep);
        return Math.Clamp(Math.Round(next, 2), MinOpacity, MaxOpacity);
    }

    /// <summary>
    /// 缩放时保持"光标下那个像素"不动所需的窗口左上角 (屏幕坐标)。
    ///
    /// 窗口的物理尺寸与 zoom 成正比, 所以原点要按同一比例朝光标方向移动:
    /// 直接改 Size 而不动原点, 会让内容从左上角"长出去", 用户体验是画面在乱跑。
    /// </summary>
    public static Point AnchorLocation(Point formLocation, Point cursorScreen, float oldZoom, float newZoom)
    {
        if (oldZoom <= 0 || newZoom <= 0 || Math.Abs(oldZoom - newZoom) < float.Epsilon)
            return formLocation;

        double ratio = (double)newZoom / oldZoom;
        return new Point(
            cursorScreen.X - (int)Math.Round((cursorScreen.X - formLocation.X) * ratio),
            cursorScreen.Y - (int)Math.Round((cursorScreen.Y - formLocation.Y) * ratio));
    }
}
