namespace SnipasteOcr;

/// <summary>
/// 截图模式。原先用 bool 只能表达"识别/复制图片"两种, 扩展功能时无法承载第三种模式,
/// 因此改为枚举 (后续新增模式只需在此追加)。
/// </summary>
public enum SnipMode
{
    /// <summary>框选后打开结果窗口做 OCR 识别</summary>
    Ocr = 0,

    /// <summary>框选后直接把 PNG 写入剪贴板</summary>
    Image = 1,
}
