using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 防止电脑休眠 / 息屏: 用 Windows 官方 API <c>SetThreadExecutionState</c> 声明
/// "本线程正在使用中", 系统据此不进入睡眠、不关闭显示器。
///
/// <para>
/// <b>为什么不用"定时微移鼠标"</b>: 模拟鼠标移动虽然也能骗过空闲检测, 但它有真实副作用 ——
/// 会抢走用户正在操作的鼠标 (拖拽/框选被中断)、屏幕录制时画面抖动、全屏游戏/视频体验被破坏。
/// 更麻烦的是本程序自己就有全屏覆盖层 (F1/F3 框选与标注), 鼠标被程序偷偷移动会直接破坏框选。
/// <c>SetThreadExecutionState</c> 不产生任何输入事件、不移动光标, 是同样目的下唯一正确的入口。
/// </para>
///
/// <para>
/// <b>为什么不需要定时器</b>: 配合 <see cref="Kernel32.ES_CONTINUOUS"/> 只需调用<strong>一次</strong>,
/// 状态会一直保持到显式清除 —— 也就是说这里没有"每隔 N 秒动一下"的轮询, 自然也没有轮询中断
/// (睡眠边界、定时器漂移) 导致失效的问题。
/// </para>
///
/// <para>
/// <b>线程亲和性 (最容易踩的坑)</b>: 该 API 的状态是<strong>线程级</strong>的, 且线程一退出就失效。
/// 所以必须由常驻线程 (本程序的 <c>Program.Main</c> 主消息循环线程) 调用。
/// 若在临时线程里调用, 线程结束的瞬间防休眠就悄悄失效了 —— 表现为"开了但没用", 且没有任何报错。
/// </para>
///
/// <para>
/// <b>能力边界 (如实告知, 不假装万能)</b>: 本机制只阻止"空闲触发的休眠与息屏"。
/// 它<b>不能</b>阻止: 合上笔记本盖子、按下电源键、开始菜单的"睡眠/休眠"、组策略或屏保触发的锁屏。
/// 因此托盘菜单的文案用"防止电脑休眠", 而不是"保持屏幕常亮"之类更夸张的承诺。
/// </para>
///
/// <para>
/// <b>失败处理</b>: 与 <see cref="SettingsStore"/> / <see cref="StartupRegistration"/> 一致 ——
/// 任何异常与失败一律吞掉, 只是防休眠不生效, 绝不能让程序崩溃。
/// </para>
/// </summary>
public static class SleepPreventer
{
    /// <summary>
    /// 当前"保持唤醒"是否处于开启状态。
    /// 以<strong>实际调用结果</strong>为准而非以用户意愿为准: 调用失败时这里保持 false,
    /// 菜单勾选随之回到未勾选, 不会出现"勾着但根本没生效"的骗人状态
    /// (与开机自启"以注册表实际状态为准"同一约定)。
    /// </summary>
    public static bool IsActive { get; private set; }

    /// <summary>
    /// 开启 / 关闭防休眠, 返回操作后<strong>实际生效</strong>的状态。
    ///
    /// <para>必须在常驻线程上调用 (见类型注释的"线程亲和性")。</para>
    ///
    /// <para>
    /// 注意关闭时只传 <see cref="Kernel32.ES_CONTINUOUS"/>: 这是 API 规定的清除方式,
    /// 表示"之后不再有持续的执行状态需求", 空转的空闲计时器恢复正常。
    /// 若传 0, 部分系统上不会清除而是被忽略, 变成"关不掉"。
    /// </para>
    /// </summary>
    /// <param name="enabled">true = 开启防休眠, false = 恢复系统正常休眠行为</param>
    public static bool Set(bool enabled)
    {
        try
        {
            uint result = enabled
                ? Kernel32.SetThreadExecutionState(
                    // 一个都不能少: 只给 SYSTEM_REQUIRED 时显示器照样会息屏, 用户看到的就是"屏幕黑了"
                    Kernel32.ES_CONTINUOUS | Kernel32.ES_SYSTEM_REQUIRED | Kernel32.ES_DISPLAY_REQUIRED)
                : Kernel32.SetThreadExecutionState(Kernel32.ES_CONTINUOUS);

            // API 文档: 成功返回非零 (具体是调用前的旧状态), 失败返回 0
            IsActive = result != 0 && enabled;
            return IsActive;
        }
        catch
        {
            // 极端情况 (API 缺失/被策略拦截): 防休眠不生效即可, 不影响程序其它功能
            IsActive = false;
            return false;
        }
    }

    /// <summary>
    /// 按设置恢复防休眠开关 (程序启动时调用一次)。
    /// 设置文件里写着开启、但本次调用失败时返回 false, 调用方应据此把设置改回 false 并同步菜单,
    /// 保证设置文件、菜单勾选、真实状态三者一致。
    /// </summary>
    public static bool Restore(bool wanted) => wanted && Set(true);
}
