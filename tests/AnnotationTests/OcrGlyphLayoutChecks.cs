// 校验 CTC 真实字符框布局: 字符数不一致、180° 旋转、空格、缺失对齐四类回退/命中。
//
// 背景: 旧选词按字符宽度权重估算边界; CTC 对齐给出逐 token 的真实四边形。
// 这里用合成 span 驱动引擎自己的 EstimateCharacterBoxes, 再断言布局层的行为。
using Sdcb.SimdPaddleOCR;

namespace SnipasteOcr.Tests;

internal static class OcrGlyphLayoutChecks
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        // ===== 1. 真实字符框驱动光标边界 (不再按权重均分) =====
        {
            var line = Line("abc", 0, [
                Span("a", 0, 2), Span("b", 4, 6), Span("c", 8, 10),
            ]);
            var layout = OcrGlyphLayout.TryCreate(line);

            Check(layout is not null, "有效 CTC 行生成布局");
            if (layout is { } g)
            {
                Check(g.Text == "abc" && g.Boxes.Length == 3, "布局按字符展开 3 个框");
                Check(g.Boundaries.Length == 4, $"光标边界长度 = 字符数 + 1 (实得 {g.Boundaries.Length})");
                // 三个 span 各 2 列, 间隔各 2 列 -> 框宽 0.15 / 0.20 / 0.15 行列宽
                Check(Near(g.Boundaries[1], 0.15f) && Near(g.Boundaries[2], 0.35f) && Near(g.Boundaries[3], 1f),
                    $"边界来自真实框 (实得 {string.Join(",", g.Boundaries)})");

                Check(g.IndexAtSourceU(0.05f) == 0, "第一个字符左半格命中 0");
                Check(g.IndexAtSourceU(0.1f) == 1, "第一个字符右半格命中 1");
                Check(g.IndexAtSourceU(0.4f) == 2,
                    "真实宽度边界与权重估算不同 (u=0.4 应落在第三个字符)");
                Check(g.IndexAtSourceU(1f) == 3, "行尾命中 n, 可完整包含最后一个字符");
            }
        }

        // ===== 2. 空格是独立 token, 不能被丢掉 =====
        {
            var layout = OcrGlyphLayout.TryCreate(Line("a b", 0, [
                Span("a", 0, 2), Span(" ", 4, 6), Span("b", 8, 10),
            ]));
            Check(layout is not null, "含空格的行生成布局");
            if (layout is { } g)
            {
                Check(g.Text == "a b" && g.Boxes.Length == 3, "空格占用一个字符位置");
                Check(g.Boxes[1].Text == " ", $"空格框保留空格文本 (实得 \"{g.Boxes[1].Text}\")");
                Check(g.Boxes[1].X2 > g.Boxes[1].X1, "空格框有正宽度");
                Check(g.IndexAtSourceU(0.2f) == 1, "空格左半格命中空格字符");
            }
        }

        // ===== 3. 180 度旋转: 字符框在源图右侧, 文本顺序不变 =====
        {
            var line = Line("abc", 180, [
                Span("a", 0, 2), Span("b", 4, 6), Span("c", 8, 10),
            ]);
            var layout = OcrGlyphLayout.TryCreate(line);

            Check(layout is not null, "180 度行生成布局");
            if (layout is { } g)
            {
                Check(g.RotationDegrees == 180, "布局记录 180 度旋转");
                Check(g.Boxes[0].X1 > g.Boxes[2].X1, "文本首字符的框在源图右侧");
                Check(g.IndexAtSourceU(0.95f) == 0, "源图右端命中首字符");
                Check(g.IndexAtSourceU(0.55f) == 2, "源图中部按文本顺序命中第三个字符");
                Check(g.IndexAtSourceU(0f) == 3, "源图左端命中行尾 n");
            }
        }

        // ===== 4. 多字符 token: 框共享, 光标边界按字符均分 =====
        {
            var layout = OcrGlyphLayout.TryCreate(Line("abc", 0, [
                Span("ab", 0, 4), Span("c", 8, 10),
            ]));
            Check(layout is not null, "多字符 token 生成布局");
            if (layout is { } g)
            {
                Check(g.Boxes.Length == 3, $"多字符 token 展开为 3 个字符框 (实得 {g.Boxes.Length})");
                Check(Near(g.Boxes[0].X1, g.Boxes[1].X1) && Near(g.Boxes[0].X2, g.Boxes[1].X2),
                    "同一 token 的字符共享四边形");
                Check(g.Boundaries[1] < g.Boundaries[2] && g.Boundaries[2] < g.Boundaries[3],
                    $"光标边界保持单调 (实得 {string.Join(",", g.Boundaries)})");
            }
        }

        // ===== 5. CTC 缺失 / span 非法 / 竖排: 一律回退, 不让整行选词失效 =====
        {
            var noCtc = Line("abc", 0, null);
            Check(OcrGlyphLayout.TryCreate(noCtc) is null, "没有 CTC 对齐时回退");

            Check(OcrGlyphLayout.TryCreate(Line("abc", 0, [Span("a", 0, 30)])) is null,
                "span 超出时间步时回退 (EstimateCharacterBoxes 抛异常)");

            var tall = Line("abc", 0, [Span("a", 0, 2), Span("b", 4, 6), Span("c", 8, 10)], height: 100, width: 20);
            Check(OcrGlyphLayout.TryCreate(tall) is null, "竖排/长条行的字符框轴不同, 回退");
        }

        // ===== 6. 字符数不一致: 宁可回退, 不能错位 =====
        {
            var mismatch = Line("abcd", 0, [
                Span("a", 0, 2), Span("b", 4, 6), Span("c", 8, 10),
            ]);
            Check(OcrGlyphLayout.TryCreate(mismatch) is null, "token 文本与行文本不一致时回退");

            var build = OcrGlyphLayout.BuildAll([Line("abc", 0, [Span("a", 0, 2), Span("b", 4, 6), Span("c", 8, 10)]), mismatch]);
            Check(build.Length == 2 && build[0] is not null && build[1] is null,
                "BuildAll 逐行回退, 不影响其它行");
        }

        return runner.ToResult();
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 1e-4f;

    /// <summary>构造带 CTC 行的检测结果; 列参数固定 20 步 / 100 内容宽, 与 span 的列坐标对应</summary>
    private static PaddleOcrLine Line(string text, int rotationDegrees, PaddleOcrCtcSpan[]? spans,
        float width = 100, float height = 20)
        => new()
        {
            Text = text,
            Box = new PaddleOcrDetectionBox(0, 0, width, 0, width, height, 0, height, 1f),
            RecognitionScore = 1f,
            ClassificationScore = 1f,
            ClassificationLabel = 0,
            AppliedRotationDegrees = rotationDegrees,
            EmittedCount = (uint)(spans?.Length ?? 0),
            CtcSpans = spans!,
            RecognitionTimeSteps = 20,
            RecognitionContentWidth = 100,
            RecognitionTensorWidth = 100,
        };

    private static PaddleOcrCtcSpan Span(string text, int startColumn, int endColumn)
        => new()
        {
            Text = text,
            Score = 1f,
            StartColumn = startColumn,
            EndColumn = endColumn,
        };
}
