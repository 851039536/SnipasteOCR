using System.ComponentModel;
using System.Drawing.Drawing2D;
using SnipasteOcr.Annotations;
using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 全屏截图覆盖层 (仿 Snipaste)。
/// - 识别模式 (F1): 拖拽框选 → 松开鼠标立即识别, 不出现标注工具栏;
/// - 图片模式 (F3): 拖拽框选 → 选区下方浮出标注工具栏 →
///   可绘制矩形/椭圆/箭头/画笔/马赛克/文字 → 双击/回车确认, Esc/右键取消。
///
/// 坐标系约定 (关键):
/// - <b>物理像素</b>: <see cref="_screen"/> 底图与所有 <see cref="Annotation"/> 几何数据。
/// - <b>逻辑像素</b>: WinForms 控件/鼠标事件坐标, 与物理像素相差 <see cref="ScaleFactor"/> 倍。
/// 两者在鼠标事件入口 (<see cref="ToPhysical"/>) 与绘制出口 (<see cref="ToLogical(PointF)"/>) 各转换一次。
/// </summary>
public sealed class SnipOverlayForm : Form
{
    private readonly Bitmap _screen;
    private readonly SnipMode _mode;
    private Point _anchor;
    private bool _dragging;
    private Rectangle? _selection; // 客户区 (逻辑) 坐标

    /// <summary>覆盖层客户区对应的物理屏幕矩形 (截图与屏幕坐标换算的基准)</summary>
    private readonly Rectangle _virtualScreen;

    /// <summary>上次绘制底部提示条时所在的屏幕 (客户区坐标), 跨屏移动时用于触发重绘</summary>
    private Rectangle _hintScreen;

    /// <summary>已完成的标注 (物理像素坐标)</summary>
    private readonly List<Annotation> _annotations = [];

    /// <summary>撤销/重做历史</summary>
    private readonly AnnotationHistory _history = new();

    /// <summary>正在拖拽绘制的标注 (未提交, 不参与命中测试与历史)</summary>
    private Annotation? _pending;

    /// <summary>
    /// 当前选中的标注索引 (-1 = 无)。
    ///
    /// <b>必须存索引, 不能存 Annotation 引用</b>: <see cref="AnnotationHistory"/> 在撤销/重做时
    /// 会把整表深拷贝并逐个 Clone (<c>Replace</c> 里是 <c>target.Add(a.Clone())</c>),
    /// 持有引用会在一次 Ctrl+Z 之后指向已被丢弃的副本 ——
    /// 表现为"选中框还画着, 拖动的却是另一个对象, 而屏幕上那个纹丝不动"。
    /// </summary>
    private int _selected = -1;

    /// <summary>正在拖动已选中的标注</summary>
    private bool _movingSelected;

    /// <summary>本次拖动是否已把旧状态压入撤销栈 (首次真正位移时才压)</summary>
    private bool _moveHistoryPushed;

    /// <summary>上一次指针的物理位置 (用于按增量平移)</summary>
    private PointF _moveLastPhysical;

    /// <summary>正在调整选区的手柄</summary>
    private SelectionHandle _resizeHandle = SelectionHandle.None;

    /// <summary>开始调整时的原选区 (拖拽期间始终以它为基准重算, 不累加)</summary>
    private Rectangle _resizeStart;

    /// <summary>选区手柄边长 (逻辑像素)</summary>
    private const int SelectionHandleSize = 9;

    /// <summary>手柄命中容差 (逻辑像素), 向外扩张让它更好点中</summary>
    private const int SelectionHandleGrow = 4;

    /// <summary>当前是否有过一次有效框选 (决定工具栏是否显示)</summary>
    private bool _hasSelection;

    private readonly AnnotationToolbar _toolbar = new();

    /// <summary>跟随光标的 15×15 像素放大镜; 只显示, 不参与导出合成</summary>
    private readonly SnipMagnifierControl _magnifier;

    /// <summary>取色成功/失败后的短暂提示 (会替换底部提示条)</summary>
    private readonly System.Windows.Forms.Timer _toastTimer = new() { Interval = 1400 };
    private string? _toast;

    private TextEditorOverlay? _editor;
    private PointF _textAnchorPhysical;

    /// <summary>正在输入的文字内容 (由编辑控件同步, 绘制由本窗体负责)</summary>
    private string? _editingText;

    /// <summary>正在输入的文字颜色 (开始编辑时锁定, 避免中途换色导致重影)</summary>
    private Color _editingColor = AnnotationPalette.Colors[0];

    /// <summary>
    /// 仅供交互自检替换识别结果窗口的启动动作, 避免自检触发真实 OCR 引擎。
    /// 生产路径恒为 null; 位图所有权交给回调。
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    internal Action<Bitmap, Rectangle>? OcrResultLauncherForTest { get; set; }

    /// <summary>构造覆盖层并在显示前抓取整屏截图; mode 决定确认后走 OCR 还是复制图片</summary>
    public SnipOverlayForm(SnipMode mode)
    {
        _mode = mode;

        // 先抓取屏幕 (必须在本窗体显示之前)
        Rectangle vs = SystemInformation.VirtualScreen;
        _virtualScreen = vs;
        _screen = ScreenGrab.Copy(vs);

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
        // 贴图只对图片模式有意义 (识别模式的产物是文字), 识别模式下隐藏该按钮
        _toolbar.ShowPin = mode == SnipMode.Image;
        _toolbar.ToolChanged += OnToolChanged;
        _toolbar.UndoRequested += DoUndo;
        _toolbar.RedoRequested += DoRedo;
        _toolbar.ConfirmRequested += Confirm;
        _toolbar.OcrRequested += ConfirmToOcr;
        _toolbar.CancelRequested += Close;
        _toolbar.SaveRequested += SaveToFile;
        _toolbar.PinRequested += PinToScreen;
        Controls.Add(_toolbar);

        // 放大镜放在工具栏之后加入: 未框选时它浮在底层控件之上; 框选完成后隐藏,
        // 不会盖住工具栏。鼠标命中由控件的 HTTRANSPARENT 交还给覆盖层。
        _magnifier = new SnipMagnifierControl(_screen, vs.Location);
        Controls.Add(_magnifier);

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toast = null;
            Invalidate();
        };

