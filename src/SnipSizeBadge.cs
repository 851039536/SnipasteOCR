using System.Drawing.Drawing2D;
using System.Globalization;
using SnipasteOcr.Annotations;

namespace SnipasteOcr;

/// <summary>
/// 框选时跟随选区的实时尺寸徽标: 显示<b>物理像素</b>宽 × 高, 即最终导出/识别的真实尺寸。
///
/// 两处刻意的设计:
/// - 度量与绘制共用同一份 <see cref="Segments"/> 与间距常量。各写一份就会重演工具栏
///   "宽度与实际布局不符"的坑 (尾部凭空留白或文字溢出面板)。
/// - <see cref="ComputeLayout"/> 是纯函数且永远把徽标夹紧在屏幕内: 选区贴着上边缘时改放选区内侧,
///   贴着右边缘时水平回退。历史实现只判了纵向, 靠右框选时标签右半截被屏幕裁掉。
///
/// 徽标只画在覆盖层上, 不参与 <c>RenderResult</c> 合成, 因此不会烧进截图。
/// </summary>
internal static class SnipSizeBadge
{
    private const float FontSize = 12f;
    private const int PadX = 9;
    private const int PadY = 4;

    /// <summary>与选区边框的间距</summary>
    private const int Gap = 6;

    /// <summary>与屏幕边缘的最小间距 (保证任何框选位置都完整可见)</summary>
    private const int Margin = 4;

    private const int Radius = 5;
    private const int CrossGap = 5;
    private const int UnitGap = 7;

    private const TextFormatFlags TextFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private static readonly Color Accent = Color.FromArgb(255, 33, 150, 243);
    private static readonly Color Muted = Color.FromArgb(255, 156, 166, 180);

    /// <summary>
    /// 徽标的分段内容 (文本 / 颜色 / 与前一段的间距)。
    /// 分段而非整串: 数字要用白色、× 用选区同色、单位用弱色, 而带空格的整串在
    /// GDI 的 DT_CALCRECT 下会吞掉尾随空格, 度量与实际绘制就对不上了。
    /// </summary>
    private static (string Text, Color Color, int GapBefore)[] Segments(Size physical) =>
    [
        (physical.Width.ToString(CultureInfo.InvariantCulture), Color.White, 0),
        ("\u00d7", Accent, CrossGap),
        (physical.Height.ToString(CultureInfo.InvariantCulture), Color.White, CrossGap),
        ("px", Muted, UnitGap),
    ];

    /// <summary>文本部分的整体尺寸 (不含面板内边距); 与绘制走同一份分段累加</summary>
    public static Size MeasureText(Size physical)
    {
        using var font = UiFont.Create(FontSize);
        int w = 0, h = 0;
        foreach (var (text, _, gap) in Segments(physical))
        {
            Size s = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFlags);
            w += gap + s.Width;
            h = Math.Max(h, s.Height);
        }
        return new Size(w, h);
    }

    /// <summary>
    /// 徽标面板外框 (逻辑坐标)。优先贴在选区上方; 上方放不下则移入选区内侧顶端
    /// (不放选区下方 —— 那里是工具栏的位置), 最后夹紧到屏幕内。
    /// <paramref name="avoid"/> 传工具栏外框: 工具栏是子控件, 会整块盖住窗体自绘内容,
    /// 选区贴底时它会翻转到选区上方, 正好压在徽标上, 因此重叠时必须让位。
    /// </summary>
    public static Rectangle ComputeLayout(Rectangle sel, Size textSize, Size client, Rectangle? avoid = null)
    {
        int w = textSize.Width + PadX * 2;
        int h = textSize.Height + PadY * 2;

        int x = sel.Left;
        int y = sel.Top - h - Gap;
        if (y < Margin)
            y = sel.Top + Gap;

        var box = Place(x, y, w, h, client);

        // 依次尝试: 工具栏上方 → 选区内侧顶端 → 工具栏下方。
        // 都被占（如工具栏跨满整屏）就保持原位, 至少不越出客户区。
        if (avoid is { } tb && box.IntersectsWith(tb))
        {
            int[] candidates = [tb.Top - h - Gap, sel.Top + Gap, tb.Bottom + Gap];
            foreach (int candidate in candidates)
            {
                var shifted = Place(x, candidate, w, h, client);
                if (!shifted.IntersectsWith(tb))
                {
                    box = shifted;
                    break;
                }
            }
        }

        return box;
    }

    /// <summary>把徽标左上角放到 (x, y), 并夹紧到客户区内 (客户区比徽标小时也不抛)</summary>
    private static Rectangle Place(int x, int y, int w, int h, Size client) => new(
        Math.Clamp(x, Margin, Math.Max(Margin, client.Width - w - Margin)),
        Math.Clamp(y, Margin, Math.Max(Margin, client.Height - h - Margin)),
        w,
        h);

    /// <summary>在覆盖层上绘制徽标; physical 必须是 <see cref="SnipGeometry.ExportRect"/> 的结果尺寸</summary>
    public static void Draw(Graphics g, Rectangle sel, Size physical, Size client, Rectangle? toolbar = null)
    {
        Size textSize = MeasureText(physical);
        Rectangle box = ComputeLayout(sel, textSize, client, toolbar);

        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            // 投影: 亮色截图上也能把面板从底图里分离出来
            using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            using (var sp = RoundRect(new Rectangle(box.X, box.Y + 1, box.Width, box.Height), Radius))
                g.FillPath(shadow, sp);

            using (var bg = new SolidBrush(Color.FromArgb(235, 20, 22, 26)))
            using (var path = RoundRect(box, Radius))
            {
                g.FillPath(bg, path);
                using var border = new Pen(Color.FromArgb(150, Accent), 1f);
                g.DrawPath(border, path);
            }
        }
        finally
        {
            g.SmoothingMode = smoothing;
        }

        using var font = UiFont.Create(FontSize);
        int x = box.Left + PadX;
        int y = box.Top + (box.Height - textSize.Height) / 2;
        foreach (var (text, color, gap) in Segments(physical))
        {
            x += gap;
            TextRenderer.DrawText(g, text, font, new Point(x, y), color, TextFlags);
            x += TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFlags).Width;
        }
    }

    /// <summary>圆角矩形路径 (半径自动收缩到短边以内, 避免小面板上 AddArc 画出自交路径)</summary>
    private static GraphicsPath RoundRect(Rectangle r, int radius)
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