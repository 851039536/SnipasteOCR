using System.Drawing.Drawing2D;

namespace SnipasteOcr.Annotations;

/// <summary>
/// 颜色与线宽弹层 (标注工具栏「样式」按钮的弹出面板)。
///
/// 为什么是独立顶层窗口, 而不是工具栏里的子控件:
/// 1. 工具栏高度只有 44px、宽度守着 650px 上限, 8 色 + 3 档线宽常驻需要约 106px, 单行放不下;
/// 2. 覆盖层是整屏 TopMost 窗体并在 <c>OnPaint</c> 里整屏绘制底图, 子控件的绘制顺序与裁剪
///    都受它影响; 一个不抢焦点的顶层窗口 (WS_EX_NOACTIVATE) 最稳, 也不会打断框选。
///
/// 面板只负责"选", 不持有状态: 颜色与线宽仍由 <see cref="AnnotationToolbar"/> 保存,
/// 选择后立即回调, 工具栏预览与正在绘制的标注同一帧更新。
/// </summary>
internal sealed class StylePopupForm : Form
{
    /// <summary>色块边长</summary>
    public const int Swatch = 32;

    /// <summary>色块间距</summary>
    public const int SwatchGap = 4;

    /// <summary>面板内边距</summary>
    public const int Pad = 12;

    /// <summary>每行色块数 (4×2 一屏看全 8 色, 比"点击循环"少 3~7 次点击)</summary>
    public const int Columns = 4;

    private const int SepGap = 8;

    /// <summary>分隔线上边距</summary>
    private const int SepTop = Pad + Swatch * 2 + SwatchGap + SepGap;

    /// <summary>线宽行上边距 (分隔线下方)</summary>
    private const int WidthTop = SepTop + 1 + SepGap;

    /// <summary>面板尺寸 (逻辑像素)。绘制与命中全部由这里的常量推导, 不另写一份</summary>
    public static Size PanelSize { get; } = new(
        Pad * 2 + Columns * Swatch + (Columns - 1) * SwatchGap,
        WidthTop + Swatch + Pad);

    private readonly AnnotationToolbar _owner;
    private int _hoverColor = -1;
    private int _hoverWidth = -1;

