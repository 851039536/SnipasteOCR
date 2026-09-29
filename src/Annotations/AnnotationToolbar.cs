using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace SnipasteOcr.Annotations;

/// <summary>
/// 标注工具栏: 框选完成后浮现在选区下方, 提供工具切换、颜色、线宽、撤销/重做与确认/取消。
///
/// 自绘而非用 <see cref="ToolStrip"/>, 原因:
/// 1. 覆盖层无边框且需要跟随选区移动, ToolStrip 的默认主题外观不协调;
/// 2. 需要精确控制尺寸来做自动翻转 (贴着屏幕底部时工具栏移到选区上方)。
/// </summary>
public sealed class AnnotationToolbar : Control
{
    // ===== 布局常量 (逻辑像素) =====
    private const int ButtonSize = 34;
    private const int IconPad = 8;
    private const int Gap = 4;
    private const int SepWidth = 9;
    private const int ToolbarHeight = 42;
    private const int PadLeft = 8;

    /// <summary>可选颜色 (与截图工具常见配色一致)</summary>
    private static readonly Color[] Palette =
    [
        Color.FromArgb(255, 235, 59, 36),    // 红
        Color.FromArgb(255, 255, 152, 0),    // 橙
        Color.FromArgb(255, 255, 214, 0),    // 黄
        Color.FromArgb(255, 76, 175, 80),    // 绿
        Color.FromArgb(255, 33, 150, 243),   // 蓝
        Color.FromArgb(255, 156, 39, 176),   // 紫
        Color.FromArgb(255, 255, 255, 255),  // 白
        Color.FromArgb(255, 24, 24, 24),     // 黑
    ];

    /// <summary>可选线宽</summary>
    private static readonly float[] Widths = [2f, 4f, 7f];

    /// <summary>当前选中的工具</summary>
    public AnnotationTool Tool { get; private set; } = AnnotationTool.None;

    /// <summary>当前颜色</summary>
    public Color CurrentColor { get; private set; } = Palette[0];

    /// <summary>当前线宽</summary>
    public float CurrentWidth { get; private set; } = Widths[0];

    /// <summary>是否可撤销 (影响按钮可用态)</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public bool CanUndo
    {
        get => _canUndo;
        set { if (_canUndo != value) { _canUndo = value; Invalidate(); } }
    }
    private bool _canUndo;

    /// <summary>是否可重做</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public bool CanRedo
    {
        get => _canRedo;
        set { if (_canRedo != value) { _canRedo = value; Invalidate(); } }
    }
    private bool _canRedo;

    // ===== 事件 =====
    /// <summary>工具或样式发生变化 (宿主据此更新光标与当前画笔)</summary>
    public event Action? ToolChanged;

    /// <summary>点击撤销</summary>
    public event Action? UndoRequested;

    /// <summary>点击重做</summary>
    public event Action? RedoRequested;

    /// <summary>点击确认 (复制/识别)</summary>
    public event Action? ConfirmRequested;

    /// <summary>点击取消</summary>
    public event Action? CancelRequested;

    /// <summary>点击保存到文件</summary>
    public event Action? SaveRequested;

    // ===== 命中区域 (在 OnPaint 中重建, 供鼠标命中) =====
    private readonly List<(Rectangle Rect, AnnotationTool Tool)> _toolRects = [];
    private readonly List<(Rectangle Rect, Color Color)> _colorRects = [];
    private readonly List<(Rectangle Rect, float Width)> _widthRects = [];
    private Rectangle _undoRect;
    private Rectangle _redoRect;
    private Rectangle _saveRect;
    private Rectangle _confirmRect;
    private Rectangle _cancelRect;

    private int _hoverTool = -1;
    private int _hoverColor = -1;
    private int _hoverWidth = -1;
    private string _hoverActionKey = string.Empty;

    /// <summary>构造工具栏</summary>
    public AnnotationToolbar()
    {
        // SupportsTransparentBackColor: 本控件自绘圆角背景, 需要半透明底色,
        // 未开该样式时给 BackColor 赋带 alpha 的颜色会抛 ArgumentException。
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.FromArgb(240, 38, 40, 44);
        Size = new Size(MeasurePreferredWidth(), ToolbarHeight);
        Cursor = Cursors.Hand;
    }

