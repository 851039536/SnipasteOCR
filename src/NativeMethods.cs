using System.Runtime.InteropServices;
using System.Text;

// Win32 常量/入口名必须与 Windows SDK 逐字一致 (WM_*/TPM_*/RegisterClassEx...),
// P/Invoke 也必须是 public 才能被同程序集的调用方使用 —— 这三条分析器规则在此文件不适用。
#pragma warning disable CA1707 // 标识符不应包含下划线
#pragma warning disable CA1401 // P/Invoke 不应可见
#pragma warning disable CA1711 // 标识符不应以 Ex/Delegate 结尾

// Win32 P/Invoke 声明集: 消息循环 / 热键 / 窗口 / 菜单 / 托盘 / 输入模拟。
// 用于 NativeAOT 手动消息循环与纯 Win32 托盘实现。
// (文件级注释不写 XML doc: 它不挂在任何类型上, 开了文档生成会报 CS1587)
namespace SnipasteOcr.Native;

/// <summary>热键 ID (传给 RegisterHotKey, WM_HOTKEY 消息 wParam 返回)</summary>
public enum HotKeyId
{
    SnipOcr = 0xB001,
    SnipImage = 0xB002,
}

/// <summary>user32.dll 的窗口、消息、热键、菜单与托盘宿主 P/Invoke 集合</summary>
public static class User32
{
    // ===== 消息常量 =====
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_APP = 0x8000;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_COMMAND = 0x0111;
    public const int HTCAPTION = 2;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WS_EX_TOOLWINDOW = 0x0080;
    /// <summary>
    /// 不激活: 点击窗口不会抢走焦点。
    /// 正确值是 <b>0x08000000</b> —— 旧代码写成 0x0800, 那不是任何有效的 WS_EX 位,
    /// 等于从未设置过 (贴图用字面量躲开了, 托盘宿主窗口则一直是"可激活"状态)。
    /// </summary>
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    /// <summary>鼠标穿透: 命中测试直接落到下层窗口, 本窗口收不到任何鼠标消息</summary>
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    /// <summary>扩展样式索引 (GetWindowLongPtr / SetWindowLongPtr)</summary>
    public const int GWL_EXSTYLE = -20;

    // SetWindowPos 标志 (winuser.h)
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint TPM_LEFTALIGN = 0;
    public const uint TPM_RETURNCMD = 0x0100;

    public const uint TPM_TOPALIGN = 0x0000;

    public const uint TPM_RIGHTBUTTON = 0x0002;

    // ===== 菜单常量 =====
    public const uint MF_STRING = 0;
    public const uint MF_SEPARATOR = 0x800;
    /// <summary>菜单项被选中 (配合 MF_RADIOCHECK 显示单选圆点)</summary>
    public const uint MF_CHECKED = 0x00000008;
    /// <summary>菜单项未选中</summary>
    public const uint MF_UNCHECKED = 0x00000000;
    /// <summary>显示单选圆点而非勾号</summary>
    public const uint MF_RADIOCHECK = 0x00000200;
    /// <summary>该项是子菜单: uIdNewItem 位置传子菜单句柄</summary>
    public const uint MF_POPUP = 0x00000010;
    /// <summary>按命令 ID 查找菜单项</summary>
    public const uint MF_BYCOMMAND = 0x00000000;

    // ===== 结构体 =====

    /// <summary>Win32 消息结构 (字段顺序必须与 SDK 一致, 否则 PeekMessage 读错)</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public System.Drawing.Point pt;
    }

    /// <summary>窗口类注册结构; CharSet=Unicode 让字符串字段走 W 入口</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    // ===== 消息循环 =====

    // winuser.h: PM_REMOVE 0x0001
    public const uint PM_REMOVE = 0x0001;

    /// <summary>从消息队列取消息; hWnd=0 取当前线程全部消息, PM_REMOVE 表示取出</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    /// <summary>查询虚拟键状态 (高位 0x8000 = 当前按下); 用于 WndProc 兜底判断 Ctrl</summary>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    /// <summary>把键盘/鼠标消息翻译成字符消息 (WM_CHAR 等), 配合 DispatchMessage 使用</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);
    /// <summary>把消息派发给对应窗口的 WndProc</summary>

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    // ===== 热键 =====

    // RegisterHotKey 修饰符 (winuser.h)
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    /// <summary>阻止按键继续传递给下层窗口 (避免触发目标程序自身的快捷键)</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    /// <summary>注册全局热键 (fsModifiers 用 MOD_* 组合, vk 为虚拟键码)</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>注销热键</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>释放鼠标捕获 (贴图窗口靠它把拖动交给系统的标题栏拖拽逻辑)</summary>
    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    /// <summary>发送窗口消息 (与 ReleaseCapture 配合发起 WM_NCLBUTTONDOWN 拖动)</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ===== 窗口类 / 窗口 =====

    /// <summary>注册窗口类; 成功返回类原子号, 失败 0</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    /// <summary>创建窗口 (含扩展样式), 返回窗口句柄</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    /// <summary>默认窗口过程: 未处理的系统消息交给系统 (默认光标/边框/销毁等)</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>销毁窗口 (异步, 会发 WM_DESTROY)</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr hWnd);

    /// <summary>显示/隐藏窗口; SW_HIDE 隐藏, SW_SHOWNA 显示但不抢焦点</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public const int SW_HIDE = 0;
    public const int SW_SHOWNA = 8;

    /// <summary>把窗口设为前台 (弹菜单前必须, 否则菜单立即消失)</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    // 本项目仅发布 win-x64, 因此直接绑 *PtrW 版本 (32 位下这两个入口不存在)
    /// <summary>读取窗口属性 (扩展样式用 <see cref="GWL_EXSTYLE"/>)</summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    /// <summary>写入窗口属性; 改样式后必须再 SetWindowPos(SWP_FRAMECHANGED) 才会生效</summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>改变窗口位置/尺寸/Z 序/样式生效; 鼠标穿透切换后必须调一次</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    // ===== 菜单 =====

    /// <summary>创建弹出菜单</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    /// <summary>追加菜单项 (文本项传 ID, 分隔线传 MF_SEPARATOR + 空文本)</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIdNewItem, string? lpNewItem);

    /// <summary>销毁菜单</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr hMenu);

    /// <summary>
    /// 在 [first..last] 位置区间内做单选互斥: 把 check 项标为圆点选中, 其余清空。
    /// 用于托盘菜单的模型档位切换 (Medium / Tiny 二选一)。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CheckMenuRadioItem(
        IntPtr hMenu, uint idFirst, uint idLast, uint idCheck, uint uFlags);

    /// <summary>
    /// 设置/清除单个菜单项的勾选 (复选项用, 配合 MF_BYCOMMAND + MF_CHECKED/MF_UNCHECKED)。
    /// 与 <see cref="CheckMenuRadioItem"/> 的区别: 前者是区间内单选, 本方法是单项独立复选。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CheckMenuItem(IntPtr hMenu, uint uIDCheckItem, uint uCheck);

    /// <summary>
    /// 修改已存在菜单项的文本 (档位切换后用新名称刷新, 如追加当前档位)。
    /// 必须显式 CharSet.Unicode: 否则默认按 ANSI 绑定 ModifyMenuA, 中文文本会乱码
    /// (AppendMenu 同理, 已在上面指定)。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ModifyMenu(
        IntPtr hMenu, uint uPosition, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

    /// <summary>在指定屏幕坐标弹出菜单并阻塞等待选择; TPM_RETURNCMD 时返回选中项的命令 ID (0 = 取消)</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int TrackPopupMenuEx(
    IntPtr hMenu,
    uint uFlags,
    int x,
    int y,
    IntPtr hWnd,
    IntPtr lptpm);

    // ===== 图标 =====


    /// <summary>向窗口异步投递消息 (弹菜单后补一个 WM_NULL, 让主循环知道菜单已处理完毕)</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    /// <summary>销毁图标句柄</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>shell32.dll 的托盘图标 API</summary>
public static class Shell32
{
    /// <summary>
    /// 托盘图标数据结构。
    /// 字段顺序与 <c>SizeConst</c> 必须与 SDK 完全一致, 否则 Shell_NotifyIcon 会静默失败
    /// (图标不出现且没有任何错误提示)。新增字段只能追加在末尾。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    public const uint NIM_ADD = 0;
    public const uint NIM_MODIFY = 1;
    public const uint NIM_DELETE = 2;
    public const uint NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 1;
    public const uint NIF_ICON = 2;
    public const uint NIF_TIP = 4;
    public const uint NIF_VERSION = 4;
    public const uint NIF_SHOWTIP = 0x80000000;
    /// <summary>本次修改带气泡正文 (与 szInfo/szInfoTitle/dwInfoFlags 配合)</summary>
    public const uint NIF_INFO = 0x00000010;
    /// <summary>气泡使用信息图标</summary>
    public const uint NIIF_INFO = 0x00000001;

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
}

/// <summary>Win32 窗口过程委托 (必须静态持有引用, 防止被 GC 回收)</summary>
public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

/// <summary>kernel32.dll: 模块句柄、线程 ID 与防休眠状态</summary>
public static class Kernel32
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ===== 电源/休眠控制 =====

    // SetThreadExecutionState 的标志位 (winbase.h)。
    // 这套 API 是微软为"防止系统休眠/息屏"专门提供的入口, 与"模拟鼠标移动"相比没有任何副作用:
    // 不产生输入事件、不移动光标、不干扰用户鼠标操作与截图框选。
    /// <summary>重置空闲计时器 (系统/显示器保持"在用"状态), 与其它标志组合使用</summary>
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
    /// <summary>强制显示器保持开启 (仅 ES_SYSTEM_REQUIRED 不阻止息屏)</summary>
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;
    /// <summary>不清除此前设置的状态, 直到显式调用本函数并只传该标志为止</summary>
    public const uint ES_CONTINUOUS = 0x80000000;

    /// <summary>
    /// 设置当前线程的执行状态需求。
    /// 注意语义是"线程级"的: 状态在<strong>调用它的那个线程</strong>上生效, 且该线程退出后自动失效。
    /// 因此必须由常驻线程 (本程序的主消息循环线程) 调用, 不能在临时线程里调用后指望长期有效。
    /// 返回 0 表示失败 (老系统不支持 / 被策略拦截)。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SetThreadExecutionState(uint esFlags);
}

/// <summary>前台窗口与焦点挂接 (覆盖层必须在 OnShown 里抢前台, 否则打字没反应)</summary>
public static class Foreground
{
    public const uint WM_NULL = 0x0000;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    /// <summary>把键盘焦点设到指定窗口 (须与 SetForegroundWindow 配合; 只对同线程窗口或已挂接线程有效)</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetFocus(IntPtr hWnd);
}

/// <summary>光标屏幕坐标</summary>
public static class CursorPos
{
    /// <summary>Win32 POINT 结构</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);
}

/// <summary>
/// DWM 窗口外观 (目前只用圆角)。
///
/// Win11 给顶级窗口 8px 圆角是系统行为, 但无边框窗体 (FormBorderStyle.None) 默认不参与,
/// 需要显式声明 DWMWA_WINDOW_CORNER_PREFERENCE。Win10 上该属性不存在, 调用返回失败 ——
/// 忽略即可, 窗口保持直角, 不能因此抛异常或影响启动。
/// </summary>
public static class Dwmapi
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>请求系统圆角 (仅 Win11 生效; 其它系统静默忽略)</summary>
    public static void TryRoundCorners(IntPtr hwnd) => TrySetCornerPreference(hwnd, DWMWCP_ROUND);

    /// <summary>
    /// 显式要求直角 (贴图窗口用)。
    /// 无边框窗口在系统默认策略下本是直角, 但 Win11 会给顶级窗口补 8px 圆角;
    /// 声明式固定比"靠默认"可靠, 且能防将来系统策略变化把截图像素四角裁掉。
    /// </summary>
    public static void TrySquareCorners(IntPtr hwnd) => TrySetCornerPreference(hwnd, DWMWCP_DONOTROUND);

    private static void TrySetCornerPreference(IntPtr hwnd, int preference)
    {
        if (hwnd == IntPtr.Zero)
            return;
        try
        {
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }
        catch (DllNotFoundException) { /* Win7 无 dwmapi: 保持直角 */ }
        catch (EntryPointNotFoundException) { /* Win10 无该属性: 保持直角 */ }
    }

    /// <summary>
    /// 让系统标题栏跟随深色主题。
    /// 不设的话会出现"深色内容 + 白色标题栏"的割裂 (截图里一眼就能看到)。
    /// Win10 1809 之前没有该属性, 返回失败忽略。
    /// </summary>
    public static void TryUseDarkTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        int enabled = 1;
        try
        {
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException) { /* Win7 无 dwmapi: 保持直角 */ }
        catch (EntryPointNotFoundException) { /* Win10 无该属性: 保持直角 */ }
    }
}
