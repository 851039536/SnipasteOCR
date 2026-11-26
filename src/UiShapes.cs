using System.Drawing.Drawing2D;

namespace SnipasteOcr;

/// <summary>
/// 界面绘制的基础形状。
///
/// 圆角矩形曾有四份逐字复制的实现 (工具栏 / 样式弹层 / 尺寸徽标 / 结果窗口 / 窗口 Region),
/// 半径收缩与退化尺寸的处理各写各的: 其中一份没有 <c>d &lt;= 0</c> 保护, 传 0 半径时
/// 会生成空路径 (不画), 而其它版本会画成直角矩形 —— 同一视觉语言出现两种结果。
/// 这里收拢为一份, 新增圆角控件直接复用, 不要再抄一遍 AddArc 序列。
/// </summary>
internal static class UiShapes
{
    /// <summary>
    /// 圆角矩形路径 (半径自动收缩到短边以内; 尺寸退化时返回直角矩形)。
    /// 角度序列 180/270/0/90 与 <see cref="GraphicsPath.CloseFigure"/> 是像素级断言依赖的,
    /// 调整前先看 ToolbarCheck / SnipSizeCheck。
    /// </summary>
    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }

        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
