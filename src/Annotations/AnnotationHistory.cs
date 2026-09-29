namespace SnipasteOcr.Annotations;

/// <summary>
/// 标注的撤销/重做历史。
///
/// 采用「快照 + 列表」的简单模型而非命令模式: 标注数量是手工量级 (通常 &lt; 50),
/// 每次操作复制一份列表的代价可以忽略, 换来的是实现简单、绝不出错。
/// </summary>
public sealed class AnnotationHistory
{
    /// <summary>撤销栈深度上限 (防止极端情况下内存无上限增长)</summary>
    private const int MaxDepth = 100;

    private readonly List<List<Annotation>> _undo = [];
    private readonly List<List<Annotation>> _redo = [];

    /// <summary>是否可撤销</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>是否可重做</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// 在修改<b>之前</b>记录当前状态。
    /// 调用约定: 先 <see cref="Push"/> 旧状态, 再修改 <paramref name="current"/> 指向的列表。
    /// </summary>
    /// <param name="current">当前标注列表 (会被深拷贝保存)</param>
    public void Push(IReadOnlyList<Annotation> current)
    {
        _undo.Add(Snapshot(current));

        // 超出深度上限时丢弃最旧的记录
        if (_undo.Count > MaxDepth)
            _undo.RemoveAt(0);

        _redo.Clear();
    }

    /// <summary>撤销: 把 <paramref name="current"/> 恢复到上一个状态; 无可撤销时返回 false</summary>
    public bool Undo(List<Annotation> current)
    {
        if (_undo.Count == 0)
            return false;

        // 当前状态进重做栈, 再还原上一状态
        _redo.Add(Snapshot(current));

        var prev = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        Replace(current, prev);
        return true;
    }

    /// <summary>重做: 无可重做时返回 false</summary>
    public bool Redo(List<Annotation> current)
    {
        if (_redo.Count == 0)
            return false;

        _undo.Add(Snapshot(current));

        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        Replace(current, next);
        return true;
    }

    /// <summary>清空历史 (重新框选后调用: 旧历史对新选区无意义)</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>深拷贝一份列表 (标注本身也要克隆, 否则后续修改会污染历史)</summary>
    private static List<Annotation> Snapshot(IReadOnlyList<Annotation> source)
    {
        var copy = new List<Annotation>(source.Count);
        foreach (var a in source)
            copy.Add(a.Clone());
        return copy;
    }

    /// <summary>用快照内容整体替换目标列表 (保持同一 List 实例, 因为外部持有引用)</summary>
    private static void Replace(List<Annotation> target, List<Annotation> snapshot)
    {
        target.Clear();
        foreach (var a in snapshot)
            target.Add(a.Clone());
    }
}
