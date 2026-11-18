namespace SnipasteOcr;

/// <summary>
/// 活着的贴图窗口登记表。
///
/// 存在的唯一理由是<b>鼠标穿透无法自愈</b>: 开启 WS_EX_TRANSPARENT 后窗口收不到任何鼠标消息,
/// 用户右键关不掉、也点不到菜单, 只能从托盘统一恢复。
/// 用弱引用持有: 贴图关闭后不必显式反注册也不会泄漏。
/// </summary>
public static class PinnedRegistry
{
    private static readonly List<WeakReference<PinnedWindowBase>> _items = [];

    /// <summary>登记一个贴图窗口</summary>
    public static void Register(PinnedWindowBase window)
    {
        Prune();
        _items.Add(new WeakReference<PinnedWindowBase>(window));
    }

    /// <summary>当前存活的贴图数量 (已关闭的自动剔除)</summary>
    public static int Count
    {
        get
        {
            Prune();
            return _items.Count;
        }
    }

    /// <summary>是否有贴图处于穿透状态 (托盘菜单据此决定是否可用)</summary>
    public static bool AnyClickThrough
    {
        get
        {
            Prune();
            foreach (var item in _items)
            {
                if (item.TryGetTarget(out var window) && window.ClickThrough)
                    return true;
            }
            return false;
        }
    }

    /// <summary>取消全部贴图的鼠标穿透 —— 穿透后唯一的"逃生口"</summary>
    public static int ClearClickThrough()
    {
        Prune();
        int restored = 0;
        foreach (var item in _items)
        {
            if (!item.TryGetTarget(out var window) || !window.ClickThrough)
                continue;
            window.ClickThrough = false;
            restored++;
        }
        return restored;
    }

    /// <summary>关闭全部贴图</summary>
    public static int CloseAll()
    {
        var alive = Alive();
        foreach (var window in alive)
            window.Close();
        _items.Clear();
        return alive.Count;
    }

    /// <summary>剔除已被回收的条目</summary>
    private static void Prune()
        => _items.RemoveAll(item => !item.TryGetTarget(out _));

    private static List<PinnedWindowBase> Alive()
    {
        Prune();
        var list = new List<PinnedWindowBase>(_items.Count);
        foreach (var item in _items)
        {
            if (item.TryGetTarget(out var window))
                list.Add(window);
        }
        return list;
    }
}
