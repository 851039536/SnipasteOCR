using System.Drawing.Drawing2D;
using SnipasteOcr.Annotations;

namespace SnipasteOcr;

/// <summary>
/// 全屏截图覆盖层 (仿 Snipaste): 拖拽框选 → 选区下方浮出标注工具栏 →
/// 可绘制矩形/椭圆/箭头/画笔/马赛克/文字 → 双击/回车确认, Esc/右键取消。
///
/// 坐标系约定 (关键):
/// - <b>物理像素</b>: <see cref="_screen"/> 底图与所有 <see cref="Annotation"/> 几何数据。
/// - <b>逻辑像素</b>: WinForms 控件/鼠标事件坐标, 与物理像素相差 <see cref="ScaleFactor"/> 倍。
/// 两者在鼠标事件入口 (<see cref="ToPhysical"/>) 与绘制出口 (<see cref="ToLogical"/>) 各转换一次。
/// </summary>
public sealed class SnipOverlayForm : Form
{
    private readonly Bitmap _screen;
    private readonly SnipMode _mode;
    private Point _anchor;
    private bool _dragging;
    private Rectangle? _selection; // 客户区 (逻辑) 坐标

    /// <summary>已完成的标注 (物理像素坐标)</summary>
    private readonly List<Annotation> _annotations = [];

    /// <summary>撤销/重做历史</summary>
    private readonly AnnotationHistory _history = new();

    /// <summary>正在拖拽绘制的标注 (未提交, 不参与命中测试与历史)</summary>
    private Annotation? _pending;

    /// <summary>当前是否有过一次有效框选 (决定工具栏是否显示)</summary>
    private bool _hasSelection;

    private readonly AnnotationToolbar _toolbar = new();
    private TextEditorOverlay? _editor;
    private PointF _textAnchorPhysical;

    /// <summary>构造覆盖层并在显示前抓取整屏截图; mode 决定确认后走 OCR 还是复制图片</summary>
    public SnipOverlayForm(SnipMode mode)
    {
        _mode = mode;

        // 先抓取屏幕 (必须在本窗体显示之前)
        Rectangle vs = SystemInformation.VirtualScreen;
        _screen = new Bitmap(vs.Width, vs.Height);
        using (var g = Graphics.FromImage(_screen))
            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, _screen.Size, CopyPixelOperation.SourceCopy);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Cursor = Cursors.Cross;
        StartPosition = FormStartPosition.Manual;
        Bounds = vs;
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        KeyPreview = true;

        // 工具栏: 初始隐藏, 框选完成后出现在选区下方
        _toolbar.Visible = false;
        _toolbar.ToolChanged += OnToolChanged;
        _toolbar.UndoRequested += DoUndo;
        _toolbar.RedoRequested += DoRedo;
        _toolbar.ConfirmRequested += Confirm;
        _toolbar.CancelRequested += Close;
        _toolbar.SaveRequested += SaveToFile;
        Controls.Add(_toolbar);

