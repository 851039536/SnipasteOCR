namespace SnipasteOcr;

/// <summary>
/// 界面配色的唯一来源。
///
/// 同一种强调蓝曾散落在覆盖层边框、尺寸徽标、标注工具栏选中态、结果窗口选中框与
/// 菜单选中边框 (6 处), 深色底也有 38,41,47 / 24,26,30 / 20,22,26 三套。
/// 各写各的常量, 改主题时漏改一处就会出现"预览与结果窗口不是同一个蓝"。
/// 这里只收拢<b>数值完全相同</b>的颜色, 不合并相近色, 保证纯重构、观感不变。
/// </summary>
internal static class UiTheme
{
    /// <summary>强调蓝: 选区边框、选中态、确认按钮、菜单选中边框</summary>
    public static readonly Color Accent = Color.FromArgb(255, 33, 150, 243);

    /// <summary>结果窗口工具栏 / 图片背景的深色底</summary>
    public static readonly Color Surface = Color.FromArgb(255, 38, 41, 47);

    /// <summary>结果窗口画布底色 (比工具栏深一档)</summary>
    public static readonly Color Canvas = Color.FromArgb(255, 24, 26, 30);

    /// <summary>覆盖层标注工具栏底色 (半透明, 浮在截图上)</summary>
    public static readonly Color FloatingBar = Color.FromArgb(240, 38, 40, 44);

    /// <summary>框选尺寸徽标面板底色</summary>
    public static readonly Color BadgeSurface = Color.FromArgb(235, 20, 22, 26);

    /// <summary>右下角状态提示底色</summary>
    public static readonly Color StatusSurface = Color.FromArgb(190, 30, 33, 38);
}
