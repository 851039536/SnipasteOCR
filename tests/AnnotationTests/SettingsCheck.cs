// 校验设置模型: 新增「截图标注修饰键」的默认值、旧配置迁移与 JSON 往返。
// 迁移是高风险区 (AGENTS.md 明确要求新增字段对旧配置可用), 且它不依赖桌面会话。
using System.Reflection;
using System.Text.Json;
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class SettingsCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        // Normalize 是加载路径的最后一步 (私有), 按真实调用方式验证
        var normalize = typeof(SettingsStore).GetMethod("Normalize", BindingFlags.NonPublic | BindingFlags.Static)!;
        AppSettings Normalize(AppSettings s) => (AppSettings)normalize.Invoke(null, [s])!;

        // ===== 1. 旧配置 (没有 hotKeyImageModifiers) 必须继承原修饰键 =====
        // 当年两个热键共用一套修饰键, 升级后若读成 0, 用户的 Ctrl+F3 会悄悄变成裸 F3。
        {
            const string legacy = """
                {
                  "modelProfile": 0,
                  "hotKeyOcr": 81,
                  "hotKeyImage": 83,
                  "hotKeyModifiers": 3,
                  "sortReadingOrder": true,
                  "runAtStartup": false
                }
                """;
            var s = JsonSerializer.Deserialize(legacy, SettingsJsonContext.Default.AppSettings)!;
            Check(s.HotKeyImageModifiers == -1, "旧配置缺字段时读出哨兵值 -1 (尚未迁移)");

            Normalize(s);
            Check(s.HotKeyOcr == 81 && s.HotKeyImage == 83, "旧配置的键码保持不变");
            Check(s.HotKeyImageModifiers == 3,
                $"旧配置继承原修饰键 (实得 {s.HotKeyImageModifiers}, 期望 3)");
        }

        // ===== 2. 用户显式存过的 0 不能被迁移改写 =====
        {
            const string saved = """
                { "hotKeyOcr": 81, "hotKeyImage": 83, "hotKeyModifiers": 3, "hotKeyImageModifiers": 0 }
                """;
            var s = Normalize(JsonSerializer.Deserialize(saved, SettingsJsonContext.Default.AppSettings)!);
            Check(s.HotKeyImageModifiers == 0,
                $"显式保存的「无修饰键」不被覆盖 (实得 {s.HotKeyImageModifiers})");
        }

        // ===== 3. 首次运行 / 配置损坏回退 =====
        // 默认对象也必须归一化, 否则 -1 会被当成位掩码传给 RegisterHotKey。
        {
            var s = Normalize(new AppSettings());
            Check(s.HotKeyImageModifiers == 0,
                $"首次运行时哨兵值被归一化 (实得 {s.HotKeyImageModifiers})");
            Check(s.HotKeyOcr == 0x70 && s.HotKeyImage == 0x72, "默认热键仍为 F1 / F3");
        }

        // ===== 4. 新字段必须真的落盘且往返一致 =====
        {
            var original = new AppSettings { HotKeyModifiers = 3, HotKeyImageModifiers = 0 };
            string json = JsonSerializer.Serialize(original, SettingsJsonContext.Default.AppSettings);
            Check(json.Contains("hotKeyImageModifiers", StringComparison.OrdinalIgnoreCase),
                "新字段写入配置文件");

            var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
            Check(back.HotKeyModifiers == 3 && back.HotKeyImageModifiers == 0,
                $"两组修饰键往返一致 (实得 {back.HotKeyModifiers}/{back.HotKeyImageModifiers})");
        }

        // ===== 5. 单击复制开关: 旧配置缺字段时为 true, 显式 false 保留 =====
        // 不能让"读取旧配置"把新功能悄悄关掉, 也不能把用户主动关掉的开关重新打开。
        {
            const string legacy = """
                { "hotKeyOcr": 81, "hotKeyImage": 83 }
                """;
            var old = JsonSerializer.Deserialize(legacy, SettingsJsonContext.Default.AppSettings)!;
            Check(old.ClickToCopy, "旧配置缺 ClickToCopy 时默认为 true");

            const string disabled = """
                { "clickToCopy": false }
                """;
            var off = JsonSerializer.Deserialize(disabled, SettingsJsonContext.Default.AppSettings)!;
            Check(!off.ClickToCopy, "显式保存的 false 不被默认值覆盖");

            Check(new AppSettings().ClickToCopy, "新配置默认开启单击复制");

            string json = JsonSerializer.Serialize(new AppSettings { ClickToCopy = false }, SettingsJsonContext.Default.AppSettings);
            Check(json.Contains("clickToCopy", StringComparison.OrdinalIgnoreCase), "新字段写入配置文件");
            var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)!;
            Check(!back.ClickToCopy, "单击复制开关往返一致");
        }

        // ===== 6. 单击行为互斥: 翻译开启时必须关闭复制 =====
        // 旧版本允许两个开关同时为 true, 实际以翻译优先, 但托盘仍显示"单击复制"已勾选。
        // 加载时收敛为互斥, 保证勾选与实际行为一致。
        {
            var both = Normalize(new AppSettings { ClickToCopy = true, ClickToTranslate = true });
            Check(!both.ClickToCopy, "单击翻译开启时自动关闭单击复制");
            Check(both.ClickToTranslate, "单击翻译开关本身保持不变");

            var copyOnly = Normalize(new AppSettings { ClickToCopy = true, ClickToTranslate = false });
            Check(copyOnly.ClickToCopy && !copyOnly.ClickToTranslate,
                "仅开启单击复制时两项状态不变");
        }

        return runner.ToResult();
    }
}