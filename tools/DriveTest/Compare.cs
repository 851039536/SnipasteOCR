// 比较驱动测试抓取的截图, 判断"打字后"是否真的出现了新文字像素。
// 纯文本日志无法证明画面上有字, 这里做像素级 diff。
using System.Drawing;
using System.Drawing.Imaging;

internal static class Compare
{
    public static void Run(string dir)
    {
        string before = Path.Combine(dir, "before_type.png");
        string after = Path.Combine(dir, "after_type.png");
        string enter = Path.Combine(dir, "after_enter.png");

        if (!File.Exists(before) || !File.Exists(after))
        {
            Console.WriteLine("!! 缺少截图文件");
            return;
        }

        using var b = new Bitmap(before);
        using var a = new Bitmap(after);

        Console.WriteLine($"before_type: {b.Width}x{b.Height}");
        Console.WriteLine($"after_type : {a.Width}x{a.Height}");

        if (b.Width != a.Width || b.Height != a.Height)
        {
            Console.WriteLine("!! 尺寸不同, 无法比较");
            return;
        }

        // 全屏比较 (3840x1080 = 414 万像素; 用 LockBits 批量读取, 否则 GetPixel 太慢)
        Console.WriteLine("全屏逐像素比较 (LockBits)...");
        var (diff, minX, minY, maxX, maxY, diffColors) = DiffFull(b, a);
        Console.WriteLine($"差异像素数: {diff}");

        if (diff > 0)
        {
            Console.WriteLine($"差异包围盒: ({minX},{minY}) - ({maxX},{maxY})  尺寸 {maxX - minX + 1}x{maxY - minY + 1}");
            Console.WriteLine("差异中占比最高的颜色 (前 8):");
            foreach (var kv in diffColors.OrderByDescending(k => k.Value).Take(8))
            {
                var c = Color.FromArgb(kv.Key);
                Console.WriteLine($"  RGB({c.R},{c.G},{c.B}) x{kv.Value}");
            }

            bool hasTextRed = diffColors.Keys.Any(k => { var c = Color.FromArgb(k); return c.R > 180 && c.G < 120 && c.B < 120; });
            bool hasDark = diffColors.Keys.Any(k => { var c = Color.FromArgb(k); return c.R < 80 && c.G < 80 && c.B < 80; });
            Console.WriteLine($"包含红色文字像素: {hasTextRed}");
            Console.WriteLine($"包含深色描边像素: {hasDark}");
        }
        else
        {
            Console.WriteLine("!! 打字前后全屏完全相同 -> 打字没有任何可见效果");
        }

        // 检测"打字后"图里是否存在红色文字色 (即使与 before 相同也要查, 排除两图都无字)
        int redInAfter = CountColorNear(a, 235, 59, 36, 40);
        int redInBefore = CountColorNear(b, 235, 59, 36, 40);
        Console.WriteLine();
        Console.WriteLine($"红色文字色像素: before={redInBefore}, after={redInAfter}");

        // 回车后应生成标注并关闭覆盖层
        if (File.Exists(enter))
        {
            using var en = new Bitmap(enter);
            Console.WriteLine($"after_enter 尺寸: {en.Width}x{en.Height}");
        }
    }

    /// <summary>用 LockBits 做大图快速差异比较</summary>
    static (int diff, int minX, int minY, int maxX, int maxY, Dictionary<int, int> colors) DiffFull(Bitmap b, Bitmap a)
    {
        int w = a.Width, h = a.Height;
        var rect = new Rectangle(0, 0, w, h);
        var bd1 = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bd2 = a.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int diff = 0, minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var colors = new Dictionary<int, int>();

        try
        {
            int stride = bd1.Stride;
            byte[] p1 = new byte[stride * h];
            byte[] p2 = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(bd1.Scan0, p1, 0, p1.Length);
            System.Runtime.InteropServices.Marshal.Copy(bd2.Scan0, p2, 0, p2.Length);

            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    if (p1[o] == p2[o] && p1[o + 1] == p2[o + 1] &&
                        p1[o + 2] == p2[o + 2] && p1[o + 3] == p2[o + 3]) continue;

                    diff++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;

                    int key = (p2[o + 3] << 24) | (p2[o + 2] << 16) | (p2[o + 1] << 8) | p2[o];
                    colors[key] = colors.GetValueOrDefault(key) + 1;
                }
            }
        }
        finally
        {
            b.UnlockBits(bd1);
            a.UnlockBits(bd2);
        }

        return (diff, minX, minY, maxX, maxY, colors);
    }

    /// <summary>统计接近指定颜色的像素数</summary>
    static int CountColorNear(Bitmap bmp, int r, int g, int bb, int tol)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int n = 0;
        try
        {
            int stride = bd.Stride;
            byte[] p = new byte[stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, p, 0, p.Length);
            for (int y = 0; y < bmp.Height; y++)
            {
                int row = y * stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int o = row + x * 4;
                    if (Math.Abs(p[o] - bb) <= tol && Math.Abs(p[o + 1] - g) <= tol && Math.Abs(p[o + 2] - r) <= tol)
                        n++;
                }
            }
        }
        finally { bmp.UnlockBits(bd); }
        return n;
    }
}
