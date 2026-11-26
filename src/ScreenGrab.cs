namespace SnipasteOcr;

/// <summary>
/// 屏幕抓取的唯一入口。
///
/// 抓整屏与抓指定矩形曾各写一份 <c>new Bitmap</c> + <c>Graphics.CopyFromScreen</c>;
/// 两处的像素格式与 <see cref="CopyPixelOperation.SourceCopy"/> 语义必须一致,
/// 否则 OCR 与贴图拿到的颜色通道可能不同。
///
/// 这里不吞异常: 副屏休眠、锁屏、远程会话切换时 CopyFromScreen 会抛
/// Win32Exception("句柄无效"), 由调用方决定是提示还是取消本次截图。
/// </summary>
internal static class ScreenGrab
{
    /// <summary>抓取屏幕上的指定矩形 (物理像素); 失败时释放半成品位图并原样抛出</summary>
    public static Bitmap Copy(Rectangle rect)
    {
        var bitmap = new Bitmap(rect.Width, rect.Height);
        try
        {
            using var g = Graphics.FromImage(bitmap);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
