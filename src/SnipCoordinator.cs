namespace SnipasteOcr;

/// <summary>
/// 截图流程协调: 启动截图覆盖层, 结束后打开 OCR 窗口或把图片写入剪贴板
/// </summary>
public static class SnipCoordinator
{
    // 跟踪所有打开的窗体, 避免重复启动截图层
    private static readonly HashSet<Form> _openForms = [];

    /// <summary>当前是否有可见的截图覆盖层</summary>
    internal static bool OverlayVisible => _openForms.OfType<SnipOverlayForm>().Any(o => o.IsHandleCreated && o.Visible);
    internal static Form? GetOverlay() => _openForms.OfType<SnipOverlayForm>().FirstOrDefault();

    /// <summary>启动截图; 已有截图层在用时忽略</summary>
    public static void Start(SnipMode mode)
    {
        // 已有截图层在用时忽略
        if (_openForms.OfType<SnipOverlayForm>().Any())
            return;

        var overlay = new SnipOverlayForm(mode);
        _openForms.Add(overlay);
        overlay.FormClosed += (_, _) => _openForms.Remove(overlay);
        overlay.Show();
    }

    /// <summary>
    /// 识别剪贴板中的图片 (不经过截图层)。
    /// 复用 <see cref="OcrResultForm"/>, 因此结果窗口的选词/复制/导出行为与截图路径完全一致 ——
    /// 这正是"同一逻辑只写一份"的体现: 这里只负责取图, 识别与展示都交给既有组件。
    /// </summary>
    /// <returns>是否成功取到图片并打开了结果窗口; false 表示剪贴板里没有图片</returns>
    public static bool StartFromClipboard()
    {
        // 剪贴板可能被其它进程独占, GetImage 会抛 ExternalException, 必须兜住
        Image? img = null;
        try
        {
            if (Clipboard.ContainsImage())
                img = Clipboard.GetImage();
        }
        catch
        {
            img = null;
        }

        if (img is null)
            return false;

        // OcrResultForm 会持有并使用该位图, 因此不在这里 Dispose;
        // 转成 Bitmap 以统一类型 (GetImage 可能返回其它 Image 派生类)
        Bitmap bmp = img as Bitmap ?? new Bitmap(img);
        if (!ReferenceEquals(bmp, img))
            img.Dispose();

        var form = new OcrResultForm(bmp);
        _openForms.Add(form);
        form.FormClosed += (_, _) => _openForms.Remove(form);
        form.Show();
        return true;
    }
}
