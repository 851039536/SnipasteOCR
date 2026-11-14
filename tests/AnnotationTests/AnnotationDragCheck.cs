// 校验标注拖拽的 Ctrl 约束几何: 矩形/椭圆变正, 箭头吸附 8 方向。
using System.Drawing;
using SnipasteOcr.Annotations;

namespace SnipasteOcr.Tests;

internal static class AnnotationDragCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);
        static bool Near(float a, float b, float tolerance = 0.01f) => MathF.Abs(a - b) <= tolerance;

        var start = new PointF(10, 20);
        var end = new PointF(110, 60);   // 宽 100, 高 40

        // 不按 Ctrl: 所有工具都原样返回终点 (不能悄悄改变普通拖拽)
        foreach (var tool in AnnotationToolInfo.AllTools)
        {
            Check(AnnotationDrag.ConstrainEnd(tool, start, end, constrain: false) == end,
                $"{AnnotationToolInfo.DisplayName(tool)} 未按 Ctrl 时不改终点");
        }

        var rect = AnnotationDrag.ConstrainEnd(AnnotationTool.Rectangle, start, end, constrain: true);
        Check(Near(rect.X, start.X + 100) && Near(rect.Y, start.Y + 100),
            $"矩形 Ctrl 变正方形 (实得 {rect})");
        Check(Near(MathF.Abs(rect.X - start.X), MathF.Abs(rect.Y - start.Y)),
            "矩形 Ctrl 后宽高相等");

        var tall = AnnotationDrag.ConstrainEnd(
            AnnotationTool.Ellipse, start, new PointF(30, -80), constrain: true);
        Check(Near(tall.X, start.X + 100) && Near(tall.Y, start.Y - 100),
            $"椭圆反向拖拽 Ctrl 仍为正方形且方向正确 (实得 {tall})");

        var zeroAxis = AnnotationDrag.ConstrainEnd(
            AnnotationTool.Rectangle, start, new PointF(10, 120), constrain: true);
        Check(Near(MathF.Abs(zeroAxis.X - start.X), 100) && Near(zeroAxis.Y, start.Y + 100),
            $"某一轴为 0 时 Ctrl 仍得到有效正方形 (实得 {zeroAxis})");

        var pan = AnnotationDrag.ConstrainEnd(AnnotationTool.Pen, start, end, constrain: true);
        Check(pan == end, "画笔不受 Ctrl 约束");

        var mosaic = AnnotationDrag.ConstrainEnd(AnnotationTool.Mosaic, start, end, constrain: true);
        Check(mosaic == end, "马赛克不受 Ctrl 约束");

        // 箭头: 保留长度, 角度吸附到最近的 45°。
        var arrowStart = new PointF(0, 0);
        var horiz = AnnotationDrag.ConstrainEnd(
            AnnotationTool.Arrow, arrowStart, new PointF(100, 10), constrain: true);
        Check(Near(horiz.Y, 0) && Near(horiz.X, MathF.Sqrt(100 * 100 + 10 * 10)),
            $"箭头近水平时吸附到水平并保留长度 (实得 {horiz})");

        var vert = AnnotationDrag.ConstrainEnd(
            AnnotationTool.Arrow, arrowStart, new PointF(5, -100), constrain: true);
        Check(Near(vert.X, 0) && Near(vert.Y, -MathF.Sqrt(5 * 5 + 100 * 100)),
            $"箭头近垂直时吸附到垂直并保留长度 (实得 {vert})");

        var diag = AnnotationDrag.ConstrainEnd(
            AnnotationTool.Arrow, arrowStart, new PointF(100, 90), constrain: true);
        Check(Near(diag.X, diag.Y) && Near(diag.X, MathF.Sqrt(100 * 100 + 90 * 90) / MathF.Sqrt(2)),
            $"箭头近 45° 时吸附到 45° (实得 {diag})");

        var tiny = AnnotationDrag.ConstrainEnd(
            AnnotationTool.Arrow, arrowStart, new PointF(0.3f, 0.4f), constrain: true);
        Check(tiny == new PointF(0.3f, 0.4f), "箭头长度不足 1px 时不计算角度, 原样返回");

        Check(AnnotationToolInfo.ConstraintHint(AnnotationTool.Rectangle).Length > 0 &&
              AnnotationToolInfo.ConstraintHint(AnnotationTool.Ellipse).Length > 0 &&
              AnnotationToolInfo.ConstraintHint(AnnotationTool.Arrow).Length > 0,
            "三种可约束工具的提示文案非空");
        Check(AnnotationToolInfo.ConstraintHint(AnnotationTool.Pen).Length == 0 &&
              AnnotationToolInfo.ConstraintHint(AnnotationTool.Text).Length == 0,
            "无约束工具不显示 Ctrl 提示");

        return runner.ToResult();
    }
}
