using System.Text.Json;
using System.Text.Json.Serialization;

namespace SnipasteOcr;

/// <summary>
/// 应用设置模型。字段均为简单类型, 便于 JSON 源生成序列化。
/// 新增字段时必须有默认值, 保证旧配置文件反序列化后仍可用。
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// 目标语言为「自动」: 中文原文译成英文, 英文原文译成中文。
    /// 用一个可读字符串存进配置, 用户手改 settings.json 时也能看懂。
    /// </summary>
    public const string AutoTranslateTarget = "自动（中↔英）";

    /// <summary>上次选择的识别模型档位</summary>
    public OcrModelProfile ModelProfile { get; set; } = OcrModelProfile.Medium;

    /// <summary>截图并识别热键的虚拟键码 (默认 F1 = 0x70)</summary>
    public int HotKeyOcr { get; set; } = 0x70;

    /// <summary>
    /// 截图标注热键的虚拟键码 (默认 F3 = 0x72)。
    /// 注意 VK_F1=0x70 起算, 所以 VK_F3 = 0x72 而不是 0x73 —— 0x73 是 F4。
    /// 踩过的坑: 早期版本把 0x73 写成 F3, 于是"F3"实际注册成了 F4:
    /// 用户按 F3 毫无反应, 按 F4 反而触发截图, 且设置界面显示"F3"让人完全摸不着头脑。
    /// </summary>
    public int HotKeyImage { get; set; } = 0x72;

    /// <summary>截图识别热键的修饰符 (0 = 无; MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8)</summary>
    public int HotKeyModifiers { get; set; } = 0;

    /// <summary>
    /// 截图标注热键的修饰符。
    ///
    /// 早期版本两个热键<strong>共用</strong> <see cref="HotKeyModifiers"/>, 但对话框允许分别录制,
    /// 于是出现过"界面显示 F3、实际注册的是 Ctrl+F3"的假显示。现在各自独立。
    ///
    /// 默认 -1 = "配置里还没有这一项", 由 <see cref="SettingsStore.Load"/> 迁移为与识别热键相同 ——
    /// 旧用户升级后行为完全不变, 不会因为反序列化成 0 而悄悄丢掉修饰键。
    /// </summary>
    public int HotKeyImageModifiers { get; set; } = -1;

    /// <summary>OCR 结果是否按阅读顺序 (上→下, 左→右) 重排</summary>
    public bool SortReadingOrder { get; set; } = true;

    /// <summary>
    /// 单击识别结果窗口中的文本块时自动复制该块。
    /// 默认开启 —— 这是用户最常用的动作; 但单击就改写剪贴板是副作用,
    /// 托盘菜单提供开关, 便于只想浏览结果的用户关掉。旧配置没有该字段时靠属性初值得到 true。
    /// </summary>
    public bool ClickToCopy { get; set; } = true;

    /// <summary>
    /// 翻译 API Key 的 DPAPI 密文 (Base64)。
    ///
    /// 这里只能存密文: <see cref="SettingsJsonContext"/> 会把本类所有属性原样写进
    /// %LOCALAPPDATA%\SnipasteOCR\settings.json, 明文 Key 会被同机其它进程读到。
    /// 解密失败 (换机器/换账户) 时由界面提示重新输入, 绝不回退明文。
    /// </summary>
    public string TranslateApiKeyProtected { get; set; } = "";

    /// <summary>
    /// 翻译模型名。默认值跟随 DeepSeek 官方当前模型, 但官方会调整/合并模型名,
    /// 因此放进配置而不是写死在请求代码或界面文案里。
    /// </summary>
    public string TranslateModel { get; set; } = "deepseek-flash";

    /// <summary>
    /// 翻译目标语言。默认自动判断方向 (中文→英文 / 英文→中文);
    /// 也可固定为 English / 简体中文 / 日本語 / 한국어 或自定义语言名。
    /// </summary>
    public string TranslateTargetLanguage { get; set; } = AutoTranslateTarget;

    /// <summary>
    /// 是否已同意把识别文字发送到 DeepSeek 翻译。
    /// 默认 false: 首次点「翻译」时明确告知并询问, 拒绝则不发请求。
    /// </summary>
    public bool TranslateConsentGiven { get; set; }

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
        AppSettings loaded;
        try
        {
            if (!File.Exists(FilePath))
                return Normalize(new AppSettings());

            string json = File.ReadAllText(FilePath);
            loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                     ?? new AppSettings();
        }
        catch
        {
            // 文件损坏 / 无权限 / 反序列化失败: 用默认值, 不影响启动
            return Normalize(new AppSettings());
        }

        Normalize(loaded);

        // 一次性迁移: 早期版本把"F3"错写成 0x73 (实为 F4), 该错误默认值已经落盘到用户配置里。
        // 若只改代码里的默认值, 老用户按 F3 仍然没反应 —— 必须把已存盘的错误值也纠正过来。
        //
        // 判据刻意收得很窄: 只有"整组值恰好等于旧版默认组合"才迁移。
        // 用户若主动改过 OCR 热键或加过修饰键, 说明这份配置被手工调过, 此时 0x73 很可能是
        // 他真的想用 F4, 就应当尊重原意、不动它 (误迁移比不迁移更糟: 会悄悄改掉用户的选择)。
        if (loaded.HotKeyOcr == 0x70 && loaded.HotKeyImage == 0x73 && loaded.HotKeyModifiers == 0)
        {
            loaded.HotKeyImage = 0x72;   // 0x73(F4) -> 0x72(F3)

            // 立即落盘, 让修正固化下来 (只做一次, 之后不再命中该分支)。
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

    /// <summary>
    /// 补齐"配置里没有"的新字段。所有加载路径 (文件 / 首次运行 / 损坏回退) 都必须经过这里,
    /// 否则未归一化的哨兵值 (-1) 会被当成修饰符位掩码传给 RegisterHotKey。
    /// </summary>
    private static AppSettings Normalize(AppSettings s)
    {
        // -1 = 旧配置没有这一项。当年两个热键共用修饰键, 继承即可, 升级后行为完全不变;
        // 用户显式存过的 0 (确实不要修饰键) 不受影响。
        if (s.HotKeyImageModifiers < 0)
            s.HotKeyImageModifiers = s.HotKeyModifiers;
        return s;
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
