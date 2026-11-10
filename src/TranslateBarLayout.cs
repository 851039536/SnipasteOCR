namespace SnipasteOcr;

/// <summary>
/// 结果窗口底部译文条的布局 (唯一来源)。
///
/// 绘制、鼠标命中、状态栏避让都必须从 <see cref="Compute"/> 取矩形 ——
/// 三处各写一份"宽度减内边距"很容易在改间距后错位: 画面上的关闭图标与
/// 实际可点区域对不上, 或者状态栏叠在译文条上。
/// 与 <see cref="SnipSizeBadge"/> 同一约定: 纯函数、不碰控件, 便于离线断言。
/// </summary>
internal static class TranslationBarLayout
{
    /// <summary>译文条与客户区边缘的间距</summary>
    public const int Margin = 8;

    /// <summary>文本框内边距</summary>
    public const int PadX = 10;
    public const int PadY = 8;

    /// <summary>标题与正文之间的间距</summary>
    public const int TitleGap = 4;

    private const int CloseSize = 16;
    private const int CloseGap = 6;

    /// <summary>「复制」按钮与关闭图标之间的间距</summary>
    public const int CopyGap = 8;

    /// <summary>「复制」按钮左右内边距 (文字宽度由调用方量好传入)</summary>
    public const int CopyPadX = 6;

    public readonly record struct Layout(
        Rectangle Box,
        Rectangle Title,
        Rectangle Text,
        Rectangle Copy,
        Rectangle Close);

    /// <summary>正文可用宽度 (客户区扣掉左右外边距与内边距)</summary>
    public static int ContentWidth(int clientWidth)
        => Math.Max(1, clientWidth - Margin * 2 - PadX * 2);

    /// <summary>
    /// 译文条最大高度: 不超过 120 逻辑像素, 也不超过图片视口的一半。
    /// 上限是必要的 —— 长译文不能把整张截图盖住, 超出的部分宁可让用户去复制。
    /// </summary>
    public static int MaxHeight(int clientHeight, int toolbarHeight)
        => Math.Min(120, Math.Max(48, Math.Max(0, clientHeight - toolbarHeight) / 2));

    /// <summary>
    /// 贴住客户区底部计算各矩形。正文高度为 0 (例如空文本) 时仍然保留一条只有标题的窄条。
    /// <paramref name="copySize"/> 是「复制」文字的测量尺寸; 按钮宽度 = 文字 + 左右内边距。
    /// </summary>
    public static Layout Compute(Size client, Size titleSize, Size copySize, Size bodySize, int maxHeight)
    {
        int width = Math.Max(1, client.Width - Margin * 2);
        int contentW = ContentWidth(client.Width);

        // 先按客户区夹紧上限, 再让正文吃掉剩余空间: 小窗口里标题与内边距优先保留
        int maxH = Math.Max(0, Math.Min(maxHeight, client.Height - Margin * 2));
        int titleH = Math.Max(0, titleSize.Height);
        int bodyAvail = Math.Max(0, maxH - PadY * 2 - titleH - TitleGap);
        int bodyH = Math.Min(Math.Max(0, bodySize.Height), bodyAvail);
        int h = Math.Min(maxH, PadY * 2 + titleH + TitleGap + bodyH);

        int top = Math.Max(0, client.Height - Margin - h);
        var box = new Rectangle(Margin, top, width, h);
        var close = new Rectangle(box.Right - PadX - CloseSize, box.Y + PadY, CloseSize, CloseSize);

        // 「复制」按钮贴在关闭图标左侧; 标题再让出它的宽度, 三者不会互相覆盖
        int copyW = Math.Max(0, copySize.Width) + CopyPadX * 2;
        var copy = new Rectangle(close.Left - CopyGap - copyW, box.Y + PadY, copyW, CloseSize);
        int titleW = Math.Max(1, Math.Min(contentW, copy.Left - (box.X + PadX) - CopyGap));
        var title = new Rectangle(box.X + PadX, box.Y + PadY, titleW, titleH);

        int textTop = title.Bottom + TitleGap;
        var text = new Rectangle(
            box.X + PadX, textTop, contentW,
            Math.Max(0, box.Bottom - PadY - textTop));

        return new Layout(box, title, text, copy, close);
    }
}
