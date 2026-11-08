using SnipasteOcr.Annotations;

namespace SnipasteOcr;

/// <summary>
/// 截图覆盖层上的放大镜控件: 显示光标周围 15×15 个物理像素、中心像素颜色与屏幕坐标。
///
/// 显示层选择"父窗体的子控件"而不是在 <see cref="SnipOverlayForm.OnPaint"/> 里自绘:
/// 覆盖层每次重绘都要把整屏位图缩放一遍, 鼠标每移动 1px 就整屏重绘会明显卡顿;
/// 子控件只重绘自己这一小块, 鼠标命中通过 HTTRANSPARENT 交还给父窗体。
///
/// 不参与 <see cref="SnipOverlayForm.RenderResult"/>: 它只是屏幕上的辅助控件,
/// 不会被画进 <c>_screen</c> 或导出图。
/// </summary>
internal sealed class SnipMagnifierControl : Control
{
    private const int BorderSize = 1;

    private static readonly Color GridLine = Color.FromArgb(36, 0, 0, 0);
    private static readonly Color Separator = Color.FromArgb(70, 84, 88, 96);
    private static readonly Color Border = Color.FromArgb(255, 74, 78, 86);

    private readonly Bitmap _source;
    private readonly Point _sourceOrigin;
    private readonly Font _hexFont = UiFont.Create(11f);
    private readonly Font _smallFont = UiFont.Create(9f);

    private Point _centerPhysical;
    private Color _color;

    /// <summary>构造时绑定覆盖层底图; 底图所有权仍归覆盖层, 本控件只读取不释放</summary>
    public SnipMagnifierControl(Bitmap source, Point sourceOrigin)
    {
        _source = source;
        _sourceOrigin = sourceOrigin;

        Size = SnipMagnifier.PanelSize;
        Visible = false;
        Cursor = Cursors.Cross;
        TabStop = false;

        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.Opaque,
            true);
        SetStyle(ControlStyles.Selectable, false);
    }

    /// <summary>
    /// 更新采样中心并移动到光标旁。
    /// <paramref name="cursorClient"/> 是覆盖层客户区逻辑坐标;
    /// <paramref name="centerPhysical"/> 是底图物理像素;
    /// <paramref name="screenClient"/> 是光标所在屏的客户区逻辑矩形。
    /// </summary>
    public void UpdateAt(Point cursorClient, Point centerPhysical, Rectangle screenClient)
    {
        Point clamped = SnipMagnifier.ClampSource(centerPhysical, _source.Size);
        Rectangle layout = SnipMagnifier.ComputeLayout(cursorClient, screenClient);
        bool firstShow = !Visible;
        bool movedPixel = clamped != _centerPhysical;
        bool needsRepaint = firstShow || movedPixel || Bounds != layout;

        if (firstShow || movedPixel)
        {
            _centerPhysical = clamped;
            _color = SnipMagnifier.Sample(_source, _centerPhysical);
        }

        if (Bounds != layout)
            Bounds = layout;
        Visible = true;
        if (needsRepaint)
            Invalidate();
    }

    /// <summary>鼠标命中一律交还给父窗体: 放大镜只是显示层, 不能拦截框选/取色</summary>
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        if (m.Msg == WM_NCHITTEST)
        {
            m.Result = HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int grid = SnipMagnifier.GridSize;
        int cell = SnipMagnifier.CellSize;
        int half = SnipMagnifier.GridCells / 2;

        using (var surface = new SolidBrush(UiTheme.Canvas))
            g.FillRectangle(surface, ClientRectangle);

        // 15×15 个底图像素; 边缘越界由 Sample 夹紧, 因此不会出现空洞。
        // 复用同一支画刷, 避免每个格子分配一次 GDI+ 对象。
        using (var pixel = new SolidBrush(Color.Black))
        {
            for (int row = 0; row < SnipMagnifier.GridCells; row++)
            {
                for (int col = 0; col < SnipMagnifier.GridCells; col++)
                {
                    Point source = SnipMagnifier.SourcePoint(_centerPhysical, col, row);
                    pixel.Color = SnipMagnifier.Sample(_source, source);
                    g.FillRectangle(pixel, col * cell, row * cell, cell, cell);
                }
            }
        }

        // 像素网格线: 只压一层很淡的线, 既能看出单像素边界, 又不改变颜色观感
        using (var gridPen = new Pen(GridLine))
        {
            for (int i = 1; i < SnipMagnifier.GridCells; i++)
            {
                int p = i * cell;
                g.DrawLine(gridPen, p, 0, p, grid);
                g.DrawLine(gridPen, 0, p, grid, p);
            }
        }

        // 中心像素高亮: 取色器真正读取的就是这一格
        using (var centerPen = new Pen(UiTheme.Accent, 2f))
        {
            int center = half * cell;
            g.DrawRectangle(centerPen, center - 1, center - 1, cell + 2, cell + 2);
        }

        // 信息区
        using (var info = new SolidBrush(UiTheme.Surface))
            g.FillRectangle(info, 0, grid, Width, Height - grid);
        using (var separator = new Pen(Separator))
            g.DrawLine(separator, 0, grid, Width, grid);

        const int swatchX = 8;
        int swatchY = grid + 9;
        const int swatchSize = 24;
        using (var colorBrush = new SolidBrush(_color))
            g.FillRectangle(colorBrush, swatchX, swatchY, swatchSize, swatchSize);
        using (var swatchPen = new Pen(Color.FromArgb(120, 255, 255, 255)))
            g.DrawRectangle(swatchPen, swatchX, swatchY, swatchSize, swatchSize);

        int textX = swatchX + swatchSize + 8;
        string hex = SnipMagnifier.FormatHex(_color);
        string rgb = SnipMagnifier.FormatRgb(_color);
        string position = $"X {_centerPhysical.X + _sourceOrigin.X}  Y {_centerPhysical.Y + _sourceOrigin.Y}";

        TextRenderer.DrawText(g, hex, _hexFont, new Point(textX, grid + 6), Color.White,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, rgb, _smallFont, new Point(textX, grid + 23), UiTheme.MutedText,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, position, _smallFont, new Point(textX, grid + 37), UiTheme.MutedText,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        using var border = new Pen(Border, BorderSize);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hexFont.Dispose();
            _smallFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
