// 交互式验证: 构造真实的 SnipOverlayForm, 模拟"选文字工具 -> 点击 -> 打字",
// 检查编辑框是否真的获得焦点并收到字符。这覆盖了纯离屏测试无法触及的焦点链。
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SnipasteOcr;
using SnipasteOcr.Annotations;
using Sdcb.SimdPaddleOCR;

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

            // 1b. 多屏: 工具栏必须夹紧在选区所在的那块屏内。
            //     旧实现按整个虚拟屏夹紧, 贴着接缝框选时工具栏会整条跑到另一块屏上。
            var selectionField = typeof(SnipOverlayForm).GetField("_selection", Priv)!;
            var onSelectionChanged = typeof(SnipOverlayForm).GetMethod("OnSelectionChanged", Priv)!;
            var screenToClient = typeof(SnipOverlayForm).GetMethod("ScreenRectToClient", Priv)!;
            foreach (var screen in Screen.AllScreens)
            {
                var screenClient = (Rectangle)screenToClient.Invoke(form, [screen.Bounds])!;
                if (screenClient.Width < 800)
                    continue;
                var edgeSel = new Rectangle(screenClient.Right - 200, screenClient.Top + 40, 160, 120);
                selectionField.SetValue(form, edgeSel);
                onSelectionChanged.Invoke(form, null);
                Application.DoEvents();
                Check(screenClient.Contains(toolbar.Bounds),
                    $"工具栏留在选区所在屏内 ({screen.DeviceName}: toolbar={toolbar.Bounds}, screen={screenClient})");
            }
            // 恢复正常选区, 后续用例继续使用
            selectionField.SetValue(form, sel);
            onSelectionChanged.Invoke(form, null);
            Application.DoEvents();

            // 1c. 悬停提示: 工具栏只有图标, 工具名与快捷键必须靠 tooltip 说明
            toolbar.Refresh();
            Application.DoEvents();
            var toolTip = (ToolTip)typeof(AnnotationToolbar).GetField("_toolTip", Priv)!.GetValue(toolbar)!;
            var toolRects = (List<(Rectangle Rect, AnnotationTool Tool)>)typeof(AnnotationToolbar)
                .GetField("_toolRects", Priv)!.GetValue(toolbar)!;
            var hover = typeof(AnnotationToolbar).GetMethod("OnMouseMove", Priv)!;
            Check(toolRects.Count == 6, $"工具栏建立 6 个工具命中区 (实得 {toolRects.Count})");
            if (toolRects.Count > 0)
            {
                var r = toolRects[0].Rect;
                hover.Invoke(toolbar, [new MouseEventArgs(MouseButtons.None, 0, r.Left + r.Width / 2, r.Top + r.Height / 2, 0)]);
                string tipText = toolTip.GetToolTip(toolbar) ?? string.Empty;
                Check(tipText.Contains("矩形 (1)"),
                    $"工具提示包含名称与快捷键 (实得 \"{tipText}\")");
            }
            var cancelRect = (Rectangle)typeof(AnnotationToolbar).GetField("_cancelRect", Priv)!.GetValue(toolbar)!;
            if (!cancelRect.IsEmpty)
            {
                hover.Invoke(toolbar, [new MouseEventArgs(MouseButtons.None, 0, cancelRect.Left + 2, cancelRect.Top + 2, 0)]);
                string cancelTip = toolTip.GetToolTip(toolbar) ?? string.Empty;
                Check(cancelTip.Contains("Esc"),
                    $"取消按钮提示带 Esc (实得 \"{cancelTip}\")");
            }
            hover.Invoke(toolbar, [new MouseEventArgs(MouseButtons.None, 0, 1, 1, 0)]);
            Check(string.IsNullOrEmpty(toolTip.GetToolTip(toolbar)), "移出可点区域后清空提示");

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
                // 同步读取: 这里不能 DoEvents —— 覆盖层是全屏前台窗口, 真实鼠标移动会产生
                // 额外的 OnMouseMove 并覆盖注入的选区, 造成偶发的"宽高没有逐步增大"。
                var live = (Rectangle?)selectionField.GetValue(form);
                Check(live is not null, $"拖拽第 {i} 步已有实时选区 (宽高可显示)");
                if (live is { } lr) liveSizes.Add(lr.Size);
            }
            Application.DoEvents();
            mouseUp.Invoke(form, [MouseArgs(new Point(start.X + 180, start.Y + 120))]);
            Application.DoEvents();

            // 松手即识别只属于识别模式; 图片模式必须停留在「选区 + 工具栏」阶段
            Check(!form.IsDisposed, "图片模式松手后覆盖层保持打开");
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

            // 9. 识别模式不应出现「可点但无反应」的贴图按钮
            Check(toolbar.ShowPin, "图片模式显示贴图按钮");
            using (var ocrOverlay = new SnipOverlayForm(SnipMode.Ocr))
            {
                var ocrToolbar = (AnnotationToolbar)toolbarField.GetValue(ocrOverlay)!;
                Check(!ocrToolbar.ShowPin, "识别模式隐藏贴图按钮");
                Check(ocrToolbar.Width < toolbar.Width,
                    $"识别模式工具栏更窄 (识别={ocrToolbar.Width}, 图片={toolbar.Width})");

                // 未框选提示必须写明松开鼠标后做什么与右键取消
                var hintProp = typeof(SnipOverlayForm).GetProperty("HintText", Priv)!;
                string ocrHint = (string)hintProp.GetValue(ocrOverlay)!;
                Check(ocrHint.Contains("识别") && ocrHint.Contains("右键"),
                    $"识别模式提示写明识别与右键取消 (实得 \"{ocrHint}\")");
                Check(ocrHint.Contains("松开"),
                    $"识别模式提示写明松手即识别 (实得 \"{ocrHint}\")");
                string imageHint = (string)hintProp.GetValue(form)!;
                Check(imageHint.Contains("复制图片") && imageHint.Contains("右键"),
                    $"图片模式提示写明复制图片与右键取消 (实得 \"{imageHint}\")");

                // 识别模式没有「选区持久化 + 标注」阶段: 即使直接触发选区变化也不能进入
                typeof(SnipOverlayForm).GetField("_selection", Priv)!
                    .SetValue(ocrOverlay, new Rectangle(100, 100, 200, 100));
                typeof(SnipOverlayForm).GetMethod("OnSelectionChanged", Priv)!.Invoke(ocrOverlay, null);
                Check(!(bool)typeof(SnipOverlayForm).GetField("_hasSelection", Priv)!.GetValue(ocrOverlay)!,
                    "识别模式不进入选区+工具栏状态");

                // 数字键 1..6 在识别模式不得切换标注工具
                typeof(SnipOverlayForm).GetMethod("OnKeyDown", Priv)!
                    .Invoke(ocrOverlay, [new KeyEventArgs(Keys.D1)]);
                Check(ocrToolbar.Tool == AnnotationTool.None, "识别模式数字键不切换标注工具");
            }

            // 10. 两个热键的修饰键必须各自独立 (旧实现只保存第一个框的, 第二个框的显示会骗人)
            using (var dlg = new HotKeyForm(0x70, 0x2, 0x51, 0x1))   // Ctrl+F1 / Alt+Q
            {
                Check(dlg.OcrModifiers == 0x2 && dlg.ImageModifiers == 0x1,
                    $"对话框分别保留两个修饰键 (实得 {dlg.OcrModifiers}/{dlg.ImageModifiers})");
                Check(dlg.OcrKey == 0x70 && dlg.ImageKey == 0x51, "对话框分别保留两个键码");
            }

            // 11. 托盘菜单里的按键文案必须随热键更新 (不能写死 F1/F3)
            using (var tray = new TrayController())
            {
                var hMenu = (IntPtr)typeof(TrayController).GetField("_hMenu", Priv)!.GetValue(tray)!;

                tray.SyncHotKeyLabels(0x70, 0, 0x72, 0);
                Check(MenuText(hMenu, 0x1000).Contains("F1", StringComparison.Ordinal),
                    $"默认菜单显示 F1 (实得 \"{MenuText(hMenu, 0x1000)}\")");
                Check(MenuText(hMenu, 0x1001).Contains("F3", StringComparison.Ordinal),
                    $"默认菜单显示 F3 (实得 \"{MenuText(hMenu, 0x1001)}\")");

                tray.SyncHotKeyLabels(0x51, 0x2 | 0x1, 0x2C, 0x4);   // Ctrl+Alt+Q / Shift+PrintScreen
                string ocrText = MenuText(hMenu, 0x1000);
                string imgText = MenuText(hMenu, 0x1001);
                Check(ocrText == "截图识别 (Ctrl+Alt+Q)",
                    $"改键后菜单同步识别热键 (实得 \"{ocrText}\")");
                Check(imgText == "截图标注 (Shift+PrintScreen)",
                    $"改键后菜单同步标注热键 (实得 \"{imgText}\")");
            }

            // 12. 真实键盘输入回归: 此前只用反射直接调用 OnKeyPress, 绕过了真实焦点链。
            //     实际使用中"点过工具栏 → 再点画布"会让工具栏先拿到焦点,
            //     此时必须把焦点交还给文字编辑控件, 否则打字全部落到工具栏上 (表现为"文字功能失效")。
            {
                toolbar.SetTool(AnnotationTool.Text);
                bool toolbarFocused = toolbar.Focus();   // 模拟刚点过工具栏
                Application.DoEvents();

                typeof(SnipOverlayForm).GetMethod("BeginAnnotate", Priv)!
                    .Invoke(form, [new Point(400, 300)]);   // 选区 (320,220,260,180) 内点击定位
                Application.DoEvents();

                var ed = (TextEditorOverlay?)editorField.GetValue(form);
                Check(ed is not null && !ed.IsDisposed, "点击后创建文字编辑控件");
                Check(ed is not null && ed.Focused,
                    $"点画布后编辑控件拿到真实焦点 (工具栏先聚焦={toolbarFocused}, 编辑控件聚焦={ed?.Focused})");

                if (ed is null || ed.IsDisposed)
                    return runner.ToResult();

                // 真实按键只会送到"系统前台窗口"。测试期间前台可能被终端抢走,
                // 这两条失败说明是测试环境问题, 而不是产品问题。
                Check(GetForegroundWindow() == form.Handle,
                    $"覆盖层处于系统前台 (前台=0x{GetForegroundWindow():X}, 覆盖层=0x{form.Handle:X})");
                Check(GetFocus() == ed.Handle,
                    $"系统焦点在编辑控件 (focus=0x{GetFocus():X}, editor=0x{ed.Handle:X})");

                // 走 SendInput 同一条系统输入队列: 这是此前断言覆盖不到的真实路径
                SendVirtualKey(0x41);   // VK_A
                Application.DoEvents();
                Check(ed.Value == "a", $"真实按键进入编辑控件 (实得 \"{ed.Value}\")");

                // 中文输入法: 编辑控件必须拿到 IME 上下文, 否则微软拼音等组合输入无法落到控件上
                // (表现为"英文/数字能打, 中文怎么打都没字")。
                IntPtr himc = ImmGetContext(ed.Handle);
                Check(himc != IntPtr.Zero, "编辑控件具备 IME 上下文 (中文输入法可用)");
                if (himc != IntPtr.Zero)
                    ImmReleaseContext(ed.Handle, himc);
            }
        }

        // 13. 结果窗口: 单击自动复制 (F-1)、隐藏选区与多选阅读顺序 (O-3/O-4)、事件退订 (O-5)
        {
            var statusField = typeof(OcrService).GetField("StatusChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
            int StatusSubscribers() =>
                (statusField.GetValue(OcrService.Instance) as Delegate)?.GetInvocationList().Length ?? 0;

            int subscribersBefore = StatusSubscribers();
            var src = new Bitmap(600, 300);
            var resultForm = new OcrResultForm(src);
            var settings = SettingsStore.Current;
            bool savedClickToCopy = settings.ClickToCopy;
            bool savedClickToTranslate = settings.ClickToTranslate;
            // 用户可能在真实配置里开了「单击翻译」: 这里必须强制关闭,
            // 否则 13.1「单击复制」会走翻译分支, 甚至真的发网络请求。
            settings.ClickToTranslate = false;
            string? originalClip = GetClipboardTextSafe();

            try
            {
                _ = resultForm.Handle; // 建句柄但不 Show: Show 会触发 Shown -> 真的开始 OCR
                Check(StatusSubscribers() == subscribersBefore + 1, "结果窗口订阅引擎状态事件");

                // 无边框窗口的移动方式: 识别期间的状态栏必须提示, 不能只靠用户猜
                string initialStatus = (string)typeof(OcrResultForm).GetField("_statusText", Priv)!.GetValue(resultForm)!;
                Check(initialStatus.Contains("拖动空白处移动"),
                    $"初始状态提示拖动空白处移动窗口 (实得 \"{initialStatus}\")");

                // 状态栏长文本: 面板夹在窗口内, 文字框宽度有限, EndEllipsis 才会生效
                var statusLayout = typeof(OcrResultForm)
                    .GetMethod("ComputeStatusLayout", BindingFlags.NonPublic | BindingFlags.Static)!;
                var (longBox, longText) = ((Rectangle Box, Rectangle Text))statusLayout.Invoke(
                    null, [new Size(2000, 20), resultForm.ClientSize, 0])!;
                Check(longBox.Right <= resultForm.ClientSize.Width - 8 && longBox.Left >= 4,
                    $"超长状态文本的面板不越出窗口 (box={longBox}, client={resultForm.ClientSize})");
                Check(longText.Width < 2000,
                    $"超长状态文本获得有限绘制宽度, 省略号可生效 (text={longText})");
                var (shortBox, _) = ((Rectangle Box, Rectangle Text))statusLayout.Invoke(
                    null, [new Size(100, 20), resultForm.ClientSize, 0])!;
                Check(shortBox.Right == resultForm.ClientSize.Width - 8,
                    $"短状态文本仍贴右侧 (box={shortBox})");

                // 检测顺序故意与阅读顺序不同: 输入 Bottom, Top, Middle
                var lines = new[]
                {
                    Det("Bottom", 10, 200),
                    Det("Top", 10, 20),
                    Det("Middle", 10, 110),
                };
                typeof(OcrResultForm).GetField("_lines", Priv)!.SetValue(resultForm, lines);
                typeof(OcrResultForm).GetField("_ocrDone", Priv)!.SetValue(resultForm, true);
                typeof(OcrResultForm).GetField("_wordOrder", Priv)!.SetValue(resultForm, true);
                var buildWeights = typeof(OcrResultForm).GetMethod("BuildCharWeights", BindingFlags.NonPublic | BindingFlags.Static)!;
                typeof(OcrResultForm).GetField("_lineCumW", Priv)!.SetValue(resultForm, buildWeights.Invoke(null, [lines]));
                typeof(OcrResultForm).GetField("_lineRank", Priv)!.SetValue(
                    resultForm, OcrSelection.BuildReadingRanks(lines, sortReadingOrder: true));

                settings.ClickToCopy = true;
                var mouseDown = typeof(OcrResultForm).GetMethod("OnMouseDown", Priv)!;
                var mouseMove = typeof(OcrResultForm).GetMethod("OnMouseMove", Priv)!;
                var mouseUp = typeof(OcrResultForm).GetMethod("OnMouseUp", Priv)!;

                void ClickAt(Point p)
                {
                    mouseDown.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0)]);
                    mouseUp.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, p.X, p.Y, 0)]);
                }

                var topPoint = BoxCenter(resultForm, lines[1].Box);
                var middlePoint = BoxCenter(resultForm, lines[2].Box);
                var bottomPoint = BoxCenter(resultForm, lines[0].Box);
                var selected = (HashSet<int>)typeof(OcrResultForm).GetField("_selected", Priv)!.GetValue(resultForm)!;

                // 13.1 单击文本块: 选中整行并复制该块
                SetClipboardTextSafe("--marker--");
                ClickAt(topPoint);
                Check(GetClipboardTextSafe() == "Top",
                    $"单击复制当前检测块 (实得 \"{GetClipboardTextSafe()}\")");
                Check(selected.SetEquals([1]), "单击后整行保持选中");
                string status = (string)typeof(OcrResultForm).GetField("_statusText", Priv)!.GetValue(resultForm)!;
                Check(status.Contains("已复制", StringComparison.Ordinal), $"状态栏显示已复制 (实得 \"{status}\")");

                // 13.2 Shift+单击只切换多选, 不写剪贴板
                SetClipboardTextSafe("--marker--");
                keybd_event(0x10, 0, 0, IntPtr.Zero); // VK_SHIFT down
                try { ClickAt(bottomPoint); }
                finally { keybd_event(0x10, 0, 0x0002, IntPtr.Zero); } // KEYEVENTF_KEYUP
                Check(GetClipboardTextSafe() == "--marker--", "Shift+单击不写剪贴板");
                Check(selected.SetEquals([0, 1]),
                    $"Shift+单击切换多选 (实得 {string.Join(",", selected.OrderBy(i => i))})");

                // 13.3 块内拖动是字符级拖选, 松手不复制
                SetClipboardTextSafe("--marker--");
                mouseDown.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, middlePoint.X - 20, middlePoint.Y, 0)]);
                mouseMove.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, middlePoint.X + 20, middlePoint.Y, 0)]);
                mouseUp.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, middlePoint.X + 20, middlePoint.Y, 0)]);
                Check(GetClipboardTextSafe() == "--marker--", "块内拖选松手不复制");

                // 13.4 单击空白不复制
                SetClipboardTextSafe("--marker--");
                ClickAt(new Point(resultForm.ClientSize.Width - 30, resultForm.ClientSize.Height / 2));
                Check(GetClipboardTextSafe() == "--marker--", "单击空白不复制");
                Check(selected.Count == 0, "单击空白清空整行选中");

                // 13.5 关闭开关后只选中不复制
                settings.ClickToCopy = false;
                SetClipboardTextSafe("--marker--");
                ClickAt(middlePoint);
                Check(GetClipboardTextSafe() == "--marker--", "关闭 ClickToCopy 后单击不复制");
                Check(selected.SetEquals([2]), "关闭开关后仍选中整行");

                // 13.6 全选复制按阅读顺序输出
                SetClipboardTextSafe("--marker--");
                InvokeCmdKey(resultForm, Keys.Control | Keys.A);
                InvokeCmdKey(resultForm, Keys.Control | Keys.C);
                string? copied = GetClipboardTextSafe();
                // 剪贴板文本统一为 CRLF (Windows 约定), 同一份内容从不同入口复制出去换行必须一致
                string expectedAll = string.Join(Environment.NewLine, ["Top", "Middle", "Bottom"]);
                Check(copied == expectedAll,
                    $"Ctrl+A / Ctrl+C 按阅读顺序输出且换行为 CRLF (实得 \"{copied?.Replace("\r\n", "\\r\\n").Replace("\n", "\\n")}\")");

                // 13.7 可选设置「单击翻译」: 译文贴在窗口底部, 且不再自动复制
                var barTextField = typeof(OcrResultForm).GetField("_barText", Priv)!;
                var barErrorField = typeof(OcrResultForm).GetField("_barError", Priv)!;
                string BarText() => (string)barTextField.GetValue(resultForm)!;
                string Status() => (string)typeof(OcrResultForm).GetField("_statusText", Priv)!.GetValue(resultForm)!;

                settings.ClickToTranslate = true;
                settings.ClickToCopy = true; // 两个都开时以翻译为准, 剪贴板不能被写
                int translateCalls = 0;
                resultForm.TranslateForTest = (text, _) =>
                {
                    translateCalls++;
                    return Task.FromResult(new TranslateResult(true, "译文:" + text, null, 1, 2));
                };

                SetClipboardTextSafe("--marker--");
                ClickAt(topPoint);
                PumpUntil(() => BarText().Length > 0, 2000);
                Check(BarText() == "译文:Top",
                    $"单击翻译把译文显示在窗口底部 (实得 \"{BarText()}\")");
                Check(GetClipboardTextSafe() == "--marker--",
                    "开启单击翻译后单击不再写剪贴板");
                Check(selected.SetEquals([1]), "单击翻译时仍整行选中被翻译的块");
                Check(Status().Contains("翻译完成"),
                    $"状态栏反馈翻译完成 (实得 \"{Status()}\")");

                // 同一块再点一次: 译文条已显示该块结果, 不再重复发请求 (每次请求都计费)
                ClickAt(topPoint);
                Application.DoEvents();
                Check(translateCalls == 1,
                    $"同一文字块重复单击不重复请求 (请求次数={translateCalls})");

                // 13.7.1 译文条「复制」: 直接把译文写进剪贴板 (反馈走状态栏)
                SetClipboardTextSafe("--marker--");
                var copyLayout = (TranslationBarLayout.Layout)typeof(OcrResultForm)
                    .GetMethod("MeasureTranslationBar", Priv)!.Invoke(resultForm, null)!;
                var copyCenter = new Point(copyLayout.Copy.Left + copyLayout.Copy.Width / 2,
                                           copyLayout.Copy.Top + copyLayout.Copy.Height / 2);
                mouseDown.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, copyCenter.X, copyCenter.Y, 0)]);
                mouseUp.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, copyCenter.X, copyCenter.Y, 0)]);
                Check(GetClipboardTextSafe() == "译文:Top",
                    $"译文条「复制」把译文写进剪贴板 (实得 \"{GetClipboardTextSafe()}\")");
                Check(Status().Contains("已复制"),
                    $"复制译文后状态栏反馈 (实得 \"{Status()}\")");

                // 13.8 译文条自己的命中优先于背后的图片/文字块
                var measureBar = typeof(OcrResultForm).GetMethod("MeasureTranslationBar", Priv)!;
                var barLayout = (TranslationBarLayout.Layout)measureBar.Invoke(resultForm, null)!;
                var barCenter = new Point(barLayout.Box.Left + barLayout.Box.Width / 2,
                                          barLayout.Box.Top + barLayout.Box.Height / 2);
                mouseDown.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, barCenter.X, barCenter.Y, 0)]);
                mouseUp.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, barCenter.X, barCenter.Y, 0)]);
                Check(BarText() == "译文:Top", "点击译文条本体不会关闭/误改译文");
                Check(selected.SetEquals([1]), "点击译文条不会清空选区 (没有透传给空白处逻辑)");

                // 关闭图标: 清空译文条 (文字块选中保留)
                var closeCenter = new Point(barLayout.Close.Left + barLayout.Close.Width / 2,
                                            barLayout.Close.Top + barLayout.Close.Height / 2);
                mouseDown.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, closeCenter.X, closeCenter.Y, 0)]);
                Check(BarText().Length == 0, "点译文条关闭图标清空译文");

                // 13.9 失败信息同样只落在译文条, 不冒充成功
                resultForm.TranslateForTest = (_, _) =>
                    Task.FromResult(TranslateResult.Fail("网络不可用"));
                settings.ClickToTranslate = true;
                ClickAt(middlePoint);
                PumpUntil(() => BarText() == "网络不可用", 2000);
                Check(BarText() == "网络不可用",
                    $"失败提示显示在译文条 (实得 \"{BarText()}\")");
                Check((bool)barErrorField.GetValue(resultForm)!, "失败状态会切换译文条配色");

                // 失败态的「复制」必须不可用: 不能把错误提示当成译文复制出去
                SetClipboardTextSafe("--marker--");
                var errorLayout = (TranslationBarLayout.Layout)measureBar.Invoke(resultForm, null)!;
                var errorCopy = new Point(errorLayout.Copy.Left + errorLayout.Copy.Width / 2,
                                          errorLayout.Copy.Top + errorLayout.Copy.Height / 2);
                mouseDown.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, errorCopy.X, errorCopy.Y, 0)]);
                mouseUp.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, errorCopy.X, errorCopy.Y, 0)]);
                Check(GetClipboardTextSafe() == "--marker--",
                    "失败态「复制」不可用, 不会把错误提示写进剪贴板");

                // 单击空白处: 清空选区的同时收起译文条, 画面与选区保持一致
                ClickAt(new Point(resultForm.ClientSize.Width - 30, resultForm.ClientSize.Height / 2));
                Check(BarText().Length == 0, "单击空白处收起译文条");

                resultForm.TranslateForTest = null;
                resultForm.Close();
                Check(StatusSubscribers() == subscribersBefore, "关闭结果窗口后退订引擎状态事件");
            }
            finally
            {
                settings.ClickToCopy = savedClickToCopy;
                settings.ClickToTranslate = savedClickToTranslate;
                RestoreClipboard(originalClip);
                resultForm.Dispose();
                src.Dispose();
            }
        }

        // 14. O-2 / O-7: 窗口自身 DPI 基准 + CTC 真实字符框选词
        {
            var src = new Bitmap(600, 300);
            var resultForm = new OcrResultForm(src);
            var settings = SettingsStore.Current;
            bool savedClickToCopy = settings.ClickToCopy;
            bool savedClickToTranslate = settings.ClickToTranslate;
            // 本段只测拖选/DPI: 关掉单击翻译, 避免误发真实请求
            settings.ClickToTranslate = false;
            string? originalClip = GetClipboardTextSafe();

            try
            {
                _ = resultForm.Handle;
                var line = CtcDet("abc", 10, 10, 300, 40);
                var lines = new[] { line };
                typeof(OcrResultForm).GetField("_lines", Priv)!.SetValue(resultForm, lines);
                typeof(OcrResultForm).GetField("_ocrDone", Priv)!.SetValue(resultForm, true);
                typeof(OcrResultForm).GetField("_wordOrder", Priv)!.SetValue(resultForm, true);
                var buildWeights = typeof(OcrResultForm).GetMethod("BuildCharWeights", BindingFlags.NonPublic | BindingFlags.Static)!;
                typeof(OcrResultForm).GetField("_lineCumW", Priv)!.SetValue(resultForm, buildWeights.Invoke(null, [lines]));
                typeof(OcrResultForm).GetField("_lineGlyphs", Priv)!.SetValue(resultForm, OcrGlyphLayout.BuildAll(lines));
                typeof(OcrResultForm).GetField("_lineRank", Priv)!.SetValue(
                    resultForm, OcrSelection.BuildReadingRanks(lines, sortReadingOrder: true));
                settings.ClickToCopy = true;

                // 14.1 DPI: 注入 1.5 后 ImageRect 必须按窗口 DPI 居中, 而不是主屏的 1.0
                resultForm.SetDpiFactorForTest(1.5f);
                var imageRect = (RectangleF)typeof(OcrResultForm).GetMethod("ImageRect", Priv)!
                    .Invoke(resultForm, null)!;
                float scale = (float)typeof(OcrResultForm).GetField("_scale", Priv)!.GetValue(resultForm)!;
                float expectedX = (resultForm.ClientSize.Width * 1.5f - src.Width * scale) / 2f;
                Check(Math.Abs(imageRect.X - expectedX) <= 1f,
                    $"DPI=1.5 时 ImageRect 使用窗口自身 DPI (实得 {imageRect.X:F1}, 期望 {expectedX:F1})");

                var center = BoxCenter(resultForm, line.Box);
                int hit = (int)typeof(OcrResultForm).GetMethod("HitTest", Priv)!.Invoke(resultForm, [center])!;
                Check(hit == 0, $"DPI=1.5 时命中仍落在文本块内 (实得 {hit})");
                resultForm.SetDpiFactorForTest(1f);

                // 14.2 CTC 真实宽度: u=0.2..0.3 对应第二个字符;
                // 旧权重算法在这里两端都落在字符 1, 会得到空选区。
                var mouseDown = typeof(OcrResultForm).GetMethod("OnMouseDown", Priv)!;
                var mouseMove = typeof(OcrResultForm).GetMethod("OnMouseMove", Priv)!;
                var mouseUp = typeof(OcrResultForm).GetMethod("OnMouseUp", Priv)!;
                var from = BoxPointAt(resultForm, line.Box, 0.2f);
                var to = BoxPointAt(resultForm, line.Box, 0.3f);
                SetClipboardTextSafe("--marker--");
                mouseDown.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, from.X, from.Y, 0)]);
                mouseMove.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, to.X, to.Y, 0)]);
                mouseUp.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, to.X, to.Y, 0)]);
                InvokeCmdKey(resultForm, Keys.Control | Keys.C);
                Check(GetClipboardTextSafe() == "b",
                    $"CTC 真实边界把 0.2~0.3 映射到第二个字符 (实得 \"{GetClipboardTextSafe()}\")");

                // 14.3 CTC 下行尾仍能包含最后一个字符
                var head = BoxPointAt(resultForm, line.Box, 0.02f);
                var tail = BoxPointAt(resultForm, line.Box, 0.98f);
                SetClipboardTextSafe("--marker--");
                mouseDown.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, head.X, head.Y, 0)]);
                mouseMove.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, tail.X, tail.Y, 0)]);
                mouseUp.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, tail.X, tail.Y, 0)]);
                InvokeCmdKey(resultForm, Keys.Control | Keys.C);
                Check(GetClipboardTextSafe() == "abc",
                    $"CTC 下行尾拖选包含全部字符 (实得 \"{GetClipboardTextSafe()}\")");

                // 14.4 拖出文本块: 结束拖选并复位行号, 不能把状态留到下一次点击
                var inBox = BoxPointAt(resultForm, line.Box, 0.2f);
                mouseDown.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1, inBox.X, inBox.Y, 0)]);
                mouseMove.Invoke(resultForm, [new MouseEventArgs(MouseButtons.Left, 1,
                    resultForm.ClientSize.Width - 2, resultForm.ClientSize.Height - 2, 0)]);
                bool charDragging = (bool)typeof(OcrResultForm).GetField("_charDrag", Priv)!.GetValue(resultForm)!;
                int dragLine = (int)typeof(OcrResultForm).GetField("_dragLine", Priv)!.GetValue(resultForm)!;
                Check(!charDragging && dragLine == -1,
                    $"拖出文本块后结束拖选并复位行号 (charDrag={charDragging}, dragLine={dragLine})");

                resultForm.Close();
            }
            finally
            {
                settings.ClickToCopy = savedClickToCopy;
                settings.ClickToTranslate = savedClickToTranslate;
                RestoreClipboard(originalClip);
                resultForm.Dispose();
                src.Dispose();
            }
        }

        // 15. 多屏: 结果窗口必须开在截图选区所在屏 (旧实现 CenterScreen 永远主屏)
        {
            using var bmp = new Bitmap(240, 120);
            var target = Screen.AllScreens[^1];
            using var anchored = new OcrResultForm(bmp, target.Bounds);
            _ = anchored.Handle; // 建句柄触发 OnHandleCreated 的尺寸校正与居中, 不 Show 以免真的开始 OCR
            Application.DoEvents();
            var actual = Screen.FromControl(anchored);
            Check(actual.DeviceName == target.DeviceName,
                $"结果窗口开在截图所在屏 (期望 {target.DeviceName}, 实得 {actual.DeviceName})");
            bool oversized = anchored.Width > target.WorkingArea.Width || anchored.Height > target.WorkingArea.Height;
            Check(oversized || target.WorkingArea.Contains(anchored.Bounds),
                $"结果窗口整体落在该屏工作区内 (窗口={anchored.Bounds}, 工作区={target.WorkingArea})");
            anchored.Close();
        }

        // 16. O-9: 高倍缩放平移与鼠标锚点缩放
        {
            using var panSrc = new Bitmap(600, 300);
            using var panForm = new OcrResultForm(panSrc);
            try
            {
                _ = panForm.Handle;
                var zoom = typeof(OcrResultForm).GetMethod("Zoom",
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    [typeof(float), typeof(Point?)], null)!;
                var imageRect = typeof(OcrResultForm).GetMethod("ImageRect", Priv)!;
                var mouseDown = typeof(OcrResultForm).GetMethod("OnMouseDown", Priv)!;
                var mouseMove = typeof(OcrResultForm).GetMethod("OnMouseMove", Priv)!;
                var mouseUp = typeof(OcrResultForm).GetMethod("OnMouseUp", Priv)!;
                var wheel = typeof(OcrResultForm).GetMethod("OnMouseWheel", Priv)!;
                var scaleField = typeof(OcrResultForm).GetField("_scale", Priv)!;
                var panField = typeof(OcrResultForm).GetField("_panOffset", Priv)!;

                RectangleF Rect() => (RectangleF)imageRect.Invoke(panForm, null)!;
                var center = new Point(panForm.ClientSize.Width / 2, panForm.ClientSize.Height / 2);

                // 限制窗口最大尺寸: 否则大屏上 FitWindowToImage 会把窗口长到装下整张图, 平移无从触发
                panForm.MaximumSize = new Size(700, 500);

                // 放大到超出视口, 图片只能居中显示一部分; 此时中键拖动应能移动视口
                zoom.Invoke(panForm, [4f, null]);
                var before = Rect();
                mouseDown.Invoke(panForm, [new MouseEventArgs(MouseButtons.Middle, 1, center.X, center.Y, 0)]);
                mouseMove.Invoke(panForm, [new MouseEventArgs(MouseButtons.Middle, 0, center.X + 100, center.Y + 50, 0)]);
                mouseUp.Invoke(panForm, [new MouseEventArgs(MouseButtons.Middle, 1, center.X + 100, center.Y + 50, 0)]);
                var after = Rect();
                Check(Math.Abs(after.X - before.X - 100f) <= 1f && Math.Abs(after.Y - before.Y - 50f) <= 1f,
                    $"中键拖动平移图片 (dx={after.X - before.X:F1}, dy={after.Y - before.Y:F1})");

                // 未按 Ctrl 的滚轮纵向平移 (向上滚看上方内容, 图片下移)
                float beforeWheelY = Rect().Y;
                wheel.Invoke(panForm, [new MouseEventArgs(MouseButtons.None, 0, center.X, center.Y, 120)]);
                Check(Math.Abs(Rect().Y - beforeWheelY - 80f) <= 1f,
                    $"滚轮纵向平移 (dy={Rect().Y - beforeWheelY:F1})");

                // 缩放锚点: 鼠标下的原图内容在缩放后仍应落在同一客户区位置
                var anchor = new Point(panForm.ClientSize.Width / 3, panForm.ClientSize.Height / 4);
                var r0 = Rect();
                float s0 = (float)scaleField.GetValue(panForm)!;
                float imageX = (anchor.X - r0.X) / s0;
                float imageY = (anchor.Y - r0.Y) / s0;
                zoom.Invoke(panForm, [1.25f, anchor]);
                var r1 = Rect();
                float s1 = (float)scaleField.GetValue(panForm)!;
                Check(Math.Abs((anchor.X - r1.X) / s1 - imageX) <= 2f &&
                      Math.Abs((anchor.Y - r1.Y) / s1 - imageY) <= 2f,
                    "缩放以鼠标位置为锚点 (原图点不跑偏)");

                // 缩回适应窗口后平移必须归零, 图片重新居中
                panForm.SetDpiFactorForTest(1f);
                var pan = (PointF)panField.GetValue(panForm)!;
                Check(pan == PointF.Empty, $"缩放到适应窗口后平移归零 (实得 {pan})");
            }
            finally
            {
                panForm.Close();
            }
        }

        // 17. O-8: 低置信度可视化 (橙色虚线框 + 悬停状态栏百分比)
        {
            using var confSrc = new Bitmap(600, 300);
            using var confForm = new OcrResultForm(confSrc);
            try
            {
                _ = confForm.Handle;

                PaddleOcrLine ConfDet(string text, float left, float top, float score) => new()
                {
                    Text = text,
                    Box = new PaddleOcrDetectionBox(
                        left, top, left + 100, top, left + 100, top + 24, left, top + 24, 1f),
                    RecognitionScore = score,
                    ClassificationScore = 1f,
                    ClassificationLabel = 0,
                    AppliedRotationDegrees = 0,
                    EmittedCount = 0,
                };

                var low = ConfDet("Low", 10, 10, 0.62f);
                var high = ConfDet("High", 10, 90, 0.95f);
                typeof(OcrResultForm).GetField("_lines", Priv)!.SetValue(confForm, new[] { low, high });
                typeof(OcrResultForm).GetField("_ocrDone", Priv)!.SetValue(confForm, true);

                using var frame = new Bitmap(confForm.ClientSize.Width, confForm.ClientSize.Height);
                using (var g = Graphics.FromImage(frame))
                using (var pea = new PaintEventArgs(g, new Rectangle(Point.Empty, frame.Size)))
                    typeof(OcrResultForm).GetMethod("OnPaint", Priv)!.Invoke(confForm, [pea]);

                var boxToPoints = typeof(OcrResultForm).GetMethod("BoxToClientPoints", Priv)!;
                RectangleF BoundsOf(PointF[] pts) => new(
                    pts.Min(p => p.X), pts.Min(p => p.Y),
                    pts.Max(p => p.X) - pts.Min(p => p.X), pts.Max(p => p.Y) - pts.Min(p => p.Y));

                int OrangePixels(Bitmap target, RectangleF box)
                {
                    int n = 0;
                    int l = Math.Max(0, (int)Math.Floor(box.Left) - 2);
                    int r = Math.Min(target.Width, (int)Math.Ceiling(box.Right) + 2);
                    int t = Math.Max(0, (int)Math.Floor(box.Top) - 2);
                    int bottom = Math.Min(target.Height, (int)Math.Ceiling(box.Bottom) + 2);
                    for (int y = t; y < bottom; y++)
                        for (int x = l; x < r; x++)
                        {
                            var c = target.GetPixel(x, y);
                            if (c.R > 200 && c.G is > 90 and < 190 && c.B < 80)
                                n++;
                        }
                    return n;
                }

                var lowPts = (PointF[])boxToPoints.Invoke(confForm, [low.Box])!;
                var highPts = (PointF[])boxToPoints.Invoke(confForm, [high.Box])!;
                int lowOrange = OrangePixels(frame, BoundsOf(lowPts));
                int highOrange = OrangePixels(frame, BoundsOf(highPts));
                Check(lowOrange > 10, $"低置信度框画成橙色虚线 (橙色像素={lowOrange})");
                Check(highOrange == 0, $"高置信度框不画橙色 (橙色像素={highOrange})");

                // 悬停状态栏: 行文本 + 置信度百分比
                var center = new Point((int)lowPts.Average(p => p.X), (int)lowPts.Average(p => p.Y));
                typeof(OcrResultForm).GetMethod("OnMouseMove", Priv)!
                    .Invoke(confForm, [new MouseEventArgs(MouseButtons.None, 0, center.X, center.Y, 0)]);
                string confStatus = (string)typeof(OcrResultForm).GetField("_statusText", Priv)!.GetValue(confForm)!;
                Check(confStatus.Contains("置信度 62%") && confStatus.Contains("Low"),
                    $"悬停状态栏合并文本与置信度 (实得 \"{confStatus}\")");

                // 换一批识别结果后, 四角缓存必须按新数组重建 (重新识别/注入结果都走这条路径)
                typeof(OcrResultForm).GetField("_hover", Priv)!.SetValue(confForm, -1);
                var moved = ConfDet("Low", 10, 160, 0.62f);
                typeof(OcrResultForm).GetField("_lines", Priv)!.SetValue(confForm, new[] { moved });
                using var movedFrame = new Bitmap(confForm.ClientSize.Width, confForm.ClientSize.Height);
                using (var g = Graphics.FromImage(movedFrame))
                using (var pea = new PaintEventArgs(g, new Rectangle(Point.Empty, movedFrame.Size)))
                    typeof(OcrResultForm).GetMethod("OnPaint", Priv)!.Invoke(confForm, [pea]);

                var movedPts = (PointF[])boxToPoints.Invoke(confForm, [moved.Box])!;
                Check(OrangePixels(movedFrame, BoundsOf(movedPts)) > 10,
                    "更换识别结果后框画到新位置 (缓存已失效)");
                Check(OrangePixels(movedFrame, BoundsOf(lowPts)) == 0,
                    "旧位置不再残留低置信度框");
            }
            finally
            {
                confForm.Close();
            }
        }

        // 18. F-2: 双击选词 / 三击选行, 不触发单击复制
        {
            using var wordSrc = new Bitmap(600, 200);
            using var wordForm = new OcrResultForm(wordSrc);
            var wordSettings = SettingsStore.Current;
            bool savedClickToCopy = wordSettings.ClickToCopy;
            string? originalClip = GetClipboardTextSafe();
            try
            {
                _ = wordForm.Handle;
                var line = Det("Hello world", 10, 10);
                typeof(OcrResultForm).GetField("_lines", Priv)!.SetValue(wordForm, new[] { line });
                typeof(OcrResultForm).GetField("_ocrDone", Priv)!.SetValue(wordForm, true);
                var buildWeights = typeof(OcrResultForm).GetMethod("BuildCharWeights",
                    BindingFlags.NonPublic | BindingFlags.Static)!;
                typeof(OcrResultForm).GetField("_lineCumW", Priv)!
                    .SetValue(wordForm, buildWeights.Invoke(null, [new[] { line }]));

                var down = typeof(OcrResultForm).GetMethod("OnMouseDown", Priv)!;
                var up = typeof(OcrResultForm).GetMethod("OnMouseUp", Priv)!;
                var partialField = typeof(OcrResultForm).GetField("_partial", Priv)!;
                var selectedField = typeof(OcrResultForm).GetField("_selected", Priv)!;

                wordSettings.ClickToCopy = true;
                SetClipboardTextSafe("--marker--");

                var top = BoxPointAt(wordForm, line.Box, 0.05f);
                var pt = new Point(top.X, top.Y + 10);

                down.Invoke(wordForm, [new MouseEventArgs(MouseButtons.Left, 2, pt.X, pt.Y, 0)]);
                up.Invoke(wordForm, [new MouseEventArgs(MouseButtons.Left, 2, pt.X, pt.Y, 0)]);
                var wordRange = ((int Line, int Start, int End)?)partialField.GetValue(wordForm);
                Check(wordRange is { Line: 0, Start: 0, End: 5 },
                    $"双击选中英文单词 (实得 {wordRange})");
                Check(((HashSet<int>)selectedField.GetValue(wordForm)!).SetEquals([0]),
                    "双击后整行仍保持选中");
                Check(GetClipboardTextSafe() == "--marker--", "双击第二下松手不写剪贴板");
                string wordStatus = (string)typeof(OcrResultForm).GetField("_statusText", Priv)!.GetValue(wordForm)!;
                Check(wordStatus.Contains("已选 5 字符"), $"双击后状态栏显示 5 字符 (实得 \"{wordStatus}\")");

                down.Invoke(wordForm, [new MouseEventArgs(MouseButtons.Left, 3, pt.X, pt.Y, 0)]);
                up.Invoke(wordForm, [new MouseEventArgs(MouseButtons.Left, 3, pt.X, pt.Y, 0)]);
                Check(partialField.GetValue(wordForm) is null, "三击切换为整行选中 (无行内范围)");
                Check(GetClipboardTextSafe() == "--marker--", "三击动作不写剪贴板");
            }
            finally
            {
                wordSettings.ClickToCopy = savedClickToCopy;
                RestoreClipboard(originalClip);
                wordForm.Close();
            }
        }

        // 19. 识别模式 (F1): 松手即识别, 全程不出现标注工具栏, 但保留尺寸徽标
        {
            using var ocrOverlay = new SnipOverlayForm(SnipMode.Ocr);
            var ocrToolbar = (AnnotationToolbar)typeof(SnipOverlayForm).GetField("_toolbar", Priv)!.GetValue(ocrOverlay)!;
            var ocrSelection = typeof(SnipOverlayForm).GetField("_selection", Priv)!;
            var ocrHasSelection = typeof(SnipOverlayForm).GetField("_hasSelection", Priv)!;

            int launches = 0;
            Size launchedSize = default;
            Rectangle launchedAnchor = default;
            ocrOverlay.OcrResultLauncherForTest = (bmp, anchor) =>
            {
                launches++;
                launchedSize = bmp.Size;
                launchedAnchor = anchor;
                bmp.Dispose();
            };

            ocrOverlay.Show();
            Application.DoEvents();

            // 即使有人直接触发选区变化, 识别模式也不得进入「选区 + 工具栏」状态
            typeof(SnipOverlayForm).GetMethod("OnSelectionChanged", Priv)!.Invoke(ocrOverlay, null);
            Application.DoEvents();
            Check(!(bool)ocrHasSelection.GetValue(ocrOverlay)!,
                "识别模式不进入选区+工具栏状态");
            Check(!ocrToolbar.Visible, "识别模式工具栏始终不可见");

            var down = typeof(SnipOverlayForm).GetMethod("OnMouseDown", Priv)!;
            var move = typeof(SnipOverlayForm).GetMethod("OnMouseMove", Priv)!;
            var up = typeof(SnipOverlayForm).GetMethod("OnMouseUp", Priv)!;

            down.Invoke(ocrOverlay, [MouseArgs(new Point(200, 150))]);
            move.Invoke(ocrOverlay, [MouseArgs(new Point(380, 270))]);

            var live = (Rectangle?)ocrSelection.GetValue(ocrOverlay);
            Check(live == new Rectangle(200, 150, 180, 120),
                $"识别模式拖拽实时更新选区 (实得 {live})");
            Check(!ocrToolbar.Visible, "识别模式拖拽中也不显示工具栏");
            Check(launches == 0, "识别模式松手前不启动识别");

            if (live is { } sel)
            {
                var screenBmp = (Bitmap)typeof(SnipOverlayForm).GetField("_screen", Priv)!.GetValue(ocrOverlay)!;
                float sf = (float)typeof(SnipOverlayForm).GetProperty("ScaleFactor", Priv)!.GetValue(ocrOverlay)!;
                var export = SnipGeometry.ExportRect(sel, sf, screenBmp.Size);

                // 用户明确要求识别模式拖拽时仍显示「宽 × 高」徽标
                var badge = SnipSizeBadge.ComputeLayout(
                    sel, SnipSizeBadge.MeasureText(export.Size), ocrOverlay.ClientSize, null);
                using var frame = new Bitmap(ocrOverlay.ClientSize.Width, ocrOverlay.ClientSize.Height);
                using (var g = Graphics.FromImage(frame))
                using (var pea = new PaintEventArgs(g, new Rectangle(Point.Empty, frame.Size)))
                {
                    typeof(SnipOverlayForm).GetMethod("OnPaint", Priv)!.Invoke(ocrOverlay, [pea]);
                }

                int whites = 0;
                for (int y = badge.Top + 1; y < Math.Min(frame.Height, badge.Bottom - 1); y++)
                    for (int x = badge.Left + 1; x < Math.Min(frame.Width, badge.Right - 1); x++)
                    {
                        var c = frame.GetPixel(x, y);
                        if (c.R > 230 && c.G > 230 && c.B > 230) whites++;
                    }
                Check(whites > 5, $"识别模式拖拽时仍绘制尺寸徽标 (白色像素={whites})");

                up.Invoke(ocrOverlay, [MouseArgs(new Point(380, 270))]);

                Check(launches == 1, $"识别模式松手立即识别 (实得 {launches} 次)");
                Check(launchedSize == export.Size,
                    $"识别裁剪尺寸等于导出尺寸 (实得 {launchedSize}, 期望 {export.Size})");
                var vs = (Rectangle)typeof(SnipOverlayForm).GetField("_virtualScreen", Priv)!.GetValue(ocrOverlay)!;
                Check(launchedAnchor == new Rectangle(vs.Left + export.X, vs.Top + export.Y, export.Width, export.Height),
                    $"识别裁剪锚点落在屏幕坐标 (实得 {launchedAnchor})");
                Check(ocrOverlay.IsDisposed, "识别模式松手后覆盖层关闭");

                Application.DoEvents();
                Check(launches == 1, "识别模式松手后不会重复识别");
            }
        }

        // 19.1 识别模式: 小于 MinDragSide 的微拖不触发识别, 覆盖层保持打开
        {
            using var tinyOverlay = new SnipOverlayForm(SnipMode.Ocr);
            int launches = 0;
            tinyOverlay.OcrResultLauncherForTest = (bmp, _) => { launches++; bmp.Dispose(); };
            tinyOverlay.Show();
            Application.DoEvents();

            var down = typeof(SnipOverlayForm).GetMethod("OnMouseDown", Priv)!;
            var move = typeof(SnipOverlayForm).GetMethod("OnMouseMove", Priv)!;
            var up = typeof(SnipOverlayForm).GetMethod("OnMouseUp", Priv)!;
            var selection = typeof(SnipOverlayForm).GetField("_selection", Priv)!;

            // 3×1: 两边都小于阈值
            down.Invoke(tinyOverlay, [MouseArgs(new Point(200, 150))]);
            up.Invoke(tinyOverlay, [MouseArgs(new Point(203, 151))]);

            // 180×2: 单边小于阈值
            down.Invoke(tinyOverlay, [MouseArgs(new Point(200, 150))]);
            move.Invoke(tinyOverlay, [MouseArgs(new Point(380, 152))]);
            up.Invoke(tinyOverlay, [MouseArgs(new Point(380, 152))]);

            Check(launches == 0, $"微拖不触发识别 (实得 {launches} 次)");
            Check(!tinyOverlay.IsDisposed, "微拖后覆盖层保持打开");
            Check(selection.GetValue(tinyOverlay) is null, "微拖不产生选区");

            tinyOverlay.Close();
        }

        // 20. 放大镜与取色器: 真实窗口下验证可见性、C 键与文字编辑让行
        {
            using var magnifierOverlay = new SnipOverlayForm(SnipMode.Image);
            string? originalClip = GetClipboardTextSafe();
            Point originalCursor = Cursor.Position;
            try
            {
                var overlayMagnifier = (SnipMagnifierControl)typeof(SnipOverlayForm)
                    .GetField("_magnifier", Priv)!.GetValue(magnifierOverlay)!;
                var overlayToolbar = (AnnotationToolbar)typeof(SnipOverlayForm)
                    .GetField("_toolbar", Priv)!.GetValue(magnifierOverlay)!;
                var overlaySelection = typeof(SnipOverlayForm).GetField("_selection", Priv)!;
                var overlayHasSelection = typeof(SnipOverlayForm).GetField("_hasSelection", Priv)!;
                var overlaySelectionChanged = typeof(SnipOverlayForm).GetMethod("OnSelectionChanged", Priv)!;
                var overlayScreenToClient = typeof(SnipOverlayForm).GetMethod("ScreenRectToClient", Priv)!;
                var overlayMove = typeof(SnipOverlayForm).GetMethod("OnMouseMove", Priv)!;

                magnifierOverlay.Show();
                Application.DoEvents();

                overlaySelection.SetValue(magnifierOverlay, null);
                overlayHasSelection.SetValue(magnifierOverlay, false);
                overlayToolbar.SetTool(AnnotationTool.None);
                overlayToolbar.Visible = false;

                Cursor.Position = magnifierOverlay.PointToScreen(new Point(300, 300));
                Application.DoEvents();

                Point cursorClient = magnifierOverlay.PointToClient(Cursor.Position);
                overlayMove.Invoke(magnifierOverlay,
                    [new MouseEventArgs(MouseButtons.None, 0, cursorClient.X, cursorClient.Y, 0)]);
                Application.DoEvents();

                Check(overlayMagnifier.Visible, "未框选时鼠标移动后放大镜可见");
                Check(magnifierOverlay.ClientRectangle.Contains(overlayMagnifier.Bounds),
                    $"放大镜完整落在覆盖层客户区内 (实得 {overlayMagnifier.Bounds})");
                var screenClient = (Rectangle)overlayScreenToClient.Invoke(
                    magnifierOverlay, [Screen.FromPoint(Cursor.Position).Bounds])!;
                Check(screenClient.Contains(overlayMagnifier.Bounds),
                    $"放大镜留在光标所在屏内 (实得 {overlayMagnifier.Bounds}, 屏 {screenClient})");
                Check(!overlayMagnifier.Bounds.Contains(cursorClient), "放大镜不盖住光标");

                float sf = (float)typeof(SnipOverlayForm).GetProperty("ScaleFactor", Priv)!.GetValue(magnifierOverlay)!;
                var screenBmp = (Bitmap)typeof(SnipOverlayForm).GetField("_screen", Priv)!.GetValue(magnifierOverlay)!;
                var physical = new Point(
                    (int)Math.Floor(cursorClient.X * sf),
                    (int)Math.Floor(cursorClient.Y * sf));
                Color expected = SnipMagnifier.Sample(screenBmp, physical);

                SetClipboardTextSafe("--marker--");
                typeof(SnipOverlayForm).GetMethod("OnKeyDown", Priv)!
                    .Invoke(magnifierOverlay, [new KeyEventArgs(Keys.C)]);
                Application.DoEvents();
                Check(GetClipboardTextSafe() == SnipMagnifier.FormatHex(expected),
                    $"C 复制光标像素颜色 (期望 {SnipMagnifier.FormatHex(expected)}, 实得 {GetClipboardTextSafe()})");

                // 文字编辑期间必须整体让行, C 不能被当成取色快捷键
                using (var editorForColor = new TextEditorOverlay(14f))
                {
                    var overlayEditor = typeof(SnipOverlayForm).GetField("_editor", Priv)!;
                    overlayEditor.SetValue(magnifierOverlay, editorForColor);
                    SetClipboardTextSafe("--marker--");
                    typeof(SnipOverlayForm).GetMethod("OnKeyDown", Priv)!
                        .Invoke(magnifierOverlay, [new KeyEventArgs(Keys.C)]);
                    Check(GetClipboardTextSafe() == "--marker--", "文字编辑期间 C 不触发取色");
                    overlayEditor.SetValue(magnifierOverlay, null);
                }

                // 框选完成后放大镜让位给工具栏与选区内容
                overlaySelection.SetValue(magnifierOverlay, new Rectangle(100, 100, 300, 200));
                overlaySelectionChanged.Invoke(magnifierOverlay, null);
                Application.DoEvents();
                Check(!overlayMagnifier.Visible, "框选完成后放大镜隐藏");
            }
            finally
            {
                Cursor.Position = originalCursor;
                RestoreClipboard(originalClip);
                if (!magnifierOverlay.IsDisposed)
                    magnifierOverlay.Close();
            }
        }

        // 17. 翻译窗口: 真实窗口 + 注入假翻译结果, 不联网
        {
            // 生产路径是 form.Show(结果窗口); 测试同样给一个 owner, 保持与真实使用一致。
            using var owner = new Form { Text = "translate-owner", ShowInTaskbar = false };
            owner.Show();
            Application.DoEvents();

            using var tr = new TranslateForm(
                "Hello world", "English", false,
                _ => Task.FromResult(new TranslateResult(true, "你好，世界", null, 12, 34)));
            tr.Show(owner);
            PumpUntil(() => tr.CopyEnabledForTest, 3000);
            Application.DoEvents();

            Check(tr.SourceTextForTest == "Hello world", "翻译窗口显示原文");
            Check(tr.TranslationTextForTest == "你好，世界",
                $"翻译窗口显示译文 (实得 \"{tr.TranslationTextForTest}\")");
            Check(tr.CopyEnabledForTest, "翻译成功后复制按钮可用");
            Check(tr.StatusTextForTest.Contains("tokens"),
                $"状态栏显示 token 用量 (实得 \"{tr.StatusTextForTest}\")");

            // 居中: 按 owner 所在屏的工作区居中, 不是 CenterParent (结果窗口贴边时会被推出屏幕)
            Rectangle workArea = Screen.FromControl(tr).WorkingArea;
            int dx = tr.Left + tr.Width / 2 - (workArea.Left + workArea.Width / 2);
            int dy = tr.Top + tr.Height / 2 - (workArea.Top + workArea.Height / 2);
            Check(Math.Abs(dx) <= 4 && Math.Abs(dy) <= 4,
                $"译文窗口在所在屏工作区居中 (偏移 {dx},{dy}, screen={workArea})");
            Check(Screen.FromControl(tr).Equals(Screen.FromControl(owner)),
                "译文窗口开在 owner 所在屏");

            // 走真实按键验证 Esc: 反射调用 ProcessCmdKey 会绕过焦点链,
            // 掩盖“只读 TextBox 吞掉 Escape”这类真实键盘问题。
            tr.Activate();
            SetForegroundWindow(tr.Handle);
            Application.DoEvents();
            bool foreground = GetForegroundWindow() == tr.Handle;
            Check(foreground,
                $"翻译窗口处于系统前台 (前台=0x{GetForegroundWindow():X}, 窗口=0x{tr.Handle:X})");
            if (foreground)
            {
                SendVirtualKey(0x1B);   // VK_ESCAPE
                PumpUntil(() => !tr.Visible, 2000);
            }
            Check(!tr.Visible,
                $"Esc 关闭翻译窗口 (visible={tr.Visible}, disposed={tr.IsDisposed}, handle={tr.IsHandleCreated})");

            using var trErr = new TranslateForm(
                "x", "English", false,
                _ => Task.FromResult(TranslateResult.Fail("网络不可用")));
            trErr.Show(owner);
            PumpUntil(() => trErr.TranslationTextForTest == "网络不可用", 3000);
            Check(trErr.TranslationTextForTest == "网络不可用",
                $"失败提示显示在译文区 (实得 \"{trErr.TranslationTextForTest}\")");
            Check(!trErr.CopyEnabledForTest, "翻译失败时复制按钮保持禁用");
            trErr.Close();
            owner.Close();

            // 设置对话框: 只验证 Key 输入与掩码显示, 不点保存 (避免改写真实配置)
            using var settingsOwner = new Form { ShowInTaskbar = false };
            settingsOwner.Show();
            Application.DoEvents();
            using var settingsForm = new TranslateSettingsForm();
            settingsForm.Show(settingsOwner);
            Application.DoEvents();
            settingsForm.SetKeyTextForTest("sk-test-abcd");
            Application.DoEvents();
            Check(settingsForm.KeyBoxTextForTest == "sk-test-abcd", "翻译设置的 Key 输入框可输入");
            Check(settingsForm.KeyStatusForTest.Contains("****abcd"),
                $"Key 状态只显示末 4 位 (实得 \"{settingsForm.KeyStatusForTest}\")");
            settingsForm.Close();
            settingsOwner.Close();
        }

        return runner.ToResult();
    }

    /// <summary>泵消息直到条件成立或超时 (翻译窗口的结果是异步回投的)</summary>
    private static void PumpUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
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

    /// <summary>检测框中心对应的客户端坐标 (走产品自己的坐标换算, 避免测试另写一份)</summary>
    private static Point BoxCenter(OcrResultForm form, PaddleOcrDetectionBox box)
    {
        var mi = typeof(OcrResultForm).GetMethod("BoxToClientPoints", Priv)!;
        var pts = (PointF[])mi.Invoke(form, [box])!;
        return new Point(
            (int)Math.Round(pts.Average(p => p.X)),
            (int)Math.Round(pts.Average(p => p.Y)));
    }

    /// <summary>检测框顶边上归一化位置 u 处的客户端坐标</summary>
    private static Point BoxPointAt(OcrResultForm form, PaddleOcrDetectionBox box, float u)
    {
        var mi = typeof(OcrResultForm).GetMethod("BoxToClientPoints", Priv)!;
        var pts = (PointF[])mi.Invoke(form, [box])!;
        return new Point(
            (int)Math.Round(pts[0].X + (pts[1].X - pts[0].X) * u),
            (int)Math.Round(pts[0].Y + (pts[1].Y - pts[0].Y) * u));
    }

    /// <summary>构造逐字 CTC 行 (每字占 2 列, 字间空 2 列), 用于验证真实字符边界</summary>
    private static PaddleOcrLine CtcDet(string text, float left, float top, float width, float height)
    {
        int timeSteps = Math.Max(20, text.Length * 4 + 2);
        var spans = new PaddleOcrCtcSpan[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            spans[i] = new PaddleOcrCtcSpan
            {
                Text = text[i].ToString(),
                Score = 1f,
                StartColumn = i * 4,
                EndColumn = i * 4 + 2,
            };
        }

        return new PaddleOcrLine
        {
            Text = text,
            Box = new PaddleOcrDetectionBox(
                left, top, left + width, top, left + width, top + height, left, top + height, 1f),
            RecognitionScore = 1f,
            ClassificationScore = 1f,
            ClassificationLabel = 0,
            AppliedRotationDegrees = 0,
            EmittedCount = (uint)text.Length,
            CtcSpans = spans,
            RecognitionTimeSteps = timeSteps,
            RecognitionContentWidth = 100,
            RecognitionTensorWidth = 100,
        };
    }

    /// <summary>反射调用结果窗口的 ProcessCmdKey (protected 且参数是 ref Message)</summary>
    private static bool InvokeCmdKey(OcrResultForm form, Keys key)
    {
        var mi = typeof(OcrResultForm).GetMethod("ProcessCmdKey", Priv)!;
        object?[] args = [new Message { Msg = 0x0100 }, key];   // WM_KEYDOWN
        return (bool)mi.Invoke(form, args)!;
    }

    /// <summary>构造一行检测结果 (轴对齐 100x24)</summary>
    private static PaddleOcrLine Det(string text, float left, float top)
        => new()
        {
            Text = text,
            Box = new PaddleOcrDetectionBox(
                left, top, left + 100, top, left + 100, top + 24, left, top + 24, 1f),
            RecognitionScore = 1f,
            ClassificationScore = 1f,
            ClassificationLabel = 0,
            AppliedRotationDegrees = 0,
            EmittedCount = 0,
        };

    /// <summary>读剪贴板: 其它程序短暂占用是常态, 重试几次再判失败</summary>
    private static string? GetClipboardTextSafe()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch { Thread.Sleep(20); }
        }
        return null;
    }

    /// <summary>写剪贴板: 同样带重试, 避免被别的进程瞬间占用导致后续断言连锁失败</summary>
    private static void SetClipboardTextSafe(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch { Thread.Sleep(20); }
        }
        // 仍失败说明剪贴板长期不可用: 后续断言会如实失败
    }

    private static void RestoreClipboard(string? text)
    {
        try
        {
            if (text is null) Clipboard.Clear();
            else Clipboard.SetText(text);
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int cchMax, uint flags);

    /// <summary>按命令 ID 读取托盘菜单项文本 (验证热键文案是动态生成的)</summary>
    private static string MenuText(IntPtr menu, uint cmd)
    {
        var sb = new StringBuilder(256);
        GetMenuStringW(menu, cmd, sb, sb.Capacity, 0);   // 0 = MF_BYCOMMAND
        return sb.ToString();
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    /// <summary>发送一次真实按键 (走系统输入队列, 与用户敲键盘同一条路径)</summary>
    private static void SendVirtualKey(byte vk)
    {
        keybd_event(vk, 0, 0, IntPtr.Zero);
        Thread.Sleep(30);
        keybd_event(vk, 0, 0x0002, IntPtr.Zero);   // KEYEVENTF_KEYUP
        Thread.Sleep(60);
    }
}
