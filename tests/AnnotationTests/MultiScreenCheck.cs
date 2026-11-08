// 校验多屏定位几何: 选区所在屏的选择, 以及工具栏夹紧到该屏 (不跨接缝)。
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class MultiScreenCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        // 常见的双屏布局: 副屏在主屏左侧 (负坐标), 接缝在 x=0
        var left = new Rectangle(-1920, 0, 1920, 1080);
        var right = new Rectangle(0, 0, 1920, 1080);
        Rectangle[] screens = [left, right];

        // ===== 1. 选区所在屏 =====
        Check(SnipGeometry.PickScreen(new Rectangle(-500, 100, 300, 200), screens) == left,
            "完整落在左屏的选区取左屏");
        Check(SnipGeometry.PickScreen(new Rectangle(300, 100, 300, 200), screens) == right,
            "完整落在右屏的选区取右屏");
        Check(SnipGeometry.PickScreen(new Rectangle(-100, 100, 300, 200), screens) == right,
            "跨接缝时取重叠面积更大的屏");
        Check(SnipGeometry.PickScreen(new Rectangle(-100, 100, 200, 200), screens) == left,
            "重叠面积相同时结果确定 (取先出现的一块)");
        Check(SnipGeometry.PickScreen(new Rectangle(-4000, 100, 100, 100), screens) == left,
            "完全不相交时取中心最近的屏");
        Check(SnipGeometry.PickScreen(new Rectangle(10, 10, 10, 10), []) == new Rectangle(10, 10, 10, 10),
            "没有屏幕信息时原样返回, 不抛异常");

        // ===== 2. 工具栏夹紧到选区所在屏 =====
        var tb = new Size(600, 42);
        const int margin = 8;

        var nearSeam = SnipGeometry.ToolbarLocation(new Rectangle(-250, 100, 200, 100), tb, left, margin);
        Check(nearSeam.X + tb.Width <= left.Right - margin,
            $"贴接缝框选时工具栏留在左屏内 (x={nearSeam.X}, 右边界={nearSeam.X + tb.Width})");
        Check(nearSeam.X >= left.Left + margin, $"工具栏不越出左屏左边缘 (x={nearSeam.X})");

        var bottom = SnipGeometry.ToolbarLocation(new Rectangle(-1500, 950, 300, 120), tb, left, margin);
        Check(bottom.Y == 950 - tb.Height - margin,
            $"选区下方放不下时翻到上方 (y={bottom.Y})");

        var tall = new Size(600, 200);
        var full = SnipGeometry.ToolbarLocation(new Rectangle(-1500, 0, 300, 1080), tall, left, margin);
        Check(full.Y == left.Bottom - tall.Height - margin,
            $"上下都放不下时贴屏幕底部内侧 (y={full.Y})");

        // 单屏行为与旧实现保持一致: 选区下方 8px, 左对齐
        var single = SnipGeometry.ToolbarLocation(
            new Rectangle(100, 100, 400, 300), tb, new Rectangle(0, 0, 1920, 1080), margin);
        Check(single == new Point(100, 408), $"单屏时工具栏贴在选区下方 (实得 {single})");

        var nearRight = SnipGeometry.ToolbarLocation(
            new Rectangle(1500, 100, 300, 100), tb, right, margin);
        Check(nearRight.X == right.Right - tb.Width - margin,
            $"单屏靠右时工具栏回退到屏内 (x={nearRight.X})");

        return runner.ToResult();
    }
}
