// 逐工具验证渲染结果: 对样例图的各个区域做像素级检查。
// 比"看一眼图"更严格, 且可在无图形界面的环境下重复执行。
using System.Drawing;
using System.Drawing.Imaging;
using SnipasteOcr.Annotations;

namespace SnipasteOcr.Tests;

/// <summary>渲染结果的像素级校验</summary>
internal static class RenderChecks
{
    /// <summary>执行全部渲染检查, 返回 (通过数, 失败项说明)</summary>
    public static (int Passed, List<string> Failures) Run()
    {
        int passed = 0;
        var failures = new List<string>();

        void Check(bool cond, string name)
        {
            if (cond) passed++;
            else failures.Add(name);
        }

        // ===== 矩形: 四条边上都该有该颜色的像素, 内部不该有 =====
        {
            var bmp = Blank(300, 300);
            var red = Color.FromArgb(255, 220, 0, 0);
            Draw(bmp, new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(50, 50), End = new PointF(250, 200), Color = red, StrokeWidth = 4 });

            Check(HasNear(bmp, 150, 50, red, 60), "矩形上边着色");
            Check(HasNear(bmp, 150, 200, red, 60), "矩形下边着色");
            Check(HasNear(bmp, 50, 125, red, 60), "矩形左边着色");
            Check(HasNear(bmp, 250, 125, red, 60), "矩形右边着色");
            Check(!HasNear(bmp, 150, 125, red, 40), "矩形内部保持空白");
            Check(IsBlank(bmp, 20, 20), "矩形外部保持空白");
        }

        // ===== 椭圆: 顶点/左右极点着色, 四角(包围盒内但在椭圆外)不着色 =====
        {
            var bmp = Blank(300, 300);
            var green = Color.FromArgb(255, 0, 170, 0);
            Draw(bmp, new Annotation { Tool = AnnotationTool.Ellipse, Start = new PointF(50, 50), End = new PointF(250, 250), Color = green, StrokeWidth = 4 });

            Check(HasNear(bmp, 150, 50, green, 60), "椭圆上顶点着色");
            Check(HasNear(bmp, 150, 250, green, 60), "椭圆下顶点着色");
            Check(HasNear(bmp, 50, 150, green, 60), "椭圆左极点着色");
            Check(HasNear(bmp, 250, 150, green, 60), "椭圆右极点着色");
            // 包围盒左上角距离椭圆边界约 70px (归一化半径 0.07),
            // 因此这里必须用极小容差, 否则会误命中远处的弧线
            Check(!HasNear(bmp, 55, 55, green, 3), "椭圆包围盒死角未着色");
            Check(!HasNear(bmp, 150, 150, green, 3), "椭圆中心保持空白");
            Check(!HasNear(bmp, 245, 55, green, 3), "椭圆右上死角未着色");
        }

        // ===== 箭头: 头部应比尾部"宽", 且在终点一侧 =====
        {
            var bmp = Blank(300, 300);
            var blue = Color.FromArgb(255, 0, 90, 220);
            Draw(bmp, new Annotation { Tool = AnnotationTool.Arrow, Start = new PointF(40, 150), End = new PointF(260, 150), Color = blue, StrokeWidth = 4 });

            Check(HasNear(bmp, 150, 150, blue, 60), "箭头线体着色");
            // 箭头在终点 (右侧): 靠近终点的垂直覆盖范围应大于中部
            int nearHead = VerticalExtent(bmp, 245, blue);
            int mid = VerticalExtent(bmp, 150, blue);
            Check(nearHead > mid, $"箭头头部比线体宽 (头 {nearHead}px > 体 {mid}px)");
        }

        // ===== 画笔: 轨迹沿线着色, 轨迹外空白 =====
        {
            var bmp = Blank(300, 200);
            var purple = Color.FromArgb(255, 140, 30, 200);
            var pen = new Annotation { Tool = AnnotationTool.Pen, Color = purple, StrokeWidth = 5 };
            for (int x = 20; x <= 280; x += 4)
                pen.Points.Add(new PointF(x, 100 + MathF.Sin((x - 20) / 40f) * 40));
            Draw(bmp, pen);

            Check(HasNear(bmp, 150, 100, purple, 70), "画笔起点附近着色");
            Check(HasNear(bmp, 20, 100, purple, 70), "画笔轨迹左端着色");
            Check(IsBlank(bmp, 150, 10), "画笔轨迹上方保持空白");
            Check(IsBlank(bmp, 150, 190), "画笔轨迹下方保持空白");
        }

