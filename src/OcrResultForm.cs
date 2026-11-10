using System.Drawing.Drawing2D;
using System.Numerics;
using SnipasteOcr.Annotations;
using SnipasteOcr.Native;
using Sdcb.SimdPaddleOCR;

namespace SnipasteOcr;

/// <summary>
/// OCR 结果窗口: 原图 + 识别框叠加显示, 单击选词 / Shift 多选,
/// Ctrl+C 复制选中文字, 支持缩放与全选/全量复制/保存
/// </summary>
public sealed class OcrResultForm : Form
{
    private readonly Bitmap _src;

    /// <summary>截图选区所在的屏幕矩形 (屏幕坐标); null 表示无截图来源 (如剪贴板识别)</summary>
    private readonly Rectangle? _anchorScreen;

    private float _scale = 1f;
    private float _minScale;

    /// <summary>
    /// 窗口所在屏幕的 DPI 因子 (物理像素 / 逻辑像素)。
    ///
    /// 不能用 <c>Graphics.FromHwnd(IntPtr.Zero)</c> 取桌面 DC —— 那是**主屏** DPI,
    /// Per-Monitor V2 下窗口移到副屏后不会跟着变, 图片尺寸、命中与字符高亮会整体偏移。
    /// 这里由 <see cref="OnHandleCreated"/> 与 <see cref="OnDpiChanged"/> 维护。
    /// </summary>
    private float _dpi = 1f;
    private bool _dpiInitialized;
    private PaddleOcrLine[] _lines = [];
    private readonly HashSet<int> _selected = [];
    private int _hover = -1;
    private CancellationTokenSource? _cts;
    private bool _ocrDone;
    private bool _draggingMove; // 按住空白处拖动移动窗口
    private Point _dragStartScreen; // 按下时的鼠标屏幕坐标
    private Point _dragStartLoc;    // 按下时的窗口位置

    // ===== 高倍缩放平移 =====
    /// <summary>相对"图片居中"位置的平移偏移 (物理像素), 由 <see cref="OcrPan"/> 夹紧</summary>
    private PointF _panOffset;
    private bool _panning;
    private Point _panAnchorClient;
    private PointF _panAnchorOffset;
    private bool _spaceHeld;

    // ===== 字符级拖选 =====
    private bool _charDrag;                              // 正在文本块内拖选
    private bool _charDragMoved;                         // 拖选是否已移动
    private int _dragLine = -1;                          // 拖选所在行
    private int _dragAnchorChar;                         // 拖选起点字符索引
    private Point _dragAnchorClient;                     // 拖选起点 (客户端坐标)
    private (int Line, int Start, int End)? _partial;    // 行内选中范围 [Start, End)
    private float[][] _lineCumW = [];                    // 每行字符累计宽度权重 (CTC 缺失时的回退)
    private OcrGlyphLayout?[] _lineGlyphs = [];          // 每行 CTC 真实字符框布局 (不支持时为 null)
    private int[] _lineRank = [];                        // _lines 索引 -> 阅读顺序名次

    // 绘制缓存: 每行检测框的原始四角 (图像坐标) 只随 OCR 结果重建;
    // 每帧按当前缩放/平移映射到客户区, 避免每行重新 LINQ + 新数组。
    private PointF[][] _lineCorners = [];
    private PaddleOcrLine[]? _lineCornersSource;
    private readonly PointF[] _quadScratch = new PointF[4];

    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStripButton _zoomOut = new();
    private readonly ToolStripButton _zoomIn = new();
    private readonly ToolStripButton _zoomReset = new();
    private readonly ToolStripLabel _zoomLabel = new();
    private readonly ToolStripButton _selectAll = new();
    private readonly ToolStripButton _copyAll = new();
    private readonly ToolStripButton _copyCsv = new();
    private readonly ToolStripButton _copyCsvComma = new();
    private readonly ToolStripButton _copyPlain = new();
    private readonly ToolStripButton _copyMarkdown = new();
    private readonly ToolStripButton _copySelection = new();
    private readonly ToolStripButton _saveImage = new();
    private readonly ToolStripButton _translateButton = new();
    private readonly ToolStripButton _closeButton = new();

    /// <summary>当前打开的译文窗口; 再次点「翻译」时先关旧窗, 避免多个窗口显示不同选区</summary>
    private TranslateForm? _translateForm;

    // ===== 底部译文条 (单击翻译) =====
    /// <summary>单击翻译的进行中请求; 连点别的块时取消上一次, 避免旧译文后到覆盖新译文</summary>
    private CancellationTokenSource? _clickTranslateCts;
    private string _barTitle = "";
    private string _barText = "";
    private bool _barError;
    /// <summary>译文条当前对应的检测行: 同一块连点不重复发请求 (失败后允许重点重试)</summary>
    private int _barLine = -1;
    private bool _barPending;
    private bool _barCopyHover;

    /// <summary>译文条上的命中区域; 关闭/复制必须先于"整条拖动"判定</summary>
    private enum TranslationBarHit { None, Body, Copy, Close }

    /// <summary>
    /// 工具栏字号 (逻辑像素, 非 pt)。
    ///
    /// 注意 GraphicsUnit.Pixel 的实际观感: <c>UiFont.Create(10f)</c> 得到 Height=13px,
    /// 而 ToolStrip 未设字体时的系统默认是 9pt/16px —— 也就是说不缩小数值反而会**变大**。
    /// 这里取 10f (13px) 比原默认略小一档, 让按钮更紧凑、文字仍清晰可读。
    /// 改这个值要连带看工具栏首选宽度 (它直接决定窗口最小宽度)。
    /// </summary>
    private const float UiFontSize = 10f;

    /// <summary>「导出」下拉: 把 5 种导出格式收进一个按钮, 避免工具栏被 5 个按钮撑满</summary>
    private readonly ToolStripDropDownButton _copyMenu = new();

    private string _statusText = string.Empty; // 右下角状态提示

    /// <summary>
    /// 初始化窗口: 无标题栏 (边缘可缩放), 初始按图片 1:1 显示 (超出工作区则收缩),
    /// 显示后自动开始后台 OCR。
    /// <paramref name="anchorScreen"/> 是截图选区所在的屏幕矩形: 窗口必须开在那块屏,
    /// 传 null (剪贴板识别) 时取光标所在屏, 不能用 CenterScreen (永远主屏)。
    /// </summary>
    public OcrResultForm(Bitmap source, Rectangle? anchorScreen = null)
    {
        _src = source;
        _anchorScreen = anchorScreen;
        Text = "SnipasteOCR";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None; // 无标题栏, 边缘细边框可缩放
        BackColor = UiTheme.Canvas;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        BuildToolStrip();
        Controls.Add(_toolStrip);
        // 最小宽度 = 工具栏完整宽度, 小截图时按钮不会被挤掉/折叠进溢出菜单
        MinimumSize = new Size(Math.Max(320, _toolStrip.GetPreferredSize(new Size(0, _toolStrip.Height)).Width + 16), 140);
        // 初始大小: 图片原始尺寸 (1:1 显示) + 工具栏
        Size = SizeFromImage();
        CenterOnTargetScreen();

        _minScale = ComputeFitScale();
        _scale = _minScale;
        UpdateZoomLabel();
        // 无边框窗口没有标题栏可抓, 移动方式不写出来用户很难发现;
        // 只在识别期间显示 (识别完成后回落到常驻操作提示, 不长期占用状态栏)
        SetStatus("正在识别\u2026 · 拖动空白处移动窗口");

        // 单例事件链会随窗口打开次数增长: 必须用具名方法订阅, 并在 OnFormClosed 退订,
        // 否则每次打开结果窗口都会留下一个捕获 this 的委托, 窗体无法回收。
        OcrService.Instance.StatusChanged += OnServiceStatus;

        Shown += (_, _) => StartOcr();
    }

