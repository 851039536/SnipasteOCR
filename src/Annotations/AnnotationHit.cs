using System.Drawing;

namespace SnipasteOcr.Annotations;

/// <summary>
/// 标注的命中判定入口。
///
/// 只做"容差 + 从上往下找"这一层，具体几何交给 <see cref="Annotation.HitTest"/> ——
/// 那条路径已经覆盖了描边/实心/折线/文字的全部情形，在这里重写一遍必然漂移。
/// </summary>
public static class AnnotationHit
{
    /// <summary>
    /// 命中容差 (物理像素)。
    /// 与线宽挂钩: 粗线要更好点中; 下限 6px 保证 1px 细线在 150% 缩放下也能选中。
    /// 工具栏预览、命中、拖动三处共用这一份，不得各取各的常数。
    /// </summary>
    public static float Tolerance(Annotation annotation)
        => MathF.Max(6f, annotation.StrokeWidth + 4f);

    /// <summary>
    /// 从最上层往下找命中项，返回索引 (未命中 -1)。
    /// 列表顺序即 z 序, 后者绘制在上, 因此必须<b>倒序</b>遍历 ——
    /// 正序会选中被压在最底下的那条, 用户会觉得"点不到刚画的东西"。
    /// </summary>
    public static int Topmost(IReadOnlyList<Annotation> list, PointF point)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].HitTest(point, Tolerance(list[i])))
                return i;
        }
        return -1;
    }
}
