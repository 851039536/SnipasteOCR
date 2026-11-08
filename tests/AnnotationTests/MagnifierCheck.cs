// 校验放大镜/取色器纯逻辑: 面板不盖住光标、贴边翻转、负坐标多屏、边缘采样与颜色格式。
using System.Drawing;
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class MagnifierCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        var screen = new Rectangle(0, 0, 1920, 1080);
        Size panel = SnipMagnifier.PanelSize;

        // ===== 1. 面板尺寸与网格 =====
        Check(SnipMagnifier.GridCells % 2 == 1 && SnipMagnifier.GridCells > 1,
            "放大网格为奇数格, 保证有唯一中心像素");
        Check(panel.Width == SnipMagnifier.GridSize &&
              panel.Height == SnipMagnifier.GridSize + SnipMagnifier.InfoHeight &&
              panel.Width > 0 && panel.Height > 0,
            $"面板尺寸由网格 + 信息区构成 (实得 {panel.Width}x{panel.Height})");

        // ===== 2. 默认放在光标右下方, 且不盖住光标 =====
        var cursor = new Point(500, 400);
        var box = SnipMagnifier.ComputeLayout(cursor, screen);
        Check(box.X > cursor.X && box.Y > cursor.Y,
            $"默认位于光标右下方 (实得 {box.Location})");
        Check(!box.Contains(cursor), "放大镜不盖住光标");
        Check(screen.Contains(box), $"面板完整落在屏幕内 (实得 {box})");

        // ===== 3. 贴右/下边缘时翻到另一侧 =====
        var right = SnipMagnifier.ComputeLayout(new Point(screen.Right - 20, 400), screen);
        Check(right.Right <= screen.Right - 20 - SnipMagnifier.CursorGap && screen.Contains(right),
            $"贴右边缘时翻到光标左侧 (实得 {right})");

        var bottom = SnipMagnifier.ComputeLayout(new Point(500, screen.Bottom - 10), screen);
        Check(bottom.Bottom <= screen.Bottom - 10 - SnipMagnifier.CursorGap && screen.Contains(bottom),
            $"贴下边缘时翻到光标上方 (实得 {bottom})");

        var corner = SnipMagnifier.ComputeLayout(new Point(2, 2), screen);
        Check(screen.Contains(corner) && !corner.Contains(new Point(2, 2)),
            $"左上角贴边时仍在屏内且不盖住光标 (实得 {corner})");

        // ===== 4. 多屏负坐标: 夹紧范围必须是光标所在屏, 不能是 0..虚拟屏 =====
        var leftScreen = new Rectangle(-1920, 0, 1920, 1080);
        var negative = SnipMagnifier.ComputeLayout(new Point(-100, 600), leftScreen);
        Check(leftScreen.Contains(negative) && !negative.Contains(new Point(-100, 600)),
            $"负坐标副屏上仍完整可见且不盖住光标 (实得 {negative})");

        // 遍历常见位置: 任何光标位置都不能被面板覆盖, 也不能越出所在屏
        bool coversCursor = false;
        bool outside = false;
        for (int x = 0; x < screen.Width; x += 137)
        {
            for (int y = 0; y < screen.Height; y += 149)
            {
                var p = new Point(x, y);
                var layout = SnipMagnifier.ComputeLayout(p, screen);
                coversCursor |= layout.Contains(p);
                outside |= !screen.Contains(layout);
            }
        }
        Check(!coversCursor, "任意光标位置上面板都不盖住光标");
        Check(!outside, "任意光标位置上面板都完整落在屏幕内");

        // ===== 5. 客户区比面板还小时不能抛 (Math.Clamp 上下界翻转的经典坑) =====
        Rectangle tiny = Rectangle.Empty;
        bool tinyOk;
        try
        {
            tiny = SnipMagnifier.ComputeLayout(new Point(10, 10), new Rectangle(0, 0, 80, 40));
            tinyOk = true;
        }
        catch
        {
            tinyOk = false;
        }
        Check(tinyOk, "客户区过小时布局不抛异常");
        Check(!tiny.IsEmpty, "极端客户区仍返回可绘制矩形");

        // ===== 6. 网格采样中心与四角 =====
        var center = new Point(100, 200);
        Check(SnipMagnifier.SourcePoint(center, 7, 7) == center, "中心格对应光标像素");
        Check(SnipMagnifier.SourcePoint(center, 0, 0) == new Point(93, 193), "左上角格偏移 -7");
        Check(SnipMagnifier.SourcePoint(center, 14, 14) == new Point(107, 207), "右下角格偏移 +7");

        // ===== 7. 越界采样夹紧到底图边缘 =====
        Check(SnipMagnifier.ClampSource(new Point(-1, 5000), new Size(1920, 1080)) == new Point(0, 1079),
            "越界点夹紧到右下有效边界");
        Check(SnipMagnifier.ClampSource(new Point(5, 5), Size.Empty) == Point.Empty,
            "空底图采样回退到原点, 不产生负索引");

        using (var bmp = new Bitmap(2, 2))
        {
            bmp.SetPixel(0, 0, Color.Red);
            bmp.SetPixel(1, 0, Color.Green);
            bmp.SetPixel(0, 1, Color.Blue);
            bmp.SetPixel(1, 1, Color.White);

            Check(SnipMagnifier.Sample(bmp, new Point(1, 0)).ToArgb() == Color.Green.ToArgb(),
                "采样命中指定像素");
            Check(SnipMagnifier.Sample(bmp, new Point(-5, -5)).ToArgb() == Color.Red.ToArgb(),
                "左上越界采样夹紧到 (0,0)");
            Check(SnipMagnifier.Sample(bmp, new Point(99, 99)).ToArgb() == Color.White.ToArgb(),
                "右下越界采样夹紧到最后像素");
        }

        // ===== 8. 颜色格式 =====
        var color = Color.FromArgb(255, 1, 171, 255);
        Check(SnipMagnifier.FormatHex(color) == "#01ABFF", $"HEX 大写且零填充 (实得 {SnipMagnifier.FormatHex(color)})");
        Check(SnipMagnifier.FormatRgb(color) == "RGB(1, 171, 255)",
            $"RGB 文本格式固定 (实得 {SnipMagnifier.FormatRgb(color)})");
        Check(SnipMagnifier.FormatHex(Color.Black) == "#000000", "黑色输出 #000000 而不是 #000");
        Check(SnipMagnifier.FormatHex(Color.White) == "#FFFFFF", "白色输出 #FFFFFF");

        return runner.ToResult();
    }
}