    /// <summary>
    /// 后台线程安全地切回 UI 线程: 句柄已销毁/正在销毁时静默丢弃。
    /// 单独检查 IsHandleCreated 不够 —— 检查与 BeginInvoke 之间句柄仍可能被销毁。
    /// </summary>
    private bool TryBeginInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
            return false;
        try
        {
            BeginInvoke(action);
            return true;
        }
        catch (ObjectDisposedException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    /// <summary>把引擎状态切回 UI 线程; 句柄已销毁时静默丢弃 (后台线程仍在识别的收尾阶段)</summary>
    private void OnServiceStatus(string text) => TryBeginInvoke(() => SetStatus(text));

    /// <summary>构建工具栏: 缩放 / 全选 / 复制 / 保存 / 关闭</summary>
    private void BuildToolStrip()
    {
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.RenderMode = ToolStripRenderMode.Professional;
        _toolStrip.Renderer = new DarkToolStripRenderer();
        _toolStrip.BackColor = UiTheme.Surface;
        _toolStrip.ForeColor = Color.FromArgb(226, 230, 236);

        // 字体必须显式指定, 不能用 ToolStrip 的系统默认字体:
        // 默认字体随系统语言/DPI 漂移, 且直接 new Font(中文字体名) 在字体缺失时
        // 会静默回退到 Microsoft Sans Serif (中文变方块且不报错)。
        // 统一走 UiFont (项目约定), 并略微缩小以给按钮留出更多横向空间。
        _toolStrip.Font = UiFont.Create(UiFontSize);

        // 统一间距: 默认 Padding 在不同 DPI 下观感松散, 收窄后按钮更紧凑整齐
        _toolStrip.Padding = new Padding(6, 2, 6, 2);
        _toolStrip.AutoSize = true;

        // 关掉溢出菜单: 工具栏已按"最小宽度 = 工具栏完整宽度"设计 (见构造函数),
        // 不存在需要折叠的情况; 而溢出按钮即使 Visible=false 也仍会在右端画一条
        // 1px 竖直分隔线 —— 实测就是右上角那条突兀的"白色竖线"(x=最右列, 贯通全高)。
        // CanOverflow=false 同时消除了这条线与"按钮被折叠"的可能。
        _toolStrip.CanOverflow = false;

        _zoomOut.Text = "-";
        _zoomOut.ToolTipText = "缩小（-）";
        _zoomOut.Click += (_, _) => Zoom(1f / 1.25f);

        _zoomLabel.Text = "100%";
        _zoomLabel.AutoSize = false;
        _zoomLabel.Width = 52;
        _zoomLabel.TextAlign = ContentAlignment.MiddleCenter;

        _zoomIn.Text = "+";
        _zoomIn.ToolTipText = "放大（+）";
        _zoomIn.Click += (_, _) => Zoom(1.25f);

        _zoomReset.Text = "适应";
        _zoomReset.ToolTipText = "适应窗口（0）";
        _zoomReset.Click += (_, _) => { _scale = _minScale; FitWindowToImage(); UpdateZoomLabel(); Invalidate(); };

        _selectAll.Text = "全选";
        _selectAll.ToolTipText = "选择全部识别结果（Ctrl+A）";
        _selectAll.Click += (_, _) => SelectAll();

        _copySelection.Text = "复制选中";
        _copySelection.ToolTipText = "复制选中文本（Ctrl+C）";
        _copySelection.Click += (_, _) => CopyText(GetSelectedText());

        _copyAll.Text = "复制全部";
        _copyAll.ToolTipText = "复制全部识别文本";
        _copyAll.Click += (_, _) => CopyText(GetAllText());

        // 5 种导出格式收进下拉: 原先一字排开 5 个按钮, 工具栏宽达 639px,
        // 小截图时窗口最小宽度被撑到 655px, 观感很挤。收进下拉后只占 1 个按钮位。
        // 下拉按钮不能也叫「复制」: 首项是「复制全部」, 两个"复制"并排容易误读
        _copyMenu.Text = "导出";
        _copyMenu.ToolTipText = "选择导出格式";
        _copyMenu.DropDownDirection = ToolStripDropDownDirection.BelowRight;

        _copyCsv.Text = "表格（制表符）";
        _copyCsv.ToolTipText = "按坐标重建为表格，可直接粘贴到 Excel";
        _copyCsv.Click += (_, _) => CopyText(OcrText.ToCsv(CurrentLines(), '\t'));

        _copyCsvComma.Text = "CSV（逗号）";
        _copyCsvComma.ToolTipText = "导出为逗号分隔 CSV 文本";
        _copyCsvComma.Click += (_, _) => CopyText(OcrText.ToCsv(CurrentLines(), ','));

        // 去硬换行的连贯文本: 屏幕上换行多是排版, 粘进文档/聊天时不想要这些断行
        _copyPlain.Text = "段落（去换行）";
        _copyPlain.ToolTipText = "合并为连贯段落（去掉排版造成的硬换行）";
        _copyPlain.Click += (_, _) => CopyText(OcrText.ToPlainText(CurrentLines()));

        _copyMarkdown.Text = "Markdown";
        _copyMarkdown.ToolTipText = "导出为 Markdown（段落间空行分隔）";
        _copyMarkdown.Click += (_, _) => CopyText(OcrText.ToMarkdown(CurrentLines()));

        _copyMenu.DropDownItems.AddRange(
        [
            _copyAll,
            new ToolStripSeparator(),
            _copyCsv,
            _copyCsvComma,
            _copyPlain,
            _copyMarkdown,
        ]);

        _saveImage.Text = "保存图片";
        _saveImage.ToolTipText = "保存截图为 PNG";
        _saveImage.Click += (_, _) => SaveImage();

        // 翻译是显式联网动作: 按钮常驻但识别完成前禁用; 未配置 Key 时点击会打开设置
        _translateButton.Text = "翻译";
        _translateButton.ToolTipText = "翻译选中文本；未选中时翻译全部（需联网）";
        _translateButton.Enabled = false;
        _translateButton.Click += (_, _) => TranslateSelectionOrAll();

        _closeButton.Text = "关闭";
        _closeButton.ToolTipText = "关闭当前窗口";
        _closeButton.Click += (_, _) => this.Close();

        // 「关闭」推到工具栏最右端: ToolStrip 默认是左对齐流式布局,
        // 把该项(及其前面的分隔线)标记为 Alignment.Right 后, 布局引擎会把它贴到右边缘,
        // 窗口变宽时它跟着走 —— 比手工塞一个空白弹簧可靠 (弹簧宽度需自己算, 易漂移)。
        _closeButton.Alignment = ToolStripItemAlignment.Right;

        var closeSep = new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right };

        _toolStrip.Items.Add(_zoomOut);
        _toolStrip.Items.Add(_zoomLabel);
        _toolStrip.Items.Add(_zoomIn);
        _toolStrip.Items.Add(_zoomReset);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_selectAll);
        _toolStrip.Items.Add(_copySelection);
        _toolStrip.Items.Add(_copyMenu);
        _toolStrip.Items.Add(_translateButton);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_saveImage);
        // 右侧组: 分隔线在最左, 因此按"右对齐项从右往左依次排布"的顺序加入,
        // 先加关闭、再加分隔线, 视觉上才是 [保存图片] | [关闭]
        _toolStrip.Items.Add(_closeButton);
        _toolStrip.Items.Add(closeSep);

    }

    /// <summary>计算当前窗口内能容纳的最大缩放比例 (不超过 100%)</summary>
    private float ComputeFitScale()
    {
        float dpi = DpiFactor();
        float availW = Math.Max(100f, (ClientSize.Width - 4) * dpi);
        float availH = Math.Max(100f, (ClientSize.Height - _toolStrip.Height - 4) * dpi);
        return Math.Min(1f, Math.Min(availW / _src.Width, availH / _src.Height));
    }

    // _src 为物理像素, 客户区/鼠标坐标为逻辑像素, 1:1 显示需按 DPI 换算
    private float DpiFactor() => _dpi;

    // 初始窗口尺寸: 图片 1:1 (逻辑像素) + 工具栏; 超出屏幕工作区时按比例收缩
    private Size SizeFromImage()
    {
        float dpi = DpiFactor();
        // 物理像素图片 1:1 显示, 客户区逻辑尺寸 = 物理尺寸 / dpi
        int w = (int)Math.Round(_src.Width / dpi) + 8;
        int h = (int)Math.Round(_src.Height / dpi) + _toolStrip.Height + 8;
        // 必须用目标屏的工作区: SystemInformation.WorkingArea 永远是主屏,
        // 在副屏截图时会把窗口尺寸按主屏收缩、位置也拉回主屏。
        Rectangle wa = TargetWorkingArea;
        float s = Math.Min(1f, Math.Min(wa.Width * 0.95f / w, wa.Height * 0.95f / h));
        if (s < 1f)
        {
            w = (int)Math.Round(w * s);
            h = (int)Math.Round(h * s);
        }
        return new Size(Math.Max(w, MinimumSize.Width), Math.Max(h, MinimumSize.Height));
    }

    /// <summary>结果窗口应落在哪块屏的工作区: 截图所在屏, 无截图来源 (剪贴板) 时取光标所在屏</summary>
    private Rectangle TargetWorkingArea =>
        _anchorScreen is { } anchor
            ? Screen.FromRectangle(anchor).WorkingArea
            : Screen.FromPoint(Cursor.Position).WorkingArea;

    /// <summary>在目标屏工作区内居中; 窗口比工作区还大时贴住左上角, 不让窗口整块跑到屏幕外</summary>
    private void CenterOnTargetScreen()
    {
        Rectangle wa = TargetWorkingArea;
        int x = wa.Left + (wa.Width - Width) / 2;
        int y = wa.Top + (wa.Height - Height) / 2;
        Location = new Point(
            Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - Width)),
            Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - Height)));
    }

    private void Zoom(float factor) => Zoom(factor, null);

    /// <summary>
    /// 缩放并保持 <paramref name="anchorClient"/> 处的原图内容不动。
    /// 不锚定的话, 高倍放大时视口总是回到图片中心, 用户刚对准的小字会跑掉。
    /// </summary>
    private void Zoom(float factor, Point? anchorClient)
    {
        float oldScale = _scale;
        float dpi = DpiFactor();
        var oldRect = ImageRect();
        // 默认锚点 = 图片区中心 (工具栏按钮 / 键盘)
        Point anchor = anchorClient ?? new Point(
            ClientSize.Width / 2,
            _toolStrip.Height + Math.Max(0, ClientSize.Height - _toolStrip.Height) / 2);
        float imageX = (anchor.X * dpi - oldRect.X) / oldScale;
        float imageY = (anchor.Y * dpi - oldRect.Y) / oldScale;

        _scale = Math.Clamp(_scale * factor, _minScale, 8f);
        FitWindowToImage();

        // 反推平移量, 让同一个原图点在新的视口中仍落在锚点位置
        // (FitWindowToImage 调整窗口尺寸时也会触发 OnClientSizeChanged 的夹紧; 这里再按新比例算一次)
        float newDpi = DpiFactor();
        float newW = _src.Width * _scale;
        float newH = _src.Height * _scale;
        float baseX = (ClientSize.Width * newDpi - newW) / 2f;
        float viewH = Math.Max(0, (ClientSize.Height - _toolStrip.Height) * newDpi);
        float baseY = _toolStrip.Height * newDpi + (viewH - newH) / 2f;
        _panOffset = new PointF(
            anchor.X * newDpi - baseX - imageX * _scale,
            anchor.Y * newDpi - baseY - imageY * _scale);
        ClampPan();
        UpdateZoomLabel();
        Invalidate();
    }

    // 缩放时窗口跟着图片大小联动; 图片比窗口大 (被钳到 fit) 时窗口不变
    // 缩放时窗口跟着图片显示大小联动: 放大窗口变大, 缩小窗口变小
    /// <summary>按当前缩放比例联动窗口尺寸; 放不下时保持窗口大小 (图片居中裁切)</summary>
    private void FitWindowToImage()
    {
        float dpi = DpiFactor();
        int newW = Math.Max(MinimumSize.Width, (int)Math.Round(_src.Width * _scale / dpi) + 8);
        int newH = Math.Max(MinimumSize.Height, (int)Math.Round(_src.Height * _scale / dpi) + _toolStrip.Height + 8);
        if (newW == ClientSize.Width && newH == ClientSize.Height)
            return; // 尺寸没变
        // 必须用窗口所在屏幕的工作区: SystemInformation.WorkingArea 永远是主屏,
        // 副屏 (负坐标) 的窗口会被下面的钳制逻辑拉回主屏
        var wa = Screen.FromControl(this).WorkingArea;
        if (newW > wa.Width || newH > wa.Height)
            return; // 超出屏幕工作区, 窗口不再变大 (图片按窗口居中裁切)
        // 保持左上角不动, 限制在工作区内
        int x = Math.Min(Location.X, wa.Right - newW);
        int y = Math.Min(Location.Y, wa.Bottom - newH);
        if (x < wa.Left) x = wa.Left;
        if (y < wa.Top) y = wa.Top;
        Location = new Point(x, y);
        Size = new Size(newW, newH);
    }

    // 窗口被手动缩放/移动后: 1:1 能放下就自动放大到 1:1 ("联动"的另一半)
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        float fit = ComputeFitScale();
        _minScale = fit;
        if (_scale < fit)
        {
            _scale = fit;
            UpdateZoomLabel();
        }
        ClampPan();
        Invalidate();
    }

    /// <summary>
    /// 句柄创建后才能拿到窗口真实 DPI。构造阶段按 96 DPI 估算初始尺寸,
    /// 这里按当前屏幕 DPI 校正一次, 否则 150% 缩放下窗口初始大小会差 1.5 倍。
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _dpi = DeviceDpi / 96f;
        if (!_dpiInitialized)
        {
            _dpiInitialized = true;
            Size = SizeFromImage();
            // 尺寸按真实 DPI 校正后要重新居中, 否则 150% 下会从目标屏中心偏出去
            CenterOnTargetScreen();
            _minScale = ComputeFitScale();
            _scale = _minScale;
        }
        else
        {
            _minScale = ComputeFitScale();
            if (_scale < _minScale) _scale = _minScale;
        }
        ClampPan();
        UpdateZoomLabel();
        Invalidate();
    }

    /// <summary>窗口跨屏移动时系统会触发; 更新 DPI 后重新按新比例换算所有坐标与缩放下限</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _dpi = e.DeviceDpiNew / 96f;
        _minScale = ComputeFitScale();
        if (_scale < _minScale) _scale = _minScale;
        ClampPan();
        UpdateZoomLabel();
        Invalidate();
    }

    /// <summary>
    /// 测试钩子: 非 null 时单击翻译走该回调, 不联网、不弹知情同意/Key 设置框。
    /// 生产路径必须保持 null。
    /// </summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<string, CancellationToken, Task<TranslateResult>>? TranslateForTest { get; set; }

    /// <summary>
    /// 仅供交互自检注入 DPI 因子, 用于在没有混合 DPI 屏幕的机器上断言坐标换算。
    /// 生产路径只由句柄创建/跨屏事件维护 <see cref="_dpi"/>。
    /// </summary>
    internal void SetDpiFactorForTest(float dpi)
    {
        _dpi = dpi;
        _minScale = ComputeFitScale();
        _scale = _minScale;
        ClampPan();
        UpdateZoomLabel();
        Invalidate();
    }

    /// <summary>更新工具栏上的缩放比例标签</summary>
    private void UpdateZoomLabel()
    {
        _zoomLabel.Text = $"{(int)Math.Round(_scale * 100)}%";
    }

    // ===== 坐标换算 (图片以缩放后尺寸居中绘制) =====

    /// <summary>图片在客户区内的居中矩形 (物理像素, 绘制直接可用)</summary>
    private RectangleF ImageRect()
    {
        // 返回物理像素坐标 (绘制时直接可用)
        float dpi = DpiFactor();
        float w = _src.Width * _scale;
        float h = _src.Height * _scale;
        float x = (ClientSize.Width * dpi - w) / 2f + _panOffset.X;
        float y = _toolStrip.Height * dpi + (ClientSize.Height * dpi - _toolStrip.Height * dpi - h) / 2f + _panOffset.Y;
        return new RectangleF(x, y, w, h);
    }

    /// <summary>图片可用视口 (物理像素): 客户区减去顶部工具栏</summary>
    private SizeF ImageViewport()
    {
        float dpi = DpiFactor();
        return new SizeF(
            Math.Max(0f, ClientSize.Width * dpi),
            Math.Max(0f, (ClientSize.Height - _toolStrip.Height) * dpi));
    }

    /// <summary>夹紧平移偏移: 图片边缘不能进入视口内侧, 小图保持居中</summary>
    private void ClampPan() => _panOffset = OcrPan.Clamp(
        _panOffset, new SizeF(_src.Width * _scale, _src.Height * _scale), ImageViewport());

    // 4 点多边形 (原图坐标 -> 客户区坐标); 保留给命中/字符高亮与离线交互断言使用
    private PointF[] BoxToClientPoints(PaddleOcrDetectionBox box)
    {
        var ir = ImageRect();
        float dpi = DpiFactor();
        return [.. OcrBox.Corners(box).Select(p =>
            new PointF((p.X * _scale + ir.X) / dpi, (p.Y * _scale + ir.Y) / dpi))];
    }

    /// <summary>每行检测框的原始四角只在 _lines 换新数组时重建 (OCR 完成一次)</summary>
    private void EnsureLineCorners()
    {
        if (ReferenceEquals(_lineCornersSource, _lines))
            return;

        var corners = new PointF[_lines.Length][];
        for (int i = 0; i < corners.Length; i++)
            corners[i] = OcrBox.Corners(_lines[i].Box);
        _lineCorners = corners;
        _lineCornersSource = _lines;
    }

    /// <summary>
    /// 把图像坐标的四角映射到客户区。复用同一个 4 点缓冲 —— DrawPolygon 同步消费,
    /// 不会保留引用, 因此每帧不再为每行分配一个新数组。
    /// </summary>
    private PointF[] MapCorners(PointF[] imageCorners, RectangleF ir, float dpi)
    {
        for (int i = 0; i < 4; i++)
        {
            _quadScratch[i] = new PointF(
                (imageCorners[i].X * _scale + ir.X) / dpi,
                (imageCorners[i].Y * _scale + ir.Y) / dpi);
        }
        return _quadScratch;
    }

    // ===== 绘制 =====

    /// <summary>渲染原图 + 识别框叠加: 选中=蓝色填充, 部分选中=加深高亮, 悬停=虚线, 其余=淡蓝边框</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        // ImageRect 返回物理像素坐标, 绘制需换算回逻辑像素 (DPI 感知)
        float dpi = DpiFactor();
        var ir = ImageRect();
        var cr = new RectangleF(ir.X / dpi, ir.Y / dpi, ir.Width / dpi, ir.Height / dpi);
        using (var br = new SolidBrush(UiTheme.Surface))
            g.FillRectangle(br, cr);
        using (var pen = new Pen(Color.FromArgb(255, 70, 74, 82)))
            g.DrawRectangle(pen, cr.X - 0.5f, cr.Y - 0.5f, cr.Width + 1f, cr.Height + 1f);

        if (_scale >= 0.95f)
        {
            g.DrawImage(_src, cr);
        }
        else
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_src, cr);
        }

        if (_ocrDone)
        {
            using var selBrush = new SolidBrush(Color.FromArgb(90, UiTheme.Accent));
            using var selPen = new Pen(UiTheme.Accent, 2f);
            using var hoverPen = new Pen(Color.FromArgb(255, 255, 193, 7), 1.6f) { DashStyle = DashStyle.Dash };
            using var idlePen = new Pen(Color.FromArgb(160, UiTheme.Accent), 1.2f);
            // 低置信度用橙色虚线, 与悬停的黄色虚线区分; 只提示, 不过滤
            using var lowPen = new Pen(Color.FromArgb(255, 152, 0), 1.8f) { DashStyle = DashStyle.Dash };

            EnsureLineCorners();
            for (int i = 0; i < _lines.Length; i++)
            {
                if (string.IsNullOrEmpty(_lines[i].Text))
                    continue;

                var pts = MapCorners(_lineCorners[i], ir, dpi);
                bool selected = _selected.Contains(i);
                bool hover = i == _hover;

                if (selected)
                {
                    g.FillPolygon(selBrush, pts);
                    g.DrawPolygon(selPen, pts);
                    // 部分选中: 再高亮行内的字符范围
                    if (_partial is { } p && p.Line == i)
                    {
                        using var strongBrush = new SolidBrush(Color.FromArgb(160, UiTheme.Accent));
                        using var strongPen = new Pen(UiTheme.Accent, 1.4f);
                        var quads = CharRangeQuads(i, p.Start, p.End);
                        if (quads is not null)
                        {
                            // CTC 真实字符框: 逐字填充, 边界与真实字形一致
                            using var path = new GraphicsPath { FillMode = FillMode.Winding };
                            foreach (var quad in quads)
                                path.AddPolygon(quad);
                            g.FillPath(strongBrush, path);
                            g.DrawPath(strongPen, path);
                        }
                        else
                        {
                            // 回退: 按字符宽度权重在检测框内插值出一条高亮带
                            var cp = CharRangePoints(i, p.Start, p.End);
                            g.FillPolygon(strongBrush, cp);
                            g.DrawPolygon(strongPen, cp);
                        }
                    }
                }
                else if (hover)
                    g.DrawPolygon(hoverPen, pts);
                else if (OcrConfidence.IsLow(_lines[i].RecognitionScore))
                    g.DrawPolygon(lowPen, pts);
                else
                    g.DrawPolygon(idlePen, pts);
            }
        }
        DrawTranslationBar(g);
        DrawStatus(g);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* OnPaint 内已填充背景 */ }

    private const int StatusPadX = 10;
    private const int StatusPadY = 4;
    private const int StatusMargin = 8;

    /// <summary>
    /// 状态栏面板与文字框 (唯一来源)。文字比可用宽度长时必须限制面板宽度,
    /// 再用 <see cref="TextFormatFlags.EndEllipsis"/> 收敛 —— 旧实现按文字实际宽度铺面板,
    /// 超长的错误信息会整条伸出窗口被硬裁, 而且绘制用的是 Point 重载, 省略号根本不生效。
    /// <paramref name="bottomInset"/> 是底部被译文条占掉的高度: 状态栏必须抬到译文条上方,
    /// 否则后绘制的状态栏会直接压在译文上。
    /// </summary>
    private static (Rectangle Box, Rectangle Text) ComputeStatusLayout(
        Size textSize, Size client, int bottomInset)
    {
        int maxWidth = Math.Max(1, client.Width - StatusMargin - 4);
        int boxW = Math.Min(textSize.Width + StatusPadX * 2, maxWidth);
        int boxH = textSize.Height + StatusPadY * 2;
        var box = new Rectangle(
            Math.Max(4, client.Width - boxW - StatusMargin),
            Math.Max(4, client.Height - Math.Max(0, bottomInset) - boxH - StatusMargin),
            boxW, boxH);
        var text = new Rectangle(
            box.X + StatusPadX, box.Y + StatusPadY,
            Math.Max(1, box.Width - StatusPadX * 2),
            Math.Max(1, box.Height - StatusPadY * 2));
        return (box, text);
    }

    // 右下角状态提示 (半透明底 + 浅色文字)
    private void DrawStatus(Graphics g)
    {
        if (string.IsNullOrEmpty(_statusText))
            return;
        using var font = UiFont.Create(12f);
        Size ts = TextRenderer.MeasureText(_statusText, font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        // 译文条可见时, 状态栏让到它上方 (二者都在底部右侧, 否则会叠在一起)
        int bottomInset = _barText.Length == 0
            ? 0
            : Math.Max(0, ClientSize.Height - MeasureTranslationBar().Box.Top + 4);
        var (box, textRect) = ComputeStatusLayout(ts, ClientSize, bottomInset);

        using var bg = new SolidBrush(UiTheme.StatusSurface);
        g.FillRectangle(bg, box);
        TextRenderer.DrawText(g, _statusText, font, textRect, Color.FromArgb(226, 230, 236),
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    private void SetStatus(string text)
    {
        if (_statusText == text)
            return;
        _statusText = text;
        Invalidate(); // 右下角状态需重绘
    }

    // ===== 底部译文条 (单击翻译) =====

    private static readonly Color BarTextColor = Color.FromArgb(226, 230, 236);
    private static readonly Color BarErrorColor = Color.FromArgb(255, 196, 120);

    /// <summary>
    /// 量出译文条布局。绘制、命中、状态栏避让都从这里取, 不各自算一份。
    /// 译文条只在可见时参与测量, 且文本很短, 直接量比维护缓存的失效逻辑更可靠。
    /// </summary>
    private TranslationBarLayout.Layout MeasureTranslationBar()
    {
        using var titleFont = UiFont.Create(10f);
        using var bodyFont = UiFont.Create(12f);
        Size titleSize = TextRenderer.MeasureText(_barTitle, titleFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        Size copySize = TextRenderer.MeasureText("复制", titleFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        // 正文必须按"扣掉内边距后的实际宽度"换行: 按 int.MaxValue 量会得到一行, 高度就永远不增高
        Size bodySize = TextRenderer.MeasureText(_barText, bodyFont,
            new Size(TranslationBarLayout.ContentWidth(ClientSize.Width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

        return TranslationBarLayout.Compute(
            ClientSize, titleSize, copySize, bodySize,
            TranslationBarLayout.MaxHeight(ClientSize.Height, _toolStrip.Height));
    }

    private void DrawTranslationBar(Graphics g)
    {
        if (_barText.Length == 0)
            return;

        var lay = MeasureTranslationBar();
        using (var bg = new SolidBrush(UiTheme.FloatingBar))
            g.FillRectangle(bg, lay.Box);
        // 左侧强调条: 与深色底形成边界, 不额外引入新配色
        using (var strip = new SolidBrush(UiTheme.Accent))
            g.FillRectangle(strip, lay.Box.X, lay.Box.Y, 3, lay.Box.Height);

        using var titleFont = UiFont.Create(10f);
        using var bodyFont = UiFont.Create(12f);
        TextRenderer.DrawText(g, _barTitle, titleFont, lay.Title, UiTheme.MutedText,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        // 复制按钮: 只有拿到可复制的译文时才点亮; 加载中/失败态置灰, 避免复制走错误文字
        Color copyColor = !CanCopyBarTranslation
            ? Color.FromArgb(110, UiTheme.MutedText)
            : _barCopyHover ? UiTheme.Accent : UiTheme.MutedText;
        TextRenderer.DrawText(g, "复制", titleFont, lay.Copy, copyColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        // 正文高度被条高夹紧, 超出的部分必须裁掉: 否则长译文会压到状态栏和窗口边缘
        GraphicsState state = g.Save();
        g.SetClip(lay.Text);
        TextRenderer.DrawText(g, _barText, bodyFont, lay.Text,
            _barError ? BarErrorColor : BarTextColor,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        g.Restore(state);

        // 关闭图标用矢量线而不是字体字形: 缺字体时字形会画出 0 像素且不报错 (工具栏图标踩过)
        using var closePen = new Pen(UiTheme.MutedText, 1.6f);
        const int pad = 5;
        g.DrawLine(closePen, lay.Close.Left + pad, lay.Close.Top + pad,
            lay.Close.Right - pad, lay.Close.Bottom - pad);
        g.DrawLine(closePen, lay.Close.Right - pad, lay.Close.Top + pad,
            lay.Close.Left + pad, lay.Close.Bottom - pad);
    }

    /// <summary>只有成功拿到译文时才允许复制; 加载中/失败态的"复制"按钮置灰</summary>
    private bool CanCopyBarTranslation => _barText.Length > 0 && !_barError && !_barPending;

    private void SetTranslationBar(string title, string text, bool error)
    {
        _barTitle = title;
        _barText = text;
        _barError = error;
        _barCopyHover = false;
        Invalidate();
    }

    private void HideTranslationBar()
    {
        if (_barText.Length == 0)
            return;
        _barTitle = "";
        _barText = "";
        _barError = false;
        _barLine = -1;
        _barPending = false;
        _barCopyHover = false;
        // 用户主动收起时取消进行中的请求: 否则结果回来后又会把条弹出来
        _clickTranslateCts?.Cancel();
        Invalidate();
    }

    /// <summary>
    /// 译文条命中: 关闭图标与「复制」优先于整条拖动。
    /// 复制按钮在不可复制时退化为普通条内区域 (拖动窗口), 不会静默复制错误内容。
    /// </summary>
    private TranslationBarHit HitTranslationBar(Point clientPoint)
    {
        if (_barText.Length == 0)
            return TranslationBarHit.None;

        var lay = MeasureTranslationBar();
        if (!lay.Box.Contains(clientPoint))
            return TranslationBarHit.None;
        if (lay.Close.Contains(clientPoint))
            return TranslationBarHit.Close;
        if (lay.Copy.Contains(clientPoint) && CanCopyBarTranslation)
            return TranslationBarHit.Copy;
        return TranslationBarHit.Body;
    }

    // ===== 鼠标交互 =====

    /// <summary>鼠标命中检测: 客户区坐标换算到原图坐标后, 从上层到下层的四边形逐个测试</summary>
    private int HitTest(Point clientPt)
    {
        if (!_ocrDone)
            return -1;

        var ir = ImageRect();
        // 逻辑客户区坐标 -> 物理像素 -> 原图坐标
        float dpi = DpiFactor();
        float ix = (clientPt.X * dpi - ir.X) / _scale;
        float iy = (clientPt.Y * dpi - ir.Y) / _scale;

        // 从最后绘制的框开始命中 (后者在上层)
        for (int i = _lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrEmpty(_lines[i].Text))
                continue;
            if (IsInBox(_lines[i].Box, ix, iy))
                return i;
        }
        return -1;
    }

    // 点在 4 边形内 (射线法; 实现见 OcrBox.Contains)
    private static bool IsInBox(PaddleOcrDetectionBox box, float x, float y)
        => OcrBox.Contains(box, x, y);

    // ===== 字符定位: 检测框是 4 边形, 按字符宽度权重把字符投影到行内位置 =====

    /// <summary>字符宽度权重: 空格窄, 半角字符约 0.6 倍, 全角字符 1.0 倍 (用于行内字符定位)</summary>
    private static float CharWeight(char c) => c == ' ' || c == '\u3000' ? 0.35f : (c < 256 ? 0.6f : 1.0f);

    /// <summary>预计算每行字符的累计宽度权重表, 供字符命中/高亮插值使用</summary>
    private static float[][] BuildCharWeights(PaddleOcrLine[] lines)
    {
        var cum = new float[lines.Length][];
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Text;
            var w = new float[t.Length + 1];
            for (int j = 0; j < t.Length; j++)
                w[j + 1] = w[j] + CharWeight(t[j]);
            cum[i] = w;
        }
        return cum;
    }

    // 点在边 (A->B) 上的投影参数 u (0..1)
    private static float ProjU(float ax, float ay, float bx, float by, float x, float y)
    {
        float dx = bx - ax, dy = by - ay;
        float len2 = dx * dx + dy * dy;
        if (len2 < 1e-6f) return 0f;
        return Math.Clamp(((x - ax) * dx + (y - ay) * dy) / len2, 0f, 1f);
    }

    // 原图坐标 -> 行内字符索引 (0..n, n = 文本长度)
    /// <summary>原图坐标在检测框行内方向上的归一化位置 (0=行首, 1=行尾), 处理 180 度旋转</summary>
    private int CharIndexAt(int line, float ix, float iy)
    {
        var b = _lines[line].Box;
        float u = (ProjU(b.X1, b.Y1, b.X2, b.Y2, ix, iy) + ProjU(b.X4, b.Y4, b.X3, b.Y3, ix, iy)) / 2f;

        // 有 CTC 真实字符框时按真实边界判定; 否则回退到字符宽度权重
        if (line < _lineGlyphs.Length && _lineGlyphs[line] is { } glyph)
            return glyph.IndexAtSourceU(u);

        var cum = _lineCumW[line];
        if (cum.Length < 2) return 0;
        return OcrSelection.CharIndexFromU(cum, u, _lines[line].AppliedRotationDegrees);
    }

    // 行内字符范围 -> 高亮四边形 (逻辑客户端坐标)
    /// <summary>行内字符范围 [start, end) 对应的高亮四边形顶点</summary>
    private PointF[] CharRangePoints(int line, int start, int end)
    {
        var ir = ImageRect();
        float dpi = DpiFactor();
        var b = _lines[line].Box;
        var cum = _lineCumW[line];
        float total = cum[cum.Length - 1];
        float t0 = total > 0 ? cum[start] / total : 0f;
        float t1 = total > 0 ? cum[Math.Min(end, cum.Length - 1)] / total : 1f;
        if (_lines[line].AppliedRotationDegrees == 180)
        {
            (t0, t1) = (1f - t1, 1f - t0);
        }
        float uA = Math.Min(t0, t1), uB = Math.Max(t0, t1);
        // 插值点先算原图坐标再整体乘 _scale (与 BoxToClientPoints 一致); 旧写法只对偏移乘 scale, 非 1:1 时错位
        var (topA, topB) = OcrBox.TopEdge(b);
        var (botA, botB) = OcrBox.BottomEdge(b);
        PointF Map(PointF p) => new((p.X * _scale + ir.X) / dpi, (p.Y * _scale + ir.Y) / dpi);
        PointF Lerp(PointF a, PointF c, float u) =>
            new(a.X + (c.X - a.X) * u, a.Y + (c.Y - a.Y) * u);
        return
        [
            Map(Lerp(topA, topB, uA)), Map(Lerp(topA, topB, uB)),
            Map(Lerp(botA, botB, uB)), Map(Lerp(botA, botB, uA)),
        ];
    }

    /// <summary>
    /// 行内字符范围 [start, end) 对应的真实字符四边形 (逻辑客户端坐标);
    /// 该行没有 CTC 布局时返回 null, 由调用方回退到 <see cref="CharRangePoints"/>。
    /// </summary>
    private PointF[][]? CharRangeQuads(int line, int start, int end)
    {
        if (line < 0 || line >= _lineGlyphs.Length || _lineGlyphs[line] is not { } glyph)
            return null;

        int a = Math.Clamp(start, 0, glyph.Boxes.Length);
        int b = Math.Clamp(end, a, glyph.Boxes.Length);
        if (a == b)
            return null;

        var ir = ImageRect();
        float dpi = DpiFactor();
        PointF Map(float x, float y) => new((x * _scale + ir.X) / dpi, (y * _scale + ir.Y) / dpi);

        var quads = new PointF[b - a][];
        for (int i = a; i < b; i++)
        {
            var box = glyph.Boxes[i];
            quads[i - a] =
            [
                Map(box.X1, box.Y1), Map(box.X2, box.Y2),
                Map(box.X3, box.Y3), Map(box.X4, box.Y4),
            ];
        }
        return quads;
    }

    /// <summary>
    /// 清空整行选中与行内部分选中。
    /// 二者必须同时清 —— 只清 _selected 会留下"画面上没有高亮、Ctrl+C 却仍能复制"的
    /// 隐藏选区 (空白单击、Shift 取消最后一行都曾踩过)。
    /// </summary>
    private void ClearSelection()
    {
        _selected.Clear();
        _partial = null;
    }

    /// <summary>
    /// 双击选中落点所在的同类连续段 (英文单词/连续汉字/数字), 三击选中整行。
    /// 不写剪贴板 —— 双击只是建立选区, 需要时按 Ctrl+C 复制。
    /// </summary>
    private void SelectWordAt(int line, bool wholeLine, Point client)
    {
        _panning = false;
        _charDrag = false;
        _charDragMoved = false;
        _dragLine = -1;
        _hover = line;

        ClearSelection();
        _selected.Add(line);
        if (!wholeLine)
        {
            var ir = ImageRect();
            float dpi = DpiFactor();
            int index = CharIndexAt(line, (client.X * dpi - ir.X) / _scale, (client.Y * dpi - ir.Y) / _scale);
            if (OcrSelection.WordRange(_lines[line].Text, index) is { } range)
                _partial = (line, range.Start, range.End);
        }
        UpdateIdleStatus();
    }

    /// <summary>全选所有检测块 (工具栏按钮与 Ctrl+A 共用)</summary>
    private void SelectAll()
    {
        ClearSelection();
        for (int i = 0; i < _lines.Length; i++)
            _selected.Add(i);
        Invalidate();
    }

    /// <summary>左键按下: 空白处=清除选中/拖动窗口, 文本块上=Shift 多选 或 进入字符级拖选</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        // 中键拖动, 或按住空格 + 左键: 平移放大后的图片, 不进入选词/拖窗口逻辑
        if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && _spaceHeld))
        {
            if (OcrPan.CanPan(new SizeF(_src.Width * _scale, _src.Height * _scale), ImageViewport()))
            {
                _panning = true;
                _panAnchorClient = e.Location;
                _panAnchorOffset = _panOffset;
                Cursor = Cursors.SizeAll;
            }
            return;
        }

        if (e.Button != MouseButtons.Left)
            return;

        // 译文条盖在图片上方: 命中必须先于文字块, 否则点关闭/复制会选中它背后的文字
        switch (HitTranslationBar(e.Location))
        {
            case TranslationBarHit.Close:
                HideTranslationBar();
                UpdateIdleStatus();
                return;
            case TranslationBarHit.Copy:
                CopyText(_barText);
                return;
            case TranslationBarHit.Body:
                // 条内其它位置按住可拖动窗口, 与点空白处一致
                _draggingMove = true;
                _dragStartScreen = Cursor.Position;
                _dragStartLoc = Location;
                return;
        }

        int hit = HitTest(e.Location);

        // 双击选词 / 三击选整行。必须在 MouseUp 之前截住: 第二下 MouseDown 若走正常
        // 单击分支, 松手时会把整行当"单击"自动复制, 覆盖掉刚选好的词。
        if (hit >= 0 && e.Clicks >= 2)
        {
            SelectWordAt(hit, e.Clicks >= 3, e.Location);
            Invalidate();
            return;
        }

        if (hit < 0)
        {
            if ((Control.ModifierKeys & Keys.Shift) == 0)
            {
                ClearSelection();
                // 选区已清空, 译文条再留着就与画面不一致了
                HideTranslationBar();
                Invalidate();
            }
            // 无边框窗体: 按住空白处拖动可移动窗口
            _draggingMove = true;
            _dragStartScreen = Cursor.Position;
            _dragStartLoc = Location;
            return;
        }

        if ((Control.ModifierKeys & Keys.Shift) != 0)
        {
            if (_selected.Contains(hit)) _selected.Remove(hit);
            else _selected.Add(hit);
            // Shift 取消最后一行时, 行内部分选区可能仍指向它: 不清掉就会变成看不见的选区
            if (_partial is { } stale && !_selected.Contains(stale.Line))
                _partial = null;
        }
        else
        {
            // 进入拖选; 未移动时 (见 OnMouseUp) 视为单击选中整行
            _charDrag = true;
            _charDragMoved = false;
            _dragLine = hit;
            var ir = ImageRect();
            float dpi = DpiFactor();
            _dragAnchorChar = CharIndexAt(hit, (e.X * dpi - ir.X) / _scale, (e.Y * dpi - ir.Y) / _scale);
            _dragAnchorClient = e.Location;
            ClearSelection();
            _selected.Add(hit); // 整行淡高亮 + 行内字符强高亮
        }
        Invalidate();
    }

    /// <summary>鼠标移动: 拖动窗口 / 字符级拖选实时更新选区 / hover 提示</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning)
        {
            if (e.Button is MouseButtons.Middle or MouseButtons.Left)
            {
                // 鼠标坐标是逻辑像素, 平移偏移是物理像素, 需乘 DPI 因子
                float dpi = DpiFactor();
                _panOffset = new PointF(
                    _panAnchorOffset.X + (e.X - _panAnchorClient.X) * dpi,
                    _panAnchorOffset.Y + (e.Y - _panAnchorClient.Y) * dpi);
                ClampPan();
                Invalidate();
            }
            else
            {
                _panning = false;
                Cursor = Cursors.Default;
            }
            return;
        }
        if (_draggingMove)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 用绝对屏幕坐标计算, 避免窗口移动后客户端坐标基准变化导致抖动
                Location = new Point(_dragStartLoc.X + Cursor.Position.X - _dragStartScreen.X,
                                    _dragStartLoc.Y + Cursor.Position.Y - _dragStartScreen.Y);
                return; // 拖动中不做 hover 检测, 减少重绘
            }
            else
            {
                _draggingMove = false;
                return;
            }
        }
        if (_charDrag)
        {
            if (e.Button == MouseButtons.Left)
            {
                // 拖选: 更新当前行内的字符范围
                if (_dragLine >= 0)
                {
                    if (!IsInBox(_lines[_dragLine].Box, (e.X * DpiFactor() - ImageRect().X) / _scale, (e.Y * DpiFactor() - ImageRect().Y) / _scale))
                    {
                        // 拖出文本块: 保留最后一次 _partial 作为定格高亮, 但复位拖选状态,
                        // 否则行号与"已移动"标记会留到下一次点击, 让 MouseUp 误判。
                        _charDrag = false;
                        _dragLine = -1;
                        _charDragMoved = false;
                        return;
                    }
                    if (Math.Abs(e.X - _dragAnchorClient.X) + Math.Abs(e.Y - _dragAnchorClient.Y) >= 3)
                        _charDragMoved = true;
                    if (_charDragMoved)
                    {
                        var ir = ImageRect();
                        float dpi = DpiFactor();
                        int cur = CharIndexAt(_dragLine, (e.X * dpi - ir.X) / _scale, (e.Y * dpi - ir.Y) / _scale);
                        string t = _lines[_dragLine].Text;
                        int a = _dragAnchorChar, b = cur;
                        _partial = (_dragLine, Math.Min(a, b), Math.Max(a, b));
                        // 拖到块边缘时, 选区自动贴到行首/行尾
                        var box = _lines[_dragLine].Box;
                        float ix = (e.X * dpi - ir.X) / _scale;
                        if (cur == 0 && ix < Math.Min(Math.Min(box.X1, box.X2), Math.Min(box.X3, box.X4)) - 2f)
                            _partial = (_dragLine, 0, Math.Min(a, t.Length));
                        if (cur == t.Length && ix > Math.Max(Math.Max(box.X1, box.X2), Math.Max(box.X3, box.X4)) + 2f)
                            _partial = (_dragLine, Math.Max(a, 0), t.Length);
                        Invalidate();
                    }
                }
                return;
            }
            _charDrag = false;
            return;
        }
        // 译文条覆盖下的文字块不参与 hover: 否则光标停在译文上会误报背后的识别文本
        TranslationBarHit barHit = HitTranslationBar(e.Location);
        if (barHit != TranslationBarHit.None)
        {
            bool copyHover = barHit == TranslationBarHit.Copy;
            if (_barCopyHover != copyHover)
            {
                _barCopyHover = copyHover;
                Invalidate();
            }
            Cursor = barHit is TranslationBarHit.Copy or TranslationBarHit.Close
                ? Cursors.Hand
                : Cursors.Default;
            if (_hover != -1)
            {
                _hover = -1;
                Invalidate();
            }
            return;
        }
        if (_barCopyHover)
        {
            _barCopyHover = false;
            Invalidate();
        }

        int hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            if (hit >= 0)
            {
                Cursor = Cursors.Hand;
                // 悬停时把置信度与行文本合并展示, 方便快速定位可能识别错的块
                SetStatus($"{_lines[hit].Text} · {OcrConfidence.Describe(_lines[hit].RecognitionScore)}");
            }
            else
            {
                Cursor = Cursors.Default;
                UpdateIdleStatus();
            }
            Invalidate();
        }
    }

    /// <summary>
    /// Ctrl+滚轮缩放 (以鼠标为锚点); 未按 Ctrl 时滚轮纵向平移 (Shift 横向),
    /// 高倍放大后不必寻找中键就能看到四周内容。
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Control.ModifierKeys & Keys.Control) != 0)
        {
            Zoom(e.Delta > 0 ? 1.15f : 1f / 1.15f, e.Location);
            return;
        }

        float dpi = DpiFactor();
        float step = e.Delta / 120f * 80f * dpi;
        var shift = (Control.ModifierKeys & Keys.Shift) != 0
            ? new PointF(step, 0)
            : new PointF(0, step);
        _panOffset = OcrPan.Clamp(
            new PointF(_panOffset.X + shift.X, _panOffset.Y + shift.Y),
            new SizeF(_src.Width * _scale, _src.Height * _scale), ImageViewport());
        Invalidate();
    }

    // 空格 + 左键拖动也可平移 (没有中键的笔记本)
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Space)
            _spaceHeld = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.Space)
            _spaceHeld = false;
    }

    /// <summary>窗口失焦时清掉空格状态, 避免回来后的第一次左键被误判为平移</summary>
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _spaceHeld = false;
        if (_panning)
        {
            _panning = false;
            Cursor = Cursors.Default;
        }
    }

    /// <summary>
    /// 左键松开: 未拖动的字符级按下视为单击 (整行选中); 点击空白处清空选中。
    /// 单击文本块时按设置自动复制该块 (F-1)。
    /// </summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_panning && (e.Button is MouseButtons.Middle or MouseButtons.Left))
        {
            _panning = false;
            Cursor = Cursors.Default;
            return;
        }
        if (e.Button == MouseButtons.Left)
            _draggingMove = false;
        if (!_charDrag || e.Button != MouseButtons.Left)
            return;

        _charDrag = false;
        int clickedLine = _dragLine;
        bool dragged = _charDragMoved;
        _dragLine = -1;
        _charDragMoved = false;

        if (!dragged && clickedLine >= 0)
        {
            ClearSelection();
            _selected.Add(clickedLine);
            Invalidate();

            // 单击翻译优先于单击复制: 开启后单击只翻译, 不再自动写剪贴板
            // (每次单击都冲掉用户剪贴板代价太大); 复制仍可用 Ctrl+C 或工具栏。
            if (SettingsStore.Current.ClickToTranslate)
            {
                TranslateLine(clickedLine);
                return;
            }

            // 只在"单击检测块"时复制: 拖选、Shift 多选、空白单击都不复制。
            // 复制后不要调用 UpdateIdleStatus, 否则"已复制 N 字符"会立刻被"已选 N 字符"覆盖;
            // 鼠标移到别的块时 OnMouseMove 会自然刷新状态。
            if (SettingsStore.Current.ClickToCopy)
                CopyText(_lines[clickedLine].Text);
            else
                UpdateIdleStatus();
            return;
        }

        if (!dragged)
        {
            ClearSelection();
            Invalidate();
        }
        UpdateIdleStatus();
    }

    // 无边框窗体: 边缘 6 像素内返回对应命中区, 由系统处理缩放并显示缩放光标;
    // 另外在 WM_KEYDOWN 兜底处理 Ctrl+C (手动消息循环下 ProcessCmdKey 可能不生效)
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x0084;
        const int WM_KEYDOWN = 0x0100;
        if (m.Msg == WM_NCHITTEST && IsHandleCreated)
        {
            const int margin = 6;
            int lp = m.LParam.ToInt32();
            Point pt = PointToClient(new Point((short)(lp & 0xFFFF), (short)(lp >> 16)));
            bool l = pt.X < margin, r = pt.X >= ClientSize.Width - margin;
            bool t = pt.Y < margin, b = pt.Y >= ClientSize.Height - margin;
            if (l && t) { m.Result = (IntPtr)13; return; } // HTTOPLEFT
            if (r && t) { m.Result = (IntPtr)14; return; } // HTTOPRIGHT
            if (l && b) { m.Result = (IntPtr)16; return; } // HTBOTTOMLEFT
            if (r && b) { m.Result = (IntPtr)17; return; } // HTBOTTOMRIGHT
            if (l) { m.Result = (IntPtr)10; return; }     // HTLEFT
            if (r) { m.Result = (IntPtr)11; return; }     // HTRIGHT
            if (t) { m.Result = (IntPtr)12; return; }     // HTTOP
            if (b) { m.Result = (IntPtr)15; return; }     // HTBOTTOM
        }
        else if (m.Msg == WM_KEYDOWN && IsHandleCreated)
        {
            int key = (int)m.WParam.ToInt64();
            bool ctrl = (User32.GetKeyState(0x11) & 0x8000) != 0; // VK_CONTROL 高位 = 当前按下
            if (ctrl && key == 0x43)
            {
                CopyText(GetSelectedText());
                m.Result = (IntPtr)0;
                return;
            }
        }
        base.WndProc(ref m);
    }

    // ===== 键盘 =====

    /// <summary>快捷键处理: Ctrl+C 复制选中 / Ctrl+A 全选 / +/- 缩放 / 0 适应 / Esc 关闭</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        const int WM_KEYDOWN = 0x0100;
        if (msg.Msg != WM_KEYDOWN)
            return base.ProcessCmdKey(ref msg, keyData);

        switch (keyData)
        {
            case Keys.Control | Keys.C:
                CopyText(GetSelectedText());
                return true;
            case Keys.Control | Keys.A:
                SelectAll();
                return true;
            case Keys.Add:
            case Keys.Oemplus:
                Zoom(1.25f);
                return true;
            case Keys.Subtract:
            case Keys.OemMinus:
                Zoom(1f / 1.25f);
                return true;
            case Keys.D0:
            case Keys.NumPad0:
                _scale = _minScale;
                FitWindowToImage();
                UpdateZoomLabel();
                Invalidate();
                return true;
            case Keys.Escape:
                Close();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ===== 文本 =====

    /// <summary>是否按阅读顺序排序 (来自设置)</summary>
    private bool _wordOrder = true;

    /// <summary>当前结果转成可排序的行集合; 按设置决定是否重排</summary>
    private IReadOnlyList<OcrText.Line> CurrentLines()
    {
        var rows = _lines.Where(l => !string.IsNullOrEmpty(l.Text)).Select(OcrText.ToLine).ToList();
        return _wordOrder ? OcrText.SortReadingOrder(rows) : rows;
    }

    /// <summary>全部识别文本 (换行拼接, 忽略空行)</summary>
    private string GetAllText()
    {
        var rows = CurrentLines();
        if (rows.Count == 0)
            return string.Empty;
        return string.Join("\n", rows.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
    }

    /// <summary>
    /// 当前选中的文本: 整行选中按行取, 行内部分选中取 [Start, End) 子串。
    /// 输出顺序按阅读顺序名次, 与「复制全部」「复制段落」一致 ——
    /// 直接按检测顺序排序时, 多栏截图的多选复制会右栏在前。
    /// </summary>
    private string GetSelectedText()
    {
        if (_selected.Count == 0)
            return string.Empty; // _partial 必然伴随 _selected (见 ClearSelection 的不变量)

        var parts = new List<string>();
        foreach (int i in _selected.OrderBy(RankOf).ThenBy(i => i))
        {
            string t = _lines[i].Text;
            if (_partial is { } p && p.Line == i)
                t = t.Substring(p.Start, p.End - p.Start);
            if (t.Length > 0)
                parts.Add(t);
        }
        return string.Join("\n", parts);
    }

    /// <summary>检测行索引 -> 阅读顺序名次; 空白行 (无名词) 排到最后</summary>
    private int RankOf(int line) => line >= 0 && line < _lineRank.Length ? _lineRank[line] : int.MaxValue;

    private bool HasSelection() => _selected.Count > 0;

    /// <summary>当前选中字符数 (用于状态栏提示)</summary>
    private int SelectedCharCount()
    {
        int n = 0;
        foreach (int i in _selected)
        {
            if (_partial is { } p && p.Line == i)
                n += p.End - p.Start;
            else
                n += _lines[i].Text.Length;
        }
        return n;
    }

    /// <summary>刷新右下角状态栏: 有选中显示字数, 否则显示操作提示</summary>
    private void UpdateIdleStatus()
    {
        if (!_ocrDone)
            return;
        if (HasSelection())
            SetStatus($"已选 {SelectedCharCount()} 字符（Ctrl+C 复制）");
        else
        {
            // 只有放大到超出视口时才提示平移, 平时不占用状态栏
            string panHint = _scale > _minScale + 0.001f ? " · 中键平移" : string.Empty;
            // 单击的行为随设置变化: 提示必须如实反映当前开关, 否则用户不知道单击会发生什么
            string clickHint = SettingsStore.Current.ClickToTranslate
                ? "单击翻译"
                : SettingsStore.Current.ClickToCopy ? "单击复制" : "单击/拖选";
            SetStatus($"{_lines.Length} 个文本块 · {clickHint} · 拖选 · Shift 多选 · Ctrl+滚轮缩放" + panHint);
        }
    }

    /// <summary>
    /// 翻译选区或全部文本。顺序: 知情同意 -> 确保有 Key -> 截断 -> 打开独立译文窗口。
    /// 任何一步不满足都只给提示, 不改动识别结果或剪贴板。
    /// </summary>
    private void TranslateSelectionOrAll()
    {
        if (!_ocrDone)
            return;

        string text = HasSelection()
            ? GetSelectedText()
            : OcrText.ToPlainText(CurrentLines(), joinAll: true);

        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("没有可翻译的文本");
            return;
        }

        if (!EnsureTranslationReady(out string apiKey))
            return;

        var settings = SettingsStore.Current;
        string limited = TranslateClient.LimitInput(text, out bool truncated);
        string model = string.IsNullOrWhiteSpace(settings.TranslateModel)
            ? TranslateClient.DefaultModel
            : settings.TranslateModel.Trim();

        // 目标语言默认「自动」: 中文译英文、英文译中文; 用户也可在设置里固定方向
        var direction = TranslateClient.ResolveDirection(limited, settings.TranslateTargetLanguage);

        // 已有译文窗口时只激活, 不重新发请求:
        // Close() 只取消本地 HTTP, 不保证服务端停止计费; 连续点击会重复发送。
        if (_translateForm is { IsDisposed: false })
        {
            _translateForm.Activate();
            return;
        }

        var form = new TranslateForm(
            limited, direction.Label, truncated,
            token => TranslateClient.TranslateAsync(limited, direction.Target, model, apiKey, token));
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_translateForm, form))
                _translateForm = null;

            // 请求结束后才能再次发起; 结果窗口关闭时不再碰已销毁的控件
            if (!IsDisposed && !Disposing)
                _translateButton.Enabled = _ocrDone;
        };
        _translateForm = form;
        _translateButton.Enabled = false;
        form.Show(this);

        if (truncated)
            SetStatus($"文本过长，仅翻译前 {TranslateClient.MaxInputChars} 字");
    }

    /// <summary>
    /// 翻译前的两道门: 首次知情同意 + 可用的 API Key。
    /// 任一不满足都返回 false 并已在界面给出提示, 调用方直接停止。
    /// </summary>
    private bool EnsureTranslationReady(out string apiKey)
    {
        apiKey = "";
        var settings = SettingsStore.Current;
        if (!settings.TranslateConsentGiven)
        {
            // 只在第一次点翻译时询问; 同意状态会落盘, 用户可在翻译设置里撤销
            var choice = MessageBox.Show(
                this,
                "翻译需要联网：本次会把识别出的文字发送到 DeepSeek（api.deepseek.com）进行翻译，不会上传截图。\n\n" +
                "识别本身仍然全程离线。是否继续？",
                "翻译需要联网",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1);
            if (choice != DialogResult.OK)
                return false;

            SettingsStore.Update(s => s.TranslateConsentGiven = true);
        }

        if (ProtectedKey.TryUnprotect(settings.TranslateApiKeyProtected, out apiKey) &&
            !string.IsNullOrWhiteSpace(apiKey))
            return true;

        using var dlg = new TranslateSettingsForm();
        dlg.ShowDialog(this);

        settings = SettingsStore.Current;
        if (ProtectedKey.TryUnprotect(settings.TranslateApiKeyProtected, out apiKey) &&
            !string.IsNullOrWhiteSpace(apiKey))
            return true;

        SetStatus("未配置 API Key，已打开翻译设置");
        apiKey = "";
        return false;
    }

    /// <summary>
    /// 单击某个文字块直接翻译, 结果贴在窗口底部 (可选设置, 默认关闭)。
    /// 请求与工具栏入口相互独立: 关闭译文条不影响独立译文窗口, 反之亦然。
    /// </summary>
    private void TranslateLine(int line)
    {
        if (!_ocrDone || line < 0 || line >= _lines.Length)
            return;

        string source = _lines[line].Text;
        if (string.IsNullOrWhiteSpace(source))
            return;

        // 同一块已有译文/正在请求: 不重复发送 (每次请求都计费, 连点不该重复扣费);
        // 失败后允许重点重试, 否则用户没有重试入口。
        if (_barLine == line && (_barPending || !_barError))
        {
            SetStatus(_barPending ? "正在翻译\u2026" : "译文已显示在窗口底部");
            return;
        }

        // 测试注入时跳过同意/Key: 不得联网, 也不得弹出会阻塞测试的模态框
        string apiKey = "";
        if (TranslateForTest is null && !EnsureTranslationReady(out apiKey))
            return;

        var settings = SettingsStore.Current;
        string limited = TranslateClient.LimitInput(source, out bool truncated);
        string model = string.IsNullOrWhiteSpace(settings.TranslateModel)
            ? TranslateClient.DefaultModel
            : settings.TranslateModel.Trim();
        var direction = TranslateClient.ResolveDirection(limited, settings.TranslateTargetLanguage);

        // 连点别的块: 取消上一次请求, 否则旧译文可能在新的之后才回来并覆盖它
        _clickTranslateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _clickTranslateCts = cts;
        _barLine = line;
        _barPending = true;

        string title = $"译文 · {direction.Label}" + (truncated ? " · 已截断" : "");
        SetTranslationBar(title, "正在翻译\u2026", error: false);
        SetStatus("正在翻译\u2026");

        Func<CancellationToken, Task<TranslateResult>> run = TranslateForTest is { } fake
            ? token => fake(limited, token)
            : token => TranslateClient.TranslateAsync(limited, direction.Target, model, apiKey, token);

        _ = RunLineTranslationAsync(title, run, cts);
    }

    /// <summary>
    /// 等待单击翻译结果并写入译文条。窗口关闭/请求被新点击取消后不再触碰界面;
    /// CTS 由本方法独占释放 (关闭时只 Cancel, 不提前 Dispose)。
    /// </summary>
    private async Task RunLineTranslationAsync(
        string title,
        Func<CancellationToken, Task<TranslateResult>> run,
        CancellationTokenSource cts)
    {
        try
        {
            var result = await run(cts.Token);
            if (cts.IsCancellationRequested || IsDisposed || Disposing)
                return;

            if (result.Ok)
            {
                SetTranslationBar(title, result.Text, error: false);
                SetStatus("翻译完成 · 译文显示在窗口底部");
            }
            else
            {
                SetTranslationBar(title, result.Error ?? "翻译失败。", error: true);
                SetStatus("翻译失败");
            }
        }
        catch (OperationCanceledException)
        {
            // 连点别的块/关闭窗口: 新内容或窗口本身已接管, 这里什么都不做
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested && !IsDisposed && !Disposing)
            {
                SetTranslationBar(title, "翻译失败：" + ex.Message, error: true);
                SetStatus("翻译失败");
            }
        }
        finally
        {
            if (ReferenceEquals(_clickTranslateCts, cts))
            {
                _clickTranslateCts = null;
                _barPending = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>文本写入剪贴板并在状态栏反馈结果</summary>
    private void CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("没有可复制的文本");
            return;
        }

        if (ClipboardText.TrySet(text))
        {
            int charCount = text.Count(c => c != '\r');
            SetStatus($"已复制 {charCount} 字符");
        }
        else
        {
            SetStatus("复制失败（剪贴板被占用？）");
        }
    }

    /// <summary>保存原图为 PNG</summary>
    private void SaveImage()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png",
            FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                _src.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
                SetStatus("图片已保存");
            }
            catch (Exception ex)
            {
                SetStatus("保存失败：" + ex.Message);
            }
        }
    }

    // ===== OCR =====

    /// <summary>后台线程执行 OCR, 完成后切回 UI 线程刷新识别结果; 失败时状态栏提示</summary>
    private void StartOcr()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var service = OcrService.Instance;
        var img = _src;

        Task.Run(() =>
        {
            try
            {
                var result = service.Recognize(img, token);
                if (token.IsCancellationRequested)
                    return;
                this.TryBeginInvoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;
                    _lines = result.Lines;
                    _wordOrder = SettingsStore.Current.SortReadingOrder;
                    ClearSelection();
                    _lineCumW = BuildCharWeights(_lines);
                    _lineGlyphs = OcrGlyphLayout.BuildAll(_lines);
                    _lineRank = OcrSelection.BuildReadingRanks(_lines, _wordOrder);
                    _hover = -1;
                    _ocrDone = true;
                    _translateButton.Enabled = true;
                    _minScale = ComputeFitScale();
                    if (_scale < _minScale) _scale = _minScale;
                    UpdateZoomLabel();
                    UpdateIdleStatus();
                    Invalidate();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                this.TryBeginInvoke(() =>
                {
                    SetStatus("识别失败：" + ex.Message);
                    _ocrDone = true;
                });
            }
        });
    }

    /// <summary>取消 OCR 与单击翻译任务、退订状态事件并释放位图</summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        OcrService.Instance.StatusChanged -= OnServiceStatus;
        _cts?.Cancel();
        _cts?.Dispose();
        // 只取消不释放: 翻译任务的 finally 负责释放, 这里提前 Dispose 会和它竞争
        _clickTranslateCts?.Cancel();
        _src.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>
