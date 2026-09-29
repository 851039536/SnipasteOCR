namespace SnipasteOcr.Annotations;

/// <summary>
/// 标注工具类型。工具栏按钮与键位 (1..6) 一一对应。
/// </summary>
public enum AnnotationTool
{
    /// <summary>不标注, 仅框选 (默认)</summary>
    None = 0,

    /// <summary>矩形框</summary>
    Rectangle = 1,

    /// <summary>椭圆框</summary>
    Ellipse = 2,

    /// <summary>箭头</summary>
    Arrow = 3,

    /// <summary>自由画笔</summary>
    Pen = 4,

    /// <summary>马赛克 (像素化打码)</summary>
    Mosaic = 5,

    /// <summary>文字</summary>
    Text = 6,
}

/// <summary>工具枚举的中文名/快捷键等展示元数据</summary>
public static class AnnotationToolInfo
{
    /// <summary>工具栏显示名</summary>
    public static string DisplayName(AnnotationTool tool) => tool switch
    {
        AnnotationTool.Rectangle => "矩形",
        AnnotationTool.Ellipse => "椭圆",
        AnnotationTool.Arrow => "箭头",
        AnnotationTool.Pen => "画笔",
        AnnotationTool.Mosaic => "马赛克",
        AnnotationTool.Text => "文字",
        _ => "选择",
    };

    /// <summary>快捷键数字 (1..6), None 返回 0</summary>
    public static int ShortcutDigit(AnnotationTool tool) => (int)tool;

    /// <summary>按数字键反查工具 (1..6), 其它返回 None</summary>
    public static AnnotationTool FromShortcutDigit(int digit) => digit switch
    {
        1 => AnnotationTool.Rectangle,
        2 => AnnotationTool.Ellipse,
        3 => AnnotationTool.Arrow,
        4 => AnnotationTool.Pen,
        5 => AnnotationTool.Mosaic,
        6 => AnnotationTool.Text,
        _ => AnnotationTool.None,
    };

    /// <summary>该工具是否为「按住拖拽出形状」的类型</summary>
    public static bool IsDragShape(AnnotationTool tool) =>
        tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse
             or AnnotationTool.Arrow or AnnotationTool.Mosaic;

    /// <summary>所有可选工具, 供工具栏按顺序创建按钮</summary>
    public static readonly AnnotationTool[] AllTools =
    [
        AnnotationTool.Rectangle,
        AnnotationTool.Ellipse,
        AnnotationTool.Arrow,
        AnnotationTool.Pen,
        AnnotationTool.Mosaic,
        AnnotationTool.Text,
    ];
}
