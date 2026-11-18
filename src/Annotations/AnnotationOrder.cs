namespace SnipasteOcr.Annotations;

/// <summary>
/// 标注层级调整 (纯函数)。
///
/// 层级就是列表顺序: 索引越大越靠上 (后绘制覆盖先绘制)。
/// 因此"上移一层"= 与后一个元素交换, 不涉及任何渲染改动 ——
/// <see cref="AnnotationEngine.Draw"/> 本来按传入顺序画, 换完顺序自然生效。
///
/// 所有方法都<b>只重排列表、不克隆、不入历史</b>: 历史由调用方在改动前 Push 一次,
/// 与新增/删除的约定保持一致 (每帧 Push 会把撤销栈刷爆)。
/// 索引非法时返回 -1 且不动列表。
/// </summary>
public static class AnnotationOrder
{
    /// <summary>上移一层; 已在最上层时原地不动, 返回原索引</summary>
    public static int BringForward(List<Annotation> list, int index)
    {
        if (!IsValid(list, index))
            return -1;
        if (index >= list.Count - 1)
            return index;

        (list[index], list[index + 1]) = (list[index + 1], list[index]);
        return index + 1;
    }

    /// <summary>下移一层; 已在最底层时原地不动, 返回原索引</summary>
    public static int SendBackward(List<Annotation> list, int index)
    {
        if (!IsValid(list, index))
            return -1;
        if (index <= 0)
            return index;

        (list[index], list[index - 1]) = (list[index - 1], list[index]);
        return index - 1;
    }

    /// <summary>移到最上层</summary>
    public static int BringToFront(List<Annotation> list, int index)
    {
        if (!IsValid(list, index))
            return -1;
        if (index == list.Count - 1)
            return index;

        var item = list[index];
        list.RemoveAt(index);
        list.Add(item);
        return list.Count - 1;
    }

    /// <summary>移到最底层</summary>
    public static int SendToBack(List<Annotation> list, int index)
    {
        if (!IsValid(list, index))
            return -1;
        if (index == 0)
            return index;

        var item = list[index];
        list.RemoveAt(index);
        list.Insert(0, item);
        return 0;
    }

    private static bool IsValid(List<Annotation> list, int index)
        => index >= 0 && index < list.Count;
}
