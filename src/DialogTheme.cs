namespace SnipasteOcr;

/// <summary>
/// 对话框的深色外观 (热键设置 / 翻译设置共用)。
///
/// 背景: 这两个对话框一度是"系统默认浅色 + 灰色标签 + 白色输入框", 与应用其余部分
/// (覆盖层/结果窗口/译文窗口/贴图) 的深色语言完全割裂 —— 同一个程序像两个年代的软件。
/// 这里只做样式收敛, 不改任何布局与交互: 控件仍由各窗体自己创建, 便于继续用绝对坐标。
///
/// 字号沿用窗体已设的 <see cref="UiFont"/>; 颜色一律取自 <see cref="UiTheme"/>,
/// 避免再出现 (60,60,60) 这类只适合浅底的硬编码。
/// </summary>
internal static class DialogTheme
{
    /// <summary>窗体底色与文字</summary>
    public static void StyleForm(Form form)
    {
        form.BackColor = UiTheme.Canvas;
        form.ForeColor = UiTheme.TextPrimary;

        // 标题栏也要深色, 否则"深色内容 + 白色标题栏"一眼割裂。
        // 该 DWM 属性必须在句柄存在后设置, 因此这里挂一次性回调。
        if (form.IsHandleCreated)
            SnipasteOcr.Native.Dwmapi.TryUseDarkTitleBar(form.Handle);
        else
            form.HandleCreated += (_, _) => SnipasteOcr.Native.Dwmapi.TryUseDarkTitleBar(form.Handle);
    }

    /// <summary>普通标签 (主要说明)</summary>
    public static void StyleLabel(Label label) => label.ForeColor = UiTheme.TextPrimary;

    /// <summary>弱化标签 (提示/状态/脚注)</summary>
    public static void StyleMuted(Label label) => label.ForeColor = UiTheme.MutedText;

    /// <summary>单行/多行文本输入框: 深底浅字 + 细边框</summary>
    public static void StyleTextBox(TextBox box)
    {
        box.BackColor = UiTheme.Surface;
        box.ForeColor = UiTheme.TextPrimary;
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    /// <summary>下拉框 (DropDown 样式下 BackColor 才会作用于输入区)</summary>
    public static void StyleComboBox(ComboBox box)
    {
        box.BackColor = UiTheme.Surface;
        box.ForeColor = UiTheme.TextPrimary;
        box.FlatStyle = FlatStyle.Flat;
    }

    /// <summary>复选框</summary>
    public static void StyleCheckBox(CheckBox box) => box.ForeColor = UiTheme.TextPrimary;

    /// <summary>
    /// 按钮: 主操作 (保存/确定) 用强调填充 + 白字, 其余用深灰底。
    /// 强调填充用 <see cref="UiTheme.AccentFill"/> 而不是 Accent —— 白字对比度 4.6:1 才达标。
    /// </summary>
    public static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? UiTheme.AccentFill : Color.FromArgb(255, 74, 79, 88);
        button.FlatAppearance.MouseOverBackColor = primary
            ? Color.FromArgb(255, 30, 136, 229)
            : Color.FromArgb(255, 60, 64, 72);
        button.BackColor = primary ? UiTheme.AccentFill : UiTheme.Surface;
        button.ForeColor = Color.White;
        button.UseVisualStyleBackColor = false;
    }
}