    /// <summary>工具栏首选宽度 (由各按钮加总得出)</summary>
    private static int MeasurePreferredWidth()
    {
        int w = PadLeft;
        w += ButtonSize * AnnotationToolInfo.AllTools.Length;   // 工具按钮
        w += Gap * (AnnotationToolInfo.AllTools.Length - 1);
        w += SepWidth;                                          // 分隔
        w += ButtonSize + 26;                                   // 颜色 (一个色块 + 展开提示)
        w += SepWidth;
        w += (int)(ButtonSize * 0.7f) * Widths.Length + Gap * (Widths.Length - 1); // 线宽
        w += SepWidth;
        w += ButtonSize * 2 + Gap;                              // 撤销/重做
        w += SepWidth;
        w += 58 + Gap;                                          // 保存
        w += 64 + Gap;                                          // 确认
        w += 64;                                                // 取消
        w += PadLeft;
        return w;
    }

    /// <summary>
    /// 绘制工具栏: 深色圆角底 + 工具图标 + 颜色/线宽选择 + 操作按钮。
    /// 命中区域在绘制过程中重建, 保证与视觉完全一致。
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 背景 + 圆角 (同一个路径对象同时用于填充与描边, 避免重复构造)
        using (var path = RoundedRect(ClientRectangle, 6))
        {
            using (var bg = new SolidBrush(BackColor))
                g.FillPath(bg, path);
            using var border = new Pen(Color.FromArgb(70, 255, 255, 255), 1f);
            g.DrawPath(border, path);
        }

        _toolRects.Clear();
        _colorRects.Clear();
        _widthRects.Clear();

        int x = PadLeft;
        int cy = Height / 2;

        // ---- 工具按钮 ----
        for (int i = 0; i < AnnotationToolInfo.AllTools.Length; i++)
        {
            var tool = AnnotationToolInfo.AllTools[i];
            var rect = new Rectangle(x, (Height - ButtonSize) / 2, ButtonSize, ButtonSize);
            _toolRects.Add((rect, tool));

            bool active = Tool == tool;
            bool hover = _hoverTool == i;

            DrawButtonBackdrop(g, rect, active, hover);

            DrawToolIcon(g, tool, rect, active ? Color.White : Color.FromArgb(230, 230, 230));

            x += ButtonSize + Gap;
        }

        x += SepWidth - Gap;
        DrawSeparator(g, x - SepWidth / 2 - 1, cy);
        _ = x;

        // ---- 当前颜色 (点击循环切换) ----
        {
            int swatch = 26;
            var rect = new Rectangle(x, (Height - ButtonSize) / 2, swatch, ButtonSize);
            _colorRects.Add((rect, CurrentColor));

            // 色块
            var sr = new Rectangle(rect.X, cy - swatch / 2, swatch, swatch);
            using (var cb = new SolidBrush(CurrentColor))
                g.FillRectangle(cb, sr);
            using (var cb = new Pen(Color.FromArgb(180, 255, 255, 255), 1.5f))
                g.DrawRectangle(cb, sr);
            if (_hoverColor >= 0)
            {
                using var hb = new SolidBrush(Color.FromArgb(50, 255, 255, 255));
                using var hp = RoundedRect(new Rectangle(rect.X - 2, rect.Y, rect.Width + 4, rect.Height), 5);
                g.FillPath(hb, hp);
            }

            // 下拉小三角提示「可切换」
            var tri = new[]
            {
                new Point(sr.Right + 4, cy - 3),
                new Point(sr.Right + 12, cy - 3),
                new Point(sr.Right + 8, cy + 3),
            };
            using var tb = new SolidBrush(Color.FromArgb(200, 230, 230, 230));
            g.FillPolygon(tb, tri);

            x += 26 + 16;
        }

        x += SepWidth;
        DrawSeparator(g, x - SepWidth / 2 - 1, cy);

        // ---- 线宽 ----
        for (int i = 0; i < Widths.Length; i++)
        {
            int bw = (int)(ButtonSize * 0.7f);
            var rect = new Rectangle(x, (Height - bw) / 2, bw, bw);
            _widthRects.Add((rect, Widths[i]));

            bool active = Math.Abs(CurrentWidth - Widths[i]) < 0.01f;
            DrawButtonBackdrop(g, rect, active, _hoverWidth == i);

            using (var pen = new Pen(active ? Color.White : Color.FromArgb(225, 225, 225), Widths[i]))
                g.DrawLine(pen, rect.Left + 6, cy, rect.Right - 6, cy);

            x += bw + Gap;
        }