        SyncToolbarState();
    }

    /// <summary>物理像素/逻辑像素比例 (处理多显示器 DPI 缩放)</summary>
    private float ScaleFactor => _screen.Width / (float)ClientSize.Width;

    // ===== 坐标换算 =====

    /// <summary>逻辑 (客户区) 坐标 → 物理像素</summary>
    private PointF ToPhysical(Point p) => new(p.X * ScaleFactor, p.Y * ScaleFactor);

    /// <summary>物理像素 → 逻辑 (客户区) 坐标</summary>
    private PointF ToLogical(PointF p)
    {
        float sf = ScaleFactor;
        return new PointF(p.X / sf, p.Y / sf);
    }

    /// <summary>物理矩形 → 逻辑矩形 (用于裁剪绘制)</summary>
    private RectangleF ToLogical(RectangleF r)
    {
        float sf = ScaleFactor;
        return new RectangleF(r.X / sf, r.Y / sf, r.Width / sf, r.Height / sf);
    }

    /// <summary>当前选区对应的物理像素矩形 (无选区时返回 null)</summary>
    private RectangleF? SelectionPhysical
    {
        get
        {
            if (_selection is not { } sel || sel.Width <= 2 || sel.Height <= 2)
                return null;
            float sf = ScaleFactor;
            return new RectangleF(sel.X * sf, sel.Y * sf, sel.Width * sf, sel.Height * sf);
        }
    }

    /// <summary>当前选中工具的画笔 (从工具栏同步)</summary>
    private Annotation CreateShape(AnnotationTool tool, PointF start) => new()
    {
        Tool = tool,
        Color = _toolbar.CurrentColor,
        StrokeWidth = _toolbar.CurrentWidth,
        Start = start,
        End = start,
        FontSize = AnnotationEngine.DefaultFontSize * ScaleFactor,
    };

    // ===== 绘制 =====

    /// <summary>绘制: 全屏底图 + 选区遮罩 + 标注 (已完成 + 拖拽中) + 工具栏</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImage(_screen, 0, 0, ClientSize.Width, ClientSize.Height);

        if (_selection is { } sel && sel.Width > 2 && sel.Height > 2)
        {
            float sf = ScaleFactor;

            // 选区外半透明遮罩
            using (var dim = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillRectangle(dim, ClientRectangle);
            // 把选区原样画回 (去除遮罩)
            g.DrawImage(_screen, sel, new RectangleF(sel.X * sf, sel.Y * sf, sel.Width * sf, sel.Height * sf), GraphicsUnit.Pixel);

            // 标注: 只在选区内绘制 (与最终导出一致)
            var physSel = new RectangleF(sel.X * sf, sel.Y * sf, sel.Width * sf, sel.Height * sf);
            DrawAnnotations(g, physSel);

            // 边框 + 尺寸标签
            using (var pen = new Pen(Color.White, 2f))
                g.DrawRectangle(pen, sel);
            using (var pen2 = new Pen(Color.FromArgb(255, 33, 150, 243), 2f))
                g.DrawRectangle(pen2, new Rectangle(sel.X + 1, sel.Y + 1, Math.Max(0, sel.Width - 2), Math.Max(0, sel.Height - 2)));

            DrawSizeLabel(g, sel, sf);
        }
        else
        {
            // 未框选时底部提示
            using var font = new Font("Microsoft YaHei UI", 10f);
            string hint = _mode == SnipMode.Ocr
                ? "拖拽选择区域  ·  双击/回车 确认  ·  Esc 取消"
                : "拖拽选择区域  ·  双击 确认复制图片  ·  Esc 取消";
            Size ts = TextRenderer.MeasureText(hint, font);
            Point pt = new((ClientSize.Width - ts.Width) / 2, ClientSize.Height - ts.Height - 18);
            using var bg = new SolidBrush(Color.FromArgb(190, 30, 30, 30));
            g.FillRectangle(bg, pt.X - 12, pt.Y - 4, ts.Width + 24, ts.Height + 8);
            TextRenderer.DrawText(g, hint, font, pt, Color.White);
        }
    }

    /// <summary>
    /// 绘制标注。已完成的用引擎整体绘制, 拖拽中的单独绘制在最上层,
    /// 两者都裁剪到选区内 —— 保证「预览所见 = 导出所得」。
    /// </summary>
    private void DrawAnnotations(Graphics g, RectangleF physSel)
    {
        // 物理坐标 → 逻辑坐标的变换: 用 Graphics 变换统一处理,
        // 这样标注数据保持物理像素, 而绘制自动适配 DPI
        float sf = ScaleFactor;

        var state = g.Save();
        try
        {
            // 由于 _screen 是被缩放到 ClientSize 绘制的, 标注也需同比例缩小
            g.ScaleTransform(1f / sf, 1f / sf);

            AnnotationEngine.Draw(g, _annotations, _screen, physSel);

            if (_pending is { } pending && !pending.IsDegenerate())
                AnnotationEngine.Draw(g, [pending], _screen, physSel);
        }
        finally
        {
            g.Restore(state);
        }
    }

    /// <summary>选区右上角的尺寸标签 (显示物理像素尺寸)</summary>
    private void DrawSizeLabel(Graphics g, Rectangle sel, float sf)
    {
        string label = $"{(int)Math.Round(sel.Width * sf)} \u00d7 {(int)Math.Round(sel.Height * sf)}";
        using var font = new Font("Microsoft YaHei UI", 9f);
        Size ts = TextRenderer.MeasureText(label, font);
        float ly = sel.Top - ts.Height - 8;
        if (ly < 0) ly = sel.Top + 4;
        using var bg = new SolidBrush(Color.FromArgb(210, 33, 150, 243));
        g.FillRectangle(bg, sel.Left + 4, ly, ts.Width + 10, ts.Height + 6);
        TextRenderer.DrawText(g, label, font, new Point((int)(sel.Left + 9), (int)(ly + 3)), Color.White);
    }

    // ===== 鼠标 =====

    /// <summary>
    /// 右键取消 (有标注时先清空标注, 再按一次才退出);
    /// 左键: 文字工具且点中已有文字 → 进入编辑; 点中已有标注 → 选中; 否则开始框选/绘制。
    /// </summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Right)
        {
            // 有标注时右键先清空 (避免误退出丢失工作), 再按一次退出
            if (_annotations.Count > 0)
            {
                _history.Push(_annotations);
                _annotations.Clear();
                SyncToolbarState();
                Invalidate();
                return;
            }
            Close();
            return;
        }

        if (e.Button != MouseButtons.Left)
            return;

        // 已有选区 + 选中了工具 → 在选区内按下即绘制标注
        // 在选区外按下则重新框选 (否则选中工具后就再也无法调整选区, 只能靠按 0 退回)
        if (_hasSelection && _toolbar.Tool != AnnotationTool.None)
        {
            var phys = ToPhysical(e.Location);
            if (SelectionPhysical is not { } sel || sel.Contains(phys))
            {
                BeginAnnotate(e.Location);
                return;
            }

            // 落到选区外: 重新框选, 并顺手退出标注工具避免误绘
            _toolbar.SetTool(AnnotationTool.None);
        }

        // 否则走框选
        _anchor = e.Location;
        _dragging = true;
    }

    /// <summary>开始一次标注 (拖拽形状或画笔点列); 文字工具走点击即编辑</summary>
    private void BeginAnnotate(Point logical)
    {
        var phys = ToPhysical(logical);

        // 标注需落在选区内才有效
        if (SelectionPhysical is { } sel && !sel.Contains(phys))
            return;

        // 文字工具: 点击即创建编辑框, 不做拖拽
        if (_toolbar.Tool == AnnotationTool.Text)
        {
            CommitEditorIfOpen();
            _textAnchorPhysical = phys;
            ShowTextEditor(phys);
            return;
        }

        _pending = CreateShape(_toolbar.Tool, phys);
        if (_toolbar.Tool == AnnotationTool.Pen)
            _pending.Points.Add(phys);

        _dragging = true;
        Invalidate();
    }

    /// <summary>拖拽中: 框选更新选区; 标注更新形状终点/轨迹</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (!_dragging)
            return;

        if (_pending is { } pending)
        {
            var phys = ToPhysical(e.Location);
            if (pending.Tool == AnnotationTool.Pen)
            {
                // 采样去抖: 距离过近的点丢弃, 避免轨迹点爆炸
                var last = pending.Points[^1];
                float dx = phys.X - last.X, dy = phys.Y - last.Y;
                if (dx * dx + dy * dy >= 4f)
                    pending.Points.Add(phys);
            }
            else
            {
                pending.End = phys;
            }
            Invalidate();
            return;
        }

        var rect = RectFromPoints(_anchor, e.Location);
        // 与 OnMouseUp 同一 5px 阈值: 点击时鼠标常漂移 1~3px,
        // 微小矩形不能覆盖已有选区 (否则单击会把选区"抹掉", 之后双击确认时选区消失)
        if (rect.Width >= 5 && rect.Height >= 5 && _selection != rect)
        {
            _selection = rect;
            Invalidate();
        }
    }

    /// <summary>结束拖拽: 提交标注或更新选区</summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left)
            return;

        _dragging = false;

        // 提交标注
        if (_pending is { } pending)
        {
            var phys = ToPhysical(e.Location);
            if (pending.Tool == AnnotationTool.Pen)
            {
                if (pending.Points.Count > 0)
                    pending.Points.Add(phys);
            }
            else
            {
                pending.End = phys;
            }

            // 形状过小则不记录 (避免撤销栈里全是误点产生的垃圾)
            if (!pending.IsDegenerate())
            {
                _history.Push(_annotations);
                _annotations.Add(pending);
                SyncToolbarState();
            }

            _pending = null;
            Invalidate();
            return;
        }

        // 更新选区
        var rect = RectFromPoints(_anchor, e.Location);
        // 小于 5px 视为单击: 保留上一次选区 (兼容双击确认, 双击第二下不会清空选区)
        if (rect.Width >= 5 && rect.Height >= 5)
        {
            _selection = rect;
            OnSelectionChanged();
        }
        Invalidate();
    }

    /// <summary>双击确认选区</summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        // 正在编辑文字时不确认
        if (_editor is not null)
            return;
        Confirm();
    }

    /// <summary>选区变化后重新定位工具栏 (工具栏始终贴在选区下方)</summary>
    private void OnSelectionChanged()
    {
        _hasSelection = true;
        PositionToolbar();
        SyncToolbarState();
    }

    /// <summary>
    /// 把工具栏放到选区下方; 空间不足时翻转到选区上方, 再不行则贴在屏幕底部内侧。
    /// </summary>
    private void PositionToolbar()
    {
        if (_selection is not { } sel)
            return;

        _toolbar.Visible = true;

        int tbW = _toolbar.Width;
        int tbH = _toolbar.Height;
        const int margin = 8;

        // 水平: 与选区左对齐, 但不超出屏幕
        int x = sel.Left;
        if (x + tbW > ClientSize.Width - margin)
            x = Math.Max(margin, ClientSize.Width - tbW - margin);
        if (x < margin)
            x = margin;

        // 垂直: 优先选区下方
        int y = sel.Bottom + margin;
        if (y + tbH > ClientSize.Height - margin)
        {
            // 翻转到选区上方
            y = sel.Top - tbH - margin;
            if (y < margin)
            {
                // 都不够: 贴在屏幕底部内侧
                y = ClientSize.Height - tbH - margin;
                if (y < margin) y = margin;
            }
        }

        _toolbar.Location = new Point(x, y);
        _toolbar.BringToFront();
    }

    // ===== 键盘 =====

    /// <summary>
    /// 文字编辑期间整体让出键盘处理。
    ///
    /// 本窗体 KeyPreview=true, 默认会先于焦点子控件拿到按键; 若不拦截,
    /// 编辑框里的字符会被窗体的工具快捷键逻辑吞掉 (如输入 "1" 变成切换矩形工具)。
    /// 这里直接返回 false, 让按键沿正常路径送到编辑框。
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_editor is not null)
            return false;   // 交给聚焦的编辑框
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>同上: 对话框按键 (Tab/方向键等) 也让给编辑框</summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (_editor is not null)
            return false;
        return base.ProcessDialogKey(keyData);
    }

    /// <summary>
    /// Esc 取消 / 回车确认 / Ctrl+Z 撤销 / Ctrl+Y 重做 / 数字键切工具 / Delete 清空。
    ///
    /// 注意: 本窗体设了 KeyPreview=true, 会抢在焦点子控件之前处理按键。
    /// 文字编辑期间必须整体让行 (见 <see cref="ProcessCmdKey"/>), 否则输入的字符会被当作工具快捷键吞掉。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        // 编辑文字时把所有按键交给编辑框处理
        if (_editor is not null)
            return;

        switch (e.KeyCode)
        {
            case Keys.Escape:
                // 有标注时先退出工具, 再次 Esc 才关闭
                if (_toolbar.Tool != AnnotationTool.None)
                {
                    _toolbar.SetTool(AnnotationTool.None);
                    e.Handled = true;
                    return;
                }
                Close();
                return;

            case Keys.Enter:
                Confirm();
                return;

            case Keys.Z when e.Control:
                DoUndo();
                return;

            case Keys.Y when e.Control:
                DoRedo();
                return;

            case Keys.Delete:
                // 清空全部标注 (保留选区)
                if (_annotations.Count > 0)
                {
                    _history.Push(_annotations);
                    _annotations.Clear();
                    SyncToolbarState();
                    Invalidate();
                }
                return;
        }

        // 数字键 1..6 切换工具; 0 退回框选模式
        if (!e.Control && !e.Alt)
        {
            int digit = e.KeyCode switch
            {
                >= Keys.D1 and <= Keys.D6 => e.KeyCode - Keys.D0,
                >= Keys.NumPad1 and <= Keys.NumPad6 => e.KeyCode - Keys.NumPad0,
                _ => -1,
            };

            if (digit >= 0)
            {
                _toolbar.SetTool(AnnotationToolInfo.FromShortcutDigit(digit));
                e.Handled = true;
            }
            else if (e.KeyCode is Keys.D0 or Keys.NumPad0)
            {
                _toolbar.SetTool(AnnotationTool.None);
                e.Handled = true;
            }
        }
    }

    /// <summary>工具切换: 更新光标并取消未完成的绘制</summary>
    private void OnToolChanged()
    {
        _pending = null;

        Cursor = _toolbar.Tool switch
        {
            AnnotationTool.None => Cursors.Cross,
            AnnotationTool.Text => Cursors.IBeam,
            AnnotationTool.Pen => Cursors.Cross,
            _ => Cursors.Cross,
        };

        // 切走文字工具时提交未完成的编辑
        if (_toolbar.Tool != AnnotationTool.Text)
            CommitEditorIfOpen();

        Invalidate();
    }

    /// <summary>同步工具栏的撤销/重做可用态</summary>
    private void SyncToolbarState()
    {
        _toolbar.CanUndo = _history.CanUndo;
        _toolbar.CanRedo = _history.CanRedo;
    }

    /// <summary>撤销一步</summary>
    private void DoUndo()
    {
        if (_history.Undo(_annotations))
        {
            SyncToolbarState();
            Invalidate();
        }
    }

    /// <summary>重做一步</summary>
    private void DoRedo()
    {
        if (_history.Redo(_annotations))
        {
            SyncToolbarState();
            Invalidate();
        }
    }

    // ===== 文字编辑 =====

    /// <summary>
    /// 在指定物理位置弹出就地文字编辑框。
    /// 编辑框是 WinForms 控件, 坐标为逻辑像素, 因此需要换算。
    /// </summary>
    private void ShowTextEditor(PointF phys)
    {
        var logical = ToLogical(phys);

        var editor = new TextEditorOverlay(AnnotationEngine.DefaultFontSize)
        {
            TextColor = _toolbar.CurrentColor,
            Location = new Point((int)logical.X, (int)logical.Y),
        };

        editor.ContentChanged += () =>
        {
            // 编辑框长宽变化: 只需重绘父容器以刷新底衬
            Invalidate();
        };

        editor.Committed += text =>
        {
            var ann = new Annotation
            {
                Tool = AnnotationTool.Text,
                Color = _toolbar.CurrentColor,
                StrokeWidth = _toolbar.CurrentWidth,
                Start = _textAnchorPhysical,
                FontSize = AnnotationEngine.DefaultFontSize * ScaleFactor,
                Text = text,
            };

            if (!ann.IsDegenerate())
            {
                _history.Push(_annotations);
                _annotations.Add(ann);
                SyncToolbarState();
            }

            CloseEditor();
        };

        editor.Cancelled += CloseEditor;

        _editor = editor;
        Controls.Add(editor);
        editor.BringToFront();
        editor.ResizeToContent();
        editor.Focus();
        Invalidate();
    }

    /// <summary>提交并关闭编辑框 (切换工具/确认截图前调用)</summary>
    private void CommitEditorIfOpen()
    {
        if (_editor is not { } ed)
            return;

        // 先置空引用: Commit 回调里会调用 CloseEditor, 避免递归
        _editor = null;
        ed.CommitNow();   // 触发 Committed/Cancelled → 内部走 CloseEditor
        if (!ed.IsDisposed)
            CloseEditorCore(ed);
    }

    /// <summary>关闭并释放编辑框 (不再提交)</summary>
    private void CloseEditor()
    {
        if (_editor is not { } ed)
            return;
        _editor = null;   // 先清空引用, 避免 LostFocus 递归回调
        CloseEditorCore(ed);
    }

    /// <summary>移除并释放编辑框控件, 把焦点还给覆盖层</summary>
    private void CloseEditorCore(TextEditorOverlay ed)
    {
        Controls.Remove(ed);
        ed.Dispose();
        Focus();
        Invalidate();
    }

    // ===== 结果输出 =====

    /// <summary>
    /// 生成最终图片: 裁剪选区 + 叠加标注。
    /// 无论走 OCR 还是剪贴板, 都用这一份结果, 保证两者一致。
    /// </summary>
    private Bitmap RenderResult()
    {
        float sf = ScaleFactor;
        Rectangle physical;

        if (_selection is { } sel && sel.Width > 2 && sel.Height > 2)
        {
            int x = Math.Max(0, (int)(sel.X * sf));
            int y = Math.Max(0, (int)(sel.Y * sf));
            int w = Math.Max(1, (int)Math.Round(sel.Width * sf));
            int h = Math.Max(1, (int)Math.Round(sel.Height * sf));
            w = Math.Min(w, _screen.Width - x);
            h = Math.Min(h, _screen.Height - y);
            physical = new Rectangle(x, y, w, h);
        }
        else
        {
            // 未框选: 取屏幕中央 1/2 区域
            int w = Math.Max(200, _screen.Width / 2);
            int h = Math.Max(150, _screen.Height / 2);
            physical = new Rectangle((_screen.Width - w) / 2, (_screen.Height - h) / 2, w, h);
        }

        var crop = new Bitmap(physical.Width, physical.Height);
        using (var g = Graphics.FromImage(crop))
        {
            // 最近邻插值裁剪, 保持像素精确 (不做缩放失真)
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_screen, new Rectangle(0, 0, physical.Width, physical.Height), physical, GraphicsUnit.Pixel);

            // 标注: 平移到裁剪坐标系后绘制 (标注本身是物理像素, 无需缩放)
            if (_annotations.Count > 0)
            {
                var saved = g.Save();
                try
                {
                    g.TranslateTransform(-physical.X, -physical.Y);
                    AnnotationEngine.Draw(g, _annotations, _screen);
                }
                finally
                {
                    g.Restore(saved);
                }
            }
        }

        return crop;
    }

    /// <summary>
    /// 确认: 合成标注后输出。OCR 模式打开结果窗口, 否则图片写入剪贴板;
    /// 未框选时默认取屏幕中央 1/2 区域
    /// </summary>
    private void Confirm()
    {
        CommitEditorIfOpen();

        var crop = RenderResult();

        Close();

        if (_mode == SnipMode.Ocr)
        {
            var form = new OcrResultForm(crop);
            form.Show();
        }
        else
        {
            try
            {
                Clipboard.SetImage(crop);
            }
            catch
            {
                // 剪贴板被占用时忽略
            }
            crop.Dispose();
        }
    }

    /// <summary>保存为 PNG 文件 (从工具栏触发)</summary>
    private void SaveToFile()
    {
        CommitEditorIfOpen();

        var crop = RenderResult();
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "SnipasteOCR");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"Snip_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            crop.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch
        {
            // 保存失败 (无权限/磁盘满) 静默忽略, 不打断截图流程
        }
        finally
        {
            crop.Dispose();
        }

        Close();
    }

    /// <summary>两点归一化为左上角 + 宽高的矩形</summary>
    private static Rectangle RectFromPoints(Point a, Point b)
    {
        int x = Math.Min(a.X, b.X);
        int y = Math.Min(a.Y, b.Y);
        return new Rectangle(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    /// <summary>释放全屏位图与子控件</summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _editor?.Dispose();
        _editor = null;
        _toolbar.Dispose();
        _screen.Dispose();
        base.OnFormClosed(e);
    }
}
