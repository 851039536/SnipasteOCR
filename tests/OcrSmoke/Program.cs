using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Medium;

// Headless smoke test: render a known string to a bitmap, run the same
// pipeline OcrService uses (LockBits -> BGRA32 byte buffer -> PaddleOcrAll.Run),
// and assert the engine recovers the text. Proves models + inference work.

const string Expected = "离线OCR识别测试";

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 1. Render the expected text to a bitmap (white bg, black text, large font)
using var bmp = new Bitmap(760, 150);
using (var g = Graphics.FromImage(bmp))
{
    g.Clear(Color.White);
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    using var font = new Font("Microsoft YaHei UI", 44f, FontStyle.Regular, GraphicsUnit.Pixel);
    g.DrawString(Expected, font, Brushes.Black, new PointF(20, 40));
}
bmp.Save(Path.Combine(AppContext.BaseDirectory, "input.png"), ImageFormat.Png);

// 2. Snapshot pixels exactly like OcrService.Snapshot (instant LockBits copy)
int width = bmp.Width, height = bmp.Height;
BitmapData data = bmp.LockBits(new Rectangle(0, 0, width, height),
    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
int stride;
byte[] pixels;
try
{
    stride = data.Stride;
    pixels = new byte[stride * height];
    Marshal.Copy(data.Scan0, pixels, 0, stride * height);
}
finally { bmp.UnlockBits(data); }

// 3. Load engine (same options as OcrService.EnsureInitialized)
Console.WriteLine("Loading ChineseV6Medium engine...");
var sw = System.Diagnostics.Stopwatch.StartNew();
var options = new PaddleOcrOptions
{
    UseDirectionClassification = true,
    LineWorkerCount = 0,
};
using var ocr = PaddleOcrAll.Load(ChineseV6MediumModels.Default, options);
Console.WriteLine($"Engine loaded in {sw.ElapsedMilliseconds} ms");

// 4. Run inference
sw.Restart();
var result = ocr.Run(pixels.AsSpan(), width, height, stride, ImagePixelFormat.Bgra32);
Console.WriteLine($"Inference took {sw.ElapsedMilliseconds} ms");
Console.WriteLine($"Lines detected: {result.Lines.Length}");

var sb = new System.Text.StringBuilder();
foreach (var line in result.Lines)
{
    Console.WriteLine($"  [{line.RecognitionScore:F4}] \"{line.Text}\"");
    sb.Append(line.Text);
}

string actual = sb.ToString();
Console.WriteLine();
Console.WriteLine($"Expected : {Expected}");
Console.WriteLine($"Actual   : {actual}");

// 5. Assert: all expected chars recovered (tolerate engine splitting lines)
int matched = Expected.Count(c => actual.Contains(c));
bool pass = matched == Expected.Length;
Console.WriteLine();
Console.WriteLine(pass
    ? $"PASS: all {Expected.Length} characters recovered"
    : $"FAIL: only {matched}/{Expected.Length} characters recovered");
return pass ? 0 : 1;
