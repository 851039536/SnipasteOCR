namespace SnipasteOcr.Annotations;

/// <summary>
/// 标注可选颜色与线宽 (唯一来源)。
///
/// 工具栏的「样式」按钮、弹出面板、<c>CycleColor</c> 与离线断言都从这里取 ——
/// 早先调色板与线宽表写在 <see cref="AnnotationToolbar"/> 内部, 弹层要再抄一份就会出现
/// "面板里选的颜色与工具栏预览不一致"这类漂移。
/// </summary>
internal static class AnnotationPalette
{
    /// <summary>可选颜色 (与截图工具常见配色一致)</summary>
    public static readonly Color[] Colors =
    [
        Color.FromArgb(255, 235, 59, 36),    // 红
        Color.FromArgb(255, 255, 152, 0),    // 橙
        Color.FromArgb(255, 255, 214, 0),    // 黄
        Color.FromArgb(255, 76, 175, 80),    // 绿
        UiTheme.Accent,                      // 蓝
        Color.FromArgb(255, 156, 39, 176),   // 紫
        Color.FromArgb(255, 255, 255, 255),  // 白
        Color.FromArgb(255, 24, 24, 24),     // 黑
    ];

    /// <summary>可选线宽 (逻辑像素; 马克笔会按自身规则再放大)</summary>
    public static readonly float[] Widths = [2f, 4f, 7f];

    /// <summary>颜色在表中的下标; 表外颜色 (旧配置/外部传入) 返回 -1</summary>
    public static int IndexOf(Color color)
    {
        for (int i = 0; i < Colors.Length; i++)
        {
            if (Colors[i].ToArgb() == color.ToArgb())
                return i;
        }
        return -1;
    }

    /// <summary>线宽下标; 与任一项相差小于 0.01 视为命中, 否则 -1</summary>
    public static int WidthIndexOf(float width)
    {
        for (int i = 0; i < Widths.Length; i++)
        {
            if (Math.Abs(Widths[i] - width) < 0.01f)
                return i;
        }
        return -1;
    }
}
