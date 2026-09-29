// 标注引擎离线自检: 校验几何/命中/历史/渲染。传入 sample 则额外输出可视化样例图。
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SnipasteOcr.Annotations;
using SnipasteOcr.Tests;

if (args.Contains("sample"))
{
    Console.WriteLine("样例图: " + RenderSample.Render());
    return 0;
}

int failures = 0;

void Check(bool cond, string name)
{
    Console.WriteLine((cond ? "PASS  " : "FAIL  ") + name);
    if (!cond) failures++;
}

// ===== 1. 几何: Bounds 归一化 (反向拖拽也应得到正矩形) =====
var rect = new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(100, 80), End = new PointF(20, 10) };
var b = rect.Bounds;
Check(Math.Abs(b.X - 20) < 0.01 && Math.Abs(b.Y - 10) < 0.01 && Math.Abs(b.Width - 80) < 0.01 && Math.Abs(b.Height - 70) < 0.01,
    "矩形反向拖拽 Bounds 归一化");

// ===== 2. 箭头方向: Start=尾, End=头 (两者必须各自保留) =====
var arrow = new Annotation { Tool = AnnotationTool.Arrow, Start = new PointF(10, 10), End = new PointF(60, 40) };
Check(arrow.Start.Equals(new PointF(10, 10)) && arrow.End.Equals(new PointF(60, 40)), "箭头保留起止方向");

// ===== 3. 退化判定 =====
Check(new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(5, 5), End = new PointF(6, 6) }.IsDegenerate(),
    "1px 矩形判为退化");
Check(!new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(5, 5), End = new PointF(50, 50) }.IsDegenerate(),
    "45px 矩形判为非退化");
Check(new Annotation { Tool = AnnotationTool.Pen, Points = { new PointF(1, 1) } }.IsDegenerate(),
    "单点画笔判为退化");
Check(new Annotation { Tool = AnnotationTool.Text, Start = new PointF(10, 10), Text = "  " }.IsDegenerate(),
    "空白文字判为退化");

// ===== 4. 画笔 Bounds = 轨迹包围盒 =====
var pen = new Annotation { Tool = AnnotationTool.Pen };
pen.Points.AddRange([new PointF(10, 20), new PointF(50, 5), new PointF(30, 60)]);
var pb = pen.Bounds;
Check(Math.Abs(pb.Left - 10) < 0.01 && Math.Abs(pb.Top - 5) < 0.01 &&
      Math.Abs(pb.Right - 50) < 0.01 && Math.Abs(pb.Bottom - 60) < 0.01,
    "画笔 Bounds 为轨迹包围盒");

// ===== 5. 命中测试 =====
var r2 = new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(100, 100), End = new PointF(200, 200), StrokeWidth = 2 };
Check(r2.HitTest(new PointF(100, 150), 4), "命中矩形左边");
Check(r2.HitTest(new PointF(150, 200), 4), "命中矩形下边");
Check(!r2.HitTest(new PointF(150, 150), 2), "不命中矩形内部");

var el = new Annotation { Tool = AnnotationTool.Ellipse, Start = new PointF(100, 100), End = new PointF(200, 200), StrokeWidth = 2 };
Check(el.HitTest(new PointF(150, 100), 5), "命中椭圆顶点");
Check(!el.HitTest(new PointF(105, 105), 3), "不命中椭圆角外区域");

var ar = new Annotation { Tool = AnnotationTool.Arrow, Start = new PointF(0, 0), End = new PointF(100, 0), StrokeWidth = 2 };
Check(ar.HitTest(new PointF(50, 1), 3), "命中箭头线体");
Check(!ar.HitTest(new PointF(50, 40), 3), "不命中箭头线体之外");

var mo = new Annotation { Tool = AnnotationTool.Mosaic, Start = new PointF(10, 10), End = new PointF(110, 110), StrokeWidth = 2 };
Check(mo.HitTest(new PointF(60, 60), 2), "马赛克内部算命中 (实心)");

var tx = new Annotation { Tool = AnnotationTool.Text, Start = new PointF(20, 20), Text = "hello", FontSize = 20 };
Check(tx.HitTest(new PointF(25, 28), 2), "命中文字块");

// ===== 6. 深拷贝独立性 (撤销栈正确性的前提) =====
var orig = new Annotation { Tool = AnnotationTool.Pen, StrokeWidth = 3 };
orig.Points.Add(new PointF(1, 1));
var clone = orig.Clone();
clone.Points.Add(new PointF(99, 99));
clone.StrokeWidth = 10;
Check(orig.Points.Count == 1 && Math.Abs(orig.StrokeWidth - 3) < 0.01, "Clone 后修改不影响原对象");

// ===== 7. 平移 =====
var mv = new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(10, 10), End = new PointF(20, 20) };
mv.Offset(5, -3);
Check(Math.Abs(mv.Start.X - 15) < 0.01 && Math.Abs(mv.Start.Y - 7) < 0.01, "Offset 平移正确");

// ===== 8. 历史: 撤销/重做 =====
var hist = new AnnotationHistory();
var live = new List<Annotation>();
Check(!hist.CanUndo && !hist.CanRedo, "初始无撤销/重做");

hist.Push(live);
live.Add(new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(0, 0), End = new PointF(50, 50) });
Check(live.Count == 1 && hist.CanUndo, "Push 后可撤销");