    public StylePopupForm(AnnotationToolbar owner)
    {
        _owner = owner;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = UiTheme.Surface;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer,
            true);
        ClientSize = PanelSize;
    }

    /// <summary>不抢焦点: 覆盖层必须继续接收 1..8 / Esc 等键盘操作</summary>
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 与工具栏/结果窗口同一套 8px 圆角 (Win10 忽略)
        SnipasteOcr.Native.Dwmapi.TryRoundCorners(Handle);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
            return cp;
        }
    }

    /// <summary>
    /// 显示在「样式」按钮下方; 下方放不下就翻到上方, 最后夹紧到屏幕工作区。
    /// 与工具栏一样使用物理像素坐标 (按钮矩形来自 <c>RectangleToScreen</c>)。
    /// </summary>
    public void ShowAt(Rectangle buttonScreen, Rectangle workingArea)
    {
        int x = Math.Clamp(
            buttonScreen.Left,
            workingArea.Left + 4,
            Math.Max(workingArea.Left + 4, workingArea.Right - PanelSize.Width - 4));

        int y = buttonScreen.Bottom + 6;
        if (y + PanelSize.Height > workingArea.Bottom - 4)
            y = buttonScreen.Top - PanelSize.Height - 6;
        y = Math.Clamp(y, workingArea.Top + 4,
            Math.Max(workingArea.Top + 4, workingArea.Bottom - PanelSize.Height - 4));

        Location = new Point(x, y);
        Show();
        Invalidate();
    }

    private static Rectangle ColorCell(int index) => new(
        Pad + index % Columns * (Swatch + SwatchGap),
        Pad + index / Columns * (Swatch + SwatchGap),
        Swatch, Swatch);

    private static Rectangle WidthCell(int index) => new(
        Pad + index * (Swatch + SwatchGap), WidthTop, Swatch, Swatch);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        using (var border = new Pen(Color.FromArgb(255, 58, 63, 71), 1f))
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        // ---- 色块 ----
        int currentColor = AnnotationPalette.IndexOf(_owner.CurrentColor);
        for (int i = 0; i < AnnotationPalette.Colors.Length; i++)
        {
            var cell = ColorCell(i);
            var color = AnnotationPalette.Colors[i];

            using (var fill = new SolidBrush(color))
                g.FillRectangle(fill, cell);

            // 深色/白色色块都需要一圈描边, 否则在深底上看不出边界
            using (var edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
                g.DrawRectangle(edge, cell);

            if (i == _hoverColor)
            {
                using var hover = new SolidBrush(Color.FromArgb(40, 255, 255, 255));
                g.FillRectangle(hover, cell);
            }

            if (i == currentColor)
            {
                // 选中环: 白色 2px 内环 + 深色外环, 白/黑色块上都能看见
                using (var ring = new Pen(Color.White, 2f))
                    g.DrawRectangle(ring, cell.X + 1, cell.Y + 1, cell.Width - 3, cell.Height - 3);
                using (var outer = new Pen(Color.FromArgb(130, 0, 0, 0), 1f))
                    g.DrawRectangle(outer, cell.X - 1, cell.Y - 1, cell.Width + 1, cell.Height + 1);
            }
        }

        // ---- 分隔线 ----
        using (var sep = new Pen(Color.FromArgb(255, 60, 64, 72), 1f))
            g.DrawLine(sep, Pad, SepTop, ClientSize.Width - Pad - 1, SepTop);

        // ---- 线宽 ----
        int currentWidth = AnnotationPalette.WidthIndexOf(_owner.CurrentWidth);
        bool marker = _owner.Tool == AnnotationTool.Marker;
        for (int i = 0; i < AnnotationPalette.Widths.Length; i++)
        {
            var cell = WidthCell(i);

            if (i == currentWidth)
            {
                // 选中态不用强调蓝填充: 预览线是当前色, 蓝底 + 红线会互相干扰、也看不清粗细
                using var back = new SolidBrush(Color.FromArgb(36, 255, 255, 255));
                using var path = Rounded(cell, 4);
                g.FillPath(back, path);
                using var ring = new Pen(UiTheme.Accent, 1.2f);
                g.DrawPath(ring, path);
            }
            else if (i == _hoverWidth)
            {
                using var back = new SolidBrush(Color.FromArgb(22, 255, 255, 255));
                using var path = Rounded(cell, 4);
                g.FillPath(back, path);
            }

            float width = marker
                ? MathF.Min(AnnotationEngine.MarkerWidth(AnnotationPalette.Widths[i]), cell.Height - 10f)
                : AnnotationPalette.Widths[i];
            Color preview = marker
                ? AnnotationEngine.MarkerColor(_owner.CurrentColor)
                : _owner.CurrentColor;

            using var pen = new Pen(preview, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, cell.Left + 5, cell.Top + cell.Height / 2f,
                cell.Right - 5, cell.Top + cell.Height / 2f);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int hc = -1;
        for (int i = 0; i < AnnotationPalette.Colors.Length; i++)
            if (ColorCell(i).Contains(e.Location)) { hc = i; break; }

        int hw = -1;
        for (int i = 0; i < AnnotationPalette.Widths.Length; i++)
            if (WidthCell(i).Contains(e.Location)) { hw = i; break; }

        if (hc != _hoverColor || hw != _hoverWidth)
        {
            _hoverColor = hc;
            _hoverWidth = hw;
            Cursor = hc >= 0 || hw >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverColor != -1 || _hoverWidth != -1)
        {
            _hoverColor = -1;
            _hoverWidth = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;

        for (int i = 0; i < AnnotationPalette.Colors.Length; i++)
        {
            if (!ColorCell(i).Contains(e.Location))
                continue;
            _owner.SetColor(AnnotationPalette.Colors[i]);
            return;
        }

        for (int i = 0; i < AnnotationPalette.Widths.Length; i++)
        {
            if (!WidthCell(i).Contains(e.Location))
                continue;
            _owner.SetWidth(AnnotationPalette.Widths[i]);
            return;
        }
    }

    /// <summary>Esc 只关面板 (面板不抢焦点, 这个分支是兜底; 覆盖层也会主动关它)</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Hide();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
