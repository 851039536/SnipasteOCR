namespace SnipasteOcr;

/// <summary>
/// 识别模型档位 (可在托盘菜单中切换)。
///
/// 单独成文件而不是放在 <see cref="OcrService"/> 里: 它是设置模型 (<see cref="AppSettings"/>)
/// 的字段类型, 而设置的自检不该被迫链接整个 OCR 引擎 (及其原生模型包)。
/// </summary>
public enum OcrModelProfile
{
    /// <summary>ChineseV6 Medium (132MB): 精度优先, 小字/模糊/形近字更准</summary>
    Medium = 0,
    /// <summary>ChineseV6 Tiny (6MB): 速度优先, 推理约快 9 倍</summary>
    Tiny = 1,
}