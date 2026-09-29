// 文字编辑框的诊断: 检查尺寸计算、绘制输出、以及键入后是否真的产生可见像素。
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using SnipasteOcr.Annotations;

namespace SnipasteOcr.Tests;

internal static class TextEditorDiag
{
    public static void Run()
    {
        Console.WriteLine("=== TextEditorOverlay 诊断 ===");

        var ed = new TextEditorOverlay(18f);
        Console.WriteLine($"构造后尺寸: {ed.Width} x {ed.Height}");

        // 1. 模拟键入 "AB"
        SimulateKeyPress(ed, 'A');
        SimulateKeyPress(ed, 'B');
        Console.WriteLine($"键入 \"AB\" 后 Value = \"{ed.Value}\", 尺寸 = {ed.Width} x {ed.Height}");

        // 2. 渲染到离屏位图, 统计非透明/非背景像素
        int w = Math.Max(ed.Width, 60), h = Math.Max(ed.Height, 40);
        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(255, 250, 250, 248)); // 浅色底, 便于发现深色文字
            using var pea = new PaintEventArgs(g, new Rectangle(0, 0, w, h));
            typeof(TextEditorOverlay)
                .GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(ed, [pea]);
        }

        int darkPixels = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                // 文字色是红 (235,59,36), 描边是黑/白; 统计"明显偏离浅色底"的像素
                if (c.R < 200 || c.G < 200 || c.B < 200) darkPixels++;
            }
        Console.WriteLine($"绘制后非底色像素数 = {darkPixels} (期望 > 0, 否则文字没画出来)");

        // 3. 检查字体度量
        var font = (Font)typeof(TextEditorOverlay)
            .GetField("_font", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ed)!;
        Console.WriteLine($"字体: {font.Name}, Size={font.Size}, Unit={font.Unit}, Height={font.Height}");

        using (var g = Graphics.FromImage(bmp))
        {
            var size = g.MeasureString("AB", font);
            Console.WriteLine($"MeasureString(\"AB\") = {size.Width:F1} x {size.Height:F1}");
        }

        // 4. 打印每种候选字体能否创建 + 度量是否合理
        Console.WriteLine();
        Console.WriteLine("=== 字体可用性 ===");
        foreach (var name in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimSun", "Segoe UI" })
        {
            try
            {
                using var f = new Font(name, 18f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var g = Graphics.FromImage(new Bitmap(10, 10));
                var sz = g.MeasureString("测试AB", f);
                Console.WriteLine($"  {name,-20} -> 实际 {f.Name,-20} 度量 {sz.Width:F1}x{sz.Height:F1}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name,-20} -> 失败: {ex.Message}");
            }
        }

        // 5. ResizeToContent 是否把高度算成 0 (会被 WinForms 忽略, 导致控件不可见)
        Console.WriteLine();
        Console.WriteLine("=== ResizeToContent 边界 ===");
        var ed2 = new TextEditorOverlay(18f);
        Console.WriteLine($"  空文本: {ed2.Width} x {ed2.Height}");
        SimulateKeyPress(ed2, 'W');
        Console.WriteLine($"  单字符: {ed2.Width} x {ed2.Height}");

        ed.Dispose();
        ed2.Dispose();
    }

    /// <summary>反射调用 OnKeyPress, 模拟一次字符输入</summary>
    private static void SimulateKeyPress(Control c, char ch)
    {
        var m = typeof(TextEditorOverlay)
            .GetMethod("OnKeyPress", BindingFlags.NonPublic | BindingFlags.Instance)!;
        m.Invoke(c, [new KeyPressEventArgs(ch)]);
    }
}
