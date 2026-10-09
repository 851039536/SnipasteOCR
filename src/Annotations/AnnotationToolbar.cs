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
    // 尺寸原则: 紧凑优先。工具栏是浮在截图上的辅助控件, 越窄越好。
    // 历史: 一度加到 38px 按钮 / 58px 高 + 按钮下方中文名称, 整条达 730px 且需两行,
    // 视觉松散。现在移除全部文字, 退回单行纯图标布局。
    private const int ButtonSize = 34;
    private const int IconPad = 8;
    private const int Gap = 3;
    private const int SepWidth = 8;
    // 单行布局: 控件垂直居中于工具栏高度。
    private const int ToolbarHeight = 42;
    private const int PadLeft = 7;
    private const int PadRight = 7;

    /// <summary>颜色色块边长 (与 OnPaint 中的 swatch 保持一致)</summary>
    private const int ColorBlockWidth = 22;

    // 右侧文字按钮 (保存/确认/取消): 统一宽度与字号, 让三者等宽对齐 ——
    // 原先 58/64/64 三种宽度混排, 视觉上参差不齐。
    // 宽度以"两字中文 + 左右各 8px 内边距"为准 (11pt 下约 30px 文字), 46px 足够且不显空。
    private const int TextButtonWidth = 46;
    private const int TextButtonHeight = 26;
    private const float TextButtonFontSize = 11f;

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

    /// <summary>
    /// 计算工具栏各元素的位置 (唯一布局来源)。
    /// <see cref="OnPaint"/> 与 <see cref="MeasurePreferredWidth"/> 都调用本方法 ——
    /// 早先两边各写一份累加公式, 结果宽度与实际布局对不上 (实测尾部留 27px 空白,
    /// 收紧后又变成按钮溢出 2px)。这类"同一逻辑写两遍"正是本项目的核心不变量所禁止的。
    /// </summary>
    /// <param name="width">工具栏宽度; 传 0 表示"只算所需宽度", 各元素 X 依次排布</param>
    private static ToolbarLayout ComputeLayout(int width)
    {
        int x = PadLeft;

        var tools = new Rectangle[AnnotationToolInfo.AllTools.Length];
        for (int i = 0; i < tools.Length; i++)
        {
            tools[i] = new Rectangle(x, 0, ButtonSize, ButtonSize);
            x += ButtonSize + Gap;
        }

        x += SepWidth - Gap;
        int sep1 = x - SepWidth / 2 - 1;

        var color = new Rectangle(x, 0, ColorBlockWidth, ButtonSize);
        x += ColorBlockWidth + 12;

        x += SepWidth;
        int sep2 = x - SepWidth / 2 - 1;

        int wb = (int)(ButtonSize * 0.8f);
        var widths = new Rectangle[Widths.Length];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = new Rectangle(x, 0, wb, wb);
            x += wb + Gap;
        }

        x += SepWidth - Gap;
        int sep3 = x - SepWidth / 2 - 1;

        var undo = new Rectangle(x, 0, ButtonSize, ButtonSize);
        x += ButtonSize + Gap;
        var redo = new Rectangle(x, 0, ButtonSize, ButtonSize);
        x += ButtonSize + Gap;

        x += SepWidth - Gap;
        int sep4 = x - SepWidth / 2 - 1;

        var save = new Rectangle(x, 0, TextButtonWidth, TextButtonHeight);
        x += TextButtonWidth + Gap;
        var confirm = new Rectangle(x, 0, TextButtonWidth, TextButtonHeight);
        x += TextButtonWidth + Gap;
        var cancel = new Rectangle(x, 0, TextButtonWidth, TextButtonHeight);
        x += TextButtonWidth;

        // width<=0: 返回"内容右边界 + 右留白"; 否则用给定宽度 (仅用于排版, 结果一致)
        int total = x + PadRight;
        return new ToolbarLayout(tools, color, widths, undo, redo, save, confirm, cancel,
            sep1, sep2, sep3, sep4, total);
    }

    /// <summary>布局计算结果 (各元素 X 已确定, Y 在绘制时按高度居中填充)</summary>
    private sealed record ToolbarLayout(
        Rectangle[] Tools,
        Rectangle Color,
        Rectangle[] WidthButtons,
        Rectangle Undo,
        Rectangle Redo,
        Rectangle Save,
        Rectangle Confirm,
        Rectangle Cancel,
        int Sep1,
        int Sep2,
        int Sep3,
        int Sep4,
        int TotalWidth);

    /// <summary>工具栏所需宽度 (与 OnPaint 同源, 不再各写一份)</summary>
    private static int MeasurePreferredWidth() => ComputeLayout(0).TotalWidth;

    /// <summary>
    /// 绘制工具栏: 深色圆角底 + 工具图标 + 颜色/线宽选择 + 操作按钮。
    /// 命中区域在绘制过程中重建, 保证与视觉完全一致。
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 只填充圆角底, 不描边。
        // 原先在 ClientRectangle 上描 1px 半透明边框, 但 1px 画笔以路径为中心,
        // 上/左两条边的线有一半落在 (0,0) 之外被裁剪, 下/右两条边却完整画在里面 ——
        // 同一条边框四边粗细与亮度不一致, 看起来"有些地方正常, 有些地方怪"。
        // 深色底本身与截图已有足够对比, 直接去掉边框最干净。
        using (var path = RoundedRect(ClientRectangle, 6))
        using (var bg = new SolidBrush(BackColor))
            g.FillPath(bg, path);

        _toolRects.Clear();
        _colorRects.Clear();
        _widthRects.Clear();

        // 位置统一由 ComputeLayout 给出 (与 MeasurePreferredWidth 同源),
        // 这里只负责按工具栏高度做垂直居中并绘制。
        var L = ComputeLayout(Width);
        int cy = Height / 2;

        // ---- 工具按钮 (纯图标, 等宽) ----
        for (int i = 0; i < L.Tools.Length; i++)
        {
            var tool = AnnotationToolInfo.AllTools[i];
            var rect = CenterY(L.Tools[i], ButtonSize, cy);
            _toolRects.Add((rect, tool));

            bool active = Tool == tool;
            bool hover = _hoverTool == i;

            DrawButtonBackdrop(g, rect, active, hover);

            DrawToolIcon(g, tool, rect, active ? Color.White : Color.FromArgb(230, 230, 230));
        }

        DrawSeparator(g, L.Sep1, cy);

        // ---- 当前颜色 (点击循环切换) ----
        {
            int swatch = ColorBlockWidth;
            var rect = CenterY(L.Color, ButtonSize, cy);
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
                new Point(sr.Right + 3, cy - 2),
                new Point(sr.Right + 9, cy - 2),
                new Point(sr.Right + 6, cy + 3),
            };
            using var tb = new SolidBrush(Color.FromArgb(200, 230, 230, 230));
            g.FillPolygon(tb, tri);
        }

        DrawSeparator(g, L.Sep2, cy);

        // ---- 线宽 ----
        int wb = (int)(ButtonSize * 0.8f);
        for (int i = 0; i < L.WidthButtons.Length; i++)
        {
            var rect = CenterY(L.WidthButtons[i], wb, cy);
            _widthRects.Add((rect, Widths[i]));

            bool active = Math.Abs(CurrentWidth - Widths[i]) < 0.01f;
            DrawButtonBackdrop(g, rect, active, _hoverWidth == i);

            // 预览用当前颜色而非固定白色: 所选颜色就是将要画出的颜色, 预览应如实反映,
            // 否则选"黄色细线"时预览仍是白色细线, 无从判断实际效果。
            using (var pen = new Pen(CurrentColor, Widths[i]))
                g.DrawLine(pen, rect.Left + 5, cy, rect.Right - 5, cy);
        }

        DrawSeparator(g, L.Sep3, cy);

        // ---- 撤销 / 重做 ----
        _undoRect = CenterY(L.Undo, ButtonSize, cy);
        DrawIconButton(g, _undoRect, ToolbarIcon.Undo, CanUndo, _hoverActionKey == "undo");

        _redoRect = CenterY(L.Redo, ButtonSize, cy);
        DrawIconButton(g, _redoRect, ToolbarIcon.Redo, CanRedo, _hoverActionKey == "redo");

        DrawSeparator(g, L.Sep4, cy);

        // ---- 保存 / 确认 / 取消 (等宽等高, 三者对齐) ----
        _saveRect = CenterY(L.Save, TextButtonHeight, cy);
        DrawTextButton(g, _saveRect, "保存", Color.FromArgb(70, 72, 78), _hoverActionKey == "save");

        _confirmRect = CenterY(L.Confirm, TextButtonHeight, cy);
        DrawTextButton(g, _confirmRect, "确认", Color.FromArgb(33, 150, 243), _hoverActionKey == "confirm");

        _cancelRect = CenterY(L.Cancel, TextButtonHeight, cy);
        DrawTextButton(g, _cancelRect, "取消", Color.FromArgb(70, 72, 78), _hoverActionKey == "cancel");
    }

    /// <summary>把布局结果的 Y(占位 0) 换成按工具栏高度居中的实际 Y</summary>
    private static Rectangle CenterY(Rectangle r, int height, int cy) =>
        new(r.X, cy - height / 2, r.Width, height);

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

        using var font = UiFont.Create(TextButtonFontSize);
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

    /// <summary>分隔竖线 (高度随工具栏高度自适应)</summary>
    private void DrawSeparator(Graphics g, int x, int cy)
    {
        using var pen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
        // 相对图标行中心上下对称延伸, 但不越出上下留白
        int half = Math.Min(12, Math.Min(cy - 5, Height - cy - 5));
        if (half < 0) half = 0;
        g.DrawLine(pen, x, cy - half, x, cy + half);
    }

    /// <summary>
    /// 用矢量绘制各工具图标 (不依赖任何图片资源, AOT 友好且任意 DPI 都清晰)。
    ///
    /// 图标不再垂直居中于整个按钮: 按钮下半部分留给了名称标签, 因此图标中心上移到
    /// 按钮的图标区 (高度约 <c>ButtonSize - 3</c>), 否则图标会压在文字上。
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
