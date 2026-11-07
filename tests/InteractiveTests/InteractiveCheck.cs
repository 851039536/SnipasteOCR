// 交互式验证: 构造真实的 SnipOverlayForm, 模拟"选文字工具 -> 点击 -> 打字",
// 检查编辑框是否真的获得焦点并收到字符。这覆盖了纯离屏测试无法触及的焦点链。
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SnipasteOcr;
using SnipasteOcr.Annotations;

namespace SnipasteOcr.Tests;

internal static class InteractiveCheck
{
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        Application.EnableVisualStyles();

        // 覆盖层构造时会抓屏; 这在有桌面会话的机器上可用
        SnipOverlayForm? form = null;
        try
        {
            form = new SnipOverlayForm(SnipMode.Image);
        }
        catch (Exception ex)
        {
            runner.Check(false, "构造 SnipOverlayForm 失败: " + ex.Message);
            return runner.ToResult();
        }

        using (form)
        {
            // 需要真实窗口句柄, 才能在窗体上做焦点测试
            form.Show();
            Application.DoEvents();

            var toolbarField = typeof(SnipOverlayForm).GetField("_toolbar", Priv)!;
            var toolbar = (AnnotationToolbar)toolbarField.GetValue(form)!;

            var editorField = typeof(SnipOverlayForm).GetField("_editor", Priv)!;

            // 1. 设定选区 (直接注入, 等价于用户拖拽出一个框)
            var sel = new Rectangle(100, 100, 400, 300);
            typeof(SnipOverlayForm).GetField("_selection", Priv)!.SetValue(form, sel);
            typeof(SnipOverlayForm).GetMethod("OnSelectionChanged", Priv)!.Invoke(form, null);
            Application.DoEvents();

            Check(toolbar.Visible, "框选后工具栏可见");
            Check(toolbar.Width > 0 && toolbar.Height > 0, $"工具栏有有效尺寸 ({toolbar.Width}x{toolbar.Height})");
            Check(sel.Contains(toolbar.Left, toolbar.Top) || toolbar.Bottom > sel.Bottom,
                "工具栏位于选区下方 (或翻转后在上方)");

            // 2. 切到文字工具
            toolbar.SetTool(AnnotationTool.Text);
            Application.DoEvents();
            Check(toolbar.Tool == AnnotationTool.Text, "已切到文字工具");

            // 3. 在选区内点击 -> 应弹出编辑框
            var beginAnnotate = typeof(SnipOverlayForm).GetMethod("BeginAnnotate", Priv)!;
            beginAnnotate.Invoke(form, [new Point(200, 200)]);
            Application.DoEvents();

            var editor = (TextEditorOverlay?)editorField.GetValue(form);
            Check(editor is not null, "点击后创建了编辑框");

            if (editor is null)
            {
                return runner.ToResult();
            }

            Check(editor.Parent is not null, "编辑框已挂到覆盖层上 (用于接收键盘输入)");
            Check(editor.Width > 0 && editor.Height > 0, $"编辑框尺寸有效 ({editor.Width}x{editor.Height})");

            // 4. 焦点: 这是用户报的 bug 的核心
            Application.DoEvents();
            Check(editor.Focused, $"编辑框获得键盘焦点 (Focused={editor.Focused})");
            Check(form.ActiveControl == editor, $"编辑框是窗体的活动控件 (ActiveControl={form.ActiveControl?.GetType().Name})");

            // 5. 验证"让行": 编辑期间窗体的 ProcessCmdKey 必须返回 false,
            //    否则数字键会被覆盖层当作切换工具的快捷键吞掉 (输入没反应的成因之一)。
            //    ProcessCmdKey 是 protected, 用反射调用。
            bool cmdKeyBypassed = InvokeProcessCmdKey(form, Keys.D1);
            Check(cmdKeyBypassed, "编辑期间窗体 ProcessCmdKey 让行 (返回 false)");

            int toolbarBefore = (int)toolbar.Tool;
            SendChar(editor, '测');
            SendChar(editor, 'A');
            SendChar(editor, '1');
            Application.DoEvents();

            Check(editor.Value == "测A1", $"编辑框收到字符 (实得 \"{editor.Value}\")");
            Check(toolbar.Tool == AnnotationTool.Text, "输入数字 '1' 未被当作切换工具");
            Check((int)toolbar.Tool == toolbarBefore, "工具栏工具未被字符输入改变");

            // 6. 关键回归: 输入的文字必须真的被绘制到窗体上。
            //    历史 bug: 编辑框作为透明子控件被父窗口整屏绘制覆盖, 输入内容完全不可见。
            //    因此这里直接检查"窗体渲染结果里有没有文字色像素", 而不是只检查控件状态。
            var editingText = typeof(SnipOverlayForm).GetField("_editingText", Priv)!.GetValue(form);
            Check(editingText as string == "测A1", $"窗体已同步编辑内容 (实得 \"{editingText}\")");

            using (var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            {
                using (var g = Graphics.FromImage(bmp))
                using (var pea = new PaintEventArgs(g, new Rectangle(0, 0, bmp.Width, bmp.Height)))
                {
                    typeof(SnipOverlayForm).GetMethod("OnPaint", Priv)!.Invoke(form, [pea]);
                }

                // 选区 (100,100,400,300) 内应出现文字色像素
                int textPixels = 0;
                for (int y = 100; y < Math.Min(bmp.Height, 400); y++)
                    for (int x = 100; x < Math.Min(bmp.Width, 500); x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        if (c.R > 180 && c.G < 120 && c.B < 120) textPixels++;
                    }

                Check(textPixels > 20, $"输入文字被绘制到窗体上 (文字色像素={textPixels})");
            }

            // 回车提交 -> 生成文字标注
            var annotations = (List<Annotation>)typeof(SnipOverlayForm)
                .GetField("_annotations", Priv)!.GetValue(form)!;

            SendKeyDown(editor, Keys.Enter);
            Application.DoEvents();

            Check(annotations.Count == 1, $"回车后生成 1 条标注 (实得 {annotations.Count})");
            if (annotations.Count == 1)
            {
                var a = annotations[0];
                Check(a.Tool == AnnotationTool.Text, "标注类型为文字");
                Check(a.Text == "测A1", $"标注文本正确 (实得 \"{a.Text}\")");
                Check(a.FontSize > 0, $"标注字号有效 ({a.FontSize})");
                Check(a.Bounds.Width > 0 && a.Bounds.Height > 0, $"标注包围盒有效 ({a.Bounds.Width:F0}x{a.Bounds.Height:F0})");
            }

            Check(editorField.GetValue(form) is null, "提交后编辑框已关闭并清空引用");

            // 7. 选区外的点击应重新框选而非绘制
            toolbar.SetTool(AnnotationTool.Rectangle);
            Application.DoEvents();
            var outside = new Point(10, 10); // 选区 (100,100,400,300) 之外
            typeof(SnipOverlayForm).GetMethod("OnMouseDown", Priv)!
                .Invoke(form, [MouseArgs(outside)]);
            Application.DoEvents();
            Check(toolbar.Tool == AnnotationTool.None, "选区外按下会退出标注工具 (恢复可重新框选)");

            // 8. 框选过程中的实时尺寸徽标: 每一步拖拽都要更新选区并画出徽标,
            //    且徽标里的数值来自导出尺寸 (物理像素), 不是逻辑像素。
            var selectionField = typeof(SnipOverlayForm).GetField("_selection", Priv)!;
            selectionField.SetValue(form, null);

            var mouseDown = typeof(SnipOverlayForm).GetMethod("OnMouseDown", Priv)!;
            var mouseMove = typeof(SnipOverlayForm).GetMethod("OnMouseMove", Priv)!;
            var mouseUp = typeof(SnipOverlayForm).GetMethod("OnMouseUp", Priv)!;

            var start = new Point(200, 150);
            mouseDown.Invoke(form, [MouseArgs(start)]);

            var liveSizes = new List<Size>();
            for (int i = 1; i <= 3; i++)
            {
                mouseMove.Invoke(form, [MouseArgs(new Point(start.X + 60 * i, start.Y + 40 * i))]);
                Application.DoEvents();
                var live = (Rectangle?)selectionField.GetValue(form);
                Check(live is not null, $"拖拽第 {i} 步已有实时选区 (宽高可显示)");
                if (live is { } lr) liveSizes.Add(lr.Size);
            }
            mouseUp.Invoke(form, [MouseArgs(new Point(start.X + 180, start.Y + 120))]);
            Application.DoEvents();

            Check(liveSizes.Count == 3, $"拖拽全程都有选区 (实得 {liveSizes.Count}/3)");
            Check(liveSizes.Count == 3 && liveSizes[0].Width < liveSizes[1].Width && liveSizes[1].Width < liveSizes[2].Width &&
                  liveSizes[0].Height < liveSizes[1].Height && liveSizes[1].Height < liveSizes[2].Height,
                "拖拽过程中宽高逐步增大 (不是松手才更新)");

            var liveSel = (Rectangle?)selectionField.GetValue(form);
            Check(liveSel == new Rectangle(start.X, start.Y, 180, 120),
                $"松手后选区等于拖拽范围 (实得 {liveSel})");

            if (liveSel is { } finalSel)
            {
                var screenBmp = (Bitmap)typeof(SnipOverlayForm).GetField("_screen", Priv)!.GetValue(form)!;
                float sf = (float)typeof(SnipOverlayForm).GetProperty("ScaleFactor", Priv)!.GetValue(form)!;
                var exported = SnipGeometry.ExportRect(finalSel, sf, screenBmp.Size);

                Check(exported.Width == (int)Math.Round(finalSel.Width * sf) &&
                      exported.Height == (int)Math.Round(finalSel.Height * sf),
                    $"导出尺寸按物理像素换算 ({exported.Width}x{exported.Height}, scale={sf:F2})");

                // 工具栏是子控件, 会盖住徽标: 参考图与断言都按真实工具栏外框布局
                Rectangle? toolbarBox = toolbar.Visible ? toolbar.Bounds : null;
                var badgeBox = SnipSizeBadge.ComputeLayout(
                    finalSel, SnipSizeBadge.MeasureText(exported.Size), form.ClientSize, toolbarBox);
                Check(badgeBox.Left >= 0 && badgeBox.Top >= 0 &&
                      badgeBox.Right <= form.ClientSize.Width && badgeBox.Bottom <= form.ClientSize.Height,
                    $"徽标完整落在屏幕内 (实得 {badgeBox})");

                using var frame = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                using (var g = Graphics.FromImage(frame))
                using (var pea = new PaintEventArgs(g, new Rectangle(Point.Empty, frame.Size)))
                {
                    typeof(SnipOverlayForm).GetMethod("OnPaint", Priv)!.Invoke(form, [pea]);
                }

                // 徽标框内应出现白色数字像素 (只查框内, 避免与选区蓝框/文字标注混淆)。
                // 12px 字号下每位数只有十几颗纯白核心像素, 因此不设绝对阈值,
                // 而是与"同样尺寸单独画一遍"的参考图对比: 少了说明没画或画错了尺寸。
                static int WhiteCount(Bitmap bmp, Rectangle box)
                {
                    int n = 0;
                    for (int y = box.Top + 1; y < Math.Min(bmp.Height, box.Bottom - 1); y++)
                        for (int x = box.Left + 1; x < Math.Min(bmp.Width, box.Right - 1); x++)
                        {
                            var c = bmp.GetPixel(x, y);
                            if (c.R > 230 && c.G > 230 && c.B > 230) n++;
                        }
                    return n;
                }

                int numeralPixels = WhiteCount(frame, badgeBox);
                using var reference = new Bitmap(frame.Width, frame.Height);
                using (var g = Graphics.FromImage(reference))
                {
                    g.Clear(Color.White);
                    SnipSizeBadge.Draw(g, finalSel, exported.Size, reference.Size, toolbarBox);
                }
                int referencePixels = WhiteCount(reference, badgeBox);

                Check(numeralPixels > 5,
                    $"覆盖层把尺寸数字画在徽标内 (白色像素={numeralPixels})");
                Check(Math.Abs(numeralPixels - referencePixels) <= Math.Max(6, referencePixels / 4),
                    $"徽标数值与参考绘制一致 (覆盖层={numeralPixels}, 参考={referencePixels})");

                // 换一个明显不同的选区并真正重绘: 徽标必须画到新位置, 而不是固定不动。
                // 只比较两次 ComputeLayout 的结果是恒真的, 证明不了"跟随", 所以这里走真实 OnPaint。
                var movedSel = new Rectangle(320, 220, 260, 180);
                selectionField.SetValue(form, movedSel);
                typeof(SnipOverlayForm).GetMethod("OnSelectionChanged", Priv)!.Invoke(form, null);
                Application.DoEvents();

                using var movedFrame = new Bitmap(form.ClientSize.Width, form.ClientSize.Height);
                using (var g = Graphics.FromImage(movedFrame))
                using (var pea = new PaintEventArgs(g, new Rectangle(Point.Empty, movedFrame.Size)))
                {
                    typeof(SnipOverlayForm).GetMethod("OnPaint", Priv)!.Invoke(form, [pea]);
                }

                var movedExported = SnipGeometry.ExportRect(movedSel, sf, screenBmp.Size);
                var movedBox = SnipSizeBadge.ComputeLayout(
                    movedSel, SnipSizeBadge.MeasureText(movedExported.Size), form.ClientSize,
                    toolbar.Visible ? toolbar.Bounds : null);

                int movedPixels = WhiteCount(movedFrame, movedBox);
                int stalePixels = WhiteCount(movedFrame, badgeBox);
                Check(movedPixels > 5 && stalePixels < 5,
                    $"移动选区后徽标跟随到新位置 (新位置={movedPixels}, 旧位置={stalePixels})");
            }
        }

        return runner.ToResult();
    }

