using System.Drawing;

namespace SnipasteOcr.Annotations;

/// <summary>选区的 8 个调整手柄</summary>
public enum SelectionHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>
/// 选区调整手柄的命中与拖拽几何 (纯函数, 逻辑像素)。
///
/// 为什么不复用标注的 <see cref="AnnotationHit"/>: 两者坐标系与语义不同 ——
/// 标注几何存物理像素且带旋转描边, 选区几何存逻辑像素且是正矩形。
/// 但<b>导出换算仍然只有一条路</b>: 这里只产出新的 <see cref="Rectangle"/>,
/// 交给 <see cref="SnipGeometry.ExportRect"/> 做"逻辑→物理", 不得自行再算一份。
/// </summary>
public static class SelectionResize
{
    /// <summary>手柄命中矩形 (单位: 逻辑像素)</summary>
    /// <param name="selection">当前选区</param>
    /// <param name="handle">手柄</param>
    /// <param name="size">手柄边长</param>
    /// <param name="grow">向外扩张的容差, 让 9px 的手柄更容易点中</param>
    public static Rectangle HandleBounds(Rectangle selection, SelectionHandle handle, int size, int grow = 0)
    {
        if (handle == SelectionHandle.None)
            return Rectangle.Empty;

        int half = size / 2;
        int cx = selection.Left + selection.Width / 2;
        int cy = selection.Top + selection.Height / 2;

        var (x, y) = handle switch
        {
            SelectionHandle.TopLeft => (selection.Left, selection.Top),
            SelectionHandle.Top => (cx, selection.Top),
            SelectionHandle.TopRight => (selection.Right, selection.Top),
            SelectionHandle.Right => (selection.Right, cy),
            SelectionHandle.BottomRight => (selection.Right, selection.Bottom),
            SelectionHandle.Bottom => (cx, selection.Bottom),
            SelectionHandle.BottomLeft => (selection.Left, selection.Bottom),
            SelectionHandle.Left => (selection.Left, cy),
            _ => (int.MinValue, int.MinValue),
        };

        if (x == int.MinValue)
            return Rectangle.Empty;

        int pad = Math.Max(0, grow);
        return Rectangle.FromLTRB(
            x - half - pad, y - half - pad,
            x - half + size + pad, y - half + size + pad);
    }

    /// <summary>
    /// 命中哪个手柄。
    /// 角优先于边: 角与边的命中区在选区内角处重叠, 若边优先, 用户永远拖不到四个角。
    /// </summary>
    public static SelectionHandle HitHandle(Rectangle selection, Point logical, int size, int grow = 0)
    {
        ReadOnlySpan<SelectionHandle> priority =
        [
            SelectionHandle.TopLeft,
            SelectionHandle.TopRight,
            SelectionHandle.BottomRight,
            SelectionHandle.BottomLeft,
            SelectionHandle.Top,
            SelectionHandle.Right,
            SelectionHandle.Bottom,
            SelectionHandle.Left,
        ];

        foreach (var handle in priority)
        {
            var box = HandleBounds(selection, handle, size, grow);
            if (!box.IsEmpty && box.Contains(logical))
                return handle;
        }
        return SelectionHandle.None;
    }

    /// <summary>
    /// 按手柄把指针位置映射成新选区。
    ///
    /// 两条必须守住的规则:
    /// 1. 拖过对边时归一化为正矩形 (否则出现负宽, 后续 ExportRect 会算出空图);
    /// 2. 边长夹到 <paramref name="minSide"/>, 且<b>钉住不动的那条边</b> ——
    ///    从左上角往右下拖过头时, 右/下边界应保持原位, 而不是整块选区被推着跑。
    /// </summary>
    public static Rectangle Apply(Rectangle start, SelectionHandle handle, Point pointer, int minSide)
    {
        if (handle == SelectionHandle.None)
            return start;

        minSide = Math.Max(1, minSide);

        int l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;

        bool movesLeft = handle is SelectionHandle.TopLeft or SelectionHandle.Left or SelectionHandle.BottomLeft;
        bool movesRight = handle is SelectionHandle.TopRight or SelectionHandle.Right or SelectionHandle.BottomRight;
        bool movesTop = handle is SelectionHandle.TopLeft or SelectionHandle.Top or SelectionHandle.TopRight;
        bool movesBottom = handle is SelectionHandle.BottomLeft or SelectionHandle.Bottom or SelectionHandle.BottomRight;

        if (movesLeft) l = pointer.X;
        if (movesRight) r = pointer.X;
        if (movesTop) t = pointer.Y;
        if (movesBottom) b = pointer.Y;

        int left = Math.Min(l, r), right = Math.Max(l, r);
        int top = Math.Min(t, b), bottom = Math.Max(t, b);

        if (right - left < minSide)
        {
            if (movesLeft && !movesRight) left = right - minSide;
            else right = left + minSide;
        }

        if (bottom - top < minSide)
        {
            if (movesTop && !movesBottom) top = bottom - minSide;
            else bottom = top + minSide;
        }

        return Rectangle.FromLTRB(left, top, right, bottom);
    }
}
