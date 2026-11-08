// 校验识别置信度的展示规则: 阈值边界与百分比格式。
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class OcrConfidenceCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        Check(OcrConfidence.IsLow(0.69f), "0.69 低于阈值, 标为低置信度");
        Check(!OcrConfidence.IsLow(0.7f), "阈值本身不算低置信度 (边界稳定)");
        Check(!OcrConfidence.IsLow(0.95f), "高分不标低置信度");
        Check(OcrConfidence.IsLow(0f), "零置信度标为低置信度");

        Check(OcrConfidence.Describe(0.623f) == "置信度 62%",
            $"百分比四舍五入 (实得 \"{OcrConfidence.Describe(0.623f)}\")");
        Check(OcrConfidence.Describe(1f) == "置信度 100%", "满分显示 100%");
        Check(OcrConfidence.Describe(-0.2f) == "置信度 0%", "负分夹到 0%, 不显示负数");
        Check(OcrConfidence.Describe(1.5f) == "置信度 100%", "超过 1 的异常分夹到 100%");
        Check(OcrConfidence.Describe(float.NaN) == "置信度 —", "NaN 显示占位符, 不显示 NaN%");

        return runner.ToResult();
    }
}
