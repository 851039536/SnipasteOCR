// 校验表格中间模型 (OcrTable / OcrTableBuilder)。
//
// 关键契约: 这是 CSV / XLSX / Markdown 三种表格导出的唯一网格来源。
// 原实现把「分行 + 列带 + 填格」写在 OcrText.ToCsv 内部, 若要再加 XLSX 就得复制一遍;
// 这里把网格抽出来, 断言重点从"CSV 长什么样"转为"网格长什么样"。
//
// 搬迁等价性由 OcrTextChecks 里既有的 CSV 断言继续守着 (它们一条都没改),
// 本文件只补网格结构、缺失格、同格追加与边界安全。
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class OcrTableChecks
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        // ===== 1. 空 / 全空白输入 =====
        {
            Check(OcrTableBuilder.Build([]).RowCount == 0, "空列表构建空表");
            Check(OcrTableBuilder.Build([]).ColumnCount == 0, "空列表列数为 0");
            Check(OcrTableBuilder.Build([Ln("   ", 10, 10)]).RowCount == 0, "全空白文本构建空表");
            Check(OcrTable.Empty.RowCount == 0, "Empty 常量为空表");
        }

        // ===== 2. 三行两列: 网格形状与缺失格 =====
        {
            var lines = new[]
            {
                Ln("a", 10, 20), Ln("b", 200, 20),
                Ln("c", 10, 120), Ln("d", 200, 120),
                Ln("e", 10, 220),                       // 第二列缺失
            };
            var table = OcrTableBuilder.Build(lines);

            Check(table.RowCount == 3, $"网格为 3 行 (实得 {table.RowCount})");
            Check(table.ColumnCount == 2, $"网格为 2 列 (实得 {table.ColumnCount})");
            Check(table.Get(0, 0) == "a" && table.Get(0, 1) == "b", $"首行两格 (实得 \"{table.Get(0, 0)}|{table.Get(0, 1)}\")");
            Check(table.Get(2, 0) == "e", $"末行首格 (实得 \"{table.Get(2, 0)}\")");
            // 缺失格必须是空串而不是 null, 否则渲染层要各自判空
            Check(table.Get(2, 1) == string.Empty, $"缺失格为空字符串 (实得 \"{table.Get(2, 1)}\")");
        }

        // ===== 3. Get 越界防御 (渲染层不该再写一遍边界判断) =====
        {
            var table = OcrTableBuilder.Build([Ln("a", 10, 20), Ln("b", 200, 20)]);
            Check(table.Get(-1, 0) == string.Empty, "Get 负行索引返回空串");
            Check(table.Get(0, -1) == string.Empty, "Get 负列索引返回空串");
            Check(table.Get(99, 0) == string.Empty, "Get 超出行的索引返回空串");
            Check(table.Get(0, 99) == string.Empty, "Get 超出列的索引返回空串");
        }

        // ===== 4. 居中表头 + 右对齐数字: 列带必须合并为两列 =====
        // 只按左边界聚类会让表头与数字各占一列, 列数膨胀、值错位。
        {
            var lines = new[]
            {
                LnSize("姓名", 40, 20, 40),  LnSize("金额", 240, 20, 40),
                LnSize("张三", 10, 120, 40), LnSize("1,200", 260, 120, 40),
                LnSize("李四", 10, 220, 40), LnSize("98", 280, 220, 20),
            };
            var table = OcrTableBuilder.Build(lines);

            Check(table.RowCount == 3 && table.ColumnCount == 2,
                $"居中/右对齐仍为 3 行 2 列 (实得 {table.RowCount}x{table.ColumnCount})");
            Check(table.Get(0, 0) == "姓名" && table.Get(0, 1) == "金额",
                $"表头两格 (实得 \"{table.Get(0, 0)}|{table.Get(0, 1)}\")");
            Check(table.Get(1, 1) == "1,200", $"右对齐数字归第二列 (实得 \"{table.Get(1, 1)}\")");
            Check(table.Get(2, 1) == "98", $"宽度不同的数字仍归第二列 (实得 \"{table.Get(2, 1)}\")");
        }

        // ===== 5. 跨列表头: 不桥接数据列 =====
        {
            var lines = new[]
            {
                LnSize("合计", 30, 20, 230),   // 横跨两列
                LnSize("1", 10, 120, 40), LnSize("2", 250, 120, 40),
                LnSize("3", 10, 220, 40), LnSize("4", 250, 220, 40),
            };
            var table = OcrTableBuilder.Build(lines);

            Check(table.RowCount == 3 && table.ColumnCount == 2,
                $"跨列表头表格仍为 3 行 2 列 (实得 {table.RowCount}x{table.ColumnCount})");
            // 宽表头归入重叠更大的首列, 第二格留空
            Check(table.Get(0, 0) == "合计" && table.Get(0, 1) == string.Empty,
                $"跨列表头落在首列 (实得 \"{table.Get(0, 0)}|{table.Get(0, 1)}\")");
            Check(table.Get(1, 0) == "1" && table.Get(1, 1) == "2", "数据行两列不被宽表头桥接");
        }

        // ===== 6. 单列表: 居中/缩进差异不拆列 =====
        {
            var table = OcrTableBuilder.Build([LnSize("第一行", 30, 20, 60), LnSize("第二行", 40, 120, 60)]);
            Check(table.RowCount == 2 && table.ColumnCount == 1,
                $"单列表保持 1 列 (实得 {table.RowCount}x{table.ColumnCount})");
        }

        // ===== 7. 同格追加: 横向重叠的两块落在同一格时用空格接续 =====
        {
            // 两块的 x 区间几乎完全重叠 -> 只有一个列带 -> 同一格追加
            var table = OcrTableBuilder.Build([Ln("x", 10, 20), Ln("y", 12, 20)]);
            Check(table.ColumnCount == 1, $"重叠块只产生 1 列 (实得 {table.ColumnCount})");
            Check(table.Get(0, 0) == "x y", $"同格内容用空格接续 (实得 \"{table.Get(0, 0)}\")");
        }

        // ===== 8. 单一来源契约: 网格行数 == CSV 渲染行数 =====
        // 三者 (排序/表格/段落) 共用 ClusterRows, 这里用 CSV 做可见代理:
        // 若将来有人在渲染层再分一次行, 这条断言会立刻失败。
        {
            var lines = new[]
            {
                Ln("r1c1", 10, 20), Ln("r1c2", 200, 24),
                Ln("r2c1", 10, 120), Ln("r2c2", 200, 118),
                Ln("r3c1", 10, 220),
            };
            var table = OcrTableBuilder.Build(lines);
            var csvRows = SplitLines(OcrText.ToCsv(lines));

            Check(table.RowCount == 3, $"网格划分为 3 行 (实得 {table.RowCount})");
            Check(csvRows.Length == table.RowCount,
                $"CSV 行数与网格行数一致 (网格 {table.RowCount} / CSV {csvRows.Length})");
        }

        // ===== 9. ColumnExtent: 列带范围能覆盖对应单元格 =====
        {
            var lines = new[]
            {
                Ln("a", 10, 20), Ln("b", 200, 20),
                Ln("c", 10, 120), Ln("d", 200, 120),
            };
            var table = OcrTableBuilder.Build(lines);

            Check(table.Columns.Count == table.ColumnCount, "Columns 数量与列数一致");
            if (table.Columns.Count == 2)
            {
                var first = table.Columns[0];
                var second = table.Columns[1];
                // 第一列必须覆盖左列单元格 [10, 90], 第二列覆盖 [200, 280]
                Check(first.Left <= 10f && first.Right >= 90f,
                    $"首列范围覆盖左列单元格 ({first.Left}~{first.Right})");
                Check(second.Left <= 200f && second.Right >= 280f,
                    $"次列范围覆盖右列单元格 ({second.Left}~{second.Right})");
                Check(first.Right <= second.Left, "列带之间不重叠且按列序排列");
            }
        }

        // ===== 10. 重构后的 CSV 黄金字符串 (逐字比对, 守住搬迁等价性) =====
        {
            // 用例来自重构前的既有断言, 输出必须一字不差
            var caseA = new[]
            {
                Ln("a", 10, 20), Ln("b", 200, 20),
                Ln("c", 10, 120), Ln("d", 200, 120),
                Ln("e", 10, 220),
            };
            var rowsA = SplitLines(OcrText.ToCsv(caseA, '\t'));
            Check(rowsA.Length == 3 && rowsA[0] == "a\tb" && rowsA[2] == "e",
                $"黄金用例 A: 三行两列且末列缺失不留尾随分隔符 (实得 \"{string.Join("|", rowsA)}\")");

            var caseB = new[]
            {
                Ln("plain", 10, 20),
                Ln("has,comma", 200, 20),
                Ln("has\"quote", 10, 120),
            };
            string csvB = OcrText.ToCsv(caseB, ',');
            Check(csvB.Contains("\"has,comma\"") && csvB.Contains("\"has\"\"quote\""),
                $"黄金用例 B: 分隔符与引号转义不变 (实得 {csvB})");

            var caseC = new[]
            {
                Ln("A", 10, 100), Ln("B", 90, 103),
            };
            var rowsC = SplitLines(OcrText.ToCsv(caseC, '\t'));
            Check(rowsC.Length == 1 && rowsC[0] == "A\tB",
                $"黄金用例 C: 基线微差同行且仅相接的块仍分两列 (实得 \"{string.Join("|", rowsC)}\")");
        }

        return runner.ToResult();
    }

    /// <summary>按行拆分导出的文本 (兼容 CRLF, 忽略空行)</summary>
    private static string[] SplitLines(string text)
        => text.Split('\n')
               .Select(r => r.TrimEnd('\r'))
               .Where(r => r.Length > 0)
               .ToArray();

    /// <summary>构造一行 (左/上给定, 固定 80x20 大小)</summary>
    private static OcrText.Line Ln(string text, float left, float top)
        => new(text, left, top, left + 80, top + 20);

    /// <summary>构造一行 (显式宽度, 用于居中/右对齐表格用例)</summary>
    private static OcrText.Line LnSize(string text, float left, float top, float width, float height = 20)
        => new(text, left, top, left + width, top + height);
}
