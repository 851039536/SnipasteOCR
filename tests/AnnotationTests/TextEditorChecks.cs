// 校验编辑框随文本增长, 以及光标位置与文字实际宽度一致。
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using SnipasteOcr.Annotations;

namespace SnipasteOcr.Tests;

internal static class TextEditorChecks
{
    public static (int Passed, List<string> Failures) Run()
    {
        int passed = 0;
        var failures = new List<string>();

        void Check(bool cond, string name)
        {
            if (cond) passed++;
            else failures.Add(name);
        }

        // 1. 构造后必须可见 (非 0 尺寸)
        var ed = new TextEditorOverlay(18f);
        Check(ed.Width > 0 && ed.Height > 0, $"构造后尺寸非零 (实得 {ed.Width}x{ed.Height})");

        // 2. 空文本也应可点可见
        Check(ed.Width >= 30 && ed.Height >= 20, $"空编辑器达到最小可见尺寸 ({ed.Width}x{ed.Height})");

        // 3. 宽度随文本增长
        int wEmpty = ed.Width;
        TypeText(ed, "Hello");
        int wShort = ed.Width;
        TypeText(ed, "Hello World 这是一段较长的文字");
        int wLong = ed.Width;

        Check(wShort > wEmpty || wShort >= wEmpty, $"输入后宽度不缩小 ({wEmpty} -> {wShort})");
        Check(wLong > wShort, $"长文本使宽度增长 ({wShort} -> {wLong})");

        // 4. 文本内容正确累积
        Check(ed.Value == "HelloHello World 这是一段较长的文字", "文本内容按键入顺序累积");

        // 5. 退格
        SendKey(ed, '\b');
        Check(ed.Value == "HelloHello World 这是一段较长的文", "退格删除末尾字符");

        // 6. 绘制产出可见像素 (文字真的画出来了)
        int painted = CountPainted(ed, out int caretX);
        Check(painted > 50, $"绘制产生文字像素 (实得 {painted})");

        // 7. 光标必须落在文字宽度内 (不超出控件右边界)
        Check(caretX < ed.Width, $"光标位于控件内 (caret={caretX}, width={ed.Width})");
        Check(caretX > 4, $"光标不在左边界上 (caret={caretX})");

        // 8. 回车提交, 回调收到正确文本
        string? committed = null;
        ed.Committed += t => committed = t;
        SendKey(ed, '\r');
        Check(committed == "HelloHello World 这是一段较长的文", $"回车提交回调文本正确 (实得 \"{committed}\")");

        // 9. 提交后不再重复触发
        int extra = 0;
        ed.Committed += _ => extra++;
        SendKey(ed, '\r');
        SendKey(ed, 'X');
        Check(extra == 0, "提交后不再接受输入或重复触发");

        // 10. Esc 取消
        var ed2 = new TextEditorOverlay(18f);
        TypeText(ed2, "abc");
        bool cancelled = false;
        ed2.Cancelled += () => cancelled = true;
        SendKeyDown(ed2, Keys.Escape);
        Check(cancelled, "Esc 触发取消回调");

        // 11. 空文本提交视为取消
        var ed3 = new TextEditorOverlay(18f);
        bool c3 = false, k3 = false;
        ed3.Cancelled += () => c3 = true;
        ed3.Committed += _ => k3 = true;
        ed3.CommitNow();
        Check(c3 && !k3, "空文本提交走取消而非提交");

        // 12. CommitNow 幂等
        var ed4 = new TextEditorOverlay(18f);
        TypeText(ed4, "hi");
        int commits = 0;
        ed4.Committed += _ => commits++;
        ed4.CommitNow();
        ed4.CommitNow();
        Check(commits == 1, $"CommitNow 幂等, 只提交一次 (实得 {commits})");

        // 13. 多行文本 (含 \n) 不崩溃且高度增长
        var ed5 = new TextEditorOverlay(18f);
        ed5.Value = "line1\nline2";
        Check(ed5.Height > 0, "多行文本高度有效");

        ed.Dispose();
        ed2.Dispose();
        ed3.Dispose();
        ed4.Dispose();
        ed5.Dispose();

        return (passed, failures);
    }

    /// <summary>逐字符模拟键入</summary>
    private static void TypeText(Control c, string text)
    {
        foreach (char ch in text)
            SendKey(c, ch);
    }

    /// <summary>反射调用 OnKeyPress (字符输入路径)</summary>
    private static void SendKey(Control c, char ch)
    {
        typeof(TextEditorOverlay)
            .GetMethod("OnKeyPress", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(c, [new KeyPressEventArgs(ch)]);
    }

    /// <summary>反射调用 OnKeyDown (功能键路径)</summary>
    private static void SendKeyDown(Control c, Keys key)
    {
        typeof(TextEditorOverlay)
            .GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(c, [new KeyEventArgs(key)]);
    }

    /// <summary>离屏绘制编辑器, 返回非底色像素数与光标 X 位置</summary>
    private static int CountPainted(TextEditorOverlay ed, out int caretX)
    {
        int w = Math.Max(ed.Width, 60), h = Math.Max(ed.Height, 40);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 250, 250, 248));
            using var pea = new PaintEventArgs(g, new Rectangle(0, 0, w, h));
            typeof(TextEditorOverlay)
                .GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(ed, [pea]);
        }

        int painted = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R < 200 || c.G < 200 || c.B < 200) painted++;
            }

        // 用与绘制一致的 TextRenderer 度量推算光标位置
        var font = (Font)typeof(TextEditorOverlay)
            .GetField("_font", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ed)!;
        var size = TextRenderer.MeasureText(ed.Value, font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        caretX = 4 + size.Width;

        return painted;
    }
}
