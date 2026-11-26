using SnipasteOcr.Annotations;

namespace SnipasteOcr;

/// <summary>
/// 文字贴图: 把一段文字钉在屏幕上 (整理待办、抄下地址、对照术语时用)。
///
/// 绘制用 GDI+ 的 <see cref="Graphics.DrawString(string, Font, Brush, float, float)"/>
/// 而不是 <c>TextRenderer.DrawText</c> —— 后者走 ClearType, 在半透明/分层窗口上
/// 会产生明显彩边 (与标注文字轮廓绘制同一结论)。
/// </summary>
public sealed class PinnedTextForm : PinnedWindowBase
{
    private const int PaddingX = 12;
    private const int PaddingY = 8;

    private readonly string _text;
    private readonly Font _font;          // 所有权归本窗口
    private readonly Font _hintFont;      // 右下角操作提示
    private readonly Size _contentSize;

    /// <summary>创建文字贴图</summary>
    /// <param name="text">要显示的文字 (多行用 \n)</param>
    /// <param name="font">字体; <b>所有权转移给本窗口</b>, 关闭时释放, 调用方不要复用</param>
    /// <param name="location">窗口左上角 (屏幕坐标)</param>
    public PinnedTextForm(string text, Font font, Point location)
    {
        _text = text;
        _font = font;
        _hintFont = UiFont.Create(9f);
        _contentSize = Measure(text, font);

        Location = location;
        Cursor = Cursors.SizeAll;

        // 没有内容的贴图没有意义, 直接按最小尺寸显示, 避免出现 1x1 无法操作的小方块
        PinnedRegistry.Register(this);
    }

    protected override void RefreshPinnedSize()
        => ApplyLogicalSize(new Size(
            Math.Max(60, _contentSize.Width + PaddingX * 2),
            Math.Max(30, _contentSize.Height + PaddingY * 2)));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        using (var background = new SolidBrush(Color.FromArgb(252, 250, 250)))
            g.FillRectangle(background, ClientRectangle);

        using (var border = new Pen(Color.FromArgb(70, 0, 0, 0)))
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        using (var brush = new SolidBrush(Color.FromArgb(32, 34, 38)))
            g.DrawString(_text, _font, brush, PaddingX, PaddingY);

        // 提示文字放在右下, 让用户知道能拖动/关闭 (文字贴图不支持滚轮缩放)
        using (var hint = new SolidBrush(Color.FromArgb(140, 130, 130, 130)))
        {
            const string hintText = "拖动移动 · 右键关闭";
            SizeF size = g.MeasureString(hintText, _hintFont);
            g.DrawString(hintText, _hintFont, hint,
                ClientSize.Width - size.Width - 6, ClientSize.Height - size.Height - 2);
        }
    }

    /// <summary>测量文本像素尺寸 (用位图做临时画布, 不依赖窗口句柄)</summary>
    private static Size Measure(string text, Font font)
    {
        using var canvas = new Bitmap(1, 1);
        using var g = Graphics.FromImage(canvas);
        SizeF size = g.MeasureString(text, font);
        return new Size((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _font.Dispose();
        _hintFont.Dispose();
        base.OnFormClosed(e);
    }
}
