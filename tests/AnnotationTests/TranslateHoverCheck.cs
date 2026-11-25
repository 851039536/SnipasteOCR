// 校验结果窗口悬停「译」按钮的布局: 贴块、让开工具栏/译文条、越界换边、粘滞区可点到。
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class TranslateHoverCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        var client = new Size(560, 400);
        const int toolbar = 30;

        // 1. 常规: 贴在检测框右侧、垂直居中, 粘滞区覆盖块与按钮之间的空隙
        var block = new RectangleF(100, 100, 200, 20);
        var pl = TranslateHoverButton.Compute(block, client, toolbar, client.Height);
        Check(pl.Button.Left == (int)block.Right + TranslateHoverButton.Gap,
            $"按钮贴在块右侧留 {TranslateHoverButton.Gap}px (button={pl.Button}, block={block})");
        Check(pl.Button.Right <= client.Width - TranslateHoverButton.Edge,
            $"按钮不越出客户区右边 (button={pl.Button}, client={client})");
        int expectedTop = (int)Math.Round(block.Top + (block.Height - TranslateHoverButton.Size) / 2f);
        Check(pl.Button.Top == expectedTop,
            $"按钮与检测框垂直居中 (button={pl.Button}, 期望 top={expectedTop})");
        Check(pl.Hover.Contains(new Point((int)block.Right + 2, pl.Button.Top + 2)),
            "粘滞区覆盖块与按钮之间的空隙 (否则按钮会在指针穿过时消失)");
        Check(pl.Hover.Contains(pl.Button.Location), "粘滞区包含按钮本身");

        // 2. 贴顶: 不进入工具栏
        var high = TranslateHoverButton.Compute(new RectangleF(100, 31, 120, 10), client, toolbar, client.Height);
        Check(high.Button.Top >= toolbar + TranslateHoverButton.Edge,
            $"块贴着工具栏时按钮仍让开工具栏 (button={high.Button}, toolbar={toolbar})");

        // 3. 贴底/译文条: 上移而不是压住译文
        var low = TranslateHoverButton.Compute(new RectangleF(100, 390, 120, 8), client, toolbar, bottomLimit: 300);
        Check(low.Button.Bottom <= 300 - TranslateHoverButton.Edge,
            $"按钮不压在译文条上 (button={low.Button}, limit=300)");

        // 4. 右侧放不下: 换到左侧, 且粘滞区朝块的方向延伸
        var wide = new RectangleF(400, 100, 150, 20); // right=550, 右侧只剩 10px
        var flipped = TranslateHoverButton.Compute(wide, client, toolbar, client.Height);
        Check(flipped.Button.Right <= client.Width - TranslateHoverButton.Edge,
            $"右侧空间不足时按钮不越界 (button={flipped.Button}, client={client})");
        Check(flipped.Button.Right <= (int)wide.Left - TranslateHoverButton.Gap,
            $"右侧不足时改贴到块左侧 (button={flipped.Button}, block={wide})");
        Check(flipped.Hover.Contains(new Point((int)wide.Left - 2, flipped.Button.Top + 2)),
            "换到左侧后粘滞区仍覆盖块与按钮之间的空隙");

        // 5. 块占满整行: 夹进客户区并保持可点 (宁可压住一点文字, 也不能画到窗口外)
        var full = new RectangleF(0, 100, 560, 20);
        var forced = TranslateHoverButton.Compute(full, client, toolbar, client.Height);
        Check(forced.Button.Left >= TranslateHoverButton.Edge &&
              forced.Button.Right <= client.Width - TranslateHoverButton.Edge,
            $"块占满整行时按钮仍完整落在客户区内 (button={forced.Button})");
        Check(forced.Button.Height == TranslateHoverButton.Size && forced.Button.Width == TranslateHoverButton.Size,
            "按钮尺寸固定为圆形直径");

        // 6. 极窄窗口: 不产生负坐标 / 零宽命中区
        var tiny = new Size(40, 120);
        var t = TranslateHoverButton.Compute(new RectangleF(0, 20, 40, 10), tiny, toolbarHeight: 0, bottomLimit: tiny.Height);
        Check(t.Button.Left >= TranslateHoverButton.Edge && t.Button.Width == TranslateHoverButton.Size,
            $"极窄客户区仍得到可点按钮 (button={t.Button}, client={tiny})");
        Check(t.Button.Bottom <= tiny.Height, $"极窄客户区高度也不越界 (button={t.Button})");

        return runner.ToResult();
    }
}
