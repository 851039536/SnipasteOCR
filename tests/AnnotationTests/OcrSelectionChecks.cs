// 校验选词几何纯函数: 行内字符索引反解与多选复制的阅读顺序名次。
//
// 回归背景: 旧实现把字符索引限制在 0..n-1, 拖到行尾永远少选最后一个字符;
// 多选/全选复制按检测顺序而非阅读顺序拼接, 多栏截图会先复制右栏。
using Sdcb.SimdPaddleOCR;

namespace SnipasteOcr.Tests;

internal static class OcrSelectionChecks
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        // ===== 1. 字符中线反解: 3 个 ASCII 字符, 累计权重 [0, .6, 1.2, 1.8] =====
        {
            float[] cum = [0f, 0.6f, 1.2f, 1.8f];
            Check(OcrSelection.CharIndexFromU(cum, 0f) == 0, "行首位置返回 0");
            Check(OcrSelection.CharIndexFromU(cum, 1f) == 3, "行尾位置返回 n, 最后一个字符可被选中");
            Check(OcrSelection.CharIndexFromU(cum, 0.1f) == 0, "第一个字符左半格返回 0");
            Check(OcrSelection.CharIndexFromU(cum, 0.2f) == 1, "第一个字符右半格返回 1");
            Check(OcrSelection.CharIndexFromU(cum, 0.99f) == 3, "最后一个字符右半格返回 n");

            // 拖到行尾得到 n, 选区 [0, n) 覆盖全部字符 (旧实现返回 n-1, 复制少一字)
            int cursorAtEnd = OcrSelection.CharIndexFromU(cum, 1f);
            Check(cursorAtEnd == cum.Length - 1,
                $"从行首拖到行尾的选区为 [0, n) (实得终点 {cursorAtEnd})");
        }

        // ===== 2. 180 度旋转: 物理左端对应文字行尾 =====
        {
            float[] cum = [0f, 0.6f, 1.2f, 1.8f];
            Check(OcrSelection.CharIndexFromU(cum, 0f, 180) == 3, "180 度时物理行首返回 n");
            Check(OcrSelection.CharIndexFromU(cum, 1f, 180) == 0, "180 度时物理行尾返回 0");
        }

        // ===== 3. 退化输入 =====
        {
            Check(OcrSelection.CharIndexFromU([], 0.5f) == 0, "空累计表返回 0");
            Check(OcrSelection.CharIndexFromU([0f], 0.5f) == 0, "零字符累计表返回 0");
            Check(OcrSelection.CharIndexFromU([0f, 1f], 0f) == 0, "单字符行首返回 0");
            Check(OcrSelection.CharIndexFromU([0f, 1f], 1f) == 1, "单字符行尾返回 1");
        }

        // ===== 4. 阅读顺序名次: 输入顺序右栏在前, 名次必须左栏在前 =====
        {
            var lines = new[]
            {
                Det("R1", 300, 20), Det("L1", 10, 20),
                Det("R2", 300, 120), Det("L2", 10, 120),
            };
            var ranks = OcrSelection.BuildReadingRanks(lines, sortReadingOrder: true);
            Check(ranks.Length == 4, $"名次表长度等于行数 (实得 {ranks.Length})");
            if (ranks.Length == 4)
            {
                Check(ranks[1] == 0 && ranks[0] == 1 && ranks[3] == 2 && ranks[2] == 3,
                    $"名次为 L1=0, R1=1, L2=2, R2=3 (实得 {string.Join(",", ranks)})");
            }

            var inputOrder = OcrSelection.BuildReadingRanks(lines, sortReadingOrder: false);
            Check(inputOrder.SequenceEqual([0, 1, 2, 3]), "关闭重排时名次等于输入顺序");
        }

        // ===== 5. 空白行不参与排序, 名次排到最后 =====
        {
            var lines = new[]
            {
                Det("A", 10, 20),
                Det("   ", 10, 120),
                Det("B", 10, 220),
            };
            var ranks = OcrSelection.BuildReadingRanks(lines, sortReadingOrder: true);
            Check(ranks.Length == 3 && ranks[1] == int.MaxValue, "空白行名次为 int.MaxValue");
            Check(ranks.Length == 3 && ranks[0] == 0 && ranks[2] == 1,
                $"非空白行仍按阅读顺序编号 (实得 {string.Join(",", ranks)})");
        }

        // ===== 6. 双击选词: 同类连续段, 标点单字边界 =====
        {
            Check(OcrSelection.WordRange("", 0) is null, "空文本没有选词范围");

            Check(OcrSelection.WordRange("Hello world", 1) is (0, 5),
                $"英文单词取到空格前 (实得 {OcrSelection.WordRange("Hello world", 1)})");
            Check(OcrSelection.WordRange("Hello world", 7) is (6, 11),
                $"第二个英文单词 (实得 {OcrSelection.WordRange("Hello world", 7)})");
            Check(OcrSelection.WordRange("foo_bar", 3) is (0, 7),
                "下划线属于单词字符");

            Check(OcrSelection.WordRange("OCR识别 2024", 1) is (0, 3),
                $"连续 ASCII 字母成段 (实得 {OcrSelection.WordRange("OCR识别 2024", 1)})");
            Check(OcrSelection.WordRange("OCR识别 2024", 4) is (3, 5),
                $"连续汉字成段 (实得 {OcrSelection.WordRange("OCR识别 2024", 4)})");
            Check(OcrSelection.WordRange("OCR识别 2024", 6) is (6, 10),
                $"连续数字成段 (实得 {OcrSelection.WordRange("OCR识别 2024", 6)})");

            Check(OcrSelection.WordRange("你好，世界", 2) is (2, 3),
                $"中文标点只选自身, 不吞并相邻汉字 (实得 {OcrSelection.WordRange("你好，世界", 2)})");
            Check(OcrSelection.WordRange("a  b", 1) is (1, 3), "连续空白成段");
            Check(OcrSelection.WordRange("abc", 99) is (0, 3),
                "越界索引夹到行尾字符后仍取整词");
        }

        return runner.ToResult();
    }

    /// <summary>构造一行检测结果 (四角轴对齐, 固定 80x20)</summary>
    private static PaddleOcrLine Det(string text, float left, float top)
        => new()
        {
            Text = text,
            Box = new PaddleOcrDetectionBox(
                left, top, left + 80, top, left + 80, top + 20, left, top + 20, 1f),
            // 以下成员在 SDK 中标为 required, 与本次校验无关但必须显式赋值
            RecognitionScore = 1f,
            ClassificationScore = 1f,
            ClassificationLabel = 0,
            AppliedRotationDegrees = 0,
            EmittedCount = 0,
        };
}