        x += SepWidth - Gap;
        DrawSeparator(g, x - SepWidth / 2 - 1, cy);

        // ---- 撤销 / 重做 ----
        _undoRect = new Rectangle(x, (Height - ButtonSize) / 2, ButtonSize, ButtonSize);
        DrawIconButton(g, _undoRect, ToolbarIcon.Undo, CanUndo, _hoverActionKey == "undo");
        x += ButtonSize + Gap;

        _redoRect = new Rectangle(x, (Height - ButtonSize) / 2, ButtonSize, ButtonSize);
        DrawIconButton(g, _redoRect, ToolbarIcon.Redo, CanRedo, _hoverActionKey == "redo");
        x += ButtonSize + Gap;

        x += SepWidth - Gap;
        DrawSeparator(g, x - SepWidth / 2 - 1, cy);

        // ---- 保存 / 确认 / 取消 ----
        _saveRect = new Rectangle(x, (Height - 30) / 2, 58, 30);
        DrawTextButton(g, _saveRect, "保存", Color.FromArgb(70, 72, 78), _hoverActionKey == "save");
        x += 58 + Gap;

        _confirmRect = new Rectangle(x, (Height - 30) / 2, 64, 30);
        DrawTextButton(g, _confirmRect, "确认", Color.FromArgb(33, 150, 243), _hoverActionKey == "confirm");
        x += 64 + Gap;

