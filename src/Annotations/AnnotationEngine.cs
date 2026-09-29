using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SnipasteOcr.Annotations;

/// <summary>
/// 标注渲染引擎: 把 <see cref="Annotation"/> 列表画到任意 <see cref="Graphics"/> 上。
///
/// 覆盖层实时预览与最终合成导出共用本类, 因此两者像素级一致 —— 预览看到什么, 导出的就是什么。
/// 所有坐标都是位图物理像素。
/// </summary>
public static class AnnotationEngine
{
    /// <summary>箭头默认箭头长度 (物理像素)</summary>
    private const float ArrowHeadLength = 14f;

    /// <summary>马赛克默认像素块大小 (物理像素); 越大越糊</summary>
    public const int DefaultMosaicBlock = 10;

    /// <summary>文字默认字号 (物理像素)</summary>
    public const float DefaultFontSize = 18f;

    /// <summary>用于文字度量的字体族 (与绘制保持一致)</summary>
    private const string FontFamilyName = "Microsoft YaHei UI";

    // ===== 文字度量 =====

    /// <summary>
    /// 度量文字尺寸 (物理像素)。使用 GDI 的 TextRenderer, 与最终绘制方式一致。
    /// </summary>
    public static SizeF MeasureText(string? text, float fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return SizeF.Empty;

        // 多行文本按 \n 拆分, 取最大行宽 + 总高度
        using var font = CreateFont(fontSize);
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        float width = 0f;
        int lineHeight = font.Height;

        foreach (string line in lines)
        {
            Size s = TextRenderer.MeasureText(line.Length == 0 ? " " : line, font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            if (s.Width > width)
                width = s.Width;
        }

        return new SizeF(width, lineHeight * lines.Length);
    }

    /// <summary>创建标注用字体 (带缓存以复用 GDI 字体句柄)</summary>
    private static Font CreateFont(float size)
    {
        // Font 本身很轻 (GDI+ 有内部缓存), 这里不再自建缓存以免管理句柄生命周期
        return new Font(FontFamilyName, size, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    // ===== 绘制入口 =====

    /// <summary>
    /// 绘制全部标注 (按顺序叠加, 后画的在上层)。
    /// </summary>
    /// <param name="g">目标 Graphics</param>
    /// <param name="annotations">标注列表</param>
    /// <param name="source">底图, 马赛克需要从中取原始像素</param>
    /// <param name="clip">可选裁剪区域 (覆盖层只绘制选区内的标注)</param>
    /// <param name="skip">可选, 绘制时跳过该标注 (用于拖拽中的形状: 已由预览层单独绘制)</param>
    public static void Draw(
        Graphics g, IReadOnlyList<Annotation> annotations, Bitmap source,
        RectangleF? clip = null, Annotation? skip = null)
    {
        if (annotations.Count == 0)
            return;

        var oldClip = g.Clip;
        var oldInterp = g.InterpolationMode;
        var oldSmoothing = g.SmoothingMode;
        var oldPixelOffset = g.PixelOffsetMode;
        var oldCompositing = g.CompositingMode;

        try
        {
            if (clip is { } c)
                g.SetClip(c, CombineMode.Intersect);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            foreach (var a in annotations)
            {
                if (ReferenceEquals(a, skip) || a.IsDegenerate())
                    continue;
                DrawOne(g, a, source);
            }
        }
        finally
        {
            g.Clip = oldClip;
            g.InterpolationMode = oldInterp;
            g.SmoothingMode = oldSmoothing;
            g.PixelOffsetMode = oldPixelOffset;
            g.CompositingMode = oldCompositing;
        }
    }

    /// <summary>按工具类型分发到具体绘制方法</summary>
    private static void DrawOne(Graphics g, Annotation a, Bitmap source)
    {
        switch (a.Tool)
        {
            case AnnotationTool.Rectangle:
                DrawRectangle(g, a);
                break;
            case AnnotationTool.Ellipse:
                DrawEllipse(g, a);
                break;
            case AnnotationTool.Arrow:
                DrawArrow(g, a);
                break;
            case AnnotationTool.Pen:
                DrawPen(g, a);
                break;
            case AnnotationTool.Mosaic:
                DrawMosaic(g, a, source);
                break;
            case AnnotationTool.Text:
                DrawText(g, a);
                break;
        }
    }

    // ===== 各工具绘制 =====

    /// <summary>矩形: 描边 (带白色外衬以适配深浅背景)</summary>
    private static void DrawRectangle(Graphics g, Annotation a)
    {
        var r = Normalize(a);
        if (r.Width < 1 || r.Height < 1)
            return;

        DrawWithContrast(g, a, (g2, pen) => g2.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height));
    }

    /// <summary>椭圆</summary>
    private static void DrawEllipse(Graphics g, Annotation a)
    {
        var r = Normalize(a);
        if (r.Width < 1 || r.Height < 1)
            return;

        DrawWithContrast(g, a, (g2, pen) => g2.DrawEllipse(pen, r.X, r.Y, r.Width, r.Height));
    }

    /// <summary>
    /// 带白色外衬的描边绘制: 先用较粗的半透明白色描一遍, 再画本色。
    /// 这样在浅色或深色截图上都能看清线条边界。
    /// </summary>
    private static void DrawWithContrast(Graphics g, Annotation a, Action<Graphics, Pen> draw)
    {
        using (var halo = new Pen(Color.FromArgb(150, 255, 255, 255), a.StrokeWidth + 2f))
        {
            halo.LineJoin = LineJoin.Round;
            draw(g, halo);
        }

        using var pen = new Pen(a.Color, a.StrokeWidth) { LineJoin = LineJoin.Round };
        draw(g, pen);
    }

    /// <summary>箭头: 主线 + 实心三角箭头 (带白色外衬)</summary>
    private static void DrawArrow(Graphics g, Annotation a)
    {
        float dx = a.End.X - a.Start.X;
        float dy = a.End.Y - a.Start.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f)
            return;

        // 箭头尺寸随线宽略增, 但不小于默认值
        float headLen = MathF.Max(ArrowHeadLength, a.StrokeWidth * 4.5f);
        headLen = MathF.Min(headLen, len * 0.5f); // 极短箭头时不至于整条线都是头
        float headWidth = headLen * 0.62f;

        float ux = dx / len, uy = dy / len;      // 单位方向
        float px = -uy, py = ux;                 // 法线

        // 线段终点退到箭头底部, 避免线尖从三角里戳出来
        var basePt = new PointF(a.End.X - ux * headLen, a.End.Y - uy * headLen);

        // 先画白色外衬 (线 + 略大的三角), 再画本色
        using (var haloPen = new Pen(Color.FromArgb(150, 255, 255, 255), a.StrokeWidth + 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        })
        using (var haloBrush = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
        {
            DrawArrowBody(g, a.Start, basePt, a.End, px, py, headWidth, haloPen, haloBrush);
        }

        using (var pen = new Pen(a.Color, a.StrokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        })
        using (var brush = new SolidBrush(a.Color))
        {
            DrawArrowBody(g, a.Start, basePt, a.End, px, py, headWidth, pen, brush);
        }
    }

    /// <summary>箭头线体 + 三角头 (抽出以便白色外衬与本色复用)</summary>
    /// <param name="px">箭头方向法线的 X 分量 (单位向量)</param>
    /// <param name="py">箭头方向法线的 Y 分量 (单位向量)</param>
    private static void DrawArrowBody(
        Graphics g, PointF start, PointF basePt, PointF tip,
        float px, float py, float headWidth,
        Pen pen, Brush brush)
    {
        // 线段只画到箭头底部, 避免线尖从三角里戳出来
        g.DrawLine(pen, start, basePt);

        var tri = new[]
        {
            tip,
            new PointF(basePt.X + px * headWidth / 2f, basePt.Y + py * headWidth / 2f),
            new PointF(basePt.X - px * headWidth / 2f, basePt.Y - py * headWidth / 2f),
        };

        g.FillPolygon(brush, tri);
    }

    /// <summary>画笔: 平滑折线 (带白色外衬)</summary>
    private static void DrawPen(Graphics g, Annotation a)
    {
        if (a.Points.Count < 2)
            return;

        var pts = a.Points.ToArray();

        using (var halo = new Pen(Color.FromArgb(120, 255, 255, 255), a.StrokeWidth + 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        })
            g.DrawLines(halo, pts);

        using var pen = new Pen(a.Color, a.StrokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };

        // 点数很多时直接连折线, 抗锯齿已足够平滑
        g.DrawLines(pen, pts);
    }

    /// <summary>
    /// 马赛克: 对选区内的底图像素做块平均后再放大绘制, 形成像素化效果。
    /// 直接从底图取样, 因此不会把下方的其它标注也糊进去 (与预览一致)。
    /// </summary>
    private static void DrawMosaic(Graphics g, Annotation a, Bitmap source)
    {
        var r = Normalize(a);
        if (r.Width < 2 || r.Height < 2)
            return;

        var dest = Rectangle.Round(r);
        dest = Rectangle.Intersect(dest, new Rectangle(0, 0, source.Width, source.Height));
        if (dest.Width < 2 || dest.Height < 2)
            return;

        using var block = CreateMosaicBlock(source, dest, DefaultMosaicBlock);
        if (block is null)
            return;

        // 目标区域用最近邻放大 → 硬边像素块
        var oldInterp = g.InterpolationMode;
        var oldOffset = g.PixelOffsetMode;
        try
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(block, dest, new Rectangle(0, 0, block.Width, block.Height), GraphicsUnit.Pixel);
        }
        finally
        {
            g.InterpolationMode = oldInterp;
            g.PixelOffsetMode = oldOffset;
        }
    }

    /// <summary>
    /// 生成马赛克小图: 把源区域按 <paramref name="blockSize"/> 分块取平均色, 写进一张小位图。
    /// 之后放大绘制即得到马赛克。返回 null 表示区域无效。
    /// </summary>
    private static Bitmap? CreateMosaicBlock(Bitmap source, Rectangle area, int blockSize)
    {
        if (blockSize < 2)
            blockSize = 2;

        int cols = Math.Max(1, area.Width / blockSize);
        int rows = Math.Max(1, area.Height / blockSize);

        var small = new Bitmap(cols, rows, PixelFormat.Format32bppArgb);

        // 直接按块采样源图。为速度起见用 LockBits 一次性取源区域像素。
        BitmapData? srcData = null;
        try
        {
            srcData = source.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            int stride = srcData.Stride;
            int bytes = Math.Abs(stride) * area.Height;
            byte[] buffer = new byte[bytes];
            Marshal.Copy(srcData.Scan0, buffer, 0, bytes);

            var smallData = small.LockBits(
                new Rectangle(0, 0, cols, rows), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int sStride = smallData.Stride;
                byte[] outBuf = new byte[Math.Abs(sStride) * rows];

                for (int by = 0; by < rows; by++)
                {
                    int y0 = by * blockSize;
                    int y1 = Math.Min(y0 + blockSize, area.Height);

                    for (int bx = 0; bx < cols; bx++)
                    {
                        int x0 = bx * blockSize;
                        int x1 = Math.Min(x0 + blockSize, area.Width);

                        long sb = 0, sg = 0, sr = 0;
                        int n = 0;
                        for (int y = y0; y < y1; y++)
                        {
                            int rowOff = y * stride;
                            for (int x = x0; x < x1; x++)
                            {
                                int off = rowOff + x * 4;
                                sb += buffer[off];
                                sg += buffer[off + 1];
                                sr += buffer[off + 2];
                                n++;
                            }
                        }

                        if (n == 0) n = 1;
                        int o = by * sStride + bx * 4;
                        outBuf[o] = (byte)(sb / n);
                        outBuf[o + 1] = (byte)(sg / n);
                        outBuf[o + 2] = (byte)(sr / n);
                        outBuf[o + 3] = 255;
                    }
                }

                Marshal.Copy(outBuf, 0, smallData.Scan0, outBuf.Length);
            }
            finally
            {
                small.UnlockBits(smallData);
            }
        }
        catch
        {
            small.Dispose();
            return null;
        }
        finally
        {
            if (srcData is not null)
                source.UnlockBits(srcData);
        }

        return small;
    }

    /// <summary>文字: 带描边以保证任意背景上可读 (先画白色/深色轮廓再填色)</summary>
    private static void DrawText(Graphics g, Annotation a)
    {
        if (string.IsNullOrEmpty(a.Text))
            return;

        using var font = CreateFont(a.FontSize);
        var pt = new PointF(a.Start.X, a.Start.Y);
        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        // TextRenderer 不支持描边, 用 8 方向偏移绘制深色来伪造描边
        Color outline = GetOutlineColor(a.Color);
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0)
                    continue;
                TextRenderer.DrawText(g, a.Text, font,
                    new Point((int)pt.X + dx, (int)pt.Y + dy), outline, flags);
            }
        }

        TextRenderer.DrawText(g, a.Text, font, new Point((int)pt.X, (int)pt.Y), a.Color, flags);
    }

    /// <summary>按颜色亮度选择描边色 (深色文字配白边, 浅色文字配黑边)</summary>
    private static Color GetOutlineColor(Color fill)
    {
        int lum = (fill.R * 299 + fill.G * 587 + fill.B * 114) / 1000;
        return lum > 140 ? Color.FromArgb(200, 0, 0, 0) : Color.FromArgb(200, 255, 255, 255);
    }

    /// <summary>把起止点归一化为正矩形</summary>
    private static RectangleF Normalize(Annotation a) => RectangleF.FromLTRB(
        Math.Min(a.Start.X, a.End.X), Math.Min(a.Start.Y, a.End.Y),
        Math.Max(a.Start.X, a.End.X), Math.Max(a.Start.Y, a.End.Y));
}