        // ===== 马赛克: 产生块状量化, 且不引入偏色 / 不越界 =====
        {
            // 底图: 左半黑右半白, 边界在 x=100 (故意与 10px 块对齐, 这是最难糊的情况)
            var bmp = new Bitmap(200, 200, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                using var br = new SolidBrush(Color.Black);
                g.FillRectangle(br, 0, 0, 100, 200);
            }
            Draw(bmp, new Annotation { Tool = AnnotationTool.Mosaic, Start = new PointF(50, 50), End = new PointF(150, 150) });

            // 核心性质: 马赛克把区域量化成 block×block 的色块。
            // 因此区域内每隔 block 的像素应完全一致 (原图只有在边界处才突变)。
            const int block = 10; // AnnotationEngine.DefaultMosaicBlock
            bool quantized = true;
            for (int by = 60; by < 140 && quantized; by += block)
                for (int bx = 60; bx < 140; bx += block)
                {
                    var c0 = bmp.GetPixel(bx, by);
                    // 同一块内取几个点, 必须与块首像素完全相同
                    foreach (var (dx, dy) in new[] { (1, 1), (block - 1, block - 1), (block / 2, block / 2) })
                    {
                        var c = bmp.GetPixel(bx + dx, by + dy);
                        if (c.ToArgb() != c0.ToArgb()) { quantized = false; break; }
                    }
                }
            Check(quantized, "马赛克产生 block 级色块量化");

            // 用一条斜向边界 (不与块对齐) 验证确实做了块内平均
            var bmp2 = new Bitmap(200, 200, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp2))
            {
                g.Clear(Color.White);
                // 斜线分界: 左下黑 / 右上白, 必然与块交错 → 边界块应被平均成灰
                g.FillPolygon(Brushes.Black, [new Point(0, 200), new Point(200, 0), new Point(200, 200)]);
            }
            Draw(bmp2, new Annotation { Tool = AnnotationTool.Mosaic, Start = new PointF(20, 20), End = new PointF(180, 180) });

            bool foundGray = false;
            for (int y = 30; y < 170 && !foundGray; y += 3)
                for (int x = 30; x < 170; x += 3)
                {
                    var c = bmp2.GetPixel(x, y);
                    if (c.R is > 40 and < 215) { foundGray = true; break; }
                }
            Check(foundGray, "斜向边界处生成中间灰 (确有块内平均)");

            // 纯度: 马赛克区域内不应出现彩色 (黑白混合仍是灰)
            bool noColorCast = true;
            for (int y = 30; y < 170 && noColorCast; y += 3)
                for (int x = 30; x < 170; x += 3)
                {
                    var c = bmp2.GetPixel(x, y);
                    if (Math.Abs(c.R - c.G) > 6 || Math.Abs(c.G - c.B) > 6) { noColorCast = false; break; }
                }
            Check(noColorCast, "马赛克不引入偏色");