hist.Undo(live);
Check(live.Count == 0 && hist.CanRedo, "Undo 恢复空列表");

hist.Redo(live);
Check(live.Count == 1, "Redo 恢复标注");

// 撤销栈独立性: Push 记录的是«修改前»的快照, 因此 Undo→Redo 后应回到 Push 时的值 (2)
hist.Push(live);
live[0].StrokeWidth = 42;
Check(Math.Abs(live[0].StrokeWidth - 42) < 0.01, "直接修改当前对象已生效");
hist.Undo(live);
Check(Math.Abs(live[0].StrokeWidth - 2) < 0.01, "Undo 回到 Push 时的值 (不被后续修改污染)");
hist.Redo(live);
Check(Math.Abs(live[0].StrokeWidth - 42) < 0.01, "Redo 恢复被撤销的修改");

// 深度上限: 超出后不无限增长
var h2 = new AnnotationHistory();
var l2 = new List<Annotation>();
for (int i = 0; i < 150; i++) { h2.Push(l2); l2.Add(new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(0, 0), End = new PointF(10 + i, 10) }); }
int undos = 0;
while (h2.Undo(l2)) undos++;
Check(undos <= 100, $"撤销深度受上限约束 (实得 {undos})");

// ===== 9. 渲染: 真正画一遍, 确认不抛异常且像素被改变 =====
var canvas = new Bitmap(300, 200, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(canvas))
{
    g.Clear(Color.White);
    AnnotationEngine.Draw(g, [
        new Annotation { Tool = AnnotationTool.Rectangle, Start = new PointF(10, 10), End = new PointF(80, 60), Color = Color.Red, StrokeWidth = 3 },
        new Annotation { Tool = AnnotationTool.Ellipse,   Start = new PointF(90, 10), End = new PointF(160, 60), Color = Color.Blue, StrokeWidth = 3 },
        new Annotation { Tool = AnnotationTool.Arrow,     Start = new PointF(170, 60), End = new PointF(260, 15), Color = Color.Green, StrokeWidth = 3 },
        pen,
        mo,
        tx,
    ], canvas);
}
Check(canvas.GetPixel(10, 35).R > 150 || canvas.GetPixel(10, 35).B > 150, "矩形描边产生了着色");
Check(canvas.GetPixel(150, 100).B > 0, "马赛克区域被绘制");
Check(canvas.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(), "未标注区域保持不变");

// 马赛克确实改变了像素 (对纯色底图, 中心与采样块应一致且非异常值)
var mosaicSrc = new Bitmap(200, 200, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(mosaicSrc))
{
    g.Clear(Color.White);
    using var br = new SolidBrush(Color.Black);
    g.FillRectangle(br, 100, 0, 100, 200);   // 右半黑
}
using (var g = Graphics.FromImage(mosaicSrc))
{
    AnnotationEngine.Draw(g, [
        new Annotation { Tool = AnnotationTool.Mosaic, Start = new PointF(90, 50), End = new PointF(150, 150) }
    ], mosaicSrc);
}
var mid = mosaicSrc.GetPixel(120, 100);
Check(mid.R < 250 || mid.G < 250, "马赛克把黑白边界糊成中间灰");

// ===== 10. 文字度量 =====
var s1 = AnnotationEngine.MeasureText("测试文字", 18);
Check(s1.Width > 0 && s1.Height > 0, "文字度量返回正尺寸");
var s2 = AnnotationEngine.MeasureText("", 18);
Check(s2.Width == 0 && s2.Height == 0, "空文字度量返回 0");

// ===== 11. 渲染结果的像素级校验 =====
Console.WriteLine();
Console.WriteLine("--- 渲染校验 ---");
var (renderPassed, renderFailures) = RenderChecks.Run();
foreach (var f in renderFailures)
{
    Console.WriteLine("FAIL  " + f);
    failures++;
}
Console.WriteLine($"渲染检查: {renderPassed} 通过, {renderFailures.Count} 失败");

Console.WriteLine();
Console.WriteLine("--- 工具栏校验 ---");
var (tbPassed, tbFailures) = ToolbarCheck.Run();
foreach (var f in tbFailures)
{
    Console.WriteLine("FAIL  " + f);
    failures++;
}
Console.WriteLine($"工具栏检查: {tbPassed} 通过, {tbFailures.Count} 失败");

Console.WriteLine();
Console.WriteLine("--- 文字编辑框校验 ---");
var (tePassed, teFailures) = TextEditorChecks.Run();
foreach (var f in teFailures)
{
    Console.WriteLine("FAIL  " + f);
    failures++;
}
Console.WriteLine($"编辑框检查: {tePassed} 通过, {teFailures.Count} 失败");

Console.WriteLine();
Console.WriteLine("--- OCR 后处理校验 ---");
var (otPassed, otFailures) = OcrTextChecks.Run();
foreach (var f in otFailures)
{
    Console.WriteLine("FAIL  " + f);
    failures++;
}
Console.WriteLine($"OCR 后处理检查: {otPassed} 通过, {otFailures.Count} 失败");

Console.WriteLine();
Console.WriteLine(failures == 0 ? "全部通过" : $"{failures} 项失败");
return failures == 0 ? 0 : 1;
