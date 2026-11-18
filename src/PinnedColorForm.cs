using SnipasteOcr.Annotations;

namespace SnipasteOcr;

/// <summary>
/// 颜色贴图: 把取到的颜色钉在屏幕上 (对着设计稿调样式时省去反复取色)。
///
/// 双击复制 HEX —— 左键已被拖动占用, 用双击区分"看"和"拿"。
/// </summary>
public sealed class PinnedColorForm : PinnedWindowBase
{
    private const int Side = 150;
    private const int BarHeight = 26;

    private readonly Color _color;
    private readonly string _hex;
    private readonly Font _font;
    private readonly Font _hintFont;

    /// <summary>创建颜色贴图</summary>
    /// <param name="color">要展示的颜色</param>
    /// <param name="location">窗口左上角 (屏幕坐标)</param>
    public PinnedColorForm(Color color, Point location)
    {
        _color = color;
        // 格式化与取色复制共用同一实现, 避免贴图上写的 HEX 和剪贴板里的不是同一个
        _hex = SnipMagnifier.FormatHex(color);
        _font = UiFont.Create(12f);
        _hintFont = UiFont.Create(9f);

        Location = location;
        Cursor = Cursors.SizeAll;

        PinnedRegistry.Register(this);
    }

    protected override void RefreshPinnedSize() => ApplyLogicalSize(new Size(Side, Side));

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_color);

        // 底部深色条: 浅色块上白字、深色块上也看得清 —— 统一用深条 + 白字更稳
        using (var bar = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            g.FillRectangle(bar, 0, ClientSize.Height - BarHeight, ClientSize.Width, BarHeight);

        using (var border = new Pen(Color.FromArgb(70, 0, 0, 0)))
            g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        using (var white = new SolidBrush(Color.White))
            g.DrawString(_hex, _font, white, 8, ClientSize.Height - BarHeight + 4);

        using (var hint = new SolidBrush(Color.FromArgb(170, 255, 255, 255)))
        {
            SizeF size = g.MeasureString("双击复制", _hintFont);
            g.DrawString("双击复制", _hintFont, hint, ClientSize.Width - size.Width - 8, ClientSize.Height - BarHeight + 7);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left)
            return;

        try
        {
            Clipboard.SetText(_hex);
        }
        catch
        {
            // 剪贴板被占用: 贴图本身仍然可用, 不弹窗打断
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _font.Dispose();
        _hintFont.Dispose();
        base.OnFormClosed(e);
    }
}
