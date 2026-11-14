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

    // 延时截图的 UI 线程计时器; 只允许一个待触发的延时
    private static System.Windows.Forms.Timer? _delayTimer;

    /// <summary>启动截图; 已有截图层在用时忽略</summary>
    public static void Start(SnipMode mode)
    {
        // 用户直接按热键 = 立即截图, 取消尚未到期的延时请求, 避免稍后突然弹出第二个覆盖层
        CancelDelayed();

        // 已有截图层在用时忽略
        if (_openForms.OfType<SnipOverlayForm>().Any())
            return;

        var overlay = new SnipOverlayForm(mode);
        _openForms.Add(overlay);
        overlay.FormClosed += (_, _) => _openForms.Remove(overlay);
        overlay.Show();
    }

    /// <summary>
    /// 延时启动截图覆盖层 (托盘菜单「延时截图」)。
    ///
    /// 必须用 WinForms 的 Timer 而不是 <c>Task.Delay</c> + 后台线程:
    /// 覆盖层是 WinForms 窗体, 必须在创建托盘/热键的 UI 线程上构造;
    /// WinForms Timer 的 Tick 天然回到该线程, 且手动消息循环会正常派发 WM_TIMER。
    /// </summary>
    public static void StartDelayed(SnipMode mode, int seconds)
    {
        CancelDelayed();

        var timer = new System.Windows.Forms.Timer { Interval = Math.Max(1, seconds) * 1000 };
        _delayTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (ReferenceEquals(_delayTimer, timer))
                _delayTimer = null;

            // 不能再走 Start (它会调用 CancelDelayed) —— 此时计时器已自行释放
            StartCore(mode);
        };
        timer.Start();
    }

    private static void CancelDelayed()
    {
        if (_delayTimer is { } timer)
        {
            _delayTimer = null;
            timer.Stop();
            timer.Dispose();
        }
    }

    private static void StartCore(SnipMode mode)
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
