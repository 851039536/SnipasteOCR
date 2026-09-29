using System.Drawing.Drawing2D;

namespace SnipasteOcr.Annotations;

/// <summary>
/// 一条标注。所有几何数据都存 <b>位图物理像素</b> 坐标 (与 <c>SnipOverlayForm._screen</c> 同一坐标系),
/// 而不是窗口客户区逻辑坐标 —— 这样绘制、撤销、最终合成到导出图片三处共用一套数据,
/// 不会因为 DPI 缩放而错位。
/// </summary>
public sealed class Annotation
{
    /// <summary>工具类型 (决定如何渲染)</summary>
    public required AnnotationTool Tool { get; init; }

    /// <summary>线条/描边颜色</summary>
    public Color Color { get; init; } = Color.FromArgb(255, 235, 59, 36);

    /// <summary>线宽 (物理像素)</summary>
    public float StrokeWidth { get; set; } = 2f;

    /// <summary>
    /// 形状工具 (矩形/椭圆/箭头/马赛克) 的起止点。
    /// 用两个角点而非 Rectangle, 便于箭头表达方向 (起点 = 尾, 终点 = 箭头所指)。
    /// </summary>
    public PointF Start { get; set; }

    /// <inheritdoc cref="Start"/>
    public PointF End { get; set; }

    /// <summary>
    /// 画笔轨迹点列 (仅 <see cref="AnnotationTool.Pen"/>)。
    /// </summary>
    public List<PointF> Points { get; init; } = [];

    /// <summary>文字内容 (仅 <see cref="AnnotationTool.Text"/>)</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>字号 (物理像素, 仅文字标注)</summary>
    public float FontSize { get; set; } = 18f;

