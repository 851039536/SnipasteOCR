using SnipasteOcr.Annotations;
using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 热键设置对话框: 两个按键录制框, 分别捕获"截图识别"和"截图标注"的热键组合。
/// 录制方式: 用户点进输入框后直接按下想要的组合键 (如 Ctrl+Alt+Q), 自动解析修饰符。
/// </summary>
public sealed class HotKeyForm : Form
{
    private readonly HotKeyBox _ocrBox;
    private readonly HotKeyBox _imageBox;

    /// <summary>确定后: 截图识别的虚拟键码</summary>
    public int OcrKey => _ocrBox.VirtualKey;

    /// <summary>确定后: 截图标注的虚拟键码</summary>
    public int ImageKey => _imageBox.VirtualKey;

    /// <summary>确定后: 截图识别的修饰符</summary>
    public int OcrModifiers => _ocrBox.Modifiers;

    /// <summary>确定后: 截图标注的修饰符 (两个热键各自独立)</summary>
    public int ImageModifiers => _imageBox.Modifiers;

    public HotKeyForm(int ocrKey, int ocrMods, int imageKey, int imageMods)
    {
        Text = "SnipasteOCR - 热键设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(430, 224);
        Font = UiFont.Create(12f);
        // 与主界面同一套深色语言: 早先这里是默认浅色 WinForms 对话框, 与应用其余部分割裂
        DialogTheme.StyleForm(this);

        var tip = new Label
        {
            Text = "点击下方输入框后，直接按下想用的组合键。\n" +
                   "支持 F1-F12、字母、数字，可组合 Ctrl / Alt / Shift。\n" +
                   "仅功能键（F1-F12）可不加修饰键，其余需带修饰键。",
            Location = new Point(18, 14),
            Size = new Size(395, 64),
        };
        DialogTheme.StyleMuted(tip);

        var ocrLabel = new Label
        {
            Text = "截图识别：",
            Location = new Point(18, 92),
            Size = new Size(75, 26),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        DialogTheme.StyleLabel(ocrLabel);
        _ocrBox = new HotKeyBox
        {
            Location = new Point(100, 90),
            Size = new Size(310, 28),
        };
        _ocrBox.SetKey(ocrKey, ocrMods);

        var imageLabel = new Label
        {
            Text = "截图标注：",
            Location = new Point(18, 130),
            Size = new Size(75, 26),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        DialogTheme.StyleLabel(imageLabel);
        _imageBox = new HotKeyBox
        {
            Location = new Point(100, 128),
            Size = new Size(310, 28),
        };
        _imageBox.SetKey(imageKey, imageMods);

        var ok = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new Point(240, 176),
            Size = new Size(80, 30),
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(330, 176),
            Size = new Size(80, 30),
        };
        var reset = new Button
        {
            Text = "恢复默认",
            Location = new Point(18, 176),
            Size = new Size(90, 30),
        };
        reset.Click += (_, _) =>
        {
            _ocrBox.SetKey(AppSettings.DefaultHotKeyOcr, 0);    // F1
            _imageBox.SetKey(AppSettings.DefaultHotKeyImage, 0); // F3 (=0x72; 0x73 是 F4)
        };
        DialogTheme.StyleButton(reset);
        DialogTheme.StyleButton(ok, primary: true);
        DialogTheme.StyleButton(cancel);

        // 两个热键不能相同, 否则一个动作将永远无法触发
        ok.Click += (_, _) =>
        {
            if (_ocrBox.VirtualKey == _imageBox.VirtualKey && _ocrBox.Modifiers == _imageBox.Modifiers)
            {
                MessageBox.Show(this, "两个热键不能相同，请修改后再确定。", "SnipasteOCR",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
            }
        };

        Controls.AddRange([tip, ocrLabel, _ocrBox, imageLabel, _imageBox, reset, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>
/// 单个热键录制输入框: 获得焦点后捕获按下的组合键并显示为文本。
/// 只读 (不接收字符输入), 避免与普通文本框行为混淆。
/// </summary>
internal sealed class HotKeyBox : TextBox
{
    private int _vk;
    private int _mods;

    public HotKeyBox()
    {
        ReadOnly = true;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
        DialogTheme.StyleTextBox(this);
        ShortcutsEnabled = false;
    }

    /// <summary>虚拟键码</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int VirtualKey
    {
        get => _vk;
        set => SetKey(value, _mods);
    }

    /// <summary>修饰符位组合</summary>
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Modifiers
    {
        get => _mods;
        set => SetKey(_vk, value);
    }

    /// <summary>同时设置键与修饰符并刷新显示</summary>
    public void SetKey(int vk, int mods)
    {
        _vk = vk;
        _mods = mods;
        UpdateDisplay();
    }

    /// <summary>刷新显示文本 (不叫 Refresh, 以免隐藏 Control.Refresh)</summary>
    private void UpdateDisplay()
    {
        Text = HotKeyManager.Describe((uint)_mods, (uint)_vk);
    }

    /// <summary>
    /// 只读文本框获得焦点时, TextBox 默认全选内容 —— 深色底上会整块变成高亮蓝,
    /// 看起来像"输入框坏了"。这里取消选择, 只保留光标。
    /// </summary>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        SelectionStart = TextLength;
        SelectionLength = 0;
    }

    /// <summary>短暂显示一条提示, 随后自动恢复为当前热键</summary>
    private void FlashMessage(string message)
    {
        Text = message;
        var restore = new System.Windows.Forms.Timer { Interval = 900 };
        restore.Tick += (_, _) =>
        {
            restore.Stop();
            restore.Dispose();
            UpdateDisplay();
        };
        restore.Start();
    }

    // 捕获按键: 用 ProcessCmdKey 才能在文本框里拦到 F1/F3/Tab 等功能键
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // 单独按下的纯修饰键不构成热键, 忽略
        Keys k = keyData & Keys.KeyCode;
        if (k is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
              or Keys.None or Keys.NoName)
            return true;

        int mods = 0;
        if ((keyData & Keys.Control) != 0) mods |= (int)User32.MOD_CONTROL;
        if ((keyData & Keys.Alt) != 0) mods |= (int)User32.MOD_ALT;
        if ((keyData & Keys.Shift) != 0) mods |= (int)User32.MOD_SHIFT;

        int vk = (int)k;

        // 安全校验: 无修饰键时只允许功能键, 否则会占用普通字符影响正常打字
        bool isFunctionKey = vk is >= 0x70 and <= 0x7B;   // F1-F12
        if (mods == 0 && !isFunctionKey)
        {
            FlashMessage("需加 Ctrl/Alt/Shift 或改用 F1-F12");
            return true;
        }

        SetKey(vk, mods);
        return true;
    }
}
