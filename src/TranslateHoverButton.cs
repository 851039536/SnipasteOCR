namespace SnipasteOcr;

/// <summary>
/// 悬停「译」按钮的布局 (纯函数, 唯一来源)。
///
/// 为什么翻译要单独的按钮: 单击已经被"选中 + 复制"占满, 再叠一个翻译就只能靠
/// 全局开关二选一, 用户会陷在"想复制还得再按 Ctrl+C"的别扭里。翻译改由悬停按钮承担后,
/// 两个动作各有唯一入口, 单击永远可预期。
///
/// 绘制、鼠标命中、粘滞判定都从 <see cref="Compute"/> 取矩形 ——
/// 三处各算一份会在改间距后出现"看得见点不到"。与 <see cref="TranslationBarLayout"/> 同一约定:
/// 逻辑像素、不碰控件、便于离线断言。
/// </summary>
internal static class TranslateHoverButton
{
    /// <summary>按钮直径 (逻辑像素)。圆形比胶囊更省宽度, 单个汉字也放得下</summary>
    public const int Size = 22;

    /// <summary>按钮与检测框之间的空隙</summary>
    public const int Gap = 6;

    /// <summary>按钮与客户区/工具栏边缘的最小距离</summary>
    public const int Edge = 4;

    /// <summary>
    /// 按钮矩形与它的"粘滞区"。
    ///
    /// 粘滞区 = 按钮 + 朝检测框方向的空隙。没有它, 指针从文字块移到按钮途中经过空隙时会
    /// 先失去 hover, 按钮随即消失 (绘图与命中都依赖 hover 行), 于是永远点不到。
    /// </summary>
    public readonly record struct Placement(Rectangle Button, Rectangle Hover);

    /// <summary>
    /// 计算按钮位置。默认贴在检测框右侧并垂直居中; 右侧越界改放左侧; 两侧都放不下时
    /// 夹进客户区 (宁可压住一点文字, 也不能画到窗口外)。
    /// </summary>
    /// <param name="blockBounds">检测框在客户区的包围盒 (逻辑像素)</param>
    /// <param name="client">客户区尺寸</param>
    /// <param name="toolbarHeight">顶部工具栏高度: 按钮不进入工具栏区域</param>
    /// <param name="bottomLimit">下边界 (通常是译文条顶边): 按钮不压在译文上</param>
    public static Placement Compute(RectangleF blockBounds, Size client, int toolbarHeight, int bottomLimit)
    {
        int minTop = toolbarHeight + Edge;
        int maxTop = Math.Max(minTop, Math.Min(client.Height, bottomLimit) - Edge - Size);
        int top = (int)Math.Round(blockBounds.Top + (blockBounds.Height - Size) / 2f);
        top = Math.Clamp(top, minTop, maxTop);

        int maxLeft = Math.Max(Edge, client.Width - Edge - Size);

        int rightOf = (int)Math.Round(blockBounds.Right) + Gap;
        if (rightOf <= maxLeft)
        {
            var button = new Rectangle(rightOf, top, Size, Size);
            return new Placement(button,
                Rectangle.FromLTRB(button.Left - Gap, button.Top, button.Right, button.Bottom));
        }

        int leftOf = (int)Math.Round(blockBounds.Left) - Gap - Size;
        if (leftOf >= Edge)
        {
            var button = new Rectangle(leftOf, top, Size, Size);
            return new Placement(button,
                Rectangle.FromLTRB(button.Left, button.Top, button.Right + Gap, button.Bottom));
        }

        // 两侧都没空间 (块几乎占满窗口宽度): 夹进客户区并朝块内粘滞
        int clamped = Math.Clamp((int)Math.Round(blockBounds.Right) - Size - Edge, Edge, maxLeft);
        var fallback = new Rectangle(clamped, top, Size, Size);
        bool stuckRight = fallback.Left >= blockBounds.Left;
        return new Placement(fallback, stuckRight
            ? Rectangle.FromLTRB(fallback.Left - Gap, fallback.Top, fallback.Right, fallback.Bottom)
            : Rectangle.FromLTRB(fallback.Left, fallback.Top, fallback.Right + Gap, fallback.Bottom));
    }
}
