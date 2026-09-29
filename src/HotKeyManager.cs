using System.Runtime.InteropServices;
using System.Text;
using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 全局热键注册/重注册管理。
/// 支持运行期更换热键 (先注册新的, 成功后再注销旧的, 避免"换失败导致两个都没了")。
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    /// <summary>热键动作</summary>
    public enum Action
    {
        SnipOcr,
        SnipImage,
    }

    private readonly IntPtr _hwnd;
    private readonly Dictionary<Action, int> _ids = new()
    {
        [Action.SnipOcr] = (int)HotKeyId.SnipOcr,
        [Action.SnipImage] = (int)HotKeyId.SnipImage,
    };
    // 当前已注册成功的 (动作 -> (修饰符, 虚拟键))
    private readonly Dictionary<Action, (uint Mods, uint Vk)> _registered = new();

    public HotKeyManager(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>注册两个热键; 返回失败说明列表 (空 = 全部成功)</summary>
    public List<string> RegisterAll(int ocrVk, int imageVk, int modifiers)
    {
        var failures = new List<string>();
        if (!TryRegister(Action.SnipOcr, (uint)modifiers, (uint)ocrVk, out string e1))
            failures.Add($"截图识别热键 ({Describe((uint)modifiers, (uint)ocrVk)}) 注册失败: {e1}");
        if (!TryRegister(Action.SnipImage, (uint)modifiers, (uint)imageVk, out string e2))
            failures.Add($"仅截图热键 ({Describe((uint)modifiers, (uint)imageVk)}) 注册失败: {e2}");
        return failures;
    }

    /// <summary>
    /// 注册单个热键。若该项已注册, 先尝试注册新的; 只有新注册成功才注销旧的,
    /// 这样换键失败时原热键仍然可用。
    /// </summary>
    public bool TryRegister(Action action, uint mods, uint vk, out string error)
    {
        error = string.Empty;
        int id = _ids[action];

        bool had = _registered.TryGetValue(action, out var old);
        // 目标与原热键完全相同 -> 无需操作
        if (had && old.Mods == mods && old.Vk == vk)
            return true;

        // 先注册新的 (用不同的 id, 避免与原注册冲突)
        int probeId = id + 0x100;
        if (!User32.RegisterHotKey(_hwnd, probeId, mods | User32.MOD_NOREPEAT, vk))
        {
            int err = Marshal.GetLastWin32Error();
            error = err switch
            {
                1409 => "已被其它程序占用",
                1400 => "热键参数无效",
                _ => $"Win32 错误 {err}",
            };
            return false;
        }

        // 新的注册成功: 注销探针 id 与旧注册, 然后用正式 id 注册
        User32.UnregisterHotKey(_hwnd, probeId);
        if (had)
            User32.UnregisterHotKey(_hwnd, id);

        if (!User32.RegisterHotKey(_hwnd, id, mods | User32.MOD_NOREPEAT, vk))
        {
            // 理论上不会发生 (刚 probe 成功); 失败则回滚到旧设置
            int err = Marshal.GetLastWin32Error();
            if (had)
            {
                User32.RegisterHotKey(_hwnd, id, old.Mods | User32.MOD_NOREPEAT, old.Vk);
                _registered[action] = old;
            }
            error = $"Win32 错误 {err}";
            return false;
        }

        _registered[action] = (mods, vk);
        return true;
    }

    /// <summary>按热键 ID 判断触发的是哪个动作</summary>
    public bool TryResolve(int hotKeyId, out Action action)
    {
        foreach (var kv in _ids)
        {
            if (kv.Value == hotKeyId)
            {
                action = kv.Key;
                return true;
            }
        }
        action = default;
        return false;
    }

    /// <summary>把修饰符+虚拟键渲染成可读文本 (如 "Ctrl+Alt+Q")</summary>
    public static string Describe(uint mods, uint vk)
    {
        var sb = new StringBuilder();
        if ((mods & User32.MOD_CONTROL) != 0) sb.Append("Ctrl+");
        if ((mods & User32.MOD_ALT) != 0) sb.Append("Alt+");
        if ((mods & User32.MOD_SHIFT) != 0) sb.Append("Shift+");
        if ((mods & User32.MOD_WIN) != 0) sb.Append("Win+");
        sb.Append(KeyName(vk));
        return sb.ToString();
    }

    /// <summary>虚拟键码 -> 可读键名</summary>
    public static string KeyName(uint vk) => vk switch
    {
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",           // F1..F12
        0x30 => "0", 0x31 => "1", 0x32 => "2", 0x33 => "3", 0x34 => "4",
        0x35 => "5", 0x36 => "6", 0x37 => "7", 0x38 => "8", 0x39 => "9",
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),      // A..Z
        0x20 => "Space", 0x0D => "Enter", 0x1B => "Esc", 0x09 => "Tab",
        0x2C => "PrintScreen", 0x13 => "Pause", 0x91 => "ScrollLock",
        0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        0x2D => "Insert", 0x2E => "Delete",
        0x25 => "Left", 0x26 => "Up", 0x27 => "Right", 0x28 => "Down",
        0xC0 => "`", 0xBD => "-", 0xBB => "=", 0xDB => "[", 0xDD => "]",
        0xDC => "\\", 0xBA => ";", 0xDE => "'", 0xBC => ",", 0xBE => ".", 0xBF => "/",
        _ => $"VK(0x{vk:X2})",
    };

    /// <summary>注销全部热键</summary>
    public void Dispose()
    {
        foreach (var kv in _registered)
            User32.UnregisterHotKey(_hwnd, _ids[kv.Key]);
        _registered.Clear();
    }
}
