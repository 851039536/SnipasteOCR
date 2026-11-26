using System.Text;

namespace SnipasteOcr;

/// <summary>
/// 最小崩溃日志: 只记录未处理异常与启动失败, 不记录业务事件。
///
/// 为什么需要: 托盘常驻程序崩溃后用户只能看到"图标消失了", 没有任何现场可查;
/// 而 <c>Program</c> 的注释曾写着"所有关键事件写日志", 实际一条都没有。
/// 为什么不做完整日志: 本工具的核心卖点是离线/轻量, 不应为排查在正常路径持续写盘。
///
/// 约束:
/// - 任何 IO 异常一律吞掉 —— 日志失败绝不能影响主流程;
/// - 单文件上限 64KB, 超出时整文件重写 (不轮转、不产生多个文件);
/// - 不用 Console/Trace/EventLog (NativeAOT 裁剪与体积考量), 只写纯文本。
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 64 * 1024;
    private const int MaxExceptionChars = 16 * 1024;

    private static readonly object Gate = new();
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>日志写入中标记: 防御"记录日志本身抛异常 -> 再次记录"的递归</summary>
    private static bool _writing;

    /// <summary>单文件日志路径: %LOCALAPPDATA%\SnipasteOCR\last-error.log</summary>
    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SnipasteOCR", "last-error.log");

    /// <summary>记录一条异常; 绝不抛出 (记录失败时静默放弃)</summary>
    public static void Write(string context, Exception? exception)
    {
        if (_writing)
            return;

        _writing = true;
        try
        {
            lock (Gate)
            {
                string directory = System.IO.Path.GetDirectoryName(FilePath) ?? ".";
                Directory.CreateDirectory(directory);

                string entry = Format(context, exception);
                bool rewrite = false;
                try
                {
                    var info = new FileInfo(FilePath);
                    rewrite = info.Exists && info.Length + entry.Length > MaxBytes;
                }
                catch
                {
                    // 取不到长度就按追加处理; 下次写入还有机会重写
                }

                if (rewrite)
                    File.WriteAllText(FilePath, entry, Utf8);
                else
                    File.AppendAllText(FilePath, entry, Utf8);
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
        finally
        {
            _writing = false;
        }
    }

    private static string Format(string context, Exception? exception)
    {
        string detail = exception?.ToString() ?? "(无异常对象)";
        if (detail.Length > MaxExceptionChars)
            detail = detail[..MaxExceptionChars] + "\u2026";

        return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}" +
               $"{detail}{Environment.NewLine}{Environment.NewLine}";
    }
}
