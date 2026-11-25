namespace SnipasteOcr;

/// <summary>
/// 结果窗口底部译文面板的布局 (纯函数, 唯一来源)。
///
/// 面板<b>不覆盖图片</b>: 图片视口 = 客户区 − 工具栏 − 面板, 结果窗口在需要时向下加高。
/// 早先的版本是贴在图片上的浮层, 会压住截图最后几行 —— 译文再好看, 遮住原文就是错的。
///
/// 绘制、鼠标命中、状态栏避让都必须从 <see cref="Compute"/> 取矩形 ——
/// 三处各写一份"宽度减内边距"很容易在改间距后错位: 画面上的关闭图标与
/// 实际可点区域对不上, 或者状态栏叠在面板上。
/// 与 <see cref="TranslateHoverButton"/> 同一约定: 纯函数、不碰控件, 便于离线断言。
/// </summary>
internal static class TranslationBarLayout
{
    /// <summary>左右内边距。16 而不是 8: 面板是窗口的一部分, 需要一点呼吸感</summary>
    public const int PadX = 16;

    /// <summary>上下内边距</summary>
    public const int PadY = 12;

    /// <summary>标题行与正文之间的间距</summary>
    public const int TitleGap = 8;

    /// <summary>面板最矮高度: 单行译文也要站得住</summary>
    public const int MinHeight = 64;

    /// <summary>面板最高高度: 再长也不让译文把截图挤没</summary>
    public const int MaxHeight = 200;

    private const int CloseSize = 18;

    /// <summary>「复制译文」与关闭图标之间的间距</summary>
    public const int CloseGap = 10;

    /// <summary>「复制译文」与关闭图标之间的间距</summary>
    public const int CopyGap = 12;

    /// <summary>「复制译文」按钮左右内边距 (文字宽度由调用方量好传入)</summary>
    public const int CopyPadX = 8;

    public readonly record struct Layout(
        Rectangle Box,
        Rectangle Title,
        Rectangle Text,
        Rectangle Copy,
        Rectangle Close);

    /// <summary>正文可用宽度 (面板占满客户区宽度, 只扣左右内边距)</summary>
    public static int ContentWidth(int clientWidth)
        => Math.Max(1, clientWidth - PadX * 2);

    /// <summary>
    /// 面板高度上限: 不超过 <see cref="MaxHeight"/>, 也不超过图片视口的一半 ——
    /// 译文再长也不能把截图挤成一条缝。
    /// </summary>
    public static int MaxPanelHeight(int clientHeight, int toolbarHeight)
    {
        int viewport = Math.Max(0, clientHeight - toolbarHeight);
        return Math.Min(MaxHeight, Math.Max(MinHeight, viewport / 2));
    }

    /// <summary>按内容量出的理想高度 (未夹紧)</summary>
    public static int DesiredHeight(Size titleSize, Size bodySize)
        => PadY * 2 + Math.Max(0, titleSize.Height) + TitleGap + Math.Max(0, bodySize.Height);

    /// <summary>
    /// 贴住客户区底部、占满整宽计算各矩形。
    /// <paramref name="panelHeight"/> 由调用方按内容算好 (见 <see cref="DesiredHeight"/>) 并夹紧。
    /// </summary>
    public static Layout Compute(Size client, int panelHeight, Size titleSize, Size copySize, Size bodySize)
    {
        int width = Math.Max(1, client.Width);
        int h = Math.Clamp(panelHeight, 0, Math.Max(0, client.Height));
        var box = new Rectangle(0, client.Height - h, width, h);

        // 关闭图标贴右上角, 「复制译文」在它左侧; 标题再让出两者宽度
        var close = new Rectangle(box.Right - PadX - CloseSize, box.Y + PadY, CloseSize, CloseSize);
        int copyW = Math.Max(0, copySize.Width) + CopyPadX * 2;
        var copy = new Rectangle(close.Left - CloseGap - copyW, box.Y + PadY, copyW, CloseSize);
        int titleW = Math.Max(1, Math.Min(ContentWidth(client.Width), copy.Left - (box.X + PadX) - CopyGap));
        var title = new Rectangle(box.X + PadX, box.Y + PadY, titleW, Math.Max(0, titleSize.Height));

        int textTop = box.Y + PadY + Math.Max(0, titleSize.Height) + TitleGap;
        var text = new Rectangle(
            box.X + PadX, textTop, ContentWidth(client.Width),
            Math.Max(0, box.Bottom - PadY - textTop));

        return new Layout(box, title, text, copy, close);
    }
}
