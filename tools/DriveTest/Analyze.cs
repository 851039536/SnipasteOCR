// 分析 PrintWindow 抓到的覆盖层内容, 确认输入的文字真的画在选区里。
using System.Drawing;
using System.Drawing.Imaging;

internal static class Analyze
{
    public static void Run(string dir)
    {
        string path = Path.Combine(dir, "overlay_content.png");
        if (!File.Exists(path)) { Console.WriteLine("!! 缺少 overlay_content.png"); return; }

        using var bmp = new Bitmap(path);
        Console.WriteLine($"overlay_content: {bmp.Width}x{bmp.Height}");

        // PrintWindow 抓的是全屏覆盖层 (3840x1080), 这里缩到 1/2 -> 1920x540
        // 屏幕坐标 -> 位图坐标: 虚拟屏左上角 (-1920,0) 对应 (0,0), 再乘 0.5
        double scale = bmp.Width / 3840.0;
        Console.WriteLine($"缩放系数 = {scale}");

        // 选区屏幕坐标 (-1720,200)-(-1220,550) -> 位图坐标
        int bx1 = (int)((-1720 + 1920) * scale);
        int by1 = (int)(200 * scale);
        int bx2 = (int)((-1220 + 1920) * scale);
        int by2 = (int)(550 * scale);
        Console.WriteLine($"选区在位图中的位置: ({bx1},{by1})-({bx2},{by2})");

        // 统计选区内的颜色
        var hist = new Dictionary<int, int>();
        int red = 0, white = 0;
        for (int y = Math.Max(0, by1); y < Math.Min(bmp.Height, by2); y++)
            for (int x = Math.Max(0, bx1); x < Math.Min(bmp.Width, bx2); x++)
            {
                var c = bmp.GetPixel(x, y);
                int key = c.ToArgb();
                hist[key] = hist.GetValueOrDefault(key) + 1;
                if (c.R > 170 && c.G < 130 && c.B < 130) red++;
                if (c.R > 240 && c.G > 240 && c.B > 240) white++;
            }

        Console.WriteLine($"选区内: 偏红像素={red}, 近白像素={white}");
        Console.WriteLine("选区内主要颜色 (前 10):");
        foreach (var kv in hist.OrderByDescending(k => k.Value).Take(10))
        {
            var c = Color.FromArgb(kv.Key);
            Console.WriteLine($"  RGB({c.R},{c.G},{c.B}) x{kv.Value}");
        }

        // 打字锚点在屏幕 (-1570,320) -> 位图坐标
        int ax = (int)((-1570 + 1920) * scale);
        int ay = (int)(320 * scale);
        Console.WriteLine($"打字锚点(位图): ({ax},{ay})");

        // 在锚点周围找红色文字像素
        int found = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = Math.Max(0, ay - 20); y < Math.Min(bmp.Height, ay + 40); y++)
            for (int x = Math.Max(0, ax - 20); x < Math.Min(bmp.Width, ax + 120); x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R > 150 && c.G < 140 && c.B < 140 && c.R - c.G > 60)
                {
                    found++;
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

        Console.WriteLine($"锚点附近的红色文字像素 = {found}");
        if (found > 0)
        {
            Console.WriteLine($"文字包围盒 = ({minX},{minY})-({maxX},{maxY}) 尺寸 {maxX - minX + 1}x{maxY - minY + 1}");
            Console.WriteLine(">>> 打字内容已显示在覆盖层上");

            // 导出该区域放大图, 便于人工核对
            var box = Rectangle.FromLTRB(
                Math.Max(0, minX - 30), Math.Max(0, minY - 20),
                Math.Min(bmp.Width, maxX + 30), Math.Min(bmp.Height, maxY + 20));
            if (box.Width > 4 && box.Height > 4)
            {
                using var crop = bmp.Clone(box, PixelFormat.Format32bppArgb);
                using var big = new Bitmap(crop, new Size(crop.Width * 3, crop.Height * 3));
                string outp = Path.Combine(dir, "text_proof.png");
                big.Save(outp, ImageFormat.Png);
                Console.WriteLine($"证据图 (放大3倍): {outp}");
            }
        }
        else
        {
            Console.WriteLine(">>> !! 锚点附近没有文字像素");
        }
    }
}
