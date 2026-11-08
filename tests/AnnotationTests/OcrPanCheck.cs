// 校验高倍缩放平移的夹紧几何: 图片不能拖出视口留下背景空白, 小图保持居中。
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class OcrPanCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        var viewport = new SizeF(500, 400);

        // 大图: 单轴可移动范围为 (图片 - 视口) / 2
        var big = new SizeF(1000, 800);
        Check(OcrPan.Clamp(new PointF(999, 999), big, viewport) == new PointF(250, 200),
            "大图平移偏移夹紧到边缘 (实得 " + OcrPan.Clamp(new PointF(999, 999), big, viewport) + ")");
        Check(OcrPan.Clamp(new PointF(-999, -999), big, viewport) == new PointF(-250, -200),
            "反向平移同样夹紧 (实得 " + OcrPan.Clamp(new PointF(-999, -999), big, viewport) + ")");
        Check(OcrPan.Clamp(new PointF(100, -50), big, viewport) == new PointF(100, -50),
            "范围内偏移原样保留");

        // 小图: 偏移必须归零 (保持居中)
        var small = new SizeF(300, 200);
        Check(OcrPan.Clamp(new PointF(120, -80), small, viewport) == PointF.Empty,
            "图片比视口小时强制居中 (实得 " + OcrPan.Clamp(new PointF(120, -80), small, viewport) + ")");

        // 单轴超出: 只允许该轴平移, 另一轴仍居中
        var wide = new SizeF(1000, 300);
        Check(OcrPan.Clamp(new PointF(999, 999), wide, viewport) == new PointF(250, 0),
            "只有横向超出时纵向偏移归零 (实得 " + OcrPan.Clamp(new PointF(999, 999), wide, viewport) + ")");

        Check(OcrPan.CanPan(big, viewport), "大图允许平移");
        Check(!OcrPan.CanPan(small, viewport), "小图不允许平移");
        Check(OcrPan.CanPan(wide, viewport), "单轴超出也允许平移");

        // 尺寸恰好相等: 边界上按"不可平移"处理, 避免出现 0 范围却仍进入拖动
        Check(!OcrPan.CanPan(viewport, viewport), "图片与视口等大时不可平移");
        Check(OcrPan.Clamp(new PointF(10, 10), viewport, viewport) == PointF.Empty,
            "图片与视口等大时偏移归零");

        return runner.ToResult();
    }
}
