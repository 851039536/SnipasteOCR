using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnipasteOcr;

/// <summary>
/// 应用设置模型。字段均为简单类型, 便于 JSON 源生成序列化。
/// 新增字段时必须有默认值, 保证旧配置文件反序列化后仍可用。
/// </summary>
public sealed class AppSettings
{
    /// <summary>上次选择的识别模型档位</summary>
    public OcrModelProfile ModelProfile { get; set; } = OcrModelProfile.Medium;

    /// <summary>截图并识别热键的虚拟键码 (默认 F1 = 0x70)</summary>
    public int HotKeyOcr { get; set; } = 0x70;

    /// <summary>
    /// 仅截图热键的虚拟键码 (默认 F3 = 0x72)。
    /// 注意 VK_F1=0x70 起算, 所以 VK_F3 = 0x72 而不是 0x73 —— 0x73 是 F4。
    /// 踩过的坑: 早期版本把 0x73 写成 F3, 于是"F3"实际注册成了 F4:
    /// 用户按 F3 毫无反应, 按 F4 反而触发截图, 且设置界面显示"F3"让人完全摸不着头脑。
    /// </summary>
    public int HotKeyImage { get; set; } = 0x72;

    /// <summary>热键修饰符 (0 = 无; MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8)</summary>
    public int HotKeyModifiers { get; set; } = 0;

    /// <summary>OCR 结果是否按阅读顺序 (上→下, 左→右) 重排</summary>
    public bool SortReadingOrder { get; set; } = true;

    /// <summary>跟随系统的开机自启</summary>
    public bool RunAtStartup { get; set; }

    /// <summary>
    /// 防止电脑休眠/息屏 (SetThreadExecutionState)。默认关闭 ——
    /// 默认值必须是"不改变系统原有电源行为", 不能替用户做主把机器变成永不睡眠。
    /// 新增字段给了默认值, 旧的 settings.json 反序列化后仍然可用。
    /// </summary>
    public bool PreventSleep { get; set; }
}

/// <summary>
/// NativeAOT 下的 JSON 源生成上下文。
/// 关键: NativeAOT 会裁剪掉基于反射的序列化, 必须用源生成,
/// 否则运行时抛 NotSupportedException / 裁剪告警。
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 设置的加载/保存 (落盘到 %LOCALAPPDATA%\SnipasteOCR\settings.json)。
/// 所有异常都被吞掉并回退默认值 —— 设置读写失败绝不能让程序崩溃或无法启动。
/// </summary>
public static class SettingsStore
{
    private static readonly object _gate = new();
    private static AppSettings? _current;

    /// <summary>配置文件路径</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnipasteOCR",
        "settings.json");

    /// <summary>当前设置 (首次访问时从磁盘加载, 失败则用默认值)</summary>
    public static AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Load();
            }
        }
    }

    /// <summary>从磁盘读取设置; 任何失败都回退到默认值</summary>
    private static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            string json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                         ?? new AppSettings();

            // 一次性迁移: 早期版本把"F3"错写成 0x73 (实为 F4), 该错误默认值已经落盘到用户配置里。
            // 若只改代码里的默认值, 老用户按 F3 仍然没反应 —— 必须把已存盘的错误值也纠正过来。
            //
            // 判据刻意收得很窄: 只有"整组值恰好等于旧版默认组合"才迁移。
            // 用户若主动改过 OCR 热键或加过修饰键, 说明这份配置被手工调过, 此时 0x73 很可能是
            // 他真的想用 F4, 就应当尊重原意、不动它 (误迁移比不迁移更糟: 会悄悄改掉用户的选择)。
            if (loaded.HotKeyOcr == 0x70 && loaded.HotKeyImage == 0x73 && loaded.HotKeyModifiers == 0)
            {
                loaded.HotKeyImage = 0x72;   // 0x73(F4) -> 0x72(F3)

                // 立即落盘, 让修正在下次启动前就固化下来 (只做一次, 之后不再命中该分支)。
                // 这里刻意不走 Save(): Save() 依赖 _current, 而此刻 _current 尚未赋值,
                // 直接写文件才是正确的时机, 也避免在锁内再进锁。
                try
                {
                    File.WriteAllText(FilePath,
                        JsonSerializer.Serialize(loaded, SettingsJsonContext.Default.AppSettings));
                }
                catch
                {
                    // 写不进去(只读/占用)也不影响本次运行: 内存里已经是正确值,
                    // 下次启动会再次尝试迁移, 行为幂等。
                }
            }

            return loaded;
        }
        catch
        {
            // 文件损坏 / 无权限 / 反序列化失败: 用默认值, 不影响启动
            return new AppSettings();
        }
    }

    /// <summary>把当前设置写回磁盘; 失败静默忽略</summary>
    public static void Save()
    {
        lock (_gate)
        {
            if (_current is null)
                return;
            try
            {
                string? dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(FilePath, JsonSerializer.Serialize(_current, SettingsJsonContext.Default.AppSettings));
            }
            catch
            {
                // 磁盘只读 / 被占用: 忽略, 仅本次不持久化
            }
        }
    }

    /// <summary>就地修改设置并立即保存</summary>
    public static void Update(Action<AppSettings> mutate)
    {
        lock (_gate)
        {
            mutate(Current);
        }
        Save();
    }
}
