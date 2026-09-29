using System.Drawing.Text;

namespace SnipasteOcr.Annotations;

/// <summary>
/// 界面字体解析。
///
/// 背景: 直接 <c>new Font("Microsoft YaHei UI", ...)</c> 在字体缺失时会<b>静默回退</b>到
/// Microsoft Sans Serif, 既可能显示不出中文, 也不会报错 —— 排查成本很高。
/// 这里改为按候选列表逐个探测, 选中第一个真正存在且能承载中文的字体并缓存。
/// </summary>
internal static class UiFont
{
    /// <summary>候选字体, 按优先级排列 (覆盖简中/繁体/日文 Windows 的常见内置字体)</summary>
    private static readonly string[] Candidates =
    [
        "Microsoft YaHei UI",   // Win10/11 默认中文界面字体
        "Microsoft YaHei",      // 微软雅黑 (部分系统只注册这个名字)
        "SimSun",               // 宋体 (老系统兜底)
        "SimHei",               // 黑体
        "Segoe UI",             // 无中文字体时的西文兜底 (中文会由 GDI 字体链接补上)
        "Tahoma",
        "Arial",
    ];

    private static readonly object Gate = new();
    private static string? _resolved;
    private static bool _resolvedDone;

    /// <summary>实际可用的字体族名 (首次调用时探测并缓存)</summary>
    public static string FamilyName
    {
        get
        {
            lock (Gate)
            {
                if (_resolvedDone)
                    return _resolved!;

                _resolved = Resolve();
                _resolvedDone = true;
                return _resolved;
            }
        }
    }

    /// <summary>逐个探测候选字体, 返回第一个已安装的; 全都没有则交给 GDI 默认 (null 表示用系统默认)</summary>
    private static string Resolve()
    {
        try
        {
            using var installed = new InstalledFontCollection();
            var names = new HashSet<string>(
                installed.Families.Select(f => f.Name),
                StringComparer.OrdinalIgnoreCase);

            foreach (string c in Candidates)
            {
                if (names.Contains(c))
                    return c;
            }
        }
        catch
        {
            // 枚举字体失败: 直接用首选名, 交给 GDI 自行回退
        }

        return Candidates[0];
    }

    /// <summary>
    /// 按像素字号创建界面字体。
    /// 返回的 Font 由调用方负责释放 (参数无效时退回系统默认字体, 保证不抛异常)。
    /// </summary>
    public static Font Create(float pixelSize)
    {
        try
        {
            return new Font(FamilyName, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        catch
        {
            // 极端情况 (字体句柄耗尽等): 退回系统默认, 界面仍可用
            return new Font(SystemFonts.DefaultFont.FontFamily, pixelSize, FontStyle.Regular, GraphicsUnit.Pixel);
        }
    }
}
