using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SnipasteOcr.Annotations;

namespace SnipasteOcr.Tests;

/// <summary>
/// 渲染一张可视化样例图, 用于人工确认六种标注的实际观感。
/// 像素级断言无法证明"画得好不好看", 因此保留这个可选的目视检查入口。
/// </summary>
internal static class RenderSample
{
    /// <summary>生成样例图并返回其路径</summary>
    public static string Render()
    {
        // 造一张有内容的底图 (模拟截图: 白底 + 文字 + 彩色块)
        var src = new Bitmap(900, 520, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(src))
        {
            g.Clear(Color.FromArgb(255, 250, 250, 248));
            using var titleFont = new Font("Microsoft YaHei UI", 20f, FontStyle.Bold);
            g.DrawString("标注引擎可视化验证", titleFont, Brushes.Black, 30, 24);

            using var bodyFont = new Font("Microsoft YaHei UI", 13f);
            g.DrawString("六个区域分别演示: 矩形 / 椭圆 / 箭头 / 画笔 / 马赛克 / 文字", bodyFont, Brushes.DimGray, 30, 70);

            // 马赛克目标: 一段"敏感信息"
            using var mono = new Font("Consolas", 15f);
            g.DrawString("password: hunter2", mono, Brushes.Black, 560, 150);

            // 彩色渐变块, 方便观察马赛克的分块效果
            var grad = new Rectangle(560, 190, 280, 90);
            using var lg = new LinearGradientBrush(grad, Color.OrangeRed, Color.RoyalBlue, 30f);
            g.FillRectangle(lg, grad);

            // 点网格, 用于检验画笔的平滑度
            using var gridPen = new Pen(Color.FromArgb(40, 0, 0, 0), 1f);
            for (int x = 0; x < 900; x += 30) g.DrawLine(gridPen, x, 0, x, 520);
            for (int y = 0; y < 520; y += 30) g.DrawLine(gridPen, 0, y, 900, y);
        }

        var anns = new List<Annotation>
        {
            // 1. 矩形
            new() { Tool = AnnotationTool.Rectangle, Start = new PointF(40, 120), End = new PointF(230, 230), Color = Color.FromArgb(255, 235, 59, 36), StrokeWidth = 3 },
            // 2. 椭圆
            new() { Tool = AnnotationTool.Ellipse, Start = new PointF(270, 120), End = new PointF(460, 230), Color = Color.FromArgb(255, 76, 175, 80), StrokeWidth = 3 },
            // 3. 箭头 (双向, 检验方向)
            new() { Tool = AnnotationTool.Arrow, Start = new PointF(40, 280), End = new PointF(230, 380), Color = Color.FromArgb(255, 33, 150, 243), StrokeWidth = 4 },
            new() { Tool = AnnotationTool.Arrow, Start = new PointF(230, 280), End = new PointF(40, 380), Color = Color.FromArgb(255, 255, 152, 0), StrokeWidth = 4 },
            // 4. 马赛克 (盖住密码与渐变块)
            new() { Tool = AnnotationTool.Mosaic, Start = new PointF(550, 140), End = new PointF(850, 290) },
        };

        // 5. 画笔: 一段正弦波浪 (点密集, 检验折线平滑)
        var penAnn = new Annotation { Tool = AnnotationTool.Pen, Color = Color.FromArgb(255, 156, 39, 176), StrokeWidth = 4 };
        for (int i = 0; i <= 120; i++)
        {
            float t = i / 120f;
            penAnn.Points.Add(new PointF(280 + t * 220, 340 + MathF.Sin(t * MathF.PI * 4f) * 38));
        }
        anns.Add(penAnn);

        // 6. 文字 (深浅两种颜色, 检验描边自适应)
        anns.Add(new Annotation { Tool = AnnotationTool.Text, Start = new PointF(40, 430), Text = "文字标注: 支持中文与 English 123", Color = Color.FromArgb(255, 235, 59, 36), FontSize = 24 });
        anns.Add(new Annotation { Tool = AnnotationTool.Text, Start = new PointF(40, 470), Text = "第二行 · 不同颜色", Color = Color.FromArgb(255, 33, 150, 243), FontSize = 20 });

        using (var g = Graphics.FromImage(src))
            AnnotationEngine.Draw(g, anns, src);

        string outPath = Path.Combine(AppContext.BaseDirectory, "sample.png");
        src.Save(outPath, ImageFormat.Png);
        return outPath;
    }
}
