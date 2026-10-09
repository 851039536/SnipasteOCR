using System.Runtime.InteropServices;
using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 应用入口: 托盘 + 全局热键 + 手动消息循环 (NativeAOT 下 MessageLoop.Run 不可用)
/// 所有关键事件写日志, 便于诊断
/// </summary>
internal static class Program
{
    private static Mutex? _singleInstanceMutex;

    [STAThread]
    /// <summary>
    /// 程序入口: 单实例检查 -> 托盘/热键初始化 -> 手动消息循环
    /// </summary>
    private static int Main(string[] args)
    {
        try
        {
            ApplicationConfiguration.Initialize();
            // WinForms 全局初始化 (DPI 感知/默认控件行为)

            _singleInstanceMutex = new Mutex(true, @"Local\SnipasteOcr.SingleInstance", out bool createdNew);
            // 命名 Mutex 防多开: 已存在则提示后退出
            if (!createdNew)
            {
                MessageBox.Show("SnipasteOCR 已在运行 (请查看系统托盘)。", "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            // 恢复上次选择的模型档位 (设置读取失败时自动回退默认值, 不影响启动)
            OcrService.Instance.SetProfile(SettingsStore.Current.ModelProfile);

            // 让注册表与设置文件一致: 只有明确开启过才写自启项。
            // 注意方向 —— 这里是"以设置文件为准去修注册表", 不是反过来,
            // 否则用户手工删掉注册表项后设置文件仍写着 true, 两边长期不一致。
            if (StartupRegistration.IsEnabled() != SettingsStore.Current.RunAtStartup)
                StartupRegistration.SetEnabled(SettingsStore.Current.RunAtStartup);

            using var tray = new TrayController();
            // 托盘图标 + 不可见宿主窗口; 全局热键也注册在该窗口上
            tray.SnipOcrRequested += () => SnipCoordinator.Start(SnipMode.Ocr);
            tray.SnipImageRequested += () => SnipCoordinator.Start(SnipMode.Image);
            // 模型档位切换: 只登记请求, 真正的加载发生在下一次识别时 (后台线程)
            tray.ModelProfileRequested += p =>
            {
                OcrService.Instance.SetProfile(p);
                SettingsStore.Update(s => s.ModelProfile = p);   // 记住选择
            };

            // 识别剪贴板图片: 剪贴板里没有图片时给出提示 (否则点了没反应, 用户不知道原因)
            tray.OcrClipboardRequested += () =>
            {
                if (!SnipCoordinator.StartFromClipboard())
                {
                    MessageBox.Show(
                        "剪贴板里没有图片。\n\n请先复制一张图片 (或用 F2 截图), 再试。",
                        "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };

            // 开机自启: 写注册表可能失败 (策略锁定/无权限), 失败要如实告知而不是假装成功
            tray.StartupToggleRequested += want =>
            {
                if (!StartupRegistration.SetEnabled(want))
                {
                    MessageBox.Show(
                        "无法修改开机自启设置 (注册表可能被安全策略锁定)。",
                        "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 回读真实状态落盘, 保证设置文件与注册表一致
                SettingsStore.Update(s => s.RunAtStartup = StartupRegistration.IsEnabled());
            };

            IntPtr hwnd = tray.WindowHandle;

            // 热键来自设置 (默认 F1 / F2), 支持运行期更换
            var settings = SettingsStore.Current;
            using var hotKeys = new HotKeyManager(hwnd);
            var failures = hotKeys.RegisterAll(settings.HotKeyOcr, settings.HotKeyImage, settings.HotKeyModifiers);

            if (failures.Count > 0)
            {
                string hint = settings.HotKeyModifiers == 0
                    ? "\n\n提示: 可右键托盘图标 → 热键设置, 改绑到其它按键 (如 Ctrl+Alt+Q)。"
                    : "\n\n可右键托盘图标 → 热键设置 改绑其它组合键。";
                MessageBox.Show(
                    "以下热键注册失败 (可能被浏览器/IDE/其它截图工具占用):\n  " +
                    string.Join("\n  ", failures) + hint +
                    "\n\n仍可右键系统托盘图标操作。",
                    "SnipasteOCR - 热键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // 托盘菜单里更换热键
            tray.HotKeyChangeRequested += () =>
            {
                using var dlg = new HotKeyForm(settings.HotKeyOcr, settings.HotKeyImage, settings.HotKeyModifiers);
                if (dlg.ShowDialog() != DialogResult.OK)
                    return;

                var newFailures = hotKeys.RegisterAll(dlg.OcrKey, dlg.ImageKey, dlg.Modifiers);
                if (newFailures.Count > 0)
                {
                    MessageBox.Show(
                        "新热键注册失败, 已保留原热键:\n  " + string.Join("\n  ", newFailures),
                        "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                settings.HotKeyOcr = dlg.OcrKey;
                settings.HotKeyImage = dlg.ImageKey;
                settings.HotKeyModifiers = dlg.Modifiers;
                SettingsStore.Save();

                MessageBox.Show(
                    $"热键已更新:\n  截图识别 = {HotKeyManager.Describe((uint)dlg.Modifiers, (uint)dlg.OcrKey)}\n" +
                    $"  截图标注 = {HotKeyManager.Describe((uint)dlg.Modifiers, (uint)dlg.ImageKey)}",
                    "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            tray.ExitRequested += () => Environment.Exit(0);

            while (true)
            {
                try
                {
                    // 手动消息泵: 泵空消息队列, 空闲时睡 1ms (NativeAOT 下无 MessageLoop.Run)
                    while (User32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, User32.PM_REMOVE))
                    {
                        if (msg.message == User32.WM_HOTKEY)
                        {
                            int id = msg.wParam.ToInt32();
                            if (hotKeys.TryResolve(id, out var action))
                                SnipCoordinator.Start(action == HotKeyManager.Action.SnipOcr ? SnipMode.Ocr : SnipMode.Image);
                        }

                        User32.TranslateMessage(ref msg);
                        User32.DispatchMessage(ref msg);
                    }
                    Thread.Sleep(1);
                }
                catch
                {
                    // 极端异常不致命, 降速后继续重试, 避免死循环空转
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败:\n" + ex, "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally
        {
            // 退出前释放 OCR 引擎 (SIMD 内存缓冲)
            OcrService.Instance.Dispose();
        }
    }

   
}
