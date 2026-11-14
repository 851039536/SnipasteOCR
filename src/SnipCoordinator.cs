using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 截图流程协调: 启动截图覆盖层, 结束后打开 OCR 窗口或把图片写入剪贴板
/// </summary>
public static class SnipCoordinator
{
    // 跟踪所有打开的窗体, 避免重复启动截图层
    private static readonly HashSet<Form> _openForms = [];

    // 最近一次 OCR 实际使用的物理屏幕矩形; 只保留在本次运行内,
    // 供托盘「重复上次识别」直接截同一区域 (不重新框选)。
    private static Rectangle? _lastOcrRegion;

    /// <summary>最近一次 OCR 区域 (物理屏幕坐标); 仅供自检断言</summary>
    internal static Rectangle? LastOcrRegionForTest => _lastOcrRegion;

    /// <summary>
    /// 仅供交互自检替换结果窗口启动动作, 避免自检触发真实 OCR 引擎。
    /// 生产路径恒为 null; 位图所有权交给回调。
    /// </summary>
    internal static Action<Bitmap, Rectangle?>? OcrResultLauncherForTest { get; set; }

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
    /// 记录最近一次实际送去 OCR 的屏幕矩形 (物理坐标)。
    /// 只记录成功进入 OCR 的路径; 剪贴板/文件识别没有"区域"概念, 不更新它。
    /// </summary>
    internal static void RememberOcrRegion(Rectangle screenRect)
    {
        if (screenRect.Width > 0 && screenRect.Height > 0)
            _lastOcrRegion = screenRect;
    }

    /// <summary>
    /// 重复上次 OCR 区域: 直接截取同一块屏幕并打开结果窗口, 不经过覆盖层。
    /// 返回 false 表示还没有上次区域, 或屏幕布局已变化导致区域失效 —— 由调用方提示用户。
    /// </summary>
    public static bool RepeatLastOcr()
    {
        if (_lastOcrRegion is not { } rect)
            return false;

        // 屏幕布局可能变化 (拔掉副屏/改分辨率): 区域必须完整落在当前虚拟屏内,
        // 否则截到的是桌面背景或其它窗口, 不如让用户重新框选。
        if (!SystemInformation.VirtualScreen.Contains(rect))
            return false;

        Bitmap? bmp = null;
        List<IntPtr> hidden = [];
        try
        {
            // 上一次结果窗口通常正盖在目标区域上; 不藏起来就会把结果窗口自己截进去,
            // 得到一个层层递归的 OCR。只短暂隐藏, 抓完立即恢复且不抢焦点。
            hidden = HideOpenForms();
            bmp = new Bitmap(rect.Width, rect.Height);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, rect.Size, CopyPixelOperation.SourceCopy);
        }
        catch
        {
            bmp?.Dispose();
            return false;
        }
        finally
        {
            RestoreHiddenForms(hidden);
        }

        ShowOcrResult(bmp, rect);
        return true;
    }

    /// <summary>抓屏前短暂隐藏本程序已跟踪的可见窗体; 返回已隐藏的句柄</summary>
    private static List<IntPtr> HideOpenForms()
    {
        List<IntPtr> hidden = [];
        foreach (var form in _openForms.ToArray())
        {
            if (form.IsDisposed || !form.IsHandleCreated || !form.Visible)
                continue;
            User32.ShowWindow(form.Handle, User32.SW_HIDE);
            hidden.Add(form.Handle);
        }

        if (hidden.Count > 0)
        {
            // 让被遮住的底层窗口收到 WM_PAINT, 否则可能抓到隐藏前的残影
            Application.DoEvents();
            Thread.Sleep(30);
        }
        return hidden;
    }

    /// <summary>恢复抓屏前隐藏的窗口; SW_SHOWNA 只显示不抢焦点</summary>
    private static void RestoreHiddenForms(List<IntPtr> hidden)
    {
        foreach (IntPtr hwnd in hidden)
            User32.ShowWindow(hwnd, User32.SW_SHOWNA);
    }

    /// <summary>
    /// 打开 OCR 结果窗口并纳入生命周期跟踪。
    /// 剪贴板/重复区域/截图三条入口共用, 保证窗体的关闭与释放行为一致。
    /// </summary>
    private static void ShowOcrResult(Bitmap bmp, Rectangle? anchorScreen)
    {
        if (OcrResultLauncherForTest is { } launcher)
        {
            launcher(bmp, anchorScreen);
            return;
        }

        var form = new OcrResultForm(bmp, anchorScreen);
        _openForms.Add(form);
        form.FormClosed += (_, _) => _openForms.Remove(form);
        form.Show();
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

        ShowOcrResult(bmp, anchorScreen: null);
        return true;
    }
}
