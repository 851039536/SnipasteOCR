// 交互验证: 标注的选中 / 拖动 / 层级 / 删除, 选区手柄调整, 以及选区整体平移。
//
// 这些行为活在 SnipOverlayForm 的鼠标状态机里, 纯离屏测不到 (几何部分已由
// AnnotationTests 的 SelectionChecks 覆盖)。这里构造真实窗口, 反射触发消息处理函数 ——
// 不涉及焦点链, 所以反射调用不会掩盖问题; 键盘用例走 ProcessCmdKey 与真实 WM_KEYDOWN,
// 与用户按键的入口一致。
//
// 最关键的一条是「导出像素不受选中装饰影响」: 选中框若被误写进 AnnotationEngine.Draw,
// 用户保存的图上会多一圈虚线框, 而这种泄漏只有逐像素比对才能发现。
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SnipasteOcr;
using SnipasteOcr.Annotations;

namespace SnipasteOcr.Tests;

internal static class SelectionInteractionChecks
{
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>把断言并入调用方的收集器</summary>
    public static void Run(CheckRunner runner)
    {
        void Check(bool cond, string name) => runner.Check(cond, name);

        using var overlay = new SnipOverlayForm(SnipMode.Image);

        var selectionField = typeof(SnipOverlayForm).GetField("_selection", Priv)!;
        var selectedField = typeof(SnipOverlayForm).GetField("_selected", Priv)!;
        var annotationsField = typeof(SnipOverlayForm).GetField("_annotations", Priv)!;
        var historyField = typeof(SnipOverlayForm).GetField("_history", Priv)!;
        var onSelectionChanged = typeof(SnipOverlayForm).GetMethod("OnSelectionChanged", Priv)!;
        var renderResult = typeof(SnipOverlayForm).GetMethod("RenderResult", Priv)!;
        var onMouseDown = typeof(SnipOverlayForm).GetMethod("OnMouseDown", Priv)!;
        var onMouseMove = typeof(SnipOverlayForm).GetMethod("OnMouseMove", Priv)!;
        var onMouseUp = typeof(SnipOverlayForm).GetMethod("OnMouseUp", Priv)!;
        var onKeyDown = typeof(SnipOverlayForm).GetMethod("OnKeyDown", Priv)!;

        overlay.Show();
        Application.DoEvents();

        var annotations = (List<Annotation>)annotationsField.GetValue(overlay)!;
        var history = (AnnotationHistory)historyField.GetValue(overlay)!;

        float sf = (float)typeof(SnipOverlayForm).GetProperty("ScaleFactor", Priv)!.GetValue(overlay)!;

        // 物理像素 → 逻辑像素 (鼠标事件用的是客户区逻辑坐标)
        Point Logical(float x, float y) => new((int)(x / sf), (int)(y / sf));

        void Down(float x, float y)
            => onMouseDown.Invoke(overlay, [new MouseEventArgs(MouseButtons.Left, 1, Logical(x, y).X, Logical(x, y).Y, 0)]);

        void Move(float x, float y)
            => onMouseMove.Invoke(overlay, [new MouseEventArgs(MouseButtons.None, 0, Logical(x, y).X, Logical(x, y).Y, 0)]);

        void Up(float x, float y)
            => onMouseUp.Invoke(overlay, [new MouseEventArgs(MouseButtons.Left, 1, Logical(x, y).X, Logical(x, y).Y, 0)]);

        void Key(Keys key) => onKeyDown.Invoke(overlay, [new KeyEventArgs(key)]);

        // 下面这组用逻辑坐标直接触发 (与真实鼠标事件一致); 上面的 Down/Move/Up 需要物理坐标。
        void DownLogical(Point p)
            => onMouseDown.Invoke(overlay, [new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0)]);
        void MoveLogical(Point p)
            => onMouseMove.Invoke(overlay, [new MouseEventArgs(MouseButtons.None, 0, p.X, p.Y, 0)]);
        void UpLogical(Point p)
            => onMouseUp.Invoke(overlay, [new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0)]);

        // 走真实的按键处理入口 (ProcessCmdKey 是方向键在 WinForms 里的第一站)
        bool CmdKey(Keys key)
        {
            object?[] args = [default(Message), key];
            return (bool)typeof(SnipOverlayForm).GetMethod("ProcessCmdKey", Priv)!.Invoke(overlay, args)!;
        }

        // 选区: 逻辑坐标 120,120 起, 320x220
        var selection = new Rectangle(120, 120, 320, 220);
        selectionField.SetValue(overlay, selection);
        onSelectionChanged.Invoke(overlay, null);
        Application.DoEvents();

        // ===== 1. 点选: 命中最上层 =====
        // 两条矩形都落在选区内。第二条 (250,250)-(350,350) 在上层。
        // 点 (300,250) 只命中第二条的上边; 第一条 (200,200)-(300,300) 在这个点只有内部, 不算命中。
        annotations.Add(new Annotation
        {
            Tool = AnnotationTool.Rectangle,
            Start = new PointF(200, 200),
            End = new PointF(300, 300),
            StrokeWidth = 2,
        });
        annotations.Add(new Annotation
        {
            Tool = AnnotationTool.Rectangle,
            Start = new PointF(250, 250),
            End = new PointF(350, 350),
            StrokeWidth = 2,
        });

        Down(300, 250);
        Check((int)selectedField.GetValue(overlay)! == 1, $"点中重叠区选中上层 (实得 {selectedField.GetValue(overlay)})");

        // ===== 2. 拖动: 位移生效, 且只压一次历史 =====
        Move(340, 280);
        Up(340, 280);

        var moved = annotations[1];
        Check(Math.Abs(moved.Start.X - 290) < 1.5f && Math.Abs(moved.Start.Y - 280) < 1.5f,
            $"拖动后标注跟随指针 (实得 {moved.Start})");
        Check(Math.Abs(moved.End.X - 390) < 1.5f, $"拖动保持尺寸不变 (实得 {moved.End})");

        // 一次撤销就应回到原位: 若每帧都压了历史, 这里只会退掉一个像素
        bool undone = history.Undo(annotations);
        Check(undone && Math.Abs(annotations[1].Start.X - 250) < 0.01f,
            $"整次拖动只占一步撤销 (实得 X={annotations[1].Start.X})");
        history.Redo(annotations);   // 复原, 不影响后续断言

        // ===== 3. 层级: Ctrl+] 上移 / Ctrl+[ 下移 =====
        selectedField.SetValue(overlay, 0);
        var firstItem = annotations[0];
        Key(Keys.Control | Keys.OemCloseBrackets);
        Check(ReferenceEquals(annotations[1], firstItem), "Ctrl+] 把选中标注上移一层");

        selectedField.SetValue(overlay, 1);
        Key(Keys.Control | Keys.OemOpenBrackets);
        Check(ReferenceEquals(annotations[0], firstItem), "Ctrl+[ 把选中标注下移一层");

        // 已在最上层时置顶不应改变任何东西
        selectedField.SetValue(overlay, annotations.Count - 1);
        int countBefore = annotations.Count;
        Key(Keys.Control | Keys.Shift | Keys.OemCloseBrackets);
        Check(annotations.Count == countBefore, "置顶不增删标注");

        // ===== 4. Esc 先取消选中, 不关窗 =====
        selectedField.SetValue(overlay, 0);
        Key(Keys.Escape);
        Check((int)selectedField.GetValue(overlay)! == -1, "Esc 取消选中");
        Check(!overlay.IsDisposed, "取消选中时 Esc 不关闭覆盖层");

        // ===== 5. Delete: 有选中只删一条 =====
        selectedField.SetValue(overlay, 0);
        int beforeDelete = annotations.Count;
        Key(Keys.Delete);
        Check(annotations.Count == beforeDelete - 1, $"Delete 只删选中的一条 (实得 {annotations.Count})");
        Check((int)selectedField.GetValue(overlay)! == -1, "删除后选中态失效");

        // ===== 6. 选区内部拖动: 整块平移, 尺寸不变, 工具栏跟随 =====
        annotations.Clear();
        selectionField.SetValue(overlay, selection);
        onSelectionChanged.Invoke(overlay, null);
        Application.DoEvents();

        var toolbar = (AnnotationToolbar)typeof(SnipOverlayForm).GetField("_toolbar", Priv)!.GetValue(overlay)!;
        var toolbarStart = toolbar.Location;

        var grip = new Point(selection.Left + 60, selection.Top + 60);
        DownLogical(grip);
        MoveLogical(new Point(grip.X + 40, grip.Y + 30));
        UpLogical(new Point(grip.X + 40, grip.Y + 30));
        Application.DoEvents();

        var dragged = (Rectangle)selectionField.GetValue(overlay)!;
        Check(dragged == new Rectangle(selection.X + 40, selection.Y + 30, selection.Width, selection.Height),
            $"框内拖动整体平移选区 (实得 {dragged})");
        Check(toolbar.Location == new Point(toolbarStart.X + 40, toolbarStart.Y + 30),
            $"工具栏跟随选区移动 (实得 {toolbar.Location}, 原位置 {toolbarStart})");

        // 单击抖动 (3px 内) 不能把选区带偏: 双击确认依赖这一点
        var beforeClick = dragged;
        var click = new Point(dragged.Left + 60, dragged.Top + 60);
        DownLogical(click);
        MoveLogical(new Point(click.X + 1, click.Y + 1));
        UpLogical(new Point(click.X + 1, click.Y + 1));
        Check((Rectangle)selectionField.GetValue(overlay)! == beforeClick,
            "3px 内的按下抖动视为单击, 选区不移动");

        // ===== 7. 方向键微调: 1px / Shift 10px, 并与拖动共用边界夹紧 =====
        bool handledLeft = CmdKey(Keys.Left);
        var afterLeft = (Rectangle)selectionField.GetValue(overlay)!;
        Check(handledLeft && afterLeft == new Rectangle(beforeClick.X - 1, beforeClick.Y, beforeClick.Width, beforeClick.Height),
            $"方向键左移 1px (实得 {afterLeft})");

        bool handledShiftUp = CmdKey(Keys.Shift | Keys.Up);
        var afterShiftUp = (Rectangle)selectionField.GetValue(overlay)!;
        Check(handledShiftUp && afterShiftUp == new Rectangle(afterLeft.X, afterLeft.Y - 10, afterLeft.Width, afterLeft.Height),
            $"Shift+方向键移动 10px (实得 {afterShiftUp})");

        selectionField.SetValue(overlay, new Rectangle(0, 120, 320, 220));
        onSelectionChanged.Invoke(overlay, null);
        CmdKey(Keys.Left);
        var clampedLeft = (Rectangle)selectionField.GetValue(overlay)!;
        Check(clampedLeft.Left == 0, $"选区贴左边时方向键不再越界 (实得 {clampedLeft})");

        // 真实 WM_KEYDOWN: 不经过反射直调, 验证按键最终落到窗体时也走同一条处理
        selectionField.SetValue(overlay, selection);
        onSelectionChanged.Invoke(overlay, null);
        Application.DoEvents();
        var beforeMessage = (Rectangle)selectionField.GetValue(overlay)!;
        SendMessageW(overlay.Handle, 0x0100 /* WM_KEYDOWN */, (IntPtr)0x25 /* VK_LEFT */, IntPtr.Zero);
        var afterMessage = (Rectangle)selectionField.GetValue(overlay)!;
        Check(afterMessage == new Rectangle(beforeMessage.X - 1, beforeMessage.Y, beforeMessage.Width, beforeMessage.Height),
            $"真实 WM_KEYDOWN 路径也能微调选区 (实得 {afterMessage})");

        // 复位, 供下面的手柄用例继续使用
        selectionField.SetValue(overlay, selection);
        onSelectionChanged.Invoke(overlay, null);
        Application.DoEvents();

        // ===== 8. 选区手柄: 右下拖拽放大 =====
        // 先清掉标注, 避免点手柄时被标注命中抢先
        annotations.Clear();
        selectionField.SetValue(overlay, selection);
        onSelectionChanged.Invoke(overlay, null);
        Application.DoEvents();

        Down(selection.Right, selection.Bottom);
        Move(selection.Right + 60, selection.Bottom + 40);
        Up(selection.Right + 60, selection.Bottom + 40);

        var resized = (Rectangle)selectionField.GetValue(overlay)!;
        Check(Math.Abs(resized.Right - (selection.Right + 60)) <= 1,
            $"右下手柄把右边界拖到指针处 (实得 {resized.Right})");
        Check(resized.Left == selection.Left && resized.Top == selection.Top,
            $"拖右下手柄时左上角不动 (实得 {resized})");

        // ===== 9. 选中装饰不得进入导出图 =====
        annotations.Add(new Annotation
        {
            Tool = AnnotationTool.Rectangle,
            Start = new PointF(selection.Left * sf + 20, selection.Top * sf + 20),
            End = new PointF(selection.Left * sf + 160, selection.Top * sf + 120),
            StrokeWidth = 3,
            Color = Color.Red,
        });

        selectedField.SetValue(overlay, -1);
        var clean = (Bitmap)renderResult.Invoke(overlay, null)!;
        selectedField.SetValue(overlay, 0);
        var withSelection = (Bitmap)renderResult.Invoke(overlay, null)!;

        Check(clean.Size == withSelection.Size, "选中前后导出尺寸一致");
        Check(Bytes(clean).SequenceEqual(Bytes(withSelection)),
            "选中装饰没有泄漏进导出图 (逐像素一致)");

        clean.Dispose();
        withSelection.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>取出位图原始像素 (BGRA), 用于逐像素比对</summary>
    private static byte[] Bytes(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int length = Math.Abs(data.Stride) * bitmap.Height;
            var buffer = new byte[length];
            Marshal.Copy(data.Scan0, buffer, 0, length);
            return buffer;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
