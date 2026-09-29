// 真实驱动测试: 直接操作实际出货的 SnipasteOcr.exe。
// 用 Win32 消息真实点击/打字, 而不是反射调用内部方法,
// 并把每一步的实际状态写进日志 —— 避免"测试验证了自己的假设"。
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class Drive
{
    [DllImport("user32.dll")] static extern IntPtr FindWindow(string? cls, string? win);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint f, IntPtr e);
    [DllImport("user32.dll")] static extern int GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool f);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004, MOUSEEVENTF_MOVE = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    static readonly StringBuilder Log = new();

    static void Say(string s)
    {
        Console.WriteLine(s);
        Log.AppendLine(s);
    }

    static IntPtr FindOverlay(uint pid)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint p);
            if (p != pid || !IsWindowVisible(h)) return true;
            GetWindowRect(h, out var r);
            // 覆盖层是全屏无边框窗口
            int w = r.R - r.L, ht = r.B - r.T;
            var cls = new StringBuilder(256); GetClassNameW(h, cls, cls.Capacity);
            if (w > 1000 && ht > 600)
            {
                Say($"  候选窗口 hwnd=0x{h:X} class={cls} size={w}x{ht} rect=({r.L},{r.T})-({r.R},{r.B})");
                if (found == IntPtr.Zero) found = h;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(120);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(200);
    }

    static void Drag(int x1, int y1, int x2, int y2)
    {
        SetCursorPos(x1, y1);
        Thread.Sleep(150);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(100);
        // 分步移动, 保证 MouseMove 事件被触发
        for (int i = 1; i <= 10; i++)
        {
            SetCursorPos(x1 + (x2 - x1) * i / 10, y1 + (y2 - y1) * i / 10);
            Thread.Sleep(40);
        }
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(300);
    }

    static void PressKey(byte vk)
    {
        keybd_event(vk, 0, 0, IntPtr.Zero);
        Thread.Sleep(40);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, IntPtr.Zero);
        Thread.Sleep(120);
    }

    /// <summary>用 SendInput 风格的 unicode 注入, 走完整键盘消息链</summary>
    static void TypeUnicode(string s)
    {
        foreach (char c in s)
        {
            keybd_event(0, 0, 0, IntPtr.Zero); // 占位, 实际用下面
            PostUnicode(c);
        }
    }

    [DllImport("user32.dll")]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public KEYBDINPUT ki; }

    static void PostUnicode(char c)
    {
        var down = new INPUT { type = 1 };
        down.ki.wScan = c; down.ki.dwFlags = KEYEVENTF_UNICODE;
        var up = new INPUT { type = 1 };
        up.ki.wScan = c; up.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
        SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
        Thread.Sleep(90);
    }

    static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 目标 exe 由命令行传入, 避免相对路径在不同工作目录下解析错误
        string exe = args.Length > 0
            ? args[0]
            : Path.GetFullPath(@"..\..\..\..\src\bin\Debug\net10.0-windows\win-x64\SnipasteOcr.exe");

        if (!File.Exists(exe)) { Say($"!! 找不到 exe: {exe}"); return; }
        Say($"启动 {exe}");

        // 带 compare 参数时只做截图比较 (不重新驱动)
        if (args.Length > 1 && args[1] == "compare")
        {
            Compare.Run(AppContext.BaseDirectory);
            return;
        }

        // 带 analyze 参数时只分析 PrintWindow 抓到的覆盖层内容
        if (args.Length > 1 && args[1] == "analyze")
        {
            Analyze.Run(AppContext.BaseDirectory);
            return;
        }

        var proc = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        if (proc is null) { Say("启动失败"); return; }
        Thread.Sleep(3000);

        Say($"进程 PID={proc.Id}, HasExited={proc.HasExited}");
        if (proc.HasExited)
        {
            // 单实例互斥: 已有实例在跑时新进程会立刻退出并弹框, 此时测试毫无意义
            Say("!! 程序已退出 — 很可能已有其它 SnipasteOcr 实例在运行 (单实例互斥)");
            Say("   请先结束所有 SnipasteOcr 进程再重试。");
            return;
        }

        // 1. 用 F2 触发截图 (默认热键)
        Say("[1] 发送 F2 触发热键...");
        SetForegroundWindow(GetForegroundWindow());
        PressKey(0x71); // VK_F2
        Thread.Sleep(1500);

        // 2. 找覆盖层窗口
        Say("[2] 枚举覆盖层窗口...");
        IntPtr overlay = FindOverlay((uint)proc.Id);

        // 热键可能被别的程序占用 -> 首次未出现时重试几次并放宽判定
        for (int attempt = 0; overlay == IntPtr.Zero && attempt < 3; attempt++)
        {
            Say($"    第 {attempt + 1} 次重试 F2...");
            PressKey(0x71);
            Thread.Sleep(1500);
            overlay = FindOverlay((uint)proc.Id);
        }

        if (overlay == IntPtr.Zero)
        {
            Say("!! 未找到覆盖层窗口 — F2 热键可能被其它程序占用");
            Say("   (浏览器/IDE/其它截图工具常占用 F2)");
            proc.Kill();
            return;
        }
        Say($"    覆盖层 hwnd=0x{overlay:X}");
        GetWindowRect(overlay, out var orc);

        // 3. 拖拽框选一块区域
        int x1 = orc.L + 200, y1 = orc.T + 200, x2 = x1 + 500, y2 = y1 + 350;
        Say($"[3] 拖拽框选 ({x1},{y1}) -> ({x2},{y2})");
        Drag(x1, y1, x2, y2);
        Thread.Sleep(800);

        // 4. 点工具栏上的"文字"按钮
        //    工具栏出现在选区下方, 从左到右第 6 个工具按钮
        //    工具栏结构: PadLeft(8) + 6 个 34px 按钮(间隔 4)
        int tbLeft = x1, tbTop = y2 + 8;
        int textBtnCx = tbLeft + 8 + (6 * 34 + 5 * 4) - 34 / 2;  // 第 6 个
        int textBtnCy = tbTop + 42 / 2;
        Say($"[4] 点击文字工具按钮 @({textBtnCx},{textBtnCy})");
        Click(textBtnCx, textBtnCy);
        Thread.Sleep(500);

        // 5. 在选区内点击定位
        int clickX = x1 + 150, clickY = y1 + 120;
        Say($"[5] 在选区内点击 @({clickX},{clickY})");
        Click(clickX, clickY);
        Thread.Sleep(700);

        // 6. 检查焦点在哪个窗口
        uint cur = GetCurrentThreadId();
        IntPtr fg = GetForegroundWindow();
        uint fgT = GetWindowThreadProcessId(fg, IntPtr.Zero);
        AttachThreadInput(cur, fgT, true);
        IntPtr focus = GetFocus();
        AttachThreadInput(cur, fgT, false);
        var focusCls = new StringBuilder(256);
        if (focus != IntPtr.Zero) GetClassNameW(focus, focusCls, focusCls.Capacity);

        GetWindowThreadProcessId(fg, out uint fgPid);
        Say($"[6] 前台窗口=0x{fg:X} (pid={fgPid}, overlay=0x{overlay:X} pid={proc.Id})");
        Say($"    焦点控件=0x{focus:X} class={focusCls}");
        Say($"    *** 前台是否为覆盖层: {fg == overlay} ***");
        Say($"    *** 焦点是否属于本进程: {fgPid == proc.Id} ***");

        // 抓一张"打字前"的图, 便于与打字后对比
        CaptureTo("before_type.png");

        // 7. 打字
        Say("[7] 输入文字 '测试ABC'");
        foreach (char c in "测试ABC") PostUnicode(c);
        Thread.Sleep(1200);
        CaptureTo("after_type.png");

        // 7b. 用 WM_PRINT 抓取覆盖层自身内容 (跨进程抓屏对置顶窗口不可靠,
        //     而 PrintWindow 走窗口自己的绘制路径, 能真实反映窗口内容)
        Say("[7b] 用 PrintWindow 抓取覆盖层内容...");
        string pwPath = Path.Combine(AppContext.BaseDirectory, "overlay_content.png");
        bool pwOk = PrintWindowToFile(overlay, pwPath);
        Say($"    PrintWindow 成功={pwOk} -> {pwPath}");

        // 8. 回车提交
        Say("[8] 回车提交");
        PressKey(0x0D);
        Thread.Sleep(1500);
        Say($"    提交后覆盖层是否仍可见: {IsWindowVisible(overlay)}");
        CaptureTo("after_enter.png");

        // 9. 收尾
        PressKey(0x1B); // Esc
        Thread.Sleep(500);
        CaptureTo("final.png");

        try { proc.Kill(); } catch { }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "drive.log"), Log.ToString());
        Say("完成");
    }

    /// <summary>抓取当前屏幕到文件</summary>
    static void CaptureTo(string name)
    {
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        using var bmp = new Bitmap(vs.Width, vs.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, bmp.Size);
        string outPath = Path.Combine(AppContext.BaseDirectory, name);
        bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        Say($"    已保存 {name}");
    }

    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>
    /// 用 PrintWindow 抓取指定窗口自身的内容。
    /// 跨进程 CopyFromScreen 对"别的进程的置顶窗口"不可靠 (可能抓到下层桌面),
    /// 而 PrintWindow 让目标窗口自己绘制到我们提供的 DC, 结果真实可信。
    /// </summary>
    static bool PrintWindowToFile(IntPtr hwnd, string path)
    {
        GetWindowRect(hwnd, out var r);
        int w = r.R - r.L, h = r.B - r.T;
        if (w <= 0 || h <= 0) return false;

        // 全屏窗口 (3840x1080) 缩到 1/4 以控制体积与耗时, 但保留足够细节看文字
        int sw = Math.Max(1, w / 2), sh = Math.Max(1, h / 2);

        using var full = new Bitmap(w, h);
        using (var g = Graphics.FromImage(full))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT))
                    return false;
            }
            finally { g.ReleaseHdc(hdc); }
        }

        using var small = new Bitmap(full, new Size(sw, sh));
        small.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return true;
    }
}