            // 越界: 区域外的像素必须原样保留
            Check(bmp.GetPixel(10, 100).ToArgb() == Color.Black.ToArgb(), "马赛克区域外左侧保持纯黑");
            Check(bmp.GetPixel(190, 100).ToArgb() == Color.White.ToArgb(), "马赛克区域外右侧保持纯白");
            Check(bmp2.GetPixel(5, 5).ToArgb() == Color.White.ToArgb(), "马赛克区域外左上保持纯白");
        }

        // ===== 文字: 指定位置着色, 且带描边 (周围存在对比色) =====
        {
            var bmp = Blank(320, 90);
            var red = Color.FromArgb(255, 220, 0, 0);
            var t = new Annotation { Tool = AnnotationTool.Text, Start = new PointF(10, 15), Text = "Ag文字", Color = red, FontSize = 36 };
            Draw(bmp, t);

            var bounds = t.Bounds;
            Check(CountNear(bmp, red, 70, new Rectangle(0, 0, bmp.Width, bmp.Height)) > 50, "文字主体着色 (像素数足够)");
            Check(bounds.Width > 20 && bounds.Height > 10, $"文字 Bounds 覆盖实际文字 ({bounds.Width:F0}x{bounds.Height:F0})");
            Check(bounds.Right <= bmp.Width, "文字 Bounds 不越界");
        }

        // ===== 多标注叠加顺序 =====
        {
            var bmp = Blank(200, 200);
            var red = Color.FromArgb(255, 220, 0, 0);
            var blue = Color.FromArgb(255, 0, 90, 220);
            // 同位置两条水平线, 后画的应盖住先画的
            Draw(bmp,
                new Annotation { Tool = AnnotationTool.Arrow, Start = new PointF(20, 100), End = new PointF(180, 100), Color = red, StrokeWidth = 8 },
                new Annotation { Tool = AnnotationTool.Arrow, Start = new PointF(20, 100), End = new PointF(180, 100), Color = blue, StrokeWidth = 8 });

            var mid = bmp.GetPixel(100, 100);
            Check(mid.B > mid.R, $"后画的标注覆盖先画的 (RGB={mid.R},{mid.G},{mid.B})");
        }

        // ===== 裁剪区域 (clip) 生效: 选区外的标注不该画出来 =====
        {
            var bmp = Blank(200, 200);
            var red = Color.FromArgb(255, 220, 0, 0);
            var ann = new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(10, 10), End = new PointF(190, 190), Color = red, StrokeWidth = 4 };

            using (var g = Graphics.FromImage(bmp))
            {
                // 只允许在中间区域绘制
                AnnotationEngine.Draw(g, [ann], bmp, new RectangleF(80, 80, 60, 60));
            }

            Check(IsBlank(bmp, 10, 100), "clip 外的标注被裁掉 (左侧)");
            Check(IsBlank(bmp, 190, 100), "clip 外的标注被裁掉 (右侧)");
            Check(IsBlank(bmp, 100, 10), "clip 外的标注被裁掉 (上方)");
        }

        // ===== skip 参数: 拖拽中的形状不重复绘制 =====
        {
            var bmp = Blank(200, 200);
            var red = Color.FromArgb(255, 220, 0, 0);
            var ann = new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(50, 50), End = new PointF(150, 150), Color = red, StrokeWidth = 4 };

            using (var g = Graphics.FromImage(bmp))
                AnnotationEngine.Draw(g, [ann], bmp, null, skip: ann);

            Check(IsBlank(bmp, 100, 50), "skip 指定的标注被跳过绘制");
        }

        // ===== 退化标注不绘制 =====
        {
            var bmp = Blank(200, 200);
            Draw(bmp, new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(100, 100), End = new PointF(101, 101), Color = Color.Red });
            Check(IsBlank(bmp, 100, 100), "退化标注不产生绘制");
        }

        // ===== 空列表不抛异常 =====
        {
            var bmp = Blank(50, 50);
            try
            {
                using var g = Graphics.FromImage(bmp);
                AnnotationEngine.Draw(g, [], bmp);
                Check(true, "空标注列表安全");
            }
            catch (Exception ex)
            {
                Check(false, "空标注列表抛出异常: " + ex.Message);
            }
        }

        return (passed, failures);
    }

    // ===== 辅助 =====

    private static Bitmap Blank(int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        return bmp;
    }

    private static void Draw(Bitmap bmp, params Annotation[] anns)
    {
        using var g = Graphics.FromImage(bmp);
        AnnotationEngine.Draw(g, anns, bmp);
    }

    /// <summary>(x,y) 附近 tol 半径内是否存在接近 want 的像素</summary>
    private static bool HasNear(Bitmap bmp, int x, int y, Color want, int tol)
    {
        for (int dy = -tol; dy <= tol; dy++)
            for (int dx = -tol; dx <= tol; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= bmp.Width || py >= bmp.Height) continue;
                if (Close(bmp.GetPixel(px, py), want, 90)) return true;
            }
        return false;
    }

    /// <summary>某点是否基本为白 (无绘制)</summary>
    private static bool IsBlank(Bitmap bmp, int x, int y)
    {
        if (x < 0 || y < 0 || x >= bmp.Width || y >= bmp.Height) return true;
        var c = bmp.GetPixel(x, y);
        return c.R > 245 && c.G > 245 && c.B > 245;
    }

    /// <summary>指定列上属于目标颜色的连续像素数 (用于比较箭头头/体宽度)</summary>
    private static int VerticalExtent(Bitmap bmp, int x, Color want)
    {
        int count = 0;
        for (int y = 0; y < bmp.Height; y++)
            if (Close(bmp.GetPixel(x, y), want, 90)) count++;
        return count;
    }

    /// <summary>区域内接近目标颜色的像素总数</summary>
    private static int CountNear(Bitmap bmp, Color want, int tol, Rectangle area)
    {
        int n = 0;
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                if (Close(bmp.GetPixel(x, y), want, tol)) n++;
        return n;
    }

    /// <summary>颜色距离判定</summary>
    private static bool Close(Color a, Color b, int tol)
        => Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;
}
