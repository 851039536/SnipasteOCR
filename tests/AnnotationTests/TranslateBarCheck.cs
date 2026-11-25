// 校验结果窗口底部译文面板的布局: 占满整宽、贴底、各元素不互相覆盖、高度按内容并可夹紧。
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class TranslateBarCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        var client = new Size(560, 400);
        var title = new Size(96, 16);
        var copy = new Size(56, 16);
        var body = new Size(420, 40);
        const int panelH = 88;

        var lay = TranslationBarLayout.Compute(client, panelH, title, copy, body);

        // 1. 面板是"图片之外的一块区域": 占满整宽、贴底、高度就是给定值
        Check(lay.Box.Left == 0 && lay.Box.Right == client.Width,
            $"面板占满客户区宽度 (box={lay.Box}, client={client})");
        Check(lay.Box.Bottom == client.Height,
            $"面板贴住客户区底部 (box={lay.Box}, client={client})");
        Check(lay.Box.Height == panelH,
            $"面板高度等于传入值 (box={lay.Box}, 期望 {panelH})");
        Check(lay.Box.Top + lay.Box.Height == client.Height,
            $"面板顶边 = 客户区高 − 面板高 (box={lay.Box})");

        // 2. 标题行: 左内边距 16, 右侧让开「复制译文」与关闭图标
        Check(lay.Title.Left == TranslationBarLayout.PadX,
            $"标题左对齐内边距 (title={lay.Title}, pad={TranslationBarLayout.PadX})");
        Check(lay.Title.Top == lay.Box.Top + TranslationBarLayout.PadY,
            $"标题上对齐内边距 (title={lay.Title}, box={lay.Box})");
        Check(lay.Title.Right <= lay.Copy.Left,
            $"标题不被「复制译文」覆盖 (title={lay.Title}, copy={lay.Copy})");

        // 3. 操作区: 关闭贴右, 「复制译文」在其左侧, 都在面板内
        Check(lay.Close.Right == lay.Box.Right - TranslationBarLayout.PadX,
            $"关闭图标贴右内边距 (close={lay.Close}, box={lay.Box})");
        Check(lay.Close.Top == lay.Box.Top + TranslationBarLayout.PadY,
            $"关闭图标与标题同一行 (close={lay.Close}, title={lay.Title})");
        Check(lay.Copy.Right == lay.Close.Left - TranslationBarLayout.CloseGap,
            $"「复制译文」与关闭图标留 {TranslationBarLayout.CloseGap}px (copy={lay.Copy}, close={lay.Close})");
        Check(lay.Copy.Width == copy.Width + TranslationBarLayout.CopyPadX * 2,
            $"「复制译文」宽度包含左右内边距 (copy={lay.Copy})");
        Check(lay.Close.Bottom <= lay.Box.Bottom && lay.Copy.Bottom <= lay.Box.Bottom,
            $"操作区不越出面板 (copy={lay.Copy}, close={lay.Close})");

        // 4. 正文: 在标题下方, 宽度等于可用宽度, 不压到底部内边距
        Check(lay.Text.Top == lay.Title.Bottom + TranslationBarLayout.TitleGap,
            $"正文在标题下方留 {TranslationBarLayout.TitleGap}px (text={lay.Text}, title={lay.Title})");
        Check(lay.Text.Width == TranslationBarLayout.ContentWidth(client.Width),
            $"正文宽度等于扣内边距后的可用宽度 (text={lay.Text})");
        Check(lay.Text.Left == TranslationBarLayout.PadX,
            $"正文左对齐内边距 (text={lay.Text})");
        Check(lay.Text.Bottom <= lay.Box.Bottom - TranslationBarLayout.PadY,
            $"正文不压到面板下内边距 (text={lay.Text}, box={lay.Box})");

        // 5. 高度策略: 按内容量, 上限 200 且不超过图片视口一半
        Check(TranslationBarLayout.DesiredHeight(title, body)
              == TranslationBarLayout.PadY * 2 + title.Height + TranslationBarLayout.TitleGap + body.Height,
            "理想高度 = 上下内边距 + 标题 + 间距 + 正文");
        Check(TranslationBarLayout.MaxPanelHeight(900, 30) == TranslationBarLayout.MaxHeight,
            "大窗口上限取固定最大值");
        Check(TranslationBarLayout.MaxPanelHeight(200, 30) == 85,
            "中等窗口取视口一半 (170/2)");
        Check(TranslationBarLayout.MaxPanelHeight(80, 30) == TranslationBarLayout.MinHeight,
            "极小窗口保底最小高度");

        // 6. 边界: 隐藏时高度 0; 传入超高被夹到客户区高; 极窄窗口仍有正宽度
        var hidden = TranslationBarLayout.Compute(client, 0, title, copy, body);
        Check(hidden.Box.Height == 0 && hidden.Text.Height == 0,
            $"面板高度 0 时不产生负尺寸 (box={hidden.Box}, text={hidden.Text})");

        var tooTall = TranslationBarLayout.Compute(new Size(560, 100), 300, title, copy, body);
        Check(tooTall.Box.Height == 100 && tooTall.Box.Top == 0,
            $"面板高度超过客户区时被夹紧 (box={tooTall.Box})");

        var narrow = TranslationBarLayout.Compute(new Size(60, 200), 80, title, copy, body);
        Check(narrow.Box.Width == 60 && narrow.Text.Width >= 1 && narrow.Title.Width >= 1,
            $"极窄客户区仍得到正宽度 (box={narrow.Box}, text={narrow.Text}, title={narrow.Title})");

        return runner.ToResult();
    }
}
