// 校验贴图窗口的尺寸 / 缩放 / 透明度 / 缩放锚点 (纯函数)。
//
// 这些公式同时被"初始尺寸""滚轮缩放""DPI 变化"三处使用。各写一份的后果很具体:
// 150% 缩放下贴图会比截图大 1.5 倍, 或者只画出左上角 2/3 的内容。
using System.Drawing;

namespace SnipasteOcr.Tests;

internal static class PinnedLayoutCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        // ===== 1. 物理像素 → 客户区逻辑尺寸 (除以 DPI 因子) =====
        {
            Check(PinnedImageLayout.ClientSize(200, 100, 1f, 1f) == new Size(200, 100),
                "100% DPI 下 1:1 尺寸不变");
            Check(PinnedImageLayout.ClientSize(200, 100, 1.5f, 1f) == new Size(133, 67),
                $"150% DPI 下按比例缩小 ({PinnedImageLayout.ClientSize(200, 100, 1.5f, 1f)})");
            Check(PinnedImageLayout.ClientSize(200, 100, 2f, 1f) == new Size(100, 50),
                "200% DPI 下缩小一半");
            Check(PinnedImageLayout.ClientSize(200, 100, 1f, 2f) == new Size(400, 200),
                "2 倍缩放尺寸翻倍");
            Check(PinnedImageLayout.ClientSize(3, 3, 1f, 0.1f) == new Size(1, 1),
                "极小图片夹到至少 1 像素");
        }

        // ===== 2. DPI 因子非法时回退 1 =====
        {
            Check(PinnedImageLayout.ClientSize(100, 100, 0f, 1f) == new Size(100, 100),
                "DPI 因子为 0 时按 1 处理");
            Check(PinnedImageLayout.ClientSize(100, 100, -2f, 1f) == new Size(100, 100),
                "DPI 因子为负时按 1 处理");
        }

        // ===== 3. 缩放步进与夹紧 =====
        {
            Check(Math.Abs(PinnedImageLayout.StepZoom(1f, 120) - 1.1f) < 0.0001f,
                "向上滚动放大 10%");
            Check(Math.Abs(PinnedImageLayout.StepZoom(1f, -120) - (1f / 1.1f)) < 0.0001f,
                "向下滚动缩小");
            Check(Math.Abs(PinnedImageLayout.StepZoom(PinnedImageLayout.MaxZoom, 120) - PinnedImageLayout.MaxZoom) < 0.0001f,
                "超过上限时夹紧");
            Check(Math.Abs(PinnedImageLayout.StepZoom(PinnedImageLayout.MinZoom, -120) - PinnedImageLayout.MinZoom) < 0.0001f,
                "低于下限时夹紧");
            Check(Math.Abs(PinnedImageLayout.StepZoom(2f, 0) - 2f) < 0.0001f, "滚轮增量为 0 时不变");

            Check(Math.Abs(PinnedImageLayout.NormalizeZoom(0f) - 1f) < 0.0001f, "缩放 0 回退为 1");
            Check(Math.Abs(PinnedImageLayout.NormalizeZoom(float.NaN) - 1f) < 0.0001f, "缩放 NaN 回退为 1");
            Check(Math.Abs(PinnedImageLayout.NormalizeZoom(99f) - PinnedImageLayout.MaxZoom) < 0.0001f,
                "超范围缩放被夹紧");
        }

        // ===== 4. 透明度步进与夹紧 =====
        {
            Check(Math.Abs(PinnedImageLayout.StepOpacity(1.0, -1) - 0.9) < 0.0001,
                "降低透明度 0.1");
            Check(Math.Abs(PinnedImageLayout.StepOpacity(0.5, 1) - 0.6) < 0.0001,
                "提高透明度 0.1");
            Check(Math.Abs(PinnedImageLayout.StepOpacity(1.0, 1) - PinnedImageLayout.MaxOpacity) < 0.0001,
                "透明度不超过上限");
            Check(Math.Abs(PinnedImageLayout.StepOpacity(0.2, -1) - PinnedImageLayout.MinOpacity) < 0.0001,
                "透明度不低于下限 (再低就完全看不见了)");
            Check(Math.Abs(PinnedImageLayout.StepOpacity(0.5, 0) - 0.5) < 0.0001, "方向 0 时不变");
        }

        // ===== 5. 缩放锚点: 光标下的像素保持不动 =====
        {
            // 窗口 (100,100), 物理 200x200, zoom 1; 光标在窗口中心 (200,200)
            var location = new Point(100, 100);
            var cursor = new Point(200, 200);

            var anchored = PinnedImageLayout.AnchorLocation(location, cursor, 1f, 2f);
            Check(anchored == new Point(0, 0), $"放大 2 倍后窗口左上角移到 (0,0) (实得 {anchored})");

            // 复核语义: 新窗口 400x400 从 (0,0) 起, 中心仍是 (200,200), 即光标位置
            var newCenter = new Point(anchored.X + 200, anchored.Y + 200);
            Check(newCenter == cursor, $"光标下的点缩放后位置不变 (实得 {newCenter})");

            var shrunken = PinnedImageLayout.AnchorLocation(location, cursor, 2f, 1f);
            Check(shrunken == new Point(150, 150), $"缩小一半后左上角移到 (150,150) (实得 {shrunken})");

            Check(PinnedImageLayout.AnchorLocation(location, cursor, 1f, 1f) == location,
                "缩放不变时位置不变");
            Check(PinnedImageLayout.AnchorLocation(location, cursor, 0f, 2f) == location,
                "原缩放非法时保持原位");
        }

        return runner.ToResult();
    }
}
