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
    /// <summary>强调蓝: 选区边框、选中态、菜单选中边框</summary>
    public static readonly Color Accent = Color.FromArgb(255, 33, 150, 243);

    /// <summary>
    /// 强调填充色: 需要**承载白色文字**的实心按钮 (目前只有「确认」)。
    ///
    /// 为什么不直接用 <see cref="Accent"/>: 白字压在 #2196F3 上实测对比度只有 3.12:1,
    /// 11~12px 的正文字号要求 ≥ 4.5:1 (WCAG AA)。#1976D2 是同一色相压暗一档, 白字 4.60:1。
    /// 图标属于非文本元素 (门槛 3:1), 仍可用亮一档的 Accent, 观感不会变闷。
    /// </summary>
    public static readonly Color AccentFill = Color.FromArgb(255, 25, 118, 210);

    /// <summary>结果窗口工具栏 / 图片背景的深色底</summary>
    public static readonly Color Surface = Color.FromArgb(255, 38, 41, 47);

    /// <summary>结果窗口画布底色 (比工具栏深一档); 放大镜网格底也用同一色</summary>
    public static readonly Color Canvas = Color.FromArgb(255, 24, 26, 30);

    /// <summary>主要文字 (深底上的正文/工具栏标签)</summary>
    public static readonly Color TextPrimary = Color.FromArgb(255, 226, 230, 236);

    /// <summary>次级文字 (译文面板里比正文弱一档可复制项)</summary>
    public static readonly Color TextSecondary = Color.FromArgb(255, 210, 220, 232);

    /// <summary>弱化文本 (尺寸徽标单位 / 放大镜 RGB 与坐标)</summary>
    public static readonly Color MutedText = Color.FromArgb(255, 156, 166, 180);

    /// <summary>失败态描边 / 错误文字</summary>
    public static readonly Color Danger = Color.FromArgb(255, 240, 82, 82);

    /// <summary>覆盖层标注工具栏底色 (半透明, 浮在截图上)</summary>
    public static readonly Color FloatingBar = Color.FromArgb(240, 38, 40, 44);

    /// <summary>框选尺寸徽标面板底色</summary>
    public static readonly Color BadgeSurface = Color.FromArgb(235, 20, 22, 26);

    /// <summary>右下角状态提示底色</summary>
    public static readonly Color StatusSurface = Color.FromArgb(190, 30, 33, 38);
}