    /// <summary>
    /// 反射调用 protected 的 ProcessCmdKey, 返回"是否让行"(即返回 false)。
    /// 参数是 ref Message, 反射需要装箱成 object[] 再取出。
    /// </summary>
    private static bool InvokeProcessCmdKey(Form form, Keys key)
    {
        var mi = typeof(SnipOverlayForm).GetMethod("ProcessCmdKey", Priv)!;
        object?[] args = [default(Message), key];
        bool handled = (bool)mi.Invoke(form, args)!;
        return !handled;   // false = 未处理, 按键继续下传
    }

    /// <summary>反射调用 OnKeyPress 模拟字符输入</summary>
    private static void SendChar(Control c, char ch)
        => typeof(TextEditorOverlay).GetMethod("OnKeyPress", Priv)!
            .Invoke(c, [new KeyPressEventArgs(ch)]);

    /// <summary>反射调用 OnKeyDown 模拟功能键</summary>
    private static void SendKeyDown(Control c, Keys key)
        => typeof(TextEditorOverlay).GetMethod("OnKeyDown", Priv)!
            .Invoke(c, [new KeyEventArgs(key)]);

    /// <summary>构造鼠标按下参数</summary>
    private static MouseEventArgs MouseArgs(Point p)
        => new(MouseButtons.Left, 1, p.X, p.Y, 0);
}
