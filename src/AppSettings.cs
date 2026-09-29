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

    /// <summary>仅截图热键的虚拟键码 (默认 F2 = 0x71)</summary>
    public int HotKeyImage { get; set; } = 0x71;

    /// <summary>热键修饰符 (0 = 无; MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8)</summary>
    public int HotKeyModifiers { get; set; } = 0;

    /// <summary>OCR 结果是否按阅读顺序 (上→下, 左→右) 重排</summary>
    public bool SortReadingOrder { get; set; } = true;

    /// <summary>跟随系统的开机自启</summary>
    public bool RunAtStartup { get; set; }
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
            return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                   ?? new AppSettings();
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