/// 深色工具栏渲染器: 文字浅色, 悬停/按下用蓝色高亮
/// </summary>
internal sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    public DarkToolStripRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // 统一浅色文字 (含状态标签/缩放比例标签)
        e.TextColor = e.Item.Enabled ? Color.FromArgb(226, 230, 236) : Color.FromArgb(110, 114, 120);
        base.OnRenderItemText(e);
    }

    /// <summary>
    /// 不画工具栏外边框。
    ///
    /// 踩过的坑: <see cref="ToolStripProfessionalRenderer"/> 的默认实现会在**右边缘**补一条
    /// 1px 竖线, 颜色不受 <c>ProfessionalColorTable.ToolStripBorder</c> 控制 ——
    /// 深色主题下它渲染成接近纯白 (实测 (242,242,242)), 表现为右上角一条突兀的"白色竖线"。
    /// 实测对照: 裸 ToolStrip 用 Professional 有该线, 换成 System 或不调 base 就没有。
    ///
    /// 本工具栏嵌在深色窗体顶部, 四边不需要描边, 直接短路掉最干净。
    /// </summary>
    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // 有意不调用 base: 该边框只会带来右边缘的白线
    }
}

internal sealed class DarkColorTable : ProfessionalColorTable
{
    private static readonly Color Bg = UiTheme.Surface;
    private static readonly Color Hover = Color.FromArgb(50, 54, 62);
    private static readonly Color Active = Color.FromArgb(28, 74, 128);

    public override Color ToolStripGradientBegin => Bg;
    public override Color ToolStripGradientMiddle => Bg;
    public override Color ToolStripGradientEnd => Bg;
    public override Color ToolStripBorder => Color.FromArgb(56, 60, 68);
    public override Color ToolStripDropDownBackground => Bg;
    public override Color ImageMarginGradientBegin => Bg;
    public override Color ImageMarginGradientMiddle => Bg;
    public override Color ImageMarginGradientEnd => Bg;
    public override Color SeparatorDark => Color.FromArgb(60, 64, 72);
    public override Color SeparatorLight => Color.FromArgb(60, 64, 72);
    public override Color ButtonSelectedHighlight => Hover;
    public override Color ButtonSelectedGradientBegin => Hover;
    public override Color ButtonSelectedGradientEnd => Hover;
    public override Color ButtonPressedGradientBegin => Active;
    public override Color ButtonPressedGradientEnd => Active;
    public override Color ButtonSelectedBorder => UiTheme.Accent;
    public override Color ButtonPressedBorder => UiTheme.Accent;
}
