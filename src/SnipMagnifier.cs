using System.Drawing;
using System.Globalization;

namespace SnipasteOcr;

/// <summary>
/// 截图覆盖层的放大镜/取色器纯逻辑: 面板定位、底图像素采样与颜色文本格式化。
///
/// 为什么独立于控件:
/// - 布局与颜色格式必须能被离线自检直接验证, 不需要创建真实窗口;
/// - 覆盖层里的放大镜只是"显示", 命中/夹紧/格式化不得各写一份。
///
/// 坐标系: <see cref="ComputeLayout"/> 使用覆盖层客户区<b>逻辑</b>像素;
/// <see cref="Sample"/> 使用 <c>_screen</c> 底图的<b>物理</b>像素。
/// </summary>
internal static class SnipMagnifier
{
    /// <summary>放大网格边长 (奇数, 保证有一个中心像素)</summary>
    public const int GridCells = 15;

    /// <summary>每个底图像素在放大镜中的显示边长 (逻辑像素)</summary>
    public const int CellSize = 10;

    /// <summary>网格下方的颜色/坐标信息区高度</summary>
    public const int InfoHeight = 52;

    /// <summary>放大镜与光标的间距</summary>
    public const int CursorGap = 18;

    /// <summary>放大镜与屏幕边缘的最小间距</summary>
    public const int Margin = 8;

    /// <summary>网格边长 (逻辑像素)</summary>
    public const int GridSize = GridCells * CellSize;

    /// <summary>整个放大镜面板的尺寸 (逻辑像素)</summary>
    public static Size PanelSize => new(GridSize, GridSize + InfoHeight);

    /// <summary>
    /// 放大镜面板左上角: 默认放在光标右下方; 贴近屏幕右/下边缘时翻到另一侧,
    /// 最后夹紧到 <paramref name="bounds"/> (光标所在屏, 客户区逻辑坐标)。
    ///
    /// 关键约束: 面板不能盖住光标 —— 否则用户看不到自己正在指的那个像素,
    /// 放大镜还会挡住鼠标命中。翻转 + 夹紧后仍尽量保持 <see cref="CursorGap"/> 的间距。
    /// </summary>
    public static Rectangle ComputeLayout(Point cursor, Rectangle bounds)
    {
        Size panel = PanelSize;

        int x = cursor.X + CursorGap;
        if (x + panel.Width > bounds.Right - Margin)
            x = cursor.X - CursorGap - panel.Width;

        int y = cursor.Y + CursorGap;
        if (y + panel.Height > bounds.Bottom - Margin)
            y = cursor.Y - CursorGap - panel.Height;

        // 屏幕比放大镜还小时 Math.Clamp 的 min > max 会抛异常: 先把上界抬到 min。
        int minX = bounds.Left + Margin;
        int maxX = Math.Max(minX, bounds.Right - panel.Width - Margin);
        int minY = bounds.Top + Margin;
        int maxY = Math.Max(minY, bounds.Bottom - panel.Height - Margin);

        return new Rectangle(
            Math.Clamp(x, minX, maxX),
            Math.Clamp(y, minY, maxY),
            panel.Width,
            panel.Height);
    }

    /// <summary>把底图坐标夹紧到有效范围; 空图 (0 边) 返回原点</summary>
    public static Point ClampSource(Point point, Size source)
        => new(
            Math.Clamp(point.X, 0, Math.Max(0, source.Width - 1)),
            Math.Clamp(point.Y, 0, Math.Max(0, source.Height - 1)));

    /// <summary>
    /// 网格第 (column, row) 格对应的底图像素坐标。
    /// 中心格 (7,7) 正好是光标所在像素; 边缘格越界由 <see cref="ClampSource"/> 兜底,
    /// 因此屏幕边缘附近也能显示完整的 15×15 网格 (边缘像素重复显示)。
    /// </summary>
    public static Point SourcePoint(Point center, int column, int row)
    {
        int half = GridCells / 2;
        return new Point(center.X + column - half, center.Y + row - half);
    }

    /// <summary>读取底图像素; 越界点先夹紧, 避免 Bitmap.GetPixel 抛异常</summary>
    public static Color Sample(Bitmap source, Point physical)
    {
        Point p = ClampSource(physical, source.Size);
        return source.GetPixel(p.X, p.Y);
    }

    /// <summary>取色器写入剪贴板的格式 (#RRGGBB, 大写、零填充)</summary>
    public static string FormatHex(Color color)
        => string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");

    /// <summary>放大镜信息区展示的 RGB 文本</summary>
    public static string FormatRgb(Color color)
        => string.Create(CultureInfo.InvariantCulture, $"RGB({color.R}, {color.G}, {color.B})");
}
