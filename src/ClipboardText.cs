namespace SnipasteOcr;

/// <summary>
/// 剪贴板文本写入的唯一入口。
///
/// Windows 剪贴板文本约定用 CRLF, 而内部选区/段落导出的字符串可能混用 \n 与 \r\n;
/// 各调用点各写一份换行转换, 同一份内容从不同按钮复制出去换行就会不一致。
/// </summary>
internal static class ClipboardText
{
    /// <summary>把任意换行风格统一为 Windows 约定 (CRLF)</summary>
    public static string Normalize(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);

    /// <summary>写入剪贴板; 剪贴板被其它进程占用时返回 false, 由调用方提示</summary>
    public static bool TrySet(string text)
    {
        try
        {
            Clipboard.SetText(Normalize(text));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