        _cancelRect = new Rectangle(x, (Height - 30) / 2, 64, 30);
        DrawTextButton(g, _cancelRect, "取消", Color.FromArgb(70, 72, 78), _hoverActionKey == "cancel");
    }

    /// <summary>工具栏上用矢量绘制的图标类型</summary>
    private enum ToolbarIcon
    {
        Undo,
        Redo,
    }

    /// <summary>
    /// 绘制图标按钮。
    ///
    /// 图标<b>全部矢量绘制, 不使用字体</b>: 原先用 "<c>↶</c>"/"<c>↷</c>"(U+21B6/U+21B7) 配合
    /// "Segoe UI Symbol", 但实测该字体在本机不存在(回退到 Microsoft Sans Serif), 且
    /// 这两个码位在本机所有字体中都<b>没有字形</b> —— 渲染结果为 0 像素, 按钮一片空白。
    /// 改为矢量绘制后既不受字体缺失影响, 任意 DPI 下也保持清晰。
    /// </summary>
    private static void DrawIconButton(Graphics g, Rectangle rect, ToolbarIcon icon, bool enabled, bool hover)
    {
        // 图标按钮没有"选中"态, 只有可用时的高亮
        DrawButtonBackdrop(g, rect, active: false, hover: enabled && hover);

        Color fg = enabled ? Color.FromArgb(235, 235, 235) : Color.FromArgb(100, 100, 100);

        int cx = rect.X + rect.Width / 2;
        int cy = rect.Y + rect.Height / 2;
        const float r = 8f;   // 圆弧半径
        const float stroke = 2f;

        using var pen = new Pen(fg, stroke)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        // 撤销: 逆时针圆弧 + 左向箭头; 重做: 顺时针圆弧 + 右向箭头
        bool redo = icon == ToolbarIcon.Redo;
        var arcRect = new RectangleF(cx - r, cy - r + 2, r * 2, r * 2);

        // 圆弧: 从左端点绕到右上 (撤销取 180°->340°, 重做取 200°->0° 的镜像)
        float startAngle = redo ? 200f : 160f;
        float sweep = redo ? 160f : -160f;
        g.DrawArc(pen, arcRect, startAngle, sweep);

        // 箭头: 圆弧起点处画一个三角, 指向左(撤销)/右(重做)
        float rad = startAngle * MathF.PI / 180f;
        float ax = cx + r * MathF.Cos(rad);
        float ay = cy + 2 + r * MathF.Sin(rad);
        float dir = redo ? 1f : -1f;

        using var brush = new SolidBrush(fg);
        g.FillPolygon(brush,
        [
            new PointF(ax + dir * 4f, ay),
            new PointF(ax - dir * 3f, ay - 4.5f),
            new PointF(ax - dir * 3f, ay + 4.5f),
        ]);
    }

    /// <summary>绘制文字按钮 (带底色)</summary>
    private static void DrawTextButton(Graphics g, Rectangle rect, string text, Color back, bool hover)
    {
        Color fill = hover ? Lighten(back, 0.15f) : back;
        using (var b = new SolidBrush(fill))
        using (var p = RoundedRect(rect, 5))
            g.FillPath(b, p);

        using var font = UiFont.Create(12f);
        Size ts = TextRenderer.MeasureText(text, font);
        TextRenderer.DrawText(g, text, font,
            new Point(rect.X + (rect.Width - ts.Width) / 2, rect.Y + (rect.Height - ts.Height) / 2), Color.White);
    }

    /// <summary>按钮悬停时提亮底色</summary>
    private static Color Lighten(Color c, float amount) => Color.FromArgb(
        c.A,
        Math.Min(255, (int)(c.R + (255 - c.R) * amount)),
        Math.Min(255, (int)(c.G + (255 - c.G) * amount)),
        Math.Min(255, (int)(c.B + (255 - c.B) * amount)));

    /// <summary>分隔竖线</summary>
    private static void DrawSeparator(Graphics g, int x, int cy)
    {
        using var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
        g.DrawLine(pen, x, cy - 11, x, cy + 11);
    }

    /// <summary>
    /// 用矢量绘制各工具图标 (不依赖任何图片资源, AOT 友好且任意 DPI 都清晰)。
    /// </summary>
    private static void DrawToolIcon(Graphics g, AnnotationTool tool, Rectangle rect, Color color)
    {
        int cx = rect.X + rect.Width / 2;
        int cy = rect.Y + rect.Height / 2;
        int s = IconPad;

        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(color);

        switch (tool)
        {
            case AnnotationTool.Rectangle:
                g.DrawRectangle(pen, cx - s, cy - s + 1, s * 2, s * 2 - 2);
                break;

            case AnnotationTool.Ellipse:
                g.DrawEllipse(pen, cx - s, cy - s + 1, s * 2, s * 2 - 2);
                break;

            case AnnotationTool.Arrow:
                // 从左上到右下的箭头 (几何推导与标注箭头共用 ArrowGeometry)
                var a = new PointF(cx - s, cy + s - 1);
                var b = new PointF(cx + s - 1, cy - s + 1);
                // 图标起止点为常量, 正常不会退化; 仍判空以避免将来改动引入除零 NaN
                if (AnnotationEngine.ArrowGeometry.TryCreate(a, b, headLength: 6f, headWidth: 8f) is { } ico)
                {
                    g.DrawLine(pen, a, ico.Base);   // 线体画到三角底部, 避免线尖从三角里戳出来
                    g.FillPolygon(brush, ico.Triangle());
                }
                break;

            case AnnotationTool.Pen:
                // 波浪线
                var pts = new List<PointF>();
                for (int i = 0; i <= 16; i++)
                {
                    float t = i / 16f;
                    float px2 = cx - s + t * s * 2;
                    float py2 = cy + MathF.Sin(t * MathF.PI * 2f) * (s * 0.55f);
                    pts.Add(new PointF(px2, py2));
                }
                g.DrawLines(pen, pts.ToArray());
                break;

            case AnnotationTool.Mosaic:
                // 九宫格: 交错填色的方块
                int cell = Math.Max(3, (s * 2) / 3);
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        var cellRect = new Rectangle(
                            cx - s + c * cell, cy - s + r * cell, cell - 1, cell - 1);
                        if ((r + c) % 2 == 0)
                            g.FillRectangle(brush, cellRect);
                        else
                            g.DrawRectangle(pen, cellRect);
                    }
                }
                break;

            case AnnotationTool.Text:
                // 字母 "T"
                g.DrawLine(pen, cx - s + 1, cy - s + 3, cx + s - 1, cy - s + 3);
                g.DrawLine(pen, cx, cy - s + 3, cx, cy + s - 1);
                break;
        }
    }

    // ===== 鼠标交互 =====

    /// <summary>悬停: 记录高亮项并重绘</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int ht = HitTool(e.Location);
        int hc = HitIndex(_colorRects, e.Location);
        int hw = HitIndex(_widthRects, e.Location);
        string ha = HitAction(e.Location);

        if (ht != _hoverTool || hc != _hoverColor || hw != _hoverWidth || ha != _hoverActionKey)
        {
            _hoverTool = ht;
            _hoverColor = hc;
            _hoverWidth = hw;
            _hoverActionKey = ha;
            Invalidate();
        }
    }

    /// <summary>离开控件时清空悬停态</summary>
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverTool != -1 || _hoverColor != -1 || _hoverWidth != -1 || _hoverActionKey.Length > 0)
        {
            _hoverTool = -1;
            _hoverColor = -1;
            _hoverWidth = -1;
            _hoverActionKey = string.Empty;
            Invalidate();
        }
    }

    /// <summary>点击: 分派到工具/颜色/线宽/操作</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        // 工具
        int ti = HitTool(e.Location);
        if (ti >= 0)
        {
            SetTool(AnnotationToolInfo.AllTools[ti]);
            return;
        }

        // 颜色: 循环切换
        if (HitIndex(_colorRects, e.Location) >= 0)
        {
            CycleColor();
            return;
        }

        // 线宽
        int wi = HitIndex(_widthRects, e.Location);
        if (wi >= 0)
        {
            CurrentWidth = Widths[wi];
            ToolChanged?.Invoke();
            Invalidate();
            return;
        }

        // 操作按钮
        switch (HitAction(e.Location))
        {
            case "undo": if (CanUndo) UndoRequested?.Invoke(); break;
            case "redo": if (CanRedo) RedoRequested?.Invoke(); break;
            case "save": SaveRequested?.Invoke(); break;
            case "confirm": ConfirmRequested?.Invoke(); break;
            case "cancel": CancelRequested?.Invoke(); break;
        }
    }

    /// <summary>点击工具区域的索引; -1 表示未命中</summary>
    private int HitTool(Point p)
    {
        for (int i = 0; i < _toolRects.Count; i++)
            if (_toolRects[i].Rect.Contains(p))
                return i;
        return -1;
    }

    /// <summary>通用列表命中</summary>
    private static int HitIndex<T>(List<(Rectangle Rect, T Value)> list, Point p)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].Rect.Contains(p))
                return i;
        return -1;
    }

    /// <summary>命中哪个操作按钮</summary>
    private string HitAction(Point p)
    {
        if (_undoRect.Contains(p)) return "undo";
        if (_redoRect.Contains(p)) return "redo";
        if (_saveRect.Contains(p)) return "save";
        if (_confirmRect.Contains(p)) return "confirm";
        if (_cancelRect.Contains(p)) return "cancel";
        return string.Empty;
    }

    /// <summary>切换工具 (也供键盘快捷键调用)</summary>
    public void SetTool(AnnotationTool tool)
    {
        if (Tool == tool)
            return;
        Tool = tool;
        ToolChanged?.Invoke();
        Invalidate();
    }

    /// <summary>循环切换下一个颜色</summary>
    public void CycleColor()
    {
        int idx = Array.IndexOf(Palette, CurrentColor);
        idx = (idx + 1) % Palette.Length;
        CurrentColor = Palette[idx];
        ToolChanged?.Invoke();
        Invalidate();
    }

    /// <summary>
    /// 绘制按钮底衬: 选中 = 蓝色圆角块, 悬停 = 半透明白圆角块, 其余不画。
    /// 工具按钮/线宽按钮/图标按钮共用, 保证三类按钮的高亮观感一致。
    /// </summary>
    /// <param name="active">是否为当前选中项 (优先于悬停)</param>
    private static void DrawButtonBackdrop(Graphics g, Rectangle rect, bool active, bool hover)
    {
        Color? fill = active
            ? Color.FromArgb(255, 33, 150, 243)
            : hover ? Color.FromArgb(60, 255, 255, 255) : null;
        if (fill is not { } color)
            return;

        using var brush = new SolidBrush(color);
        using var path = RoundedRect(rect, 5);
        g.FillPath(brush, path);
    }

    /// <summary>圆角矩形路径</summary>
    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
