using System.Drawing;
using Sdcb.SimdPaddleOCR;

namespace SnipasteOcr;

/// <summary>
/// <see cref="PaddleOcrDetectionBox"/> 的几何辅助。
///
/// PaddleOCR 的检测框是 <b>带旋转的四点四边形</b>, 四角在结构体里是扁平的 X1..Y4 而非数组。
/// 项目里有 5 处需要遍历这四角 (画框、命中测试、字符范围高亮、包围盒、轴对齐包围盒),
/// 若各写一遍展开, 一旦某处角点顺序写错就会静默错位 —— 因此统一到这里。
/// </summary>
internal static class OcrBox
{
    /// <summary>四角顶点, 顺序与检测框声明一致 (左上 → 右上 → 右下 → 左下)</summary>
    public static PointF[] Corners(PaddleOcrDetectionBox box) =>
    [
        new PointF(box.X1, box.Y1),
        new PointF(box.X2, box.Y2),
        new PointF(box.X3, box.Y3),
        new PointF(box.X4, box.Y4),
    ];

    /// <summary>顶边两端点 (用于行内方向的投影插值)</summary>
    public static (PointF A, PointF B) TopEdge(PaddleOcrDetectionBox box) =>
        (new PointF(box.X1, box.Y1), new PointF(box.X2, box.Y2));

    /// <summary>底边两端点 (顺序与顶边对应, 便于按同一参数 u 插值)</summary>
    public static (PointF A, PointF B) BottomEdge(PaddleOcrDetectionBox box) =>
        (new PointF(box.X4, box.Y4), new PointF(box.X3, box.Y3));

    /// <summary>轴对齐包围盒 (xyxy), 用于排序/分列与粗略容差判断</summary>
    public static (float Left, float Top, float Right, float Bottom) AxisAligned(PaddleOcrDetectionBox box) =>
        (Math.Min(Math.Min(box.X1, box.X2), Math.Min(box.X3, box.X4)),
         Math.Min(Math.Min(box.Y1, box.Y2), Math.Min(box.Y3, box.Y4)),
         Math.Max(Math.Max(box.X1, box.X2), Math.Max(box.X3, box.X4)),
         Math.Max(Math.Max(box.Y1, box.Y2), Math.Max(box.Y3, box.Y4)));

    /// <summary>点在四边形内判定 (射线法), 用于命中测试</summary>
    public static bool Contains(PaddleOcrDetectionBox box, float x, float y)
    {
        PointF[] pts = Corners(box);
        bool inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
        {
            float yi = pts[i].Y, yj = pts[j].Y;
            if ((yi > y) != (yj > y))
            {
                float t = (y - yj) / (yi - yj);
                float xi = pts[j].X + t * (pts[i].X - pts[j].X);
                if (x < xi)
                    inside = !inside;
            }
        }
        return inside;
    }
}
