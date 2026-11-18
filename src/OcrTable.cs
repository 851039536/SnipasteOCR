namespace SnipasteOcr;

/// <summary>一个列带在 x 轴上的覆盖范围 (物理像素)</summary>
public readonly record struct ColumnExtent(float Left, float Right);

/// <summary>
/// 表格结构中间模型: 由坐标重建出的「行 × 列」网格。
///
/// 这是 CSV / XLSX / Markdown 表格导出的<b>唯一来源</b> —— 三种格式若各自重算一遍分组,
/// 一旦容差或列带推导漂移, 同一张截图用 CSV 导出是三行、用 Excel 导出却是四行。
/// 各格式只负责"如何渲染这张网格", 不负责"网格长什么样"。
///
/// 空单元格统一以空字符串表达 (不区分 null 与空串), 渲染层无需再做判空。
/// </summary>
public sealed class OcrTable
{
    /// <summary>空表 (无可用文本行), 三种导出格式都应据此直接返回空内容</summary>
    public static readonly OcrTable Empty = new(new string?[0, 0], []);

    private readonly string?[,] _cells;
    private readonly ColumnExtent[] _columns;

    internal OcrTable(string?[,] cells, ColumnExtent[] columns)
    {
        _cells = cells;
        _columns = columns;
    }

    /// <summary>行数</summary>
    public int RowCount => _cells.GetLength(0);

    /// <summary>列数</summary>
    public int ColumnCount => _cells.GetLength(1);

    /// <summary>
    /// 每个列带的横向范围, 顺序即列序。
    /// XLSX 导出据此估算列宽, 让生成的表格列宽与截图中比例大致对应。
    /// </summary>
    public IReadOnlyList<ColumnExtent> Columns => _columns;

    /// <summary>取单元格文本; 越界或空单元格返回空字符串</summary>
    public string Get(int row, int col)
    {
        if (row < 0 || row >= RowCount || col < 0 || col >= ColumnCount)
            return string.Empty;
        return _cells[row, col] ?? string.Empty;
    }
}

/// <summary>
/// 从 OCR 文本块坐标构建 <see cref="OcrTable"/>。
///
/// 两步几何: 先纵向聚类成行, 再横向推导列带。
/// 这两步均为本项目的唯一实现 —— <c>OcrText.SortReadingOrder</c>、<c>OcrText.ToCsv</c>、
/// <c>OcrText.BuildParagraphs</c> 三处共用, 不得各自重写。
/// </summary>
public static class OcrTableBuilder
{
    /// <summary>构建表格网格</summary>
    /// <param name="lines">OCR 文本块 (顺序任意, 内部会按坐标重排)</param>
    public static OcrTable Build(IReadOnlyList<OcrText.Line> lines)
    {
        var valid = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        if (valid.Count == 0)
            return OcrTable.Empty;

        // 1) 纵向分行 (与阅读顺序排序共用同一实现)
        var rows = ClusterRows(valid);

        // 2) 横向分列
        var bands = BuildColumnBands(rows);
        if (bands.Count == 0)
            return OcrTable.Empty;

        // 3) 填充网格: 按与列带的重叠长度归入 (并列取最左), 缺失格留空
        var cells = new string?[rows.Count, bands.Count];
        for (int r = 0; r < rows.Count; r++)
        {
            foreach (var cell in rows[r])
            {
                int best = 0;
                float bestOverlap = float.MinValue;
                for (int c = 0; c < bands.Count; c++)
                {
                    float overlap = Math.Min(bands[c].Right, cell.Right) - Math.Max(bands[c].Left, cell.Left);
                    if (overlap > bestOverlap)
                    {
                        bestOverlap = overlap;
                        best = c;
                    }
                }

                // 同一格已有内容 (极少数重叠) 则追加
                cells[r, best] = string.IsNullOrEmpty(cells[r, best])
                    ? cell.Text
                    : cells[r, best] + " " + cell.Text;
            }
        }

        var extents = new ColumnExtent[bands.Count];
        for (int i = 0; i < bands.Count; i++)
            extents[i] = new ColumnExtent(bands[i].Left, bands[i].Right);

        return new OcrTable(cells, extents);
    }

    /// <summary>
    /// 按纵向位置把文本块聚类成行, 行内按左边界排序。
    ///
    /// 这是「阅读顺序排序」「表格/CSV 重建」「段落/Markdown 导出」共用的同一个分行步骤 ——
    /// 三处若各写一份, 一旦容差公式漂移, 同一批结果会在列表里是一行、到表格里变成两行。
    /// 分行容差取该行平均高度的一半 (下限 2px), 避免同一行的文字因基线微差被拆开。
    /// </summary>
    /// <param name="lines">已过滤空白文本的文本块</param>
    internal static List<List<OcrText.Line>> ClusterRows(IReadOnlyList<OcrText.Line> lines)
    {
        var rows = new List<List<OcrText.Line>>();

        foreach (var line in lines.OrderBy(l => l.CenterY))
        {
            List<OcrText.Line>? target = null;
            foreach (var row in rows)
            {
                // 用 Average (double 累加) 而非 Sum/Count: 两者浮点结果并不逐位相同,
                // 这里保持与重构前完全一致的数值行为, 不夹带未验证的语义变更。
                // (旧 ToCsv 在比较里重复调用 Average, 现已与 SortReadingOrder 统一为每行算一次。)
                float refCenter = (float)row.Average(x => x.CenterY);
                float refHeight = (float)row.Average(x => x.Height);
                float tolerance = Math.Max(2f, refHeight * 0.5f);
                if (Math.Abs(line.CenterY - refCenter) <= tolerance)
                {
                    target = row;
                    break;
                }
            }

            if (target is null)
                rows.Add([line]);
            else
                target.Add(line);
        }

        foreach (var row in rows)
            row.Sort((a, b) => a.Left.CompareTo(b.Left));

        return rows;
    }

