namespace SnipasteOcr;

/// <summary>
/// 框选几何的唯一来源: 逻辑选区 → 实际输出的物理像素矩形。
///
/// 为什么抽出来: 覆盖层上报的实时宽高必须等于最终裁剪导出的宽高。
/// 两边各写一份换算, 就会在取整方式或边界裁剪漂移后出现"标签写 800 × 600、导出却是 799 × 600"
/// (与 <c>OcrText.ClusterRows</c>、<c>AnnotationToolbar.ComputeLayout</c> 同一套不变量)。
///
/// 坐标约定: 入参选区是<b>逻辑</b>像素 (WinForms 客户区), 出参是<b>物理</b>像素 (底图/导出)。
/// </summary>
internal static class SnipGeometry
{
    /// <summary>
    /// 拖拽小于该边长 (逻辑像素) 视为单击而非框选。
    /// 点击时鼠标常漂移 1~3px, 若放行微小矩形, 单击会把已有选区"抹掉", 双击确认时选区就没了。
    /// </summary>
    public const int MinDragSide = 5;

    /// <summary>是否已构成有效选区 (绘制遮罩、标注裁剪、导出共用同一判据)</summary>
    public static bool IsValidSelection(Rectangle? selection)
        => selection is { } sel && sel.Width > 2 && sel.Height > 2;

    /// <summary>
    /// 逻辑选区 → 导出的物理像素矩形; 无有效选区时回退到屏幕中央 1/2 区域。
    /// 结果保证落在底图内且宽高至少 1px —— 直接拿去 new Bitmap 不会抛异常。
    /// </summary>
    public static Rectangle ExportRect(Rectangle? selection, float scale, Size screen)
    {
        if (IsValidSelection(selection))
        {
            var sel = selection!.Value;
            int x = Math.Max(0, (int)(sel.X * scale));
            int y = Math.Max(0, (int)(sel.Y * scale));
            int w = Math.Clamp((int)Math.Round(sel.Width * scale), 1, Math.Max(1, screen.Width - x));
            int h = Math.Clamp((int)Math.Round(sel.Height * scale), 1, Math.Max(1, screen.Height - y));
            return new Rectangle(x, y, w, h);
        }

        int fw = Math.Max(200, screen.Width / 2);
        int fh = Math.Max(150, screen.Height / 2);
        return new Rectangle((screen.Width - fw) / 2, (screen.Height - fh) / 2, fw, fh);
    }

    /// <summary>
    /// 目标矩形所在屏: 取重叠面积最大的一块, 完全不相交时取中心距离最近的一块。
    ///
    /// 覆盖层横跨整个虚拟屏, 工具栏/提示条若按虚拟屏夹紧, 贴着接缝框选时会整条跑到另一块屏上。
    /// 入参与返回值只需处于同一坐标系 (这里统一用覆盖层客户区坐标)。
    /// </summary>
    public static Rectangle PickScreen(Rectangle target, IReadOnlyList<Rectangle> screens)
    {
        if (screens.Count == 0)
            return target;

        Rectangle best = screens[0];
        long bestArea = -1;
        foreach (var screen in screens)
        {
            Rectangle overlap = Rectangle.Intersect(target, screen);
            long area = (long)overlap.Width * overlap.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = screen;
            }
        }
        if (bestArea > 0)
            return best;

        // 选区完全落在屏幕外 (理论上不会发生): 取中心最近的一块, 保证结果确定
        long bestDistance = long.MaxValue;
        foreach (var screen in screens)
        {
            long dx = target.Left + target.Width / 2 - (screen.Left + screen.Width / 2);
            long dy = target.Top + target.Height / 2 - (screen.Top + screen.Height / 2);
            long distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = screen;
            }
        }
        return best;
    }

    /// <summary>
    /// 工具栏左上角: 优先选区下方, 放不下翻到选区上方, 再不行贴屏幕底部内侧;
    /// 水平与垂直都夹紧到 <paramref name="bounds"/> (选区所在屏), 而不是整个虚拟屏。
    /// </summary>
    public static Point ToolbarLocation(Rectangle selection, Size toolbar, Rectangle bounds, int margin)
    {
        int minX = bounds.Left + margin;
        int maxX = bounds.Right - toolbar.Width - margin;
        int x = maxX >= minX ? Math.Clamp(selection.Left, minX, maxX) : minX;

        int y = selection.Bottom + margin;
        if (y + toolbar.Height > bounds.Bottom - margin)
        {
            y = selection.Top - toolbar.Height - margin;
            if (y < bounds.Top + margin)
            {
                y = bounds.Bottom - toolbar.Height - margin;
                if (y < bounds.Top + margin)
                    y = bounds.Top + margin;
            }
        }
        return new Point(x, y);
    }
}