using System.Drawing;

namespace SnipasteOcr.Annotations;

/// <summary>
/// 标注拖拽的 Ctrl 约束几何 (仿 Flameshot):
/// 矩形/椭圆约束为正方形/正圆, 箭头吸附到 8 个方向 (0°/45°/90°...)。
///
/// 单独成纯函数, 让覆盖层的实时预览 (<c>OnMouseMove</c>) 与松手提交 (<c>OnMouseUp</c>)
/// 共用同一份结果 —— 两边各算一份的话, 预览按约束画、提交却按原始鼠标点存,
/// 松手瞬间形状会跳变; 这也是本项目"几何单一来源"约束的一部分。
/// </summary>
public static class AnnotationDrag
{
    private const float SnapStep = MathF.PI / 4f;

    /// <summary>
    /// 对拖拽终点施加约束。不约束或工具不支持时原样返回终点。
    /// </summary>
    /// <param name="tool">当前标注工具</param>
    /// <param name="start">拖拽起点 (物理像素)</param>
    /// <param name="end">鼠标当前点 (物理像素)</param>
    /// <param name="constrain">是否按住 Ctrl</param>
    public static PointF ConstrainEnd(AnnotationTool tool, PointF start, PointF end, bool constrain)
    {
        if (!constrain ||
            tool is not (AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Arrow))
        {
            return end;
        }

        float dx = end.X - start.X;
        float dy = end.Y - start.Y;

        if (tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse)
        {
            // 取较长边作为正方形边长, 方向沿用鼠标相对起点的象限;
            // 某一轴恰好为 0 时默认取正方向, 避免退化成一个点。
            float size = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));
            float sx = dx < 0 ? -1f : 1f;
            float sy = dy < 0 ? -1f : 1f;
            return new PointF(start.X + sx * size, start.Y + sy * size);
        }

        // 箭头: 保留拖拽长度, 把角度吸附到最近的 45° 整数倍。
        // 长度过小时 atan2 的角度噪声没有意义, 直接放行 (后续会被 IsDegenerate 丢弃)。
        float length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 1f)
            return end;

        float snapped = MathF.Round(MathF.Atan2(dy, dx) / SnapStep) * SnapStep;
        return new PointF(
            start.X + MathF.Cos(snapped) * length,
            start.Y + MathF.Sin(snapped) * length);
    }
}
