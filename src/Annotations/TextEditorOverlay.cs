using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace SnipasteOcr.Annotations;

/// <summary>
/// 就地文字编辑框: 覆盖在截图上, 所见即所得地输入文字标注。
///
/// 不用 <see cref="TextBox"/> 是因为它无法做到「无边框 + 透明背景 + 带描边文字」的效果,
/// 绘制结果会与最终合成不一致。这里自绘一个轻量编辑器, 只处理最必要的键盘输入。
/// </summary>
public sealed class TextEditorOverlay : Control
{
    /// <summary>文字颜色</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color TextColor { get; set; } = Color.FromArgb(255, 235, 59, 36);

    /// <summary>字号 (物理像素 → 由宿主换算为逻辑像素后设置)</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public float FontSize { get; set; } = AnnotationEngine.DefaultFontSize;

    /// <summary>编辑完成 (回车且非空 / 失焦); 参数为最终文本</summary>
    public event Action<string>? Committed;

    /// <summary>用户放弃编辑 (Esc)</summary>
    public event Action? Cancelled;

    /// <summary>
    /// 内容变化时触发 (用于宿主实时重绘: 编辑框尺寸需要随文本增长)
    /// </summary>
    public event Action? ContentChanged;

    private string _text = string.Empty;
    private readonly Font _font;

    /// <summary>是否已提交/取消 (防止重复触发事件)</summary>
    private bool _closed;

    /// <summary>文本框内边距 (逻辑像素)</summary>
    private const int PadX = 4;
    private const int PadY = 3;

    /// <summary>光标占位宽度 (留出竖线空间, 输入时框体不会贴住最后一个字)</summary>
    private const int CaretSlack = 4;

    /// <summary>最小可见尺寸: 空编辑器也必须 > 0, 否则 WinForms 视其为不可见</summary>
    private const int MinWidth = 40;
    private const int MinHeight = 24;

    public TextEditorOverlay(float fontSizeLogical)
    {
        FontSize = fontSizeLogical;

        // 用固定字号创建字体; 缩放通过 FontSize 在构造时确定
        _font = UiFont.Create(fontSizeLogical);

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;

        // 获得焦点以接收键盘输入
        TabStop = true;

        // 关键: 构造时就给出可见尺寸。否则控件为 0x0, WinForms 不会绘制它,
        // 用户点完文字工具后看不到任何反馈。
        ResizeToContent();
    }

    /// <summary>当前文本</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public string Value
    {
        get => _text;
        set
        {
            _text = value ?? string.Empty;
            ResizeToContent();
            Invalidate();
        }
    }