        SyncToolbarState();
    }

    /// <summary>
    /// 显示后强制抢到前台并取焦点。
    ///
    /// 这是本窗体的关键一步: <see cref="Form.Show"/> 只负责显示, 不会激活窗口。
    /// 若不做这一步, 覆盖层虽然在最上层可见、鼠标也能操作, 但 <b>键盘焦点仍留在原程序</b>,
    /// 表现为"能框选、能点工具栏, 但打字没反应"(文字全输进了别的窗口)。
    ///
    /// 后台进程直接调用 SetForegroundWindow 常被系统拒绝 (前台锁定), 因此这里用
    /// AttachThreadInput 挂到当前前台线程, 借它的输入队列完成激活。
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ForceActivate();
        UpdateMagnifierAtCursor();
    }

    /// <summary>
    /// 切到别的程序时收起样式弹层。
    /// 弹层是 TopMost + 不抢焦点, 覆盖层失活它不会自动消失 —— 否则会一直飘在别人窗口上。
    /// </summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _toolbar.CloseStylePopup();
    }

    /// <summary>把本窗口强行置为前台并取得键盘焦点</summary>
    private void ForceActivate()
    {
        if (!IsHandleCreated)
            return;

        Activate();
        BringToFront();

        IntPtr hwnd = Handle;
        IntPtr fg = Foreground.GetForegroundWindow();
        uint curThread = Kernel32.GetCurrentThreadId();
        uint fgThread = fg == IntPtr.Zero ? 0 : Foreground.GetWindowThreadProcessId(fg, IntPtr.Zero);

        bool attached = false;
        if (fgThread != 0 && fgThread != curThread)
            attached = Foreground.AttachThreadInput(curThread, fgThread, true);

        try
        {
            User32.SetForegroundWindow(hwnd);
            Foreground.SetFocus(hwnd);
        }
        finally
        {
            if (attached)
                Foreground.AttachThreadInput(curThread, fgThread, false);
        }

        // 再走一遍 WinForms 的激活流程, 让 ActiveControl 正确建立
        Activate();
        Select();
    }

    /// <summary>
    /// 未框选时的底部提示。必须写明松开鼠标后做什么 (识别 / 复制图片) 以及右键也能取消 ——
    /// 旧文案只写"确认"和 Esc, 用户不知道 F1 会识别文字, 也不知道右键可以先清标注再退出。
    /// 识别模式已改为松手即识别, 因此不再提"双击/回车"。
    /// </summary>
    private string HintText => _mode == SnipMode.Ocr
        ? "拖拽选择区域  ·  松开鼠标立即识别  ·  C 复制颜色  ·  右键/Esc 取消"
        : "拖拽选择区域  ·  双击 复制图片  ·  C 复制颜色  ·  右键/Esc 取消";

    /// <summary>物理像素/逻辑像素比例 (处理多显示器 DPI 缩放)</summary>
    private float ScaleFactor => _screen.Width / (float)ClientSize.Width;

    /// <summary>
    /// 屏幕物理矩形 → 覆盖层客户区逻辑矩形。
    /// 必须除以 <see cref="ScaleFactor"/>: 覆盖层客户区等于整块虚拟屏, 但客户区单位是逻辑像素。
    /// </summary>
    private Rectangle ScreenRectToClient(Rectangle screen)
    {
        float sf = ScaleFactor;
        return new Rectangle(
            (int)Math.Floor((screen.Left - _virtualScreen.Left) / sf),
            (int)Math.Floor((screen.Top - _virtualScreen.Top) / sf),
            (int)Math.Round(screen.Width / sf),
            (int)Math.Round(screen.Height / sf));
    }

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

    /// <summary>
    /// 当前选区对应的物理像素矩形 (无选区时返回 null)。
    /// 走 <see cref="SnipGeometry.ExportRect"/> 而非自算: 命中测试/预览裁剪与最终导出必须同一份取整结果。
    /// </summary>
    private RectangleF? SelectionPhysical
        => SnipGeometry.IsValidSelection(_selection)
            ? SnipGeometry.ExportRect(_selection, ScaleFactor, _screen.Size)
            : null;

    /// <summary>
    /// 放大镜跟随光标: 未框选时、以及重新框选的过程中显示;
    /// 框选完成 (F3 出现工具栏) 或正在画标注时隐藏 —— 此时继续显示会挡住选区内容。
    /// 取色器本身不受这个可见性限制, C 键始终可用。
    /// </summary>
    private void UpdateMagnifier(Point client)
    {
        // 拖动/调整期间也不显示: 放大镜会盖住正在移动的内容, 且这些操作不需要像素级参照
        bool visible = _editingText is null
            && !_movingSelected
            && _resizeHandle == SelectionHandle.None
            && (!_hasSelection || (_dragging && _pending is null));
        if (!visible)
        {
            _magnifier.Visible = false;
            return;
        }

        PointF physical = ToPhysical(client);
        var center = new Point((int)Math.Floor(physical.X), (int)Math.Floor(physical.Y));
        Rectangle screen = ScreenRectToClient(Screen.FromPoint(PointToScreen(client)).Bounds);
        _magnifier.UpdateAt(client, center, screen);
    }

    /// <summary>窗口刚显示、鼠标还没移动时也要先把放大镜放到当前光标旁</summary>
    private void UpdateMagnifierAtCursor()
    {
        if (!IsHandleCreated)
            return;

        Point client = PointToClient(Cursor.Position);
        if (ClientRectangle.Contains(client))
            UpdateMagnifier(client);
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

    /// <summary>
    /// 拖拽终点的 Ctrl 约束 (矩形/椭圆变正, 箭头吸附 8 方向)。
    /// 实时预览与松手提交必须都走这里, 否则会看到"松手瞬间变形"。
    /// </summary>
    private static PointF ConstrainDragEnd(AnnotationTool tool, PointF start, PointF end)
        => AnnotationDrag.ConstrainEnd(tool, start, end, (Control.ModifierKeys & Keys.Control) != 0);

    // ===== 绘制 =====

    /// <summary>绘制: 全屏底图 + 选区遮罩 + 标注 (已完成 + 拖拽中) + 工具栏</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.DrawImage(_screen, 0, 0, ClientSize.Width, ClientSize.Height);

        if (_selection is { } sel && SnipGeometry.IsValidSelection(sel))
        {
            float sf = ScaleFactor;
            // 逻辑选区 → 物理矩形: 预览用的源区域、标注裁剪和导出共用同一份取整结果
            Rectangle export = SnipGeometry.ExportRect(sel, sf, _screen.Size);
            RectangleF physSel = export;

            // 选区外半透明遮罩
            using (var dim = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillRectangle(dim, ClientRectangle);
            // 把选区原样画回 (去除遮罩)
            g.DrawImage(_screen, sel, physSel, GraphicsUnit.Pixel);

            // 标注: 只在选区内绘制 (与最终导出一致)
            DrawAnnotations(g, physSel);

            // 边框 + 尺寸标签
            using (var pen = new Pen(Color.White, 2f))
                g.DrawRectangle(pen, sel);
            using (var pen2 = new Pen(UiTheme.Accent, 2f))
                g.DrawRectangle(pen2, new Rectangle(sel.X + 1, sel.Y + 1, Math.Max(0, sel.Width - 2), Math.Max(0, sel.Height - 2)));

            // 实时尺寸: 与导出共用 ExportRect, 保证"标签写的 = 实际裁出来的";
            // 同时让开工具栏 —— 选区贴底时工具栏会翻转到选区上方压住徽标
            SnipSizeBadge.Draw(g, sel, export.Size, ClientSize,
                _toolbar.Visible ? _toolbar.Bounds : null);

            // 取色提示可以出现在已框选状态 (工具栏已显示), 这时底部提示条本来不画,
            // 仍要把短暂反馈画在最上层。
            if (_toast is { } toast)
                DrawStatusBar(g, toast);
        }
        else
        {
            DrawStatusBar(g, _toast ?? HintText);
        }
    }

    /// <summary>
    /// 底部状态条: 未框选时显示操作提示, 取色后显示短暂结果。
    /// 居中于光标所在屏, 不能按整个虚拟屏居中 —— 双屏下会横跨接缝, 文字正好被切成两半。
    /// 工具栏可见时若状态条与它重叠, 就移到工具栏上方, 避免被这个不透明子控件整块盖住。
    /// </summary>
    private void DrawStatusBar(Graphics g, string text)
    {
        using var font = UiFont.Create(14f);
        Size ts = TextRenderer.MeasureText(text, font);
        Rectangle screen = ScreenRectToClient(Screen.FromPoint(Cursor.Position).Bounds);
        _hintScreen = screen;

        int x = screen.Left + (screen.Width - ts.Width) / 2;
        int y = screen.Bottom - ts.Height - 18;
        var box = new Rectangle(x - 12, y - 4, ts.Width + 24, ts.Height + 8);

        if (_toolbar.Visible && box.IntersectsWith(_toolbar.Bounds))
        {
            int above = _toolbar.Bounds.Top - ts.Height - 16;
            if (above > screen.Top + 4)
            {
                y = above;
                box = new Rectangle(x - 12, y - 4, ts.Width + 24, ts.Height + 8);
            }
        }

        using var bg = new SolidBrush(UiTheme.StatusSurface);
        g.FillRectangle(bg, box);
        TextRenderer.DrawText(g, text, font, new Point(x, y), Color.White);
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

            // 正在输入的文字: 直接画在窗体上, 不用子控件 (见 DrawEditingText 的说明)
            DrawEditingText(g, physSel);

            // 选中装饰 (虚线框 + 8 手柄) 只画在覆盖层预览里。
            // 绝不能放进 AnnotationEngine.Draw —— 那个函数同时服务 RenderResult 导出,
            // 写进去就会把选中框烧录到用户复制的图里。拖拽中的临时形状不显示选中态。
            if (_selected >= 0 && _selected < _annotations.Count && _pending is null)
            {
                var decoration = g.Save();
                try
                {
                    g.SetClip(physSel);   // 与标注一致: 选区外不可见
                    AnnotationSelectionPainter.Draw(g, _annotations[_selected], sf);
                }
                finally
                {
                    g.Restore(decoration);
                }
            }
        }
        finally
        {
            g.Restore(state);
        }
    }

    /// <summary>
    /// 绘制"正在输入中"的文字。
    ///
    /// 为什么不把 <see cref="TextEditorOverlay"/> 当子控件显示:
    /// 本窗体在 OnPaint 里整屏绘制底图, 而子控件透明背景依赖父窗口先画好背景再让子控件叠加。
    /// 该协作在本窗体上不成立 —— 实测子控件被父窗口的整屏绘制覆盖, 屏幕上完全看不到输入内容
    /// (全屏逐像素对比: 打字前后 0 像素差异)。
    /// 因此这里改为: 编辑状态由 <see cref="_editingText"/> 保存, 文字由本窗体亲自绘制,
    /// 输入仍由 TextEditorOverlay 接收键盘消息 (它作为不可见控件仅承担输入法/键盘角色)。
    /// </summary>
    private void DrawEditingText(Graphics g, RectangleF physSel)
    {
        if (_editingText is null || _editingText.Length == 0)
            return;

        var ann = new Annotation
        {
            Tool = AnnotationTool.Text,
            Color = _editingColor,
            FontSize = AnnotationEngine.DefaultFontSize * ScaleFactor,
            Start = _textAnchorPhysical,
            Text = _editingText,
        };

        // 只画在当前选区内 (与最终导出一致)
        AnnotationEngine.Draw(g, [ann], _screen, physSel);

        // 光标: 与引擎共用同一套 GDI+ 度量, 直接用世界坐标定位 (Graphics 的缩放会一并作用)
        float worldFontSize = AnnotationEngine.DefaultFontSize * ScaleFactor;
        SizeF size = AnnotationEngine.MeasureText(_editingText, worldFontSize);
        float caretX = _textAnchorPhysical.X + size.Width;
        float caretTop = _textAnchorPhysical.Y;
        float caretBottom = caretTop + MathF.Max(size.Height, worldFontSize);

        using var pen = new Pen(Color.FromArgb(230, 255, 255, 255), MathF.Max(1f, ScaleFactor));
        g.DrawLine(pen, caretX, caretTop, caretX, caretBottom);
    }

    // ===== 鼠标 =====

    /// <summary>
    /// 右键取消 (有标注时先清空标注, 再按一次才退出);
    /// 左键: 文字工具且点中已有文字 → 进入编辑; 点中已有标注 → 选中; 否则开始框选/绘制。
    /// </summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // 点击工具栏/弹层之外的任何位置都先收起样式弹层:
        // 弹层是不抢焦点的顶层窗口, 点它自己不会经过这里, 所以这里等价于"点到了外面"。
        _toolbar.CloseStylePopup();

        if (HandleRightButtonDown(e))
            return;

        if (e.Button != MouseButtons.Left)
            return;

        var pointerPhysical = ToPhysical(e.Location);

        if (TryStartResize(e.Location)) return;
        if (TrySelectAnnotation(pointerPhysical)) return;
        if (TryBeginAnnotateInSelection(e.Location, pointerPhysical)) return;

        // 否则走框选
        _anchor = e.Location;
        _dragging = true;
    }

    /// <summary>右键: 有标注先清空 (避免误退出丢失工作), 再按一次退出</summary>
    private bool HandleRightButtonDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return false;

        if (_annotations.Count > 0)
        {
            _history.Push(_annotations);
            _annotations.Clear();
            _selected = -1;
            SyncToolbarState();
            Invalidate();
        }
        else
        {
            Close();
        }
        return true;
    }

    /// <summary>
    /// 选区手柄优先, 且只在"已框选 + 未选工具"时启用。
    /// 必须排在框选之前: 手柄贴在选区边上, 若先判断框选, 贴着边按下会被当成
    /// 重新框选, 手柄就永远拖不动。
    /// </summary>
    private bool TryStartResize(Point logical)
    {
        if (!_hasSelection || _toolbar.Tool != AnnotationTool.None || _selection is not { } handleSelection)
            return false;

        var handle = SelectionResize.HitHandle(handleSelection, logical, SelectionHandleSize, SelectionHandleGrow);
        if (handle == SelectionHandle.None)
            return false;

        _resizeHandle = handle;
        _resizeStart = handleSelection;
        _dragging = true;
        return true;
    }

    /// <summary>
    /// 未选工具时点中已有标注 → 选中并进入拖动。
    /// 点在选区内空白处只取消选中, 返回 false 继续走原有的框选/标注逻辑。
    /// </summary>
    private bool TrySelectAnnotation(PointF pointerPhysical)
    {
        if (_mode != SnipMode.Image || _toolbar.Tool != AnnotationTool.None || _annotations.Count == 0)
            return false;

        int hit = AnnotationHit.Topmost(_annotations, pointerPhysical);
        if (hit >= 0)
        {
            // 刚输入完的文字先提交, 否则用户一选中就把没提交的内容弄丢了
            CommitEditorIfOpen();

            _selected = hit;
            _movingSelected = true;
            _moveHistoryPushed = false;
            _moveLastPhysical = pointerPhysical;
            _dragging = true;
            Invalidate();
            return true;
        }

        if (_selected >= 0)
        {
            _selected = -1;
            Invalidate();
        }
        return false;
    }

    /// <summary>
    /// 已有选区 + 选中了工具 → 在选区内按下即绘制标注。
    /// 在选区外按下则重新框选 (否则选中工具后就再也无法调整选区, 只能靠按 0 退回)。
    /// </summary>
    private bool TryBeginAnnotateInSelection(Point logical, PointF pointerPhysical)
    {
        if (!_hasSelection || _toolbar.Tool == AnnotationTool.None)
            return false;

        if (SelectionPhysical is not { } sel || sel.Contains(pointerPhysical))
        {
            BeginAnnotate(logical);
            return true;
        }

        // 落到选区外: 重新框选, 并顺手退出标注工具避免误绘
        _toolbar.SetTool(AnnotationTool.None);
        return false;
    }

    /// <summary>开始一次标注 (拖拽形状或画笔点列); 文字工具走点击即编辑</summary>
    private void BeginAnnotate(Point logical)
    {
        // 标注只属于图片模式; 识别模式的产物是文字, 标注工具本来就不会出现
        if (_mode != SnipMode.Image)
            return;

        var phys = ToPhysical(logical);

        // 标注需落在选区内才有效
        if (SelectionPhysical is { } sel && !sel.Contains(phys))
            return;

        // 文字工具: 点击即开始输入, 不做拖拽
        if (_toolbar.Tool == AnnotationTool.Text)
        {
            CommitEditorIfOpen();
            ShowTextEditor(phys);
            return;
        }

        // 序号: 单击即落一个自动递增的徽标, 同样不做拖拽
        if (_toolbar.Tool == AnnotationTool.StepNumber)
        {
            CommitEditorIfOpen();
            PlaceStepNumber(phys);
            return;
        }

        _pending = CreateShape(_toolbar.Tool, phys);
        if (_toolbar.Tool is AnnotationTool.Pen or AnnotationTool.Marker)
            _pending.Points.Add(phys);

        _dragging = true;
        Invalidate();
    }

    /// <summary>在指定位置落一个序号; 编号取现有最大序号 + 1, 撤销后复用被撤销的编号</summary>
    private void PlaceStepNumber(PointF phys)
    {
        var ann = new Annotation
        {
            Tool = AnnotationTool.StepNumber,
            Color = _toolbar.CurrentColor,
            StrokeWidth = _toolbar.CurrentWidth,
            Start = phys,
            End = phys,
            Number = AnnotationEngine.NextStepNumber(_annotations),
            FontSize = AnnotationEngine.DefaultFontSize * ScaleFactor,
        };

        _history.Push(_annotations);
        _annotations.Add(ann);
        _selected = -1;          // 新落的标注不自动选中, 保持"画完接着画"的手感
        SyncToolbarState();
        Invalidate();
    }

    /// <summary>拖拽中: 框选更新选区; 标注更新形状终点/轨迹</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 放大镜是独立子控件, 更新它只重绘自己那一小块, 不会触发整屏底图重绘
        UpdateMagnifier(e.Location);
        UpdateHintScreen();

        if (!_dragging)
        {
            UpdateHoverCursor(e.Location);
            return;
        }

        if (HandleSelectionResizeMove(e)) return;
        if (HandleMoveSelectedMove(e)) return;
        if (HandlePendingShapeMove(e)) return;
        HandleSelectionDragMove(e);
    }

    /// <summary>提示条跟随光标所在屏: 只有跨屏时才重绘一次 (全屏底图重绘很贵)</summary>
    private void UpdateHintScreen()
    {
        if (_dragging || SnipGeometry.IsValidSelection(_selection))
            return;

        Rectangle screen = ScreenRectToClient(Screen.FromPoint(Cursor.Position).Bounds);
        if (screen != _hintScreen)
        {
            _hintScreen = screen;
            Invalidate();
        }
    }

    /// <summary>调整选区: 每次都从拖拽起点重算, 避免"夹紧后作为新基准"造成的累积漂移</summary>
    private bool HandleSelectionResizeMove(MouseEventArgs e)
    {
        if (_resizeHandle == SelectionHandle.None)
            return false;

        var resized = Rectangle.Intersect(
            SelectionResize.Apply(_resizeStart, _resizeHandle, e.Location, SnipGeometry.MinDragSide),
            ClientRectangle);

        if (SnipGeometry.IsValidSelection(resized) && _selection != resized)
        {
            _selection = resized;
            PositionToolbar();   // 选区变了, 工具栏要跟着走
            Invalidate();
        }
        return true;
    }

    /// <summary>拖动已选中的标注: 按增量平移, 首次真正位移时才压历史</summary>
    private bool HandleMoveSelectedMove(MouseEventArgs e)
    {
        if (!_movingSelected || _selected < 0 || _selected >= _annotations.Count)
            return false;

        var phys = ToPhysical(e.Location);
        float dx = phys.X - _moveLastPhysical.X;
        float dy = phys.Y - _moveLastPhysical.Y;
        if (dx != 0 || dy != 0)
        {
            // 每帧 Push 会让一次 Ctrl+Z 只回退一个像素, 撤销栈瞬间被刷爆
            if (!_moveHistoryPushed)
            {
                _history.Push(_annotations);
                _moveHistoryPushed = true;
            }

            _annotations[_selected].Offset(dx, dy);
            _moveLastPhysical = phys;
            Invalidate();
        }
        return true;
    }

    /// <summary>拖拽中的形状: 画笔/马克笔追加轨迹点, 其余更新终点</summary>
    private bool HandlePendingShapeMove(MouseEventArgs e)
    {
        if (_pending is not { } pending)
            return false;

        var phys = ToPhysical(e.Location);
        if (pending.Tool is AnnotationTool.Pen or AnnotationTool.Marker)
        {
            // 采样去抖: 距离过近的点丢弃, 避免轨迹点爆炸
            var last = pending.Points[^1];
            float dx = phys.X - last.X, dy = phys.Y - last.Y;
            if (dx * dx + dy * dy >= 4f)
                pending.Points.Add(phys);
        }
        else
        {
            pending.End = ConstrainDragEnd(pending.Tool, pending.Start, phys);
        }
        Invalidate();
        return true;
    }

    /// <summary>框选矩形更新 (未命中任何手柄/标注时)</summary>
    private void HandleSelectionDragMove(MouseEventArgs e)
    {
        var rect = RectFromPoints(_anchor, e.Location);
        // 与 OnMouseUp 共用 SnipGeometry.MinDragSide: 点击时鼠标常漂移 1~3px,
        // 微小矩形不能覆盖已有选区 (否则单击会把选区"抹掉", 之后双击确认时选区消失)
        if (rect.Width >= SnipGeometry.MinDragSide && rect.Height >= SnipGeometry.MinDragSide && _selection != rect)
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

        if (FinishResize()) return;
        if (FinishMoveSelected()) return;
        if (CommitPendingShape(e)) return;
        FinishSelection(e);
    }

    /// <summary>结束选区调整: 走一次 OnSelectionChanged, 让工具栏重新贴边</summary>
    private bool FinishResize()
    {
        if (_resizeHandle == SelectionHandle.None)
            return false;

        _resizeHandle = SelectionHandle.None;
        OnSelectionChanged();
        Invalidate();
        return true;
    }

    /// <summary>结束拖动标注。只是单击选中时不会有位移, 也就不需要提交任何东西</summary>
    private bool FinishMoveSelected()
    {
        if (!_movingSelected)
            return false;

        _movingSelected = false;
        _moveHistoryPushed = false;
        SyncToolbarState();
        Invalidate();
        return true;
    }

    /// <summary>提交拖拽中的标注; 形状过小则丢弃</summary>
    private bool CommitPendingShape(MouseEventArgs e)
    {
        if (_pending is not { } pending)
            return false;

        var phys = ToPhysical(e.Location);
        if (pending.Tool is AnnotationTool.Pen or AnnotationTool.Marker)
        {
            if (pending.Points.Count > 0)
                pending.Points.Add(phys);
        }
        else
        {
            pending.End = ConstrainDragEnd(pending.Tool, pending.Start, phys);
        }

        // 形状过小则不记录 (避免撤销栈里全是误点产生的垃圾)
        if (!pending.IsDegenerate())
        {
            _history.Push(_annotations);
            _annotations.Add(pending);
            _selected = -1;
            SyncToolbarState();
        }

        _pending = null;
        Invalidate();
        return true;
    }

    /// <summary>结束框选: 小于阈值视为单击 (保留上一次选区, 兼容双击确认)</summary>
    private void FinishSelection(MouseEventArgs e)
    {
        var rect = RectFromPoints(_anchor, e.Location);
        if (rect.Width >= SnipGeometry.MinDragSide && rect.Height >= SnipGeometry.MinDragSide)
        {
            _selection = rect;

            // 识别模式: 松手即识别, 不进入「选区 + 标注工具栏」阶段。
            // Confirm 与双击/工具栏确认走同一条路径 (先 RenderResult 再 Close), 位图安全。
            if (_mode == SnipMode.Ocr)
            {
                Confirm();
                return;   // 窗体已关闭, 不要再 Invalidate
            }

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
        // 识别模式松手即识别, 没有「选区持久化 + 标注」阶段; 这里守住不变量,
        // 避免任何调用方 (含测试注入) 让识别模式进入标注状态。
        if (_mode != SnipMode.Image)
            return;

        _hasSelection = true;
        _magnifier.Visible = false;   // 框选完成, 放大镜让位给工具栏与选区内容
        PositionToolbar();
        SyncToolbarState();
    }

    /// <summary>
    /// 把工具栏放到选区下方; 空间不足时翻转到选区上方, 再不行则贴在屏幕底部内侧。
    /// 夹紧范围是<b>选区所在的那块屏</b>, 不是整个虚拟屏 —— 后者会让贴接缝的选区把工具栏推到另一块屏。
    /// </summary>
    private void PositionToolbar()
    {
        // 双保险: 识别模式永远不显示标注工具栏 (工具栏是截图/标注专用)
        if (_mode != SnipMode.Image)
            return;

        if (_selection is not { } sel)
            return;

        _toolbar.Visible = true;

        var screens = Screen.AllScreens.Select(s => ScreenRectToClient(s.Bounds)).ToArray();
        Rectangle screen = SnipGeometry.PickScreen(sel, screens);
        _toolbar.Location = SnipGeometry.ToolbarLocation(sel, _toolbar.Size, screen, 8);
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

        if (HandleStylePopupKey(e))
            return;

        switch (e.KeyCode)
        {
            case Keys.Escape:
                HandleEscapeKey(e);
                return;

            case Keys.C when !e.Control && !e.Alt:
                // Shift+C = 把当前像素贴成色块; C = 只复制色值
                if (e.Shift)
                    PinColorAtCursor();
                else
                    CopyColorAtCursor();
                e.Handled = true;
                return;

            case Keys.Enter:
                // Ctrl+回车 = 直接识别 (跳过剪贴板); 普通回车仍是确认
                if (e.Control)
                    ConfirmToOcr();
                else
                    Confirm();
                return;

            case Keys.Z when e.Control:
                DoUndo();
                return;

            case Keys.Y when e.Control:
                DoRedo();
                return;

            case Keys.Delete:
                HandleDeleteKey();
                return;

            // 层级调整走快捷键而非工具栏按钮: 工具栏宽度已卡在 650px 上限,
            // 再加按钮会撑破小选区上的布局 (ToolbarCheck 有硬断言)。
            case Keys.OemCloseBrackets when e.Control:
                MoveSelectedLayer(forward: true, toEnd: e.Shift);
                e.Handled = true;
                return;

            case Keys.OemOpenBrackets when e.Control:
                MoveSelectedLayer(forward: false, toEnd: e.Shift);
                e.Handled = true;
                return;
        }

        HandleToolDigit(e);
    }

    /// <summary>样式弹层打开时, 第一次 Esc 只收起面板; 其它按键也先收起面板再继续</summary>
    private bool HandleStylePopupKey(KeyEventArgs e)
    {
        if (!_toolbar.IsStylePopupOpen)
            return false;

        _toolbar.CloseStylePopup();
        if (e.KeyCode != Keys.Escape)
            return false;

        e.Handled = true;
        return true;
    }

    /// <summary>Esc 阶梯: 取消选中 → 退出工具 → 关闭窗口</summary>
    private void HandleEscapeKey(KeyEventArgs e)
    {
        // 选中某条标注时, 第一次 Esc 只取消选中 (不退出窗口)
        if (_selected >= 0)
        {
            _selected = -1;
            Invalidate();
            e.Handled = true;
            return;
        }
        // 有标注时先退出工具, 再次 Esc 才关闭
        if (_toolbar.Tool != AnnotationTool.None)
        {
            _toolbar.SetTool(AnnotationTool.None);
            e.Handled = true;
            return;
        }
        Close();
    }

    /// <summary>Delete: 有选中只删这一条, 无选中清空全部 (保留选区)</summary>
    private void HandleDeleteKey()
    {
        if (_selected >= 0 && _selected < _annotations.Count)
        {
            _history.Push(_annotations);
            _annotations.RemoveAt(_selected);
        }
        else if (_annotations.Count > 0)
        {
            _history.Push(_annotations);
            _annotations.Clear();
        }
        else
        {
            return;
        }

        _selected = -1;
        SyncToolbarState();
        Invalidate();
    }

    /// <summary>数字键 1..8 切换工具, 0 退回框选; 识别模式不劫持数字键</summary>
    private void HandleToolDigit(KeyEventArgs e)
    {
        if (_mode != SnipMode.Image || e.Control || e.Alt)
            return;

        int digit = e.KeyCode switch
        {
            >= Keys.D1 and <= Keys.D8 => e.KeyCode - Keys.D0,
            >= Keys.NumPad1 and <= Keys.NumPad8 => e.KeyCode - Keys.NumPad0,
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

    /// <summary>
    /// 取色: 读取覆盖层启动时抓取的 <see cref="_screen"/>, 不能重新 CopyFromScreen ——
    /// 那时屏幕上已经有覆盖层/遮罩/放大镜, 会把界面自己的颜色取走。
    /// </summary>
    private void CopyColorAtCursor()
    {
        if (!TrySampleCursorColor(out Color color))
            return;

        string hex = SnipMagnifier.FormatHex(color);
        if (ClipboardText.TrySet(hex))
            ShowToast($"已复制 {hex}  {SnipMagnifier.FormatRgb(color)}");
        else
            // 剪贴板被其它进程占用: 不打断截图流程, 但必须让用户知道没复制成功
            ShowToast("颜色复制失败");
    }

    /// <summary>
    /// Shift+C: 把光标处的颜色贴成色块。
    ///
    /// 取样源与取色共用 <see cref="_screen"/> (覆盖层启动时抓的底图), 不重新抓屏 ——
    /// 那时候屏幕上已经有覆盖层与放大镜, 会把界面自己的颜色取走。
    /// </summary>
    private void PinColorAtCursor()
    {
        if (!TrySampleCursorColor(out Color color))
            return;

        // 贴在光标右下方一点, 避免正好盖住用户正在看的那个像素
        new PinnedColorForm(color, new Point(Cursor.Position.X + 18, Cursor.Position.Y + 18)).Show();

        ShowToast($"已贴色块 {SnipMagnifier.FormatHex(color)}");
    }

    /// <summary>
    /// 取光标所在像素 (覆盖层底图, 不重新抓屏)。句柄/边界/工具栏遮挡三判断共用一份,
    /// 取色复制与贴色块曾各抄一遍前导, 漏掉工具栏遮挡就会取到界面自己的颜色。
    /// </summary>
    private bool TrySampleCursorColor(out Color color)
    {
        color = default;
        if (!IsHandleCreated)
            return false;

        Point client = PointToClient(Cursor.Position);
        if (!ClientRectangle.Contains(client))
            return false;
        if (_toolbar.Visible && _toolbar.Bounds.Contains(client))
            return false;   // 工具栏是不透明子控件, 它盖住的位置不是用户看到的屏幕内容

        PointF physical = ToPhysical(client);
        var point = new Point((int)Math.Floor(physical.X), (int)Math.Floor(physical.Y));
        color = SnipMagnifier.Sample(_screen, point);
        return true;
    }

    /// <summary>短暂显示底部状态, 由 <see cref="_toastTimer"/> 恢复为常规提示</summary>
    private void ShowToast(string text)
    {
        _toast = text;
        _toastTimer.Stop();
        _toastTimer.Start();
        Invalidate();
    }

    /// <summary>工具切换: 更新光标并取消未完成的绘制</summary>
    private void OnToolChanged()
    {
        _pending = null;
        _selected = -1;          // 选中态只属于"未选工具"的状态, 换工具即作废
        _magnifier.Visible = false;

        Cursor = _toolbar.Tool switch
        {
            AnnotationTool.None => Cursors.Cross,
            AnnotationTool.Text => Cursors.IBeam,
            AnnotationTool.Pen => Cursors.Cross,
            AnnotationTool.Marker => Cursors.Cross,
            AnnotationTool.StepNumber => Cursors.Hand,
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
            // 撤销会整表克隆替换, 旧索引不再对应同一个对象, 选中态必须失效
            _selected = -1;
            SyncToolbarState();
            Invalidate();
        }
    }

    /// <summary>重做一步</summary>
    private void DoRedo()
    {
        if (_history.Redo(_annotations))
        {
            _selected = -1;
            SyncToolbarState();
            Invalidate();
        }
    }

    /// <summary>
    /// 调整选中标注的层级。forward = 往上; toEnd = 直达顶/底。
    /// 层级只是列表顺序, 绘制自然跟随 (<see cref="AnnotationEngine.Draw"/> 按传入顺序画)。
    /// 已在端点时不压历史, 避免 Ctrl+Z 出现"什么都变了"的空步骤。
    /// </summary>
    private void MoveSelectedLayer(bool forward, bool toEnd)
    {
        if (_selected < 0 || _selected >= _annotations.Count)
            return;

        bool atBoundary = forward
            ? _selected == _annotations.Count - 1
            : _selected == 0;
        if (atBoundary)
            return;

        _history.Push(_annotations);

        _selected = (forward, toEnd) switch
        {
            (true, true) => AnnotationOrder.BringToFront(_annotations, _selected),
            (true, false) => AnnotationOrder.BringForward(_annotations, _selected),
            (false, true) => AnnotationOrder.SendToBack(_annotations, _selected),
            _ => AnnotationOrder.SendBackward(_annotations, _selected),
        };

        Invalidate();
    }

    /// <summary>
    /// 悬停光标: 按指针下方的内容提示"这里能拖"。
    /// 只在图片模式且未选工具时生效 —— 选了工具之后光标由 <see cref="OnToolChanged"/> 决定,
    /// 两者若同时改会闪烁。
    /// </summary>
    private void UpdateHoverCursor(Point logical)
    {
        if (_mode != SnipMode.Image || _toolbar.Tool != AnnotationTool.None)
            return;

        Cursor target = Cursors.Cross;

        if (_hasSelection && _selection is { } selection)
        {
            target = SelectionResize.HitHandle(selection, logical, SelectionHandleSize, SelectionHandleGrow) switch
            {
                SelectionHandle.TopLeft or SelectionHandle.BottomRight => Cursors.SizeNWSE,
                SelectionHandle.TopRight or SelectionHandle.BottomLeft => Cursors.SizeNESW,
                SelectionHandle.Top or SelectionHandle.Bottom => Cursors.SizeNS,
                SelectionHandle.Left or SelectionHandle.Right => Cursors.SizeWE,
                _ => Cursors.Cross,
            };
        }

        // 选区手柄优先于标注命中: 手柄贴着选区边, 若先判标注就永远拖不到边
        if (ReferenceEquals(target, Cursors.Cross) && _annotations.Count > 0 &&
            AnnotationHit.Topmost(_annotations, ToPhysical(logical)) >= 0)
        {
            target = Cursors.SizeAll;
        }

        if (!ReferenceEquals(Cursor, target))
            Cursor = target;
    }

    // ===== 文字编辑 =====

    /// <summary>
    /// 在指定物理位置开始输入文字标注。
    ///
    /// 设计要点: 文字<b>显示</b>由本窗体的 OnPaint 负责 (<see cref="DrawEditingText"/>),
    /// <see cref="TextEditorOverlay"/> 只作为"接收键盘输入"的载体存在 —— 因为本窗体
    /// 整屏自绘背景, 透明子控件会被覆盖而完全看不见。
    /// </summary>
    private void ShowTextEditor(PointF phys)
    {
        _textAnchorPhysical = phys;
        _editingText = string.Empty;
        _editingColor = _toolbar.CurrentColor;

        var editor = new TextEditorOverlay(AnnotationEngine.DefaultFontSize)
        {
            TextColor = _toolbar.CurrentColor,
        };
        HookEditorEvents(editor);

        _editor = editor;

        // 不加入 Controls 树: 一旦作为可见子控件存在, 它就会被本窗体的整屏绘制覆盖。
        // 用 AddOwnedForm/隐藏窗口的方式无法可靠收键盘, 因此这里把它放到屏幕外、尺寸最小,
        // 只要它能拿到焦点即可 (焦点与可见性无关)。
        editor.Location = new Point(-32000, -32000);
        editor.Size = new Size(120, 30);
        Controls.Add(editor);

        // 取焦点链: 覆盖层已是前台窗口 (见 ForceActivate), 这里把焦点交给编辑控件
        editor.Select();
        if (!editor.Focused)
            editor.Focus();

        Invalidate();
    }

    /// <summary>编辑框的内容/提交/取消事件: 宿主只负责同步预览文本与落一条标注</summary>
    private void HookEditorEvents(TextEditorOverlay editor)
    {
        // 内容变化: 同步到 _editingText 并重绘窗体 (真正显示文字的地方)
        editor.ContentChanged += () =>
        {
            _editingText = editor.Value;
            Invalidate();
        };

        // 编辑框自身的文本变化也可能不经过 ContentChanged (例如 Value 直接赋值), 这里再兜一层
        editor.TextChanged += (_, _) =>
        {
            _editingText = editor.Value;
            Invalidate();
        };

        editor.Committed += text =>
        {
            var ann = new Annotation
            {
                Tool = AnnotationTool.Text,
                Color = _editingColor,
                StrokeWidth = _toolbar.CurrentWidth,
                Start = _textAnchorPhysical,
                FontSize = AnnotationEngine.DefaultFontSize * ScaleFactor,
                Text = text,
            };

            _editingText = null;   // 先结束预览态, 避免与正式标注重影

            if (!ann.IsDegenerate())
            {
                _history.Push(_annotations);
                _annotations.Add(ann);
                _selected = -1;
                SyncToolbarState();
            }

            CloseEditor();
        };

        editor.Cancelled += CloseEditor;
    }

    /// <summary>提交并关闭编辑框 (切换工具/确认截图前调用)</summary>
    private void CommitEditorIfOpen()
    {
        if (_editor is not { } ed)
            return;

        // 先置空引用: Commit 回调里会调用 CloseEditor, 避免递归
        _editor = null;
        ed.CommitNow();   // 触发 Committed/Cancelled
        if (!ed.IsDisposed)
            CloseEditorCore(ed);
    }

    /// <summary>关闭并释放编辑框 (不再提交)</summary>
    private void CloseEditor()
    {
        if (_editor is not { } ed)
        {
            _editingText = null;
            Invalidate();
            return;
        }
        _editor = null;   // 先清空引用, 避免 LostFocus 递归回调
        CloseEditorCore(ed);
    }

    /// <summary>移除并释放编辑框控件, 把焦点还给覆盖层</summary>
    private void CloseEditorCore(TextEditorOverlay ed)
    {
        _editingText = null;
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

        // 唯一来源: 实时尺寸徽标报的就是这个矩形, 避免"标签与导出各算一份"后漂移
        Rectangle physical = SnipGeometry.ExportRect(_selection, sf, _screen.Size);

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
    /// 确认: 合成标注后输出。OCR 模式打开结果窗口, 否则图片写入剪贴板。
    /// 未框选时默认取屏幕中央 1/2 区域。
    /// </summary>
    private void Confirm() => ConfirmCore(toOcr: _mode == SnipMode.Ocr);

    /// <summary>
    /// F3 的「识别」: 标注后的图直接送 OCR, 不经过剪贴板。
    /// 入口是 Ctrl+回车 / Ctrl+点击「确认」—— 工具栏宽度已到紧凑上限 (650px),
    /// 再加一个文字按钮会把小选区上的工具栏撑得过宽, 因此复用确认按钮而不新增控件。
    /// </summary>
    private void ConfirmToOcr()
    {
        if (_mode != SnipMode.Image)
        {
            Confirm();
            return;
        }
        ConfirmCore(toOcr: true);
    }

    /// <summary>确认动作的唯一实现; toOcr 决定产物交给结果窗口还是剪贴板</summary>
    private void ConfirmCore(bool toOcr)
    {
        CommitEditorIfOpen();

        var crop = RenderResult();

        // 结果窗口要开在截图所在屏: 物理选区相对底图左上角, 加上虚拟屏原点即屏幕坐标
        Rectangle physical = SnipGeometry.ExportRect(_selection, ScaleFactor, _screen.Size);
        Rectangle anchor = new(
            _virtualScreen.Left + physical.X, _virtualScreen.Top + physical.Y,
            physical.Width, physical.Height);

        // Close() 会释放本窗体, 测试缝必须先取出
        Action<Bitmap, Rectangle>? launcher = OcrResultLauncherForTest;

        if (toOcr)
        {
            // 记住上次区域, 供托盘「重复上次识别」直接截同一块屏;
            // 只记录真正送 OCR 的路径, 图片复制/保存不应改写它。
            SnipCoordinator.RememberOcrRegion(anchor);
            Close();
            if (launcher is { } launch)
                launch(crop, anchor);
            else
                new OcrResultForm(crop, anchor).Show();
            return;
        }

        // 图片模式: 剪贴板写入成功才关窗。
        // 踩过的坑: 原先先 Close() 再 SetImage, 失败被静默吞掉 —— 用户标注完的图直接消失,
        // 没有任何提示 (剪贴板常被 Office/远程桌面占用)。
        try
        {
            Clipboard.SetImage(crop);
        }
        catch
        {
            crop.Dispose();
            ShowToast("复制失败（剪贴板被占用）· 可点「保存」另存或按 Esc 放弃");
            return;
        }

        crop.Dispose();
        Close();
    }

    /// <summary>
    /// 贴图 (钉住): 把选区连同标注钉成屏幕上的置顶小窗, 由用户自行摆放与关闭。
    ///
    /// 与 <see cref="Confirm"/> 的区别: Confirm 是把图交出去 (剪贴板 / 识别),
    /// 本方法则是把图留在屏幕上继续看 —— 典型用途是对照着截图在别处操作。
    ///
    /// 仅在图片模式 (F3) 提供; OCR 模式的产物是文字, 钉一张原图没有意义, 且按钮本就只加在 F3 流程里。
    /// </summary>
    private void PinToScreen()
    {
        if (_mode != SnipMode.Image)
            return;

        CommitEditorIfOpen();

        var crop = RenderResult();

        // 贴图窗口直接铺在选区原来的位置上 —— 用户框选时就选好了位置,
        // 钉住后原地出现最符合直觉 (Snipaste 也是这个行为), 之后可自由拖动。
        Rectangle sel = _selection ?? Rectangle.Empty;
        Point screenLocation = new(sel.Left + Left, sel.Top + Top);

        Close();

        var pinned = new PinnedImageForm(crop, screenLocation);
        pinned.Show();
    }

    /// <summary>保存为 PNG 文件 (从工具栏触发); 失败时保留覆盖层并明确提示, 不能装作已保存</summary>
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
            AtomicFile.SaveImage(crop, file, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex)
        {
            // 原先静默吞掉异常后照样 Close(): 用户以为已保存, 实际文件不存在。
            // 这里保留框选现场, 用户可重试或改用「确认」复制。
            MessageBox.Show(this,
                "保存失败：" + ex.Message + "\n\n截图仍保留在框选界面上，可重试或改用「确认」复制。",
                "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
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
        _toastTimer.Stop();
        _toastTimer.Dispose();
        _editor?.Dispose();
        _editor = null;
        _magnifier.Dispose();   // 它读取 _screen, 必须先于底图释放
        _toolbar.Dispose();
        _screen.Dispose();
        base.OnFormClosed(e);
    }
}