    /// <summary>
    /// 形状当前归一化矩形。画笔返回轨迹包围盒 (可能为空矩形)。
    /// </summary>
    public RectangleF Bounds
    {
        get
        {
            if (Tool == AnnotationTool.Pen)
            {
                if (Points.Count == 0)
                    return RectangleF.Empty;
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var p in Points)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y > maxY) maxY = p.Y;
                }
                return RectangleF.FromLTRB(minX, minY, maxX, maxY);
            }

            if (Tool == AnnotationTool.Text)
                return TextBounds();

            return RectangleF.FromLTRB(
                Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
                Math.Max(Start.X, End.X), Math.Max(Start.Y, End.Y));
        }
    }

    /// <summary>
    /// 文字标注的包围盒。字号度量需要 GDI+, 无字体缓存时退回按字号估算,
    /// 保证 <see cref="Bounds"/> 在无 Graphics 场景 (如命中测试的快速判断) 也可用。
    /// </summary>
    private RectangleF TextBounds()
    {
        SizeF size = AnnotationEngine.MeasureText(Text, FontSize);
        return new RectangleF(Start.X, Start.Y, Math.Max(size.Width, FontSize), Math.Max(size.Height, FontSize));
    }

    /// <summary>几何外扩 <paramref name="pad"/> 像素, 用于命中测试留出手指/鼠标容差</summary>
    public RectangleF BoundsInflated(float pad)
    {
        var b = Bounds;
        if (b.IsEmpty)
            return b;
        b.Inflate(pad, pad);
        return b;
    }

    /// <summary>
    /// 该标注是否「几乎无面积」。拖拽时鼠标可能只移动 1~2px,
    /// 这类形状不写入历史 (避免撤销栈里全是垃圾)。
    /// </summary>
    public bool IsDegenerate()
    {
        if (Tool == AnnotationTool.Text)
            return string.IsNullOrWhiteSpace(Text);

        if (Tool == AnnotationTool.Pen)
            return Points.Count < 2;

        var b = Bounds;
        return b.Width < 2 && b.Height < 2;
    }

    /// <summary>命中测试: 点 (物理像素) 是否落在该标注上</summary>
    /// <param name="pt">待测点</param>
    /// <param name="tolerance">容差 (物理像素), 一般取线宽 + 几个像素</param>
    public bool HitTest(PointF pt, float tolerance)
    {
        // 先做廉价的包围盒排除
        var box = BoundsInflated(tolerance);
        if (!box.IsEmpty && !box.Contains(pt))
            return false;

        switch (Tool)
        {
            case AnnotationTool.Pen:
                return HitPolyline(pt, tolerance);

            case AnnotationTool.Rectangle:
            case AnnotationTool.Ellipse:
                // 只命中描边, 不命中内部 (便于点选叠加的下层形状)
                return HitOutline(pt, tolerance);

            case AnnotationTool.Arrow:
                return HitSegment(pt, Start, End, tolerance);

            case AnnotationTool.Mosaic:
                // 马赛克是实心块, 内部也算命中
                return Bounds.Contains(pt);

            case AnnotationTool.Text:
                return Bounds.Contains(pt);

            default:
                return false;
        }
    }

    /// <summary>画笔轨迹逐段判定</summary>
    private bool HitPolyline(PointF pt, float tol)
    {
        if (Points.Count == 1)
            return Distance(pt, Points[0]) <= tol;

        for (int i = 1; i < Points.Count; i++)
        {
            if (HitSegment(pt, Points[i - 1], Points[i], tol))
                return true;
        }
        return false;
    }

    /// <summary>矩形/椭圆描边判定: 取到形状边界线的距离</summary>
    private bool HitOutline(PointF pt, float tol)
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0)
            return false;

        // 归一化到单位圆/单位方框坐标系
        float cx = b.Left + b.Width / 2f;
        float cy = b.Top + b.Height / 2f;
        float rx = b.Width / 2f;
        float ry = b.Height / 2f;

        float nx = (pt.X - cx) / rx;
        float ny = (pt.Y - cy) / ry;
        float r = MathF.Sqrt(nx * nx + ny * ny);

        if (Tool == AnnotationTool.Rectangle)
        {
            // 矩形: 里外距离由各边到点的最小距离决定 (用归一化坐标近似即可)
            float dx = MathF.Abs(nx);
            float dy = MathF.Abs(ny);
            float edge = MathF.Min(1f - dx, 1f - dy); // 到最近边的归一化距离
            // 换算回像素: 水平方向 1 单位 = rx 像素, 垂直 = ry
            float px = MathF.Abs(edge) * MathF.Min(rx, ry);
            return px <= tol || (dx > 1f && dy > 1f && DistanceToCorner(pt, b) <= tol);
        }

        // 椭圆: 归一化半径与 1 的差 * 平均半径 ≈ 到椭圆的像素距离
        float dist = MathF.Abs(r - 1f) * MathF.Min(rx, ry);
        return dist <= tol;
    }

    /// <summary>矩形四个角附近也算命中 (边角超出包围盒的部分)</summary>
    private static float DistanceToCorner(PointF pt, RectangleF b)
    {
        float best = float.MaxValue;
        Span<PointF> corners =
        [
            new(b.Left, b.Top), new(b.Right, b.Top),
            new(b.Right, b.Bottom), new(b.Left, b.Bottom),
        ];
        foreach (var c in corners)
            best = MathF.Min(best, Distance(pt, c));
        return best;
    }

    /// <summary>点到线段距离 <= tol 判定命中</summary>
    private static bool HitSegment(PointF pt, PointF a, PointF b, float tol)
        => DistanceToSegment(pt, a, b) <= tol;

    /// <summary>两点距离</summary>
    private static float Distance(PointF a, PointF b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>点到线段的最短距离</summary>
    private static float DistanceToSegment(PointF p, PointF a, PointF b)
    {
        float vx = b.X - a.X, vy = b.Y - a.Y;
        float len2 = vx * vx + vy * vy;
        if (len2 <= float.Epsilon)
            return Distance(p, a);

        float t = ((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2;
        t = Math.Clamp(t, 0f, 1f);
        return Distance(p, new PointF(a.X + t * vx, a.Y + t * vy));
    }

    /// <summary>深拷贝 (复制标注、拖拽前的原状备份用)</summary>
    public Annotation Clone() => new()
    {
        Tool = Tool,
        Color = Color,
        StrokeWidth = StrokeWidth,
        Start = Start,
        End = End,
        Points = [.. Points],
        Text = Text,
        FontSize = FontSize,
    };

    /// <summary>整体平移 (物理像素偏移)</summary>
    public void Offset(float dx, float dy)
    {
        Start = new PointF(Start.X + dx, Start.Y + dy);
        End = new PointF(End.X + dx, End.Y + dy);
        for (int i = 0; i < Points.Count; i++)
            Points[i] = new PointF(Points[i].X + dx, Points[i].Y + dy);
    }
}
