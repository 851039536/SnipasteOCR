// 校验结果窗口底部译文条的布局: 贴底、夹紧在客户区内、关闭图标可点区域与绘制一致。
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class TranslateBarCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        var client = new Size(560, 400);
        var title = new Size(96, 14);
        var copy = new Size(28, 14);
        var body = new Size(420, 60);
        int maxHeight = TranslationBarLayout.MaxHeight(client.Height, toolbarHeight: 30);

        var lay = TranslationBarLayout.Compute(client, title, copy, body, maxHeight);

        Check(lay.Box.Left == TranslationBarLayout.Margin &&
              lay.Box.Right == client.Width - TranslationBarLayout.Margin,
            $"译文条左右各留 {TranslationBarLayout.Margin}px (box={lay.Box})");
        Check(lay.Box.Bottom == client.Height - TranslationBarLayout.Margin,
            $"译文条贴住客户区底部 (box={lay.Box}, client={client})");
        Check(lay.Box.Top >= 0 && lay.Box.Left >= 0 &&
              lay.Box.Right <= client.Width && lay.Box.Bottom <= client.Height,
            $"译文条完整落在客户区内 (box={lay.Box})");

        Check(lay.Close.Left >= lay.Box.Left && lay.Close.Right <= lay.Box.Right &&
              lay.Close.Top >= lay.Box.Top && lay.Close.Bottom <= lay.Box.Bottom,
            $"关闭图标在译文条内 (close={lay.Close}, box={lay.Box})");
        Check(lay.Copy.Left >= lay.Box.Left && lay.Copy.Right <= lay.Close.Left,
            $"复制按钮位于关闭图标左侧且不出条 (copy={lay.Copy}, close={lay.Close})");
        Check(lay.Title.Right <= lay.Copy.Left,
            $"标题不被复制/关闭按钮覆盖 (title={lay.Title}, copy={lay.Copy})");
        Check(lay.Copy.Width == copy.Width + TranslationBarLayout.CopyPadX * 2,
            $"复制按钮宽度包含左右内边距 (copy={lay.Copy})");
        Check(lay.Text.Top >= lay.Title.Bottom && lay.Text.Bottom <= lay.Box.Bottom,
            $"正文在标题下方且不越出条底 (text={lay.Text}, title={lay.Title})");
        Check(lay.Text.Width == TranslationBarLayout.ContentWidth(client.Width),
            $"正文宽度等于扣除内边距后的可用宽度 (text={lay.Text}, content={TranslationBarLayout.ContentWidth(client.Width)})");

        // 高度策略: 正文矮时条也矮, 正文高时被上限夹住 (不能把整张截图盖住)
        Check(lay.Box.Height < maxHeight,
            $"短正文不会撑满上限 (height={lay.Box.Height}, max={maxHeight})");

        var tall = TranslationBarLayout.Compute(client, title, copy, new Size(420, 5000), maxHeight);
        Check(tall.Box.Height == maxHeight,
            $"超长正文被最大高度夹紧 (height={tall.Box.Height}, max={maxHeight})");
        Check(tall.Text.Bottom <= tall.Box.Bottom - TranslationBarLayout.PadY,
            $"夹紧后正文不压到条的下内边距 (text={tall.Text}, box={tall.Box})");

        // 最大高度策略本身: 不超过 120, 且不超过图片视口的一半 (至少 48)
        Check(TranslationBarLayout.MaxHeight(400, 30) == 120, "大窗口上限 120");
        Check(TranslationBarLayout.MaxHeight(200, 30) == 85, "中等窗口取视口一半");
        Check(TranslationBarLayout.MaxHeight(140, 30) == 55, "小窗口仍保留可用高度");
        Check(TranslationBarLayout.MaxHeight(80, 30) == 48, "极小窗口保底 48");

        // 极窄窗口: 宽度算出来不能是 0/负数, 否则 GDI 绘制与命中矩形都会失效
        var narrow = TranslationBarLayout.Compute(new Size(40, 120), title, copy, body, maxHeight: 60);
        Check(narrow.Box.Width >= 1 && narrow.Text.Width >= 1 && narrow.Title.Width >= 1,
            $"极窄客户区仍得到正宽度 (box={narrow.Box}, text={narrow.Text}, title={narrow.Title})");

        return runner.ToResult();
    }
}
