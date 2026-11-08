namespace SnipasteOcr;

/// <summary>
/// 识别置信度的展示规则 (纯函数): 低于阈值的文本块用异色框标出, 悬停时在状态栏显示百分比。
///
/// 只做展示, 不默认过滤低分文本 —— 过滤会让用户完全看不到"可能识别错"的内容,
/// 比看到一次可疑结果更糟。阈值先用 0.7, 需要真实样本再校准。
/// </summary>
internal static class OcrConfidence
{
    /// <summary>低于该值视为"需要人工核对"</summary>
    public const float LowThreshold = 0.7f;

    /// <summary>是否低置信度</summary>
    public static bool IsLow(float score) => score < LowThreshold;

    /// <summary>格式化为「置信度 62%」; 分数异常 (NaN/越界) 时夹到 0~100</summary>
    public static string Describe(float score)
    {
        if (float.IsNaN(score))
            return "置信度 —";
        int percent = Math.Clamp((int)Math.Round(score * 100f), 0, 100);
        return $"置信度 {percent}%";
    }
}
