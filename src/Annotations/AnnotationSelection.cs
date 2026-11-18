using System.Drawing;
using System.Drawing.Drawing2D;

namespace SnipasteOcr.Annotations;

/// <summary>
/// 选中标注的装饰绘制 (虚线选框 + 8 个手柄)。
///
/// <b>只允许在覆盖层的预览绘制里调用，绝不能进 <see cref="AnnotationEngine.Draw"/>。</b>
/// 后者同时服务实时预览与 <see cref="SnipOverlayForm"/> 的 RenderResult 导出，
/// 一旦把选中框写进去，用户导出的图片上就会多出一圈虚线框和 8 个白方块。
/// </summary>
internal static class AnnotationSelectionPainter
{
    /// <summary>手柄边长 (屏幕像素)。绘制时按 DPI 换算成物理像素。</summary>
    public const float HandleSize = 8f;

    /// <summary>8 个手柄的外接矩形 (世界坐标, 由调用方按 DPI 放大)</summary>
    public static RectangleF[] HandleRects(RectangleF bounds, float size)
    {
        float half = size / 2f;
        float cx = bounds.Left + bounds.Width / 2f;
        float cy = bounds.Top + bounds.Height / 2f;

        return
        [
            Box(bounds.Left, bounds.Top),      // 左上
            Box(cx, bounds.Top),               // 上
            Box(bounds.Right, bounds.Top),     // 右上
            Box(bounds.Right, cy),             // 右
            Box(bounds.Right, bounds.Bottom),  // 右下
            Box(cx, bounds.Bottom),            // 下
            Box(bounds.Left, bounds.Bottom),   // 左下
            Box(bounds.Left, cy),              // 左
        ];

        RectangleF Box(float x, float y) => new(x - half, y - half, size, size);
    }

    /// <summary>
    /// 绘制选中装饰。
    /// <paramref name="scaleFactor"/> 是"物理像素/逻辑像素"比例: 调用点已把 Graphics
    /// 缩放到物理坐标系, 线宽与手柄必须乘回去, 才能在任何 DPI 下都是屏幕上恒定的大小。
    /// </summary>
    public static void Draw(Graphics g, Annotation annotation, float scaleFactor)
    {
        var bounds = annotation.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        float sf = MathF.Max(0.01f, scaleFactor);
        float line = MathF.Max(1f, sf);          // 屏幕上恒为 1px
        float size = HandleSize * sf;

        using (var outline = new Pen(Color.FromArgb(220, 255, 255, 255), line) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height);

        var handles = HandleRects(bounds, size);
        using var fill = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
        using var border = new Pen(UiTheme.Accent, line);

        foreach (var h in handles)
        {
            g.FillRectangle(fill, h);
            g.DrawRectangle(border, h.X, h.Y, h.Width, h.Height);
        }
    }
}
