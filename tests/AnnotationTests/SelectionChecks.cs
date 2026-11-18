// 校验标注选中 / 层级 / 选区手柄的几何。
//
// 这三块都是纯函数, 可以完全离线断言 —— 这正是把它们从 SnipOverlayForm 里抽出来的原因。
// 窗体里的状态机 (谁抢鼠标、撤销栈压几次) 归 InteractiveTests, 这里只管"算得对不对"。
//
// 重点回归项:
//   - 最上层命中 (倒序遍历), 否则用户点不到刚画的那个;
//   - 层级操作只重排、不增删元素;
//   - 手柄角优先于边, 否则四个角永远拖不到;
//   - 选区被拖过对边时归一化为正矩形, 且最小边长夹紧时锚定边不动。
using System.Drawing;
using SnipasteOcr.Annotations;

namespace SnipasteOcr.Tests;

internal static class SelectionChecks
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        // ===== 1. 命中容差随线宽增长, 但有下限 =====
        {
            Check(Math.Abs(AnnotationHit.Tolerance(Rect(2)) - 6f) < 0.01f, "细线容差取 6px 下限");
            Check(Math.Abs(AnnotationHit.Tolerance(Rect(10)) - 14f) < 0.01f, "粗线容差 = 线宽 + 4");
        }

        // ===== 2. 最上层命中 (列表顺序即 z 序, 必须倒序查找) =====
        {
            var lower = Rect(2);
            var upper = Rect(2);
            var list = new List<Annotation> { lower, upper };

            Check(AnnotationHit.Topmost(list, new PointF(50, 0)) == 1, "重叠时命中最上层 (索引 1)");
            Check(AnnotationHit.Topmost(list, new PointF(500, 500)) == -1, "空白处返回 -1");
            Check(AnnotationHit.Topmost([], new PointF(0, 0)) == -1, "空列表返回 -1");
            Check(AnnotationHit.Topmost(list, new PointF(50, 50)) == -1, "矩形内部不算命中 (只命中描边)");
        }

        // ===== 3. 层级调整: 只重排, 不增删 =====
        {
            var a = Rect(2);
            var b = Rect(2);
            var c = Rect(2);
            var list = new List<Annotation> { a, b, c };

            Check(AnnotationOrder.BringForward(list, 0) == 1 && list.IndexOf(a) == 1,
                "上移一层交换到索引 1");
            Check(list.IndexOf(b) == 0 && list.IndexOf(c) == 2, "上移只影响相邻两个元素");
            Check(list.Count == 3, "层级操作不改变元素数量");

            Check(AnnotationOrder.BringForward(list, 2) == 2, "已在最上层时原地返回");
            Check(AnnotationOrder.SendBackward(list, 0) == 0, "已在最底层时原地返回");

            var frontItem = list[0];
            int frontIndex = AnnotationOrder.BringToFront(list, 0);
            Check(frontIndex == 2 && ReferenceEquals(list[2], frontItem), "置顶移到末尾");

            var backItem = list[2];
            int backIndex = AnnotationOrder.SendToBack(list, 2);
            Check(backIndex == 0 && ReferenceEquals(list[0], backItem), "置底移到开头");

            Check(AnnotationOrder.BringForward(list, -1) == -1, "负索引返回 -1");
            Check(AnnotationOrder.SendBackward(list, 99) == -1, "越界索引返回 -1");
        }

        // ===== 4. 手柄矩形与选区边对齐 =====
        {
            var selection = new Rectangle(100, 100, 200, 200);

            Check(SelectionResize.HandleBounds(selection, SelectionHandle.None, 9).IsEmpty, "None 手柄没有矩形");
            Check(ContainsCenter(selection, SelectionHandle.TopLeft, 9, 100, 100), "左上手柄中心在左上角");
            Check(ContainsCenter(selection, SelectionHandle.TopRight, 9, 300, 100), "右上手柄中心在右上角");
            Check(ContainsCenter(selection, SelectionHandle.BottomRight, 9, 300, 300), "右下手柄中心在右下角");
            Check(ContainsCenter(selection, SelectionHandle.BottomLeft, 9, 100, 300), "左下手柄中心在左下角");
            Check(ContainsCenter(selection, SelectionHandle.Top, 9, 200, 100), "上手柄中心在上边中点");
            Check(ContainsCenter(selection, SelectionHandle.Left, 9, 100, 200), "左手柄中心在左边中点");
        }

        // ===== 5. 手柄命中: 角优先于边 =====
        {
            var selection = new Rectangle(100, 100, 200, 200);

            Check(SelectionResize.HitHandle(selection, new Point(100, 100), 9, 0) == SelectionHandle.TopLeft,
                "角点命中左上");
            Check(SelectionResize.HitHandle(selection, new Point(300, 300), 9, 0) == SelectionHandle.BottomRight,
                "角点命中右下");
            Check(SelectionResize.HitHandle(selection, new Point(200, 100), 9, 0) == SelectionHandle.Top,
                "边中点命中上边");
            Check(SelectionResize.HitHandle(selection, new Point(200, 200), 9, 0) == SelectionHandle.None,
                "选区中心不命中手柄");
            Check(SelectionResize.HitHandle(selection, new Point(101, 101), 9, 4) == SelectionHandle.TopLeft,
                "容差让角点附近仍可命中");

            // 窄选区下角手柄与边手柄的命中区重叠, 此时必须返回角
            var narrow = new Rectangle(100, 100, 40, 40);
            Check(SelectionResize.HitHandle(narrow, new Point(108, 100), 30, 0) == SelectionHandle.TopLeft,
                "角手柄与边手柄重叠时角优先");
        }

        // ===== 6. 拖拽调整选区 =====
        {
            var selection = new Rectangle(100, 100, 200, 200);   // 100,100 → 300,300

            var bigger = SelectionResize.Apply(selection, SelectionHandle.BottomRight, new Point(400, 400), 5);
            Check(bigger == new Rectangle(100, 100, 300, 300), $"右下拖拽放大 (实得 {bigger})");

            var smaller = SelectionResize.Apply(selection, SelectionHandle.TopLeft, new Point(150, 150), 5);
            Check(smaller == new Rectangle(150, 150, 150, 150), $"左上拖拽缩小 (实得 {smaller})");

            var leftOnly = SelectionResize.Apply(selection, SelectionHandle.Left, new Point(50, 999), 5);
            Check(leftOnly.Left == 50 && leftOnly.Top == 100 && leftOnly.Bottom == 300,
                $"左边手柄只改左边界 (实得 {leftOnly})");

            // 拖过对边: 归一化成正矩形, 不能出现负宽 (那样 ExportRect 会裁出空图)
            var crossed = SelectionResize.Apply(selection, SelectionHandle.BottomRight, new Point(50, 50), 5);
            Check(crossed.Width > 0 && crossed.Height > 0, $"拖过对边仍为正矩形 (实得 {crossed})");
            Check(crossed.Left == 50 && crossed.Top == 50, $"拖过对边后原点跟随指针 (实得 {crossed})");

            // 最小边长夹紧: 锚定的左上角不能跟着跑
            var tiny = SelectionResize.Apply(selection, SelectionHandle.BottomRight, new Point(101, 101), 5);
            Check(tiny.Width >= 5 && tiny.Height >= 5, $"小于最小边长时被夹紧 (实得 {tiny})");
            Check(tiny.Left == 100 && tiny.Top == 100, $"夹紧时左上角保持锚定 (实得 {tiny})");

            Check(SelectionResize.Apply(selection, SelectionHandle.None, new Point(0, 0), 5) == selection,
                "None 手柄不改选区");
        }

        // ===== 7. 选中装饰的 8 个手柄 =====
        {
            var bounds = new RectangleF(10, 20, 100, 60);
            var handles = AnnotationSelectionPainter.HandleRects(bounds, 8);

            Check(handles.Length == 8, $"选中框有 8 个手柄 (实得 {handles.Length})");
            if (handles.Length == 8)
            {
                Check(NearCenter(handles[0], 10, 20), "手柄 0 在左上角");
                Check(NearCenter(handles[2], 110, 20), "手柄 2 在右上角");
                Check(NearCenter(handles[4], 110, 80), "手柄 4 在右下角");
                Check(NearCenter(handles[6], 10, 80), "手柄 6 在左下角");
                Check(NearCenter(handles[1], 60, 20), "手柄 1 在上边中点");
                Check(Math.Abs(handles[0].Width - 8f) < 0.01f, "手柄边长等于传入尺寸");
            }
        }

        return runner.ToResult();
    }

    /// <summary>手柄矩形是否以给定点为中心</summary>
    private static bool ContainsCenter(Rectangle selection, SelectionHandle handle, int size, int centerX, int centerY)
    {
        var box = SelectionResize.HandleBounds(selection, handle, size);
        if (box.IsEmpty)
            return false;
        float cx = box.Left + box.Width / 2f;
        float cy = box.Top + box.Height / 2f;
        return Math.Abs(cx - centerX) < 0.51f && Math.Abs(cy - centerY) < 0.51f;
    }

    /// <summary>浮点手柄矩形是否以给定点为中心</summary>
    private static bool NearCenter(RectangleF box, float x, float y)
        => Math.Abs(box.Left + box.Width / 2f - x) < 0.01f
        && Math.Abs(box.Top + box.Height / 2f - y) < 0.01f;

    /// <summary>构造一个矩形标注 (0,0)-(100,100), 只用于命中/层级测试</summary>
    private static Annotation Rect(float strokeWidth) => new()
    {
        Tool = AnnotationTool.Rectangle,
        Start = new PointF(0, 0),
        End = new PointF(100, 100),
        StrokeWidth = strokeWidth,
    };
}
