// 校验 OCR 结果后处理: 阅读顺序排序与表格/CSV 重建。
//
// 重点回归项: 「阅读顺序」与「表格重建」必须用同一套分行结果。
// 历史上这两处各写了一份几乎相同的纵向聚类, 容差公式一旦漂移,
// 同一批识别结果会在结果列表里是一行、到了复制表格里却变成两行。
using SnipasteOcr;
using Sdcb.SimdPaddleOCR;

namespace SnipasteOcr.Tests;

internal static class OcrTextChecks
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        // ===== 1. ToLine: 四边形 -> 轴对齐包围盒 =====
        {
            // 故意给一个带旋转的框 (四角不共线于轴)
            var line = OcrText.ToLine(Det("rot", 30, 10, 110, 20, 120, 50, 40, 40));
            Check(Near(line.Left, 30) && Near(line.Right, 120), $"ToLine 左右取四角极值 ({line.Left}~{line.Right})");
            Check(Near(line.Top, 10) && Near(line.Bottom, 50), $"ToLine 上下取四角极值 ({line.Top}~{line.Bottom})");
            Check(line.Text == "rot", "ToLine 保留文本");
        }

        // ===== 2. 阅读顺序: 先上→下, 再左→右 =====
        {
            // 三行, 每行两列; 输入顺序故意打乱
            var lines = new[]
            {
                Ln("r2c2", 200, 120), Ln("r1c2", 200, 20),
                Ln("r3c1", 10, 220),  Ln("r1c1", 10, 20),
                Ln("r2c1", 10, 120),
            };

            var sorted = OcrText.SortReadingOrder(lines);
            string got = string.Join(",", sorted.Select(l => l.Text));
            Check(got == "r1c1,r1c2,r2c1,r2c2,r3c1", $"阅读顺序为先上后下再左到右 (实得 {got})");
        }

        // ===== 3. 同一行的基线微差不应被拆成两行 =====
        {
            var lines = new[]
            {
                Ln("A", 10, 100), Ln("B", 90, 103),   // 中心差 3px, 行高 20 -> 容差 10px
            };
            var sorted = OcrText.SortReadingOrder(lines);
            Check(sorted.Count == 2 && sorted[0].Text == "A" && sorted[1].Text == "B",
                "同一行的微差基线仍按左到右排列");

            var csv = OcrText.ToCsv([.. lines]);
            Check(SplitLines(csv).Length == 1, $"微差基线的两个块归为表格同一行 (实得 {SplitLines(csv).Length} 行)");
        }

        // ===== 4. 关键一致性: 阅读顺序的行划分 == 表格的行划分 =====
        // 这是提取 ClusterRows 后必须守住的契约 (两处曾各写一份实现)。
        {
            var lines = new[]
            {
                Ln("r1c1", 10, 20), Ln("r1c2", 200, 24),
                Ln("r2c1", 10, 120), Ln("r2c2", 200, 118),
                Ln("r3c1", 10, 220),
            };

            var reading = OcrText.SortReadingOrder(lines);
            var csv = OcrText.ToCsv([.. lines], '\t');
            var csvRows = SplitLines(csv);

            // 阅读顺序里「同一行的相邻两块 Top 差」应远小于「跨行的相邻两块 Top 差」
            int gaps = 0;
            for (int i = 1; i < reading.Count; i++)
                if (Math.Abs(reading[i].Top - reading[i - 1].Top) > 40f) gaps++;

            Check(gaps == 2, $"阅读顺序划分为 3 行 (跨行跳跃 2 次, 实得 {gaps})");
            Check(csvRows.Length == 3, $"表格重建同样划分为 3 行 (实得 {csvRows.Length})");
        }

        // ===== 5. 表格分列: 三行两列, 制表符对齐且缺失格留空 =====
        {
            var lines = new[]
            {
                Ln("a", 10, 20), Ln("b", 200, 20),
                Ln("c", 10, 120), Ln("d", 200, 120),
                Ln("e", 10, 220),                       // 第二列缺失
            };

            var csv = OcrText.ToCsv([.. lines], '\t');
            var rows = SplitLines(csv);

            // 先断言行数, 后续的逐行断言必须做越界保护 ——
            // 否则实现一旦漂移, 这里会抛 IndexOutOfRange 让整个自检崩掉, 而不是报一条 FAIL。
            Check(rows.Length == 3, $"表格为 3 行 (实得 {rows.Length})");
            Check(rows.Length > 0 && rows[0] == "a\tb", $"首行两列制表符分隔 (实得 \"{(rows.Length > 0 ? rows[0] : "<无>")}\")");
            Check(rows.Length > 2 && rows[2] == "e", $"末列缺失时不留尾随分隔符 (实得 \"{(rows.Length > 2 ? rows[2] : "<无>")}\")");
        }

        // ===== 6. CSV 转义 =====
        {
            var lines = new[]
            {
                Ln("plain", 10, 20),
                Ln("has,comma", 200, 20),
                Ln("has\"quote", 10, 120),
            };

            var csv = OcrText.ToCsv([.. lines], ',');
            Check(csv.Contains("\"has,comma\""), $"含分隔符的字段被引号包裹 ({csv})");
            Check(csv.Contains("\"has\"\"quote\""), $"字段内引号被双写转义 ({csv})");
            Check(csv.Contains("plain"), "普通字段不加引号");
        }

        // ===== 7. 空/空白输入的安全行为 =====
        {
            Check(OcrText.ToCsv([]) == string.Empty, "空列表导出空字符串");
            Check(OcrText.ToCsv([Ln("   ", 10, 10)]).Length == 0, "全空白文本导出空字符串");
            Check(OcrText.SortReadingOrder([]).Count == 0, "空列表排序返回空");
            Check(OcrText.SortReadingOrder([Ln("only", 5, 5)]).Count == 1, "单块排序原样返回");
        }

        // ===== 8. 多列 (报刊式) 布局不应交叉串行 =====
        {
            // 左栏两行 + 右栏两行, 右栏整体更靠下, 输入顺序左右交错
            var lines = new[]
            {
                Ln("L1", 10, 20), Ln("R1", 300, 20),
                Ln("L2", 10, 120), Ln("R2", 300, 120),
            };
            var sorted = OcrText.SortReadingOrder(lines);
            string got = string.Join(",", sorted.Select(l => l.Text));
            Check(got == "L1,R1,L2,R2", $"同高并列块按左到右 (实得 {got})");
        }

        // ===== 9. OcrBox: 四角展开与包围盒 (5 处调用点共用的基础) =====
        {
            // 带旋转的框: 左上(30,10) 右上(110,20) 右下(120,50) 左下(40,40)
            var box = new PaddleOcrDetectionBox(30, 10, 110, 20, 120, 50, 40, 40, 1f);

            var c = OcrBox.Corners(box);
            Check(c.Length == 4, "Corners 返回 4 个顶点");
            Check(Near(c[0].X, 30) && Near(c[0].Y, 10), "角点0 = (X1,Y1)");
            Check(Near(c[1].X, 110) && Near(c[1].Y, 20), "角点1 = (X2,Y2)");
            Check(Near(c[2].X, 120) && Near(c[2].Y, 50), "角点2 = (X3,Y3)");
            Check(Near(c[3].X, 40) && Near(c[3].Y, 40), "角点3 = (X4,Y4)");

            var (left, top, right, bottom) = OcrBox.AxisAligned(box);
            Check(Near(left, 30) && Near(right, 120), $"AxisAligned 横向取极值 ({left}~{right})");
            Check(Near(top, 10) && Near(bottom, 50), $"AxisAligned 纵向取极值 ({top}~{bottom})");

            // 顶边/底边顺序必须"同向", 否则按同一参数 u 插值出来的高亮四边形会自交
            var (t0, t1) = OcrBox.TopEdge(box);
            var (b0, b1) = OcrBox.BottomEdge(box);
            Check(Near(t0.X, 30) && Near(t1.X, 110), "TopEdge 顺序为 X1->X2");
            Check(Near(b0.X, 40) && Near(b1.X, 120), "BottomEdge 顺序为 X4->X3 (与顶边同向)");
            // 同向性: 顶边与底边向量应大致平行且不反向
            bool sameDir = (t1.X - t0.X) * (b1.X - b0.X) + (t1.Y - t0.Y) * (b1.Y - b0.Y) > 0;
            Check(sameDir, "顶边与底边同向 (u 插值不会自交)");
        }

        // ===== 10. OcrBox.Contains: 四边形内外判定 =====
        {
            // 轴对齐方框, 便于人工判断
            var box = new PaddleOcrDetectionBox(10, 10, 90, 10, 90, 50, 10, 50, 1f);
            Check(OcrBox.Contains(box, 50, 30), "中心点在框内");
            Check(!OcrBox.Contains(box, 5, 30), "左侧框外点不在框内");
            Check(!OcrBox.Contains(box, 95, 30), "右侧框外点不在框内");
            Check(!OcrBox.Contains(box, 50, 5), "上方框外点不在框内");
            Check(!OcrBox.Contains(box, 50, 55), "下方框外点不在框内");

            // 旋转菱形: 包围盒内但菱形外的点必须判为不在框内 (与轴对齐判定区分开)
            var diamond = new PaddleOcrDetectionBox(50, 0, 100, 50, 50, 100, 0, 50, 1f);
            Check(OcrBox.Contains(diamond, 50, 50), "菱形中心在框内");
            Check(!OcrBox.Contains(diamond, 12, 12), "菱形包围盒死角判定为不在框内");
            Check(!OcrBox.Contains(diamond, 88, 88), "菱形另一侧死角判定为不在框内");
        }

        // ===== 11. ToLine 与 OcrBox.AxisAligned 一致 (两处不再各算一遍) =====
        {
            var rot = Det("r", 30, 10, 110, 20, 120, 50, 40, 40);
            var line = OcrText.ToLine(rot);
            var (l, t, r, b2) = OcrBox.AxisAligned(rot.Box);
            Check(Near(line.Left, l) && Near(line.Top, t) && Near(line.Right, r) && Near(line.Bottom, b2),
                "ToLine 与 AxisAligned 结果一致");
        }

        // ===== 12. ToPlainText: 去硬换行 + 中英之间补空格 =====
        {
            // 同一行的两块 (同一 y, 不同 x) 应接成一行, 且中文之间不插空格
            var sameRow = new[]
            {
                Ln("识别", 10, 20),
                Ln("引擎", 200, 20),
            };
            string merged = OcrText.ToPlainText(sameRow);
            Check(merged == "识别引擎", $"同行中文块直接相接, 不加空格 (实得 \"{merged}\")");

            // 中英/数字混排之间需要空格, 否则 "OCR识别" 会粘在一起
            var mixed = new[]
            {
                Ln("OCR", 10, 20),
                Ln("识别", 200, 20),
            };
            string mixedText = OcrText.ToPlainText(mixed);
            Check(mixedText == "OCR 识别", $"中英之间补一个空格 (实得 \"{mixedText}\")");

            // 不同行 -> 换行; joinAll=true 时并成一段
            var twoRows = new[]
            {
                Ln("第一行", 10, 20),
                Ln("第二行", 10, 120),
            };
            string multi = OcrText.ToPlainText(twoRows);
            Check(SplitLines(multi).Length == 2, $"不同行保留为两行 (实得 {SplitLines(multi).Length})");
            string joined = OcrText.ToPlainText(twoRows, joinAll: true);
            Check(!joined.Contains('\n'), $"joinAll 时合并为一段 (实得 \"{joined}\")");

            // 闭口标点前不该多出空格
            var punct = new[]
            {
                Ln("你好", 10, 20),
                Ln("。", 200, 20),
            };
            Check(OcrText.ToPlainText(punct) == "你好。", $"句号前不插空格 (实得 \"{OcrText.ToPlainText(punct)}\")");
        }

        // ===== 13. ToMarkdown: 段落之间空行 =====
        {
            var twoRows = new[]
            {
                Ln("段落一", 10, 20),
                Ln("段落二", 10, 120),
            };
            string md = OcrText.ToMarkdown(twoRows);
            // Markdown 里单换行会渲染成空格, 必须用空行才能真正分段
            Check(md.Contains("\n\n") || md.Contains("\r\n\r\n"), $"Markdown 段落之间有空行 (实得 {md.Replace("\r", "\\r").Replace("\n", "\\n")})");
            Check(SplitLines(md).Length == 2, $"Markdown 仍只有两段内容 (实得 {SplitLines(md).Length})");

            // 与纯文本导出的断句必须一致 (两者共用 BuildParagraphs)
            var plainRows = SplitLines(OcrText.ToPlainText(twoRows));
            var mdRows = SplitLines(md);
            Check(plainRows.SequenceEqual(mdRows), "Markdown 与纯文本的断句一致 (同源)");
        }

        // ===== 14. 新导出函数的空输入安全 =====
        {
            Check(OcrText.ToPlainText([]) == string.Empty, "ToPlainText 空列表返回空串");
            Check(OcrText.ToMarkdown([]) == string.Empty, "ToMarkdown 空列表返回空串");
            Check(OcrText.ToPlainText([Ln("   ", 10, 10)]).Length == 0, "ToPlainText 全空白返回空串");
            Check(OcrText.ToMarkdown([Ln("   ", 10, 10)]).Length == 0, "ToMarkdown 全空白返回空串");
        }

        return runner.ToResult();
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.01f;

    /// <summary>
    /// 按行拆分导出的文本 (兼容 CRLF, 忽略空行)。
    /// 必须先 TrimEnd('\r') 再去空 —— 顺序反过来时, "\r\n\r\n" 里的空行会被切成 "\r",
    /// 它在 RemoveEmptyEntries 眼里非空, 于是空白行被当成一行内容 (实测踩过)。
    /// </summary>
    private static string[] SplitLines(string text)
        => text.Split('\n')
               .Select(r => r.TrimEnd('\r'))
               .Where(r => r.Length > 0)
               .ToArray();

    /// <summary>构造一行 (左/上给定, 固定 80x20 大小)</summary>
    private static OcrText.Line Ln(string text, float left, float top)
        => new(text, left, top, left + 80, top + 20);

    /// <summary>构造一个带文本的检测结果 (文本 + 四角)</summary>
    private static PaddleOcrLine Det(string text,
        float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4)
        => new()
        {
            Text = text,
            Box = new PaddleOcrDetectionBox(x1, y1, x2, y2, x3, y3, x4, y4, 1f),
            // 以下成员在 SDK 中标为 required, 虽与本次校验无关但必须显式赋值
            RecognitionScore = 1f,
            ClassificationScore = 1f,
            ClassificationLabel = 0,
            AppliedRotationDegrees = 0,
            EmittedCount = 0,
        };
}
