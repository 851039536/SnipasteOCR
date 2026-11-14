// 校验马克笔/序号: 几何、命中、编号递增与真实渲染像素。
using System.Drawing;
using SnipasteOcr.Annotations;

namespace SnipasteOcr.Tests;

internal static class AnnotationToolsCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        // ===== 马克笔 =====
        Check(Math.Abs(AnnotationEngine.MarkerWidth(2f) - 8f) < 0.01f &&
              Math.Abs(AnnotationEngine.MarkerWidth(4f) - 16f) < 0.01f &&
              Math.Abs(AnnotationEngine.MarkerWidth(7f) - 28f) < 0.01f,
            "马克笔线宽按普通线宽放大 4 倍 (2/4/7 -> 8/16/28)");
        var markerColor = AnnotationEngine.MarkerColor(Color.FromArgb(255, 235, 59, 36));
        Check(markerColor.A < 255 && markerColor.R == 235 && markerColor.G == 59 && markerColor.B == 36,
            $"马克笔颜色保留 RGB、降低不透明度 (实得 A={markerColor.A})");

        var marker = new Annotation
        {
            Tool = AnnotationTool.Marker,
            Color = Color.FromArgb(255, 235, 59, 36),
            StrokeWidth = 2f,
        };
        marker.Points.AddRange([new PointF(10, 30), new PointF(110, 30)]);
        Check(!marker.IsDegenerate(), "两点马克笔不算退化");
        var markerBounds = marker.Bounds;
        Check(Math.Abs(markerBounds.Left - 10) < 0.01f && Math.Abs(markerBounds.Right - 110) < 0.01f &&
              Math.Abs(markerBounds.Top - 30) < 0.01f,
            $"马克笔 Bounds 为轨迹包围盒 (实得 {markerBounds})");
        Check(marker.HitTest(new PointF(60, 31), 2f), "命中马克笔轨迹");
        Check(!marker.HitTest(new PointF(60, 60), 2f), "不命中马克笔轨迹之外");

        using (var canvas = new Bitmap(140, 70))
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.White);
            AnnotationEngine.Draw(g, [marker], canvas);
            var center = canvas.GetPixel(60, 30);
            var outside = canvas.GetPixel(60, 42);
            Check(center.R > 200 && center.G < 235 && center.B < 235,
                $"马克笔中心是半透明红色 (实得 {center})");
            Check(outside.R > 245 && outside.G > 245 && outside.B > 245,
                $"马克笔宽度约 8px, 轨迹外保持白色 (实得 {outside})");
        }

        // ===== 序号 =====
        Check(AnnotationEngine.NextStepNumber([]) == 1, "空列表下一个序号从 1 开始");
        var numbered = new List<Annotation>
        {
            new() { Tool = AnnotationTool.Text, Text = "无关" },
            new() { Tool = AnnotationTool.StepNumber, Number = 2 },
            new() { Tool = AnnotationTool.StepNumber, Number = 5 },
        };
        Check(AnnotationEngine.NextStepNumber(numbered) == 6,
            $"下一个序号取现有最大值 + 1 (实得 {AnnotationEngine.NextStepNumber(numbered)})");

        float r1 = AnnotationEngine.StepBadgeRadius(9, 18f);
        float r2 = AnnotationEngine.StepBadgeRadius(10, 18f);
        float r3 = AnnotationEngine.StepBadgeRadius(100, 18f);
        Check(r2 > r1 && r3 > r2, $"多位数徽标半径递增 ({r1:F1} < {r2:F1} < {r3:F1})");

        var badge = new Annotation
        {
            Tool = AnnotationTool.StepNumber,
            Color = Color.FromArgb(255, 235, 59, 36),
            Start = new PointF(40, 40),
            Number = 1,
            FontSize = 18f,
        };
        var badgeBounds = badge.Bounds;
        Check(Math.Abs(badgeBounds.Width - r1 * 2) < 0.01f && Math.Abs(badgeBounds.Height - r1 * 2) < 0.01f,
            $"序号包围盒等于徽标直径 (实得 {badgeBounds.Width:F1})");
        Check(badge.HitTest(new PointF(40, 40), 0f), "命中序号中心");
        Check(!badge.HitTest(new PointF(5, 5), 0f), "不命中序号之外");

        var badgeClone = badge.Clone();
        Check(badgeClone.Number == 1, "克隆保留序号编号");

        using (var canvas = new Bitmap(80, 80))
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.White);
            AnnotationEngine.Draw(g, [badge], canvas);
            int colored = 0;
            var area = Rectangle.Round(badgeBounds);
            for (int y = Math.Max(0, area.Top); y < Math.Min(canvas.Height, area.Bottom); y++)
                for (int x = Math.Max(0, area.Left); x < Math.Min(canvas.Width, area.Right); x++)
                {
                    var c = canvas.GetPixel(x, y);
                    if (c.R > 150 || c.G < 100 || c.B < 100) colored++;
                }
            Check(colored > 100, $"序号徽标实际画出颜色 (彩色像素={colored})");
            var corner = canvas.GetPixel(2, 2);
            Check(corner.R > 245 && corner.G > 245 && corner.B > 245,
                $"序号徽标之外保持白色 (实得 {corner})");
        }

        // ===== 工具元数据 =====
        Check(AnnotationToolInfo.AllTools.Length == 8, "工具栏共 8 个工具");
        Check(AnnotationToolInfo.FromShortcutDigit(7) == AnnotationTool.Marker, "数字 7 对应马克笔");
        Check(AnnotationToolInfo.FromShortcutDigit(8) == AnnotationTool.StepNumber, "数字 8 对应序号");
        Check(AnnotationToolInfo.DisplayName(AnnotationTool.Marker) == "马克笔" &&
              AnnotationToolInfo.DisplayName(AnnotationTool.StepNumber) == "序号",
            "马克笔/序号中文名正确");

        return runner.ToResult();
    }
}