    /// <summary>
    /// 按当前文本重新计算控件尺寸 (随输入增长)。
    ///
    /// 注意: 必须保证结果 <b>永不为 0</b> —— WinForms 视 0 尺寸控件为不可见,
    /// 空文本时会呈现"点了没反应"的效果。
    /// 度量统一用 <see cref="TextRenderer"/> (GDI), 与 <see cref="OnPaint"/> 的绘制方式一致;
    /// 若这里用 Graphics.MeasureString (GDI+), 会带额外内边距导致光标位置偏移。
    /// </summary>
    public void ResizeToContent()
    {
        string measure = _text.Length == 0 ? " " : _text;
        Size size = TextRenderer.MeasureText(measure, _font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        int w = size.Width + PadX * 2 + CaretSlack;
        int h = Math.Max(size.Height, _font.Height) + PadY * 2;

        // 下限保证空编辑器也可见可点
        Size = new Size(Math.Max(w, MinWidth), Math.Max(h, MinHeight));

        // 这里是所有文本变更的唯一必经之路 (输入/退格/赋值), 因此统一在此通知宿主
        ContentChanged?.Invoke();
        OnTextChanged(EventArgs.Empty);
    }

    /// <summary>编辑器当前的逻辑矩形 (供宿主更新 _textAnchor)</summary>
    public Rectangle EditorBounds => new(Location, Size);

    /// <summary>自绘: 半透明底 + 文字 + 光标 (与最终合成效果近似)</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 轻微底衬, 让输入时能看清编辑区域
        using (var bg = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            g.FillRectangle(bg, ClientRectangle);

        using (var border = new Pen(Color.FromArgb(180, 33, 150, 243), 1f))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        var flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        var pt = new Point(PadX, PadY);

        // 与 AnnotationEngine 一致: 深色描边保证可读
        Color outline = Luminance(TextColor) > 140 ? Color.FromArgb(200, 0, 0, 0) : Color.FromArgb(200, 255, 255, 255);
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                TextRenderer.DrawText(g, _text, _font, new Point(pt.X + dx, pt.Y + dy), outline, flags);
            }
        TextRenderer.DrawText(g, _text, _font, pt, TextColor, flags);

        // 光标: 文本末尾的竖线。
        // 必须用 TextRenderer 度量 (与上面的绘制同一套 GDI 度量),
        // 若改用 Graphics.MeasureString 会带 GDI+ 额外内边距, 导致光标偏离字尾。
        Size textSize = TextRenderer.MeasureText(
            _text.Length == 0 ? " " : _text, _font,
            new Size(int.MaxValue, int.MaxValue), flags);
        int caretX = PadX + textSize.Width;
        if (caretX < PadX) caretX = PadX;
        if (caretX > Width - 2) caretX = Math.Max(PadX, Width - 2);

        using var caret = new Pen(Color.White, 1.5f);
        g.DrawLine(caret, caretX, PadY, caretX, Height - PadY);
    }

    /// <summary>键盘处理: 回车提交, Esc 取消, 退格删除, 其余可打印字符追加</summary>
    protected override bool IsInputKey(Keys keyData) => true;

    /// <summary>仅处理字符输入 (方向键等忽略)</summary>
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);

        if (e.KeyChar == '\r' || e.KeyChar == '\n')
        {
            e.Handled = true;
            Commit();
            return;
        }

        if (e.KeyChar == '\b')
        {
            e.Handled = true;
            if (_text.Length > 0)
            {
                _text = _text[..^1];
                ResizeToContent();
                Invalidate();
            }
            return;
        }

        // 忽略控制字符
        if (char.IsControl(e.KeyChar))
            return;

        _text += e.KeyChar;
        ResizeToContent();
        Invalidate();
        e.Handled = true;
    }

    /// <summary>Esc 取消 (KeyPress 收不到 Esc, 需在 KeyDown 处理)</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (!_closed)
            {
                _closed = true;
                Cancelled?.Invoke();
            }
        }
        else if (e.KeyCode == Keys.Enter)
        {
            // 回车通常由 KeyPress 处理; 这里兜底 (某些输入法下 KeyPress 可能不触发)
            e.Handled = true;
            e.SuppressKeyPress = true;
            Commit();
        }
    }

    /// <summary>失焦即提交 (避免编辑框挂在界面上但用户已切换工具)</summary>
    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Commit();
    }

    /// <summary>
    /// 由宿主主动提交当前内容 (如切换工具 / 确认截图时)。
    /// 幂等: 已提交或已取消后重复调用不会再次触发事件。
    /// </summary>
    public void CommitNow()
    {
        if (_closed)
            return;
        Commit();
    }

    /// <summary>提交: 非空才回调, 空文本视为取消; 保证只触发一次</summary>
    private void Commit()
    {
        if (_closed)
            return;
        _closed = true;

        string text = _text;
        if (string.IsNullOrWhiteSpace(text))
            Cancelled?.Invoke();
        else
            Committed?.Invoke(text);
    }

    /// <summary>颜色亮度 (0~255), 用于选择描边色</summary>
    private static int Luminance(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;

    /// <summary>释放字体资源</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _font.Dispose();
        base.Dispose(disposing);
    }
}
