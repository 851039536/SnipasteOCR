// 校验框选尺寸徽标: 尺寸单一来源 (标签数值 == 导出尺寸)、边界夹紧、度量与绘制一致。
using System.Drawing.Imaging;
using System.Reflection;
using System.Text;
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class SnipSizeCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        var screen = new Size(1920, 1080);
        var client = new Size(1920, 1080);
        var sel = new Rectangle(100, 50, 400, 300);

        // ===== 1. 尺寸单一来源: 逻辑选区 → 导出物理矩形 =====
        var at100 = SnipGeometry.ExportRect(sel, 1f, screen);
        Check(at100 == sel, $"100% 缩放下导出矩形等于选区 (实得 {at100})");

        var at150 = SnipGeometry.ExportRect(sel, 1.5f, screen);
        Check(at150 == new Rectangle(150, 75, 600, 450), $"150% 缩放下按物理像素放大 (实得 {at150})");

        var rounded = SnipGeometry.ExportRect(new Rectangle(0, 0, 401, 333), 1.5f, screen);
        Check(rounded.Width == 602 && rounded.Height == 500,
            $"物理尺寸四舍五入 (实得 {rounded.Width}x{rounded.Height})");

        var overflow = SnipGeometry.ExportRect(new Rectangle(1900, 1070, 200, 200), 1f, screen);
        Check(overflow.Width >= 1 && overflow.Height >= 1 &&
              overflow.Right <= screen.Width && overflow.Bottom <= screen.Height,
            $"越界选区裁剪到底图内 (实得 {overflow})");

        var fallback = SnipGeometry.ExportRect(null, 1f, screen);
        Check(fallback == new Rectangle(480, 270, 960, 540), $"未框选时取屏幕中央 1/2 (实得 {fallback})");

        Check(SnipGeometry.ExportRect(new Rectangle(0, 0, 2, 200), 1f, screen) == fallback,
            "2px 宽的选区视为无效, 与未框选同样兜底");
        Check(!SnipGeometry.IsValidSelection(new Rectangle(0, 0, 2, 200)) &&
              SnipGeometry.IsValidSelection(sel),
            "有效选区判据与边缘遮罩/导出共用 (>2px)");

        // ===== 2. 标签显示的就是导出物理尺寸 =====
        string Label(Size physical)
        {
            var mi = typeof(SnipSizeBadge).GetMethod("Segments", BindingFlags.NonPublic | BindingFlags.Static)!;
            var parts = (Array)mi.Invoke(null, [physical])!;
            var sb = new StringBuilder();
            foreach (object p in parts)
            {
                string t = (string)p.GetType().GetField("Item1")!.GetValue(p)!;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(t);
            }
            return sb.ToString();
        }

        Check(Label(new Size(1920, 1080)) == "1920 \u00d7 1080 px",
            $"标签文本为「宽 × 高 px」(实得 \"{Label(new Size(1920, 1080))}\")");

        var exported = SnipGeometry.ExportRect(sel, 1.5f, screen);
        Check(Label(exported.Size) == "600 \u00d7 450 px",
            $"150% 缩放下标签报物理尺寸而非逻辑尺寸 (实得 \"{Label(exported.Size)}\")");

        // ===== 3. 文本度量 =====
        var text = SnipSizeBadge.MeasureText(new Size(1920, 1080));
        Check(text.Width > 0 && text.Height > 0, $"文本度量返回正尺寸 ({text.Width}x{text.Height})");
        var wider = SnipSizeBadge.MeasureText(new Size(19200, 1080));
        Check(wider.Width > text.Width, "位数增加时度量宽度同步变宽 (分段累加未被空格吞掉)");

        // ===== 4. 布局: 永远贴在选区附近且完整留在屏幕内 =====
        var above = SnipSizeBadge.ComputeLayout(new Rectangle(100, 200, 400, 300), text, client);
        Check(above.Left == 100, $"优先与选区左对齐 (实得 {above.Left})");
        Check(above.Bottom <= 200 && 200 - above.Bottom <= 16,
            $"优先贴在选区上方 ({above.Bottom} vs 选区顶 200)");

        var topEdge = SnipSizeBadge.ComputeLayout(new Rectangle(100, 0, 400, 300), text, client);
        Check(topEdge.Top >= 4 && topEdge.Top >= 0, $"顶部贴边时移入选区内侧 (实得 top={topEdge.Top})");

        var rightEdge = SnipSizeBadge.ComputeLayout(new Rectangle(1820, 200, 100, 100), text, client);
        Check(rightEdge.Right <= client.Width - 4,
            $"靠右框选时整体回退, 不被屏幕裁掉 (right={rightEdge.Right}, 屏宽={client.Width})");

        var corner = SnipSizeBadge.ComputeLayout(new Rectangle(0, 0, 60, 60), text, client);
        Check(corner.Left >= 4 && corner.Top >= 4 &&
              corner.Right <= client.Width - 4 && corner.Bottom <= client.Height - 4,
            $"左上角贴边时四边都在屏幕内 (实得 {corner})");

        var full = SnipSizeBadge.ComputeLayout(new Rectangle(0, 0, 1920, 1080), text, client);
        Check(full.Left >= 4 && full.Top >= 4 &&
              full.Right <= client.Width - 4 && full.Bottom <= client.Height - 4,
            $"全屏框选时徽标仍完整可见 (实得 {full})");

        // 客户区比徽标还小时不能抛 (Math.Clamp 上下界翻转的经典坑)
        Rectangle tinyBox;
        bool tinyOk;
        try
        {
            tinyBox = SnipSizeBadge.ComputeLayout(new Rectangle(0, 0, 80, 40), text, new Size(80, 40));
            tinyOk = true;
        }
        catch
        {
            tinyBox = Rectangle.Empty;
            tinyOk = false;
        }
        Check(tinyOk && tinyBox.Left >= 0 && tinyBox.Top >= 0,
            $"可用区小于徽标时不抛异常且坐标非负 (实得 {tinyBox})");

        // ===== 5. 绘制: 面板覆盖布局框, 内容不溢出, 数字随尺寸变化 =====
        using var canvas = new Bitmap(700, 500);
        void Paint(Rectangle selection, Size physical, Bitmap target)
        {
            using var g = Graphics.FromImage(target);
            g.Clear(Color.White);
            SnipSizeBadge.Draw(g, selection, physical, target.Size);
        }

        Paint(new Rectangle(50, 150, 300, 200), new Size(1920, 1080), canvas);
        var expected = SnipSizeBadge.ComputeLayout(new Rectangle(50, 150, 300, 200), text, canvas.Size);
        Rectangle bbox = ChangedBounds(canvas, Color.White);

        Check(bbox.Width > 0 && bbox.Height > 0, "徽标产生了像素");
        Check(Rectangle.Inflate(expected, 2, 2).Contains(bbox),
            $"绘制内容不超出布局框 (bbox={bbox}, layout={expected})");
        Check(bbox.Width >= expected.Width - 2 && bbox.Height >= expected.Height - 2,
            $"面板完整覆盖布局框 (bbox={bbox}, layout={expected})");

        var panelCenter = canvas.GetPixel(expected.Left + 3, expected.Top + expected.Height / 2);
        Check(panelCenter.R < 90 && panelCenter.G < 90 && panelCenter.B < 90,
            $"面板底色为深色, 亮色截图上可读 ({panelCenter})");

        using var other = new Bitmap(700, 500);
        Paint(new Rectangle(50, 150, 300, 200), new Size(1280, 720), other);
        Check(DiffCount(canvas, other) > 0, "不同宽高画出不同内容 (数值确实参与绘制)");

        return runner.ToResult();
    }

    /// <summary>非背景像素的包围盒 (用于断言"画出来的东西都在布局框里")</summary>
    private static Rectangle ChangedBounds(Bitmap bmp, Color background)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).ToArgb() == background.ToArgb())
                    continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }

        return maxX < 0 ? Rectangle.Empty : new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>两张同尺寸位图的差异像素数</summary>
    private static int DiffCount(Bitmap a, Bitmap b)
    {
        int diff = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb())
                    diff++;
        return diff;
    }
}