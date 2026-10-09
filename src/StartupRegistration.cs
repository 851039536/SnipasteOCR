using Microsoft.Win32;

namespace SnipasteOcr;

/// <summary>
/// 开机自启: 通过当前用户的 Run 注册表键实现 (HKCU, 无需管理员权限)。
///
/// 为什么不用启动文件夹快捷方式: 需要创建 .lnk (要 COM/Shell 链接对象),
/// NativeAOT 下额外依赖且失败面更大; Run 键只写一个字符串, 最稳。
///
/// 为什么用 HKCU 而不是 HKLM: HKLM 需要管理员权限, 会带来 UAC 提权,
/// 与"单文件绿色小工具"的定位冲突。
///
/// 与 <see cref="SettingsStore"/> 一致的约定: <b>任何异常一律吞掉</b>。
/// 注册表被策略锁定 / 无权限时, 自启启不了可以接受, 但绝不能让程序崩溃或无法启动。
/// </summary>
public static class StartupRegistration
{
    /// <summary>Run 键下的值名 (= exe 文件名, 用户可在任务管理器"启动"里看到)</summary>
    private const string ValueName = "SnipasteOcr";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>当前可执行文件完整路径 (NativeAOT 单文件下即为 exe 自身路径)</summary>
    private static string ExecutablePath
    {
        get
        {
            try
            {
                // Environment.ProcessPath 在单文件/AOT 下返回 exe 本身;
                // Assembly.Location 在单文件下是空串, 不能用 (见 ILC 的 IL3000 告警)。
                string? p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p))
                    return p;

                return Path.Combine(AppContext.BaseDirectory, "SnipasteOcr.exe");
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>注册表里是否已登记自启 (且指向的就是当前 exe)</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(ValueName) is not string registered || registered.Length == 0)
                return false;

            // 路径与当前 exe 不一致时视为未启用 —— 换过位置的旧记录不该算数,
            // 否则勾选状态会骗人 (勾着但实际启动的是另一个文件)。
            return string.Equals(registered.Trim('"'), ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 写入/删除自启项。返回是否操作成功 (失败不抛异常)。
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
                return false;

            if (enabled)
            {
                string path = ExecutablePath;
                if (path.Length == 0)
                    return false;

                // 用引号包裹: 路径含空格时不加引号会被系统当成"第一个空格前是程序名"
                key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
            }
            else
            {
                // 不存在时 DeleteValue 会抛, 先判断
                if (key.GetValue(ValueName) is not null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
