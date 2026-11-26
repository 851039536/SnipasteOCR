using SnipasteOcr.Annotations;

namespace SnipasteOcr;

/// <summary>
/// 深色 ToolStrip 的统一外观 (结果窗口与译文窗口共用)。
///
/// 这两个窗口原先各抄一份 GripStyle/Renderer/BackColor/Font/Padding 设置, 连
/// <see cref="DarkColorTable"/> 都住在结果窗口文件里 —— 一个文件"拥有"了它不负责渲染的
/// 窗口的主题。字体必须显式指定: 系统默认字体会随语言/DPI 漂移, 且直接 new Font(中文字体名)
/// 在字体缺失时会静默回退到 Microsoft Sans Serif。
///
/// 间距不统一: 结果窗口 (6,2,6,2) 的宽度参与"最小宽度 = 工具栏完整宽度"的布局预算,
/// 译文窗口 (4,1,4,1) 更紧凑, 因此 <c>padding</c> 由调用方传入。
/// </summary>
internal static class DarkToolStrip
{
    /// <summary>套用深色主题; 字号与内边距按窗口传入, 不在这里统一</summary>
    public static void Apply(ToolStrip strip, float fontSize, Padding padding)
    {
        strip.GripStyle = ToolStripGripStyle.Hidden;
        strip.RenderMode = ToolStripRenderMode.Professional;
        strip.Renderer = new DarkToolStripRenderer();
        strip.BackColor = UiTheme.Surface;
        strip.ForeColor = UiTheme.TextPrimary;
        strip.Font = UiFont.Create(fontSize);
        strip.Padding = padding;
        strip.CanOverflow = false;
    }
}

/// <summary>
/// 深色工具栏渲染器: 文字浅色, 悬停/按下用蓝色高亮
/// </summary>
internal sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    public DarkToolStripRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // 统一浅色文字 (含状态标签/缩放比例标签)
        e.TextColor = e.Item.Enabled ? UiTheme.TextPrimary : UiTheme.TextDisabled;
        base.OnRenderItemText(e);
    }

    /// <summary>
    /// 不画工具栏外边框。
    ///
    /// 踩过的坑: <see cref="ToolStripProfessionalRenderer"/> 的默认实现会在**右边缘**补一条
    /// 1px 竖线, 颜色不受 <c>ProfessionalColorTable.ToolStripBorder</c> 控制 ——
    /// 深色主题下它渲染成接近纯白 (实测 (242,242,242)), 表现为右上角一条突兀的"白色竖线"。
    /// 实测对照: 裸 ToolStrip 用 Professional 有该线, 换成 System 或不调 base 就没有。
    ///
    /// 本工具栏嵌在深色窗体顶部, 四边不需要描边, 直接短路掉最干净。
    /// </summary>
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // 有意不调用 base: 该边框只会带来右边缘的白线
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    private static readonly Color Bg = UiTheme.Surface;
    private static readonly Color Hover = Color.FromArgb(50, 54, 62);
    private static readonly Color Active = Color.FromArgb(28, 74, 128);

    public override Color ToolStripGradientBegin => Bg;
    public override Color ToolStripGradientMiddle => Bg;
    public override Color ToolStripGradientEnd => Bg;
    public override Color ToolStripBorder => Color.FromArgb(56, 60, 68);
    public override Color ToolStripDropDownBackground => Bg;
    public override Color ImageMarginGradientBegin => Bg;
    public override Color ImageMarginGradientMiddle => Bg;
    public override Color ImageMarginGradientEnd => Bg;
    public override Color SeparatorDark => Color.FromArgb(60, 64, 72);
    public override Color SeparatorLight => Color.FromArgb(60, 64, 72);
    public override Color ButtonSelectedHighlight => Hover;
    public override Color ButtonSelectedGradientBegin => Hover;
    public override Color ButtonSelectedGradientEnd => Hover;
    public override Color ButtonPressedGradientBegin => Active;
    public override Color ButtonPressedGradientEnd => Active;
    public override Color ButtonSelectedBorder => UiTheme.Accent;
    public override Color ButtonPressedBorder => UiTheme.Accent;
}
