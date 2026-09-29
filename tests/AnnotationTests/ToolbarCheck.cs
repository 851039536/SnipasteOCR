// 校验工具栏: 尺寸/布局/命中区域/图标可见性。
// 其中图标可见性是回归检查 —— 历史上撤销/重做用了本机不存在的字体与码位, 渲染为全空白。
using System.Drawing.Imaging;
using System.Reflection;
using SnipasteOcr.Annotations;

namespace SnipasteOcr.Tests;

internal static class ToolbarCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        var tb = new AnnotationToolbar();

        // 触发一次真实绘制: 命中区域在 OnPaint 中被重建。
        // 直接构造 PaintEventArgs 调用 OnPaint, 比 DrawToBitmap 更可靠 (后者依赖窗口句柄与消息泵)。
        var bmp = new Bitmap(tb.Width, tb.Height);
        using (var g = Graphics.FromImage(bmp))
        using (var pea = new PaintEventArgs(g, new Rectangle(0, 0, tb.Width, tb.Height)))
        {
            typeof(AnnotationToolbar)
                .GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(tb, [pea]);
        }

        // 反射取私有字段。注意 List<(Rectangle, T)> 的元素是 ValueTuple,
        // 其成员是字段 Item1/Item2 而非属性 Rect/Value。
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;

        static Rectangle RectOf(object tuple) =>
            (Rectangle)tuple.GetType().GetField("Item1")!.GetValue(tuple)!;

        int MaxRight(string field)
        {
            var val = typeof(AnnotationToolbar).GetField(field, flags)!.GetValue(tb)!;
            int max = 0;
            foreach (var item in (System.Collections.IEnumerable)val)
            {
                var r = RectOf(item);
                if (r.Right > max) max = r.Right;
            }
            return max;
        }

        var cancel = (Rectangle)typeof(AnnotationToolbar).GetField("_cancelRect", flags)!.GetValue(tb)!;
        Check(cancel.Right > 0, "取消按钮已布局");
        Check(cancel.Right <= tb.Width, $"取消按钮不超出工具栏 (right={cancel.Right}, width={tb.Width})");
        Check(cancel.X > 0, "取消按钮可见 (非负坐标)");

        int toolsRight = MaxRight("_toolRects");
        int colorsRight = MaxRight("_colorRects");
        int widthsRight = MaxRight("_widthRects");
        Check(toolsRight < tb.Width, $"工具按钮未溢出 (right={toolsRight})");
        Check(colorsRight < tb.Width, $"颜色按钮未溢出 (right={colorsRight})");
        Check(widthsRight < tb.Width, $"线宽按钮未溢出 (right={widthsRight})");

        // 六个工具都应布局出来
        var toolRects = (System.Collections.IList)typeof(AnnotationToolbar)
            .GetField("_toolRects", flags)!.GetValue(tb)!;
        Check(toolRects.Count == 6, $"六种工具都有按钮 (实得 {toolRects.Count})");

        // 按钮不能重叠 (同一行内的命中区域互斥)
        bool overlap = false;
        var all = new List<Rectangle> { cancel };
        foreach (var item in (System.Collections.IEnumerable)typeof(AnnotationToolbar)
                     .GetField("_toolRects", flags)!.GetValue(tb)!)
            all.Add(RectOf(item));

        for (int i = 0; i < all.Count && !overlap; i++)
            for (int j = i + 1; j < all.Count; j++)
                if (all[i].IntersectsWith(all[j])) { overlap = true; break; }
        Check(!overlap, "按钮命中区域互不重叠");

        // 颜色循环应覆盖全部 8 色且能回到起点
        var seen = new HashSet<int>();
        for (int i = 0; i < 8; i++) { seen.Add(tb.CurrentColor.ToArgb()); tb.CycleColor(); }
        Check(seen.Count == 8, $"颜色循环覆盖 8 种颜色 (实得 {seen.Count})");
        Check(tb.CurrentColor == Color.FromArgb(255, 235, 59, 36), "颜色循环回到起始色");

        // 工具切换会触发事件
        int fired = 0;
        tb.ToolChanged += () => fired++;
        tb.SetTool(AnnotationTool.Mosaic);
        Check(tb.Tool == AnnotationTool.Mosaic && fired == 1, "SetTool 切换并触发一次事件");
        tb.SetTool(AnnotationTool.Mosaic);
        Check(fired == 1, "重复设置同一工具不重复触发");

        // ===== 图标必须真的画出像素 =====
        // 回归: 原先撤销/重做用 "↶"/"↷"(U+21B6/21B7) + "Segoe UI Symbol",
        // 该字体本机不存在且这两个码位在所有字体中都无字形 -> 渲染 0 像素, 按钮全空白。
        // 因此这里直接检查按钮区域的非背景像素, 确保图标可见。
        {
            var tb2 = new AnnotationToolbar { CanUndo = true, CanRedo = true };
            using var iconBmp = new Bitmap(tb2.Width, tb2.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(iconBmp))
            using (var pea = new PaintEventArgs(g, new Rectangle(0, 0, tb2.Width, tb2.Height)))
            {
                typeof(AnnotationToolbar)
                    .GetMethod("OnPaint", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(tb2, [pea]);
            }

            var undoRect = (Rectangle)typeof(AnnotationToolbar)
                .GetField("_undoRect", flags)!.GetValue(tb2)!;
            var redoRect = (Rectangle)typeof(AnnotationToolbar)
                .GetField("_redoRect", flags)!.GetValue(tb2)!;

            int CountIconPixels(Rectangle r, Bitmap bmp)
            {
                int n = 0;
                for (int y = r.Top; y < r.Bottom && y < bmp.Height; y++)
                    for (int x = r.Left; x < r.Right && x < bmp.Width; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        // 图标为浅色 (235 或 hover 高亮), 背景为深色 (~38,40,44)
                        if (c.R > 130 && c.G > 130 && c.B > 130) n++;
                    }
                return n;
            }

            int undoPx = CountIconPixels(undoRect, iconBmp);
            int redoPx = CountIconPixels(redoRect, iconBmp);

            Check(undoPx > 8, $"撤销图标可见 (非背景像素={undoPx})");
            Check(redoPx > 8, $"重做图标可见 (非背景像素={redoPx})");

            // 撤销与重做必须是不同的图形 (镜像)
            bool different = false;
            for (int y = undoRect.Top; y < undoRect.Bottom && !different; y++)
            {
                for (int x = 0; x < undoRect.Width; x++)
                {
                    var a = iconBmp.GetPixel(undoRect.Left + x, y);
                    var b2 = iconBmp.GetPixel(redoRect.Right - 1 - x, y);
                    bool la = a.R > 130, lb = b2.R > 130;
                    if (la != lb) { different = true; break; }
                }
            }
            Check(different, "撤销/重做图标互为镜像 (不是同一个图形)");

            tb2.Dispose();
        }

        tb.Dispose();
        bmp.Dispose();
        return runner.ToResult();
    }
}