    /// <summary>一个列带在 x 轴上的覆盖范围</summary>
    private readonly record struct ColumnBand(float Left, float Right);

    /// <summary>
    /// 从各行单元格的横向区间推导列带 (唯一实现)。
    ///
    /// 参考行取「单元格数最多的行」: 跨列表头通常只占一个单元格, 若把它算进去,
    /// 它的宽区间会桥接两列、让列分隔消失。参考行之间仍用区间并集求候选空隙,
    /// 再按「空隙两侧的单元格数量 + 空隙宽度」选真正的列分隔 ——
    /// 列内因对齐产生的窄空隙两侧单元格少, 列间的稳定空白两侧单元格多。
    /// 选出的分隔把并集切段合并成列带; 无法确定时宁少拆不多拆。
    /// </summary>
    private static List<ColumnBand> BuildColumnBands(List<List<OcrText.Line>> rows)
    {
        if (rows.Count == 0)
            return [];

        int maxCells = rows.Max(r => r.Count);
        // 单列表 (每行最多一个单元格): 不同行的居中/缩进不应被误拆成多列
        if (maxCells <= 1)
            return [SpanOf(rows.SelectMany(r => r))];

        var reference = rows.Where(r => r.Count == maxCells).ToList();
        if (reference.Count == 0)
            reference = rows;

        var intervals = reference
            .SelectMany(r => r)
            .Where(c => c.Right > c.Left)
            .Select(c => (c.Left, c.Right))
            .OrderBy(x => x.Left)
            .ToList();
        if (intervals.Count == 0)
            return [SpanOf(rows.SelectMany(r => r))];

        var segments = new List<(float Left, float Right)> { intervals[0] };
        for (int i = 1; i < intervals.Count; i++)
        {
            var (left, right) = intervals[i];
            // 只合并真正重叠的区间; 仅"相接"的单元格保留为 0 宽空隙,
            // 让同一行里两个紧邻的块仍能按列分开 (旧左边界聚类行为不回归)。
            if (left < segments[^1].Right)
                segments[^1] = (segments[^1].Left, Math.Max(segments[^1].Right, right));
            else
                segments.Add((left, right));
        }

        // 所有参考单元格连成一个重叠簇: 没有可用的列间空白, 只能算一列
        if (segments.Count <= 1)
            return [new ColumnBand(segments[0].Left, segments[0].Right)];

        var gaps = new List<(int Index, float Width, int Score)>();
        for (int i = 0; i < segments.Count - 1; i++)
        {
            var (segLeft, segRight) = segments[i];
            var (nextLeft, nextRight) = segments[i + 1];
            int leftCells = reference.Sum(row => row.Count(c => c.Left < segRight && c.Right > segLeft));
            int rightCells = reference.Sum(row => row.Count(c => c.Left < nextRight && c.Right > nextLeft));
            gaps.Add((i, nextLeft - segRight, leftCells + rightCells));
        }

        int splitCount = Math.Min(maxCells - 1, gaps.Count);
        var chosen = gaps
            .OrderByDescending(g => g.Score)
            .ThenByDescending(g => g.Width)
            .Take(splitCount)
            .Select(g => g.Index)
            .ToHashSet();

        var bands = new List<ColumnBand>();
        float bandLeft = segments[0].Left, bandRight = segments[0].Right;
        for (int i = 0; i < segments.Count - 1; i++)
        {
            if (chosen.Contains(i))
            {
                bands.Add(new ColumnBand(bandLeft, bandRight));
                bandLeft = segments[i + 1].Left;
                bandRight = segments[i + 1].Right;
            }
            else
            {
                bandRight = Math.Max(bandRight, segments[i + 1].Right);
            }
        }
        bands.Add(new ColumnBand(bandLeft, bandRight));
        return bands;
    }

    /// <summary>一组单元格的横向总覆盖范围</summary>
    private static ColumnBand SpanOf(IEnumerable<OcrText.Line> cells)
    {
        float left = float.MaxValue, right = float.MinValue;
        foreach (var cell in cells)
        {
            left = Math.Min(left, cell.Left);
            right = Math.Max(right, cell.Right);
        }
        return new ColumnBand(left, right);
    }
}
