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
    private bool _ocrRunning;
    /// <summary>识别失败的原始信息 (非 null 表示失败态); 界面只显示精简版, 不把异常栈甩给用户</summary>
    private string? _ocrError;
    /// <summary>失败态「点击重试」的命中矩形 (逻辑像素); 空表示当前没有该按钮</summary>
    private Rectangle _retryRect;

    /// <summary>识别中提示的动画计时 (只在识别期间运行, 结束后立刻停, 不空转)</summary>
    private readonly System.Windows.Forms.Timer _stateTimer = new() { Interval = 90 };
    private int _statePhase;
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
    /// <summary>
    /// 缩放下拉: 一个控件替代「- / 100% / + / 适应」四个。
    /// 这四个常驻占约 136px, 直接决定了窗口最小宽度 (小截图会带出一大片空白画布)。
    /// 收进下拉后最小宽度降到 320px 下限, +/-/0/Ctrl+滚轮 快捷键全部保留。
    /// </summary>
    private readonly ToolStripDropDownButton _zoomMenu = new();
    private readonly ToolStripButton _selectAll = new();
    private readonly ToolStripButton _copyAll = new();
    private readonly ToolStripButton _copyCsv = new();
    private readonly ToolStripButton _copyCsvComma = new();
    private readonly ToolStripButton _copyPlain = new();
    private readonly ToolStripButton _copyMarkdown = new();
    private readonly ToolStripButton _pinText = new();
    private readonly ToolStripButton _saveImageItem = new();
    private readonly ToolStripButton _copySelection = new();
    private readonly ToolStripButton _translateButton = new();
    private readonly ToolStripButton _closeButton = new();

    /// <summary>当前打开的译文窗口; 再次点「翻译」时先关旧窗, 避免多个窗口显示不同选区</summary>
    private TranslateForm? _translateForm;

    // ===== 悬停「译」按钮 =====
    /// <summary>指针正压在「译」按钮上 (绘制用高亮态; 不用 _hover, 那表示"在文字块上")</summary>
    private bool _translateHover;

    /// <summary>
    /// 已在「译」按钮上按下、等松手再翻译的行号。
    ///
    /// 不能在 MouseDown 里直接发起: 首次翻译会弹联网同意/Key 设置对话框,
    /// 而按下时左键仍处于按下状态, 对话框弹出后这次松手会落到对话框上 (可能误点默认按钮)。
    /// </summary>
    private int _hintPressLine = -1;

    // ===== 底部译文面板 =====
    /// <summary>进行中的翻译请求; 连点别的块时取消上一次, 避免旧译文后到覆盖新译文</summary>
    private CancellationTokenSource? _clickTranslateCts;

    /// <summary>
    /// 译文面板高度 (逻辑像素); 0 = 隐藏。
    /// 图片视口会扣掉它 (见 <see cref="ImageViewport"/>) —— 面板永远不覆盖截图。
    /// </summary>
    private int _panelHeight;

    /// <summary>为了放下面板而额外增加的窗口高度; 收起时按这个值减回去</summary>
    private int _panelGrowth;

    /// <summary>同步面板高度时正在改窗口尺寸, 避免 OnClientSizeChanged 递归</summary>
    private bool _syncingPanel;

    /// <summary>关闭图标的悬停态 (只有它和「复制译文」需要额外反馈)</summary>
    private bool _barCloseHover;
    private string _barTitle = "";
    private string _barText = "";
    private bool _barError;

    /// <summary>当前译文"疑似仍是原文" (模型没翻); 这种结果允许同一块再点一次重试, 不受去重拦截</summary>
    private bool _barEcho;
    /// <summary>译文条当前对应的检测行: 同一块连点不重复发请求 (失败后允许重点重试)</summary>
    private int _barLine = -1;
    private bool _barPending;
    private bool _barCopyHover;

    /// <summary>译文条上的命中区域; 关闭/复制必须先于"整条拖动"判定</summary>
    private enum TranslationBarHit { None, Body, Copy, Close }

    /// <summary>
    /// 工具栏字号 (逻辑像素, 非 pt)。
    ///
    /// 10px 时工具栏文字比状态栏 (12px) 还小 —— 主操作区是全窗口最小的字, 层级是反的。
    /// 现在统一到 12px; 工具栏已把缩放与"保存图片"收进下拉, 宽度仍有富余。
    /// 改这个值要连带看工具栏首选宽度 (它直接决定窗口最小宽度)。
    /// </summary>
    private const float UiFontSize = 12f;

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
        // 无边框窗口的圆角: Win11 走 DWM, Win10 退回 Region 裁剪 (见 WindowChrome)
        WindowChrome.Attach(this);
        // 最小宽度 = 工具栏完整宽度, 小截图时按钮不会被挤掉/折叠进溢出菜单
        MinimumSize = new Size(Math.Max(320, _toolStrip.GetPreferredSize(new Size(0, _toolStrip.Height)).Width + 16), 140);
        // 初始大小: 图片原始尺寸 (1:1 显示) + 工具栏
        Size = SizeFromImage();
        CenterOnTargetScreen();

        _minScale = ComputeFitScale();
        _scale = _minScale;
        UpdateZoomLabel();
        // 无边框窗口没有标题栏可抓, 移动方式不写出来用户很难发现;
        // 识别进度由画布中央的胶囊表示 (见 DrawRecognitionState), 状态栏只留移动提示。
        SetStatus("拖动空白处移动窗口");

        // 识别中的动画: 只在 _ocrRunning 期间跑, 结束立刻停, 不空转
        _stateTimer.Tick += (_, _) =>
        {
            if (!_ocrRunning)
            {
                _stateTimer.Stop();
                return;
            }
            _statePhase = (_statePhase + 1) % 4;
            Invalidate();
        };

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

    /// <summary>
    /// 引擎状态 (加载模型 / 切换档位)。
    /// 识别期间由画布中央的胶囊承载 —— 右下角再显示一遍会与"正在识别…"重复,
    /// 而且那行字正好压在截图上。
    /// </summary>
    private string _serviceStatus = string.Empty;

    /// <summary>把引擎状态切回 UI 线程; 句柄已销毁时静默丢弃 (后台线程仍在识别的收尾阶段)</summary>
    private void OnServiceStatus(string text) => TryBeginInvoke(() =>
    {
        _serviceStatus = text;
        if (_ocrRunning)
        {
            Invalidate();
            return;
        }
        SetStatus(text);
    });

    /// <summary>构建工具栏: 缩放 / 全选 / 复制 / 导出 / 翻译 / 关闭</summary>
    private void BuildToolStrip()
    {
        ApplyToolStripTheme();
        BuildZoomMenu();

        Bind(_selectAll, "全选", "选择全部识别结果（Ctrl+A）", SelectAll);

        Bind(_copySelection, "复制选中", "复制选中文本（Ctrl+C）", () => CopyText(GetSelectedText()));
        _copySelection.Enabled = false;   // 没有选区时不可点: 与「翻译」同一套可用态规则

        Bind(_copyAll, "复制全部", "复制全部识别文本", () => CopyText(GetAllText()));

        BuildExportMenu();

        Bind(_translateButton,
            "翻译",
            "翻译选中或全部（单块贴在底部，多行开独立窗口；需联网）",
            TranslateCurrent);
        _translateButton.Enabled = false;

        Bind(_closeButton, "关闭", "关闭当前窗口", Close);

        // 「关闭」推到工具栏最右端: ToolStrip 默认是左对齐流式布局,
        // 把该项(及其前面的分隔线)标记为 Alignment.Right 后, 布局引擎会把它贴到右边缘,
        // 窗口变宽时它跟着走 —— 比手工塞一个空白弹簧可靠 (弹簧宽度需自己算, 易漂移)。
        _closeButton.Alignment = ToolStripItemAlignment.Right;

        var closeSep = new ToolStripSeparator { Alignment = ToolStripItemAlignment.Right };

        _toolStrip.Items.Add(_zoomMenu);
        _toolStrip.Items.Add(new ToolStripSeparator());
        _toolStrip.Items.Add(_selectAll);
        _toolStrip.Items.Add(_copySelection);
        _toolStrip.Items.Add(_copyMenu);
        _toolStrip.Items.Add(_translateButton);
        // 右侧组: 分隔线在最左, 因此按"右对齐项从右往左依次排布"的顺序加入,
        // 先加关闭、再加分隔线, 视觉上才是 […… ] | [关闭]
        _toolStrip.Items.Add(_closeButton);
        _toolStrip.Items.Add(closeSep);
    }

    /// <summary>工具栏的字体/配色/间距 (统一在这里改, 避免各按钮各调一次)</summary>
    private void ApplyToolStripTheme()
    {
        DarkToolStrip.Apply(_toolStrip, UiFontSize, new Padding(6, 2, 6, 2));
        _toolStrip.AutoSize = true;
    }

    /// <summary>缩放下拉: 一个按钮替代「- / 100% / + / 适应」四项, 给窗口最小宽度腾出 ~76px。</summary>
    private void BuildZoomMenu()
    {
        // 快捷键 (Ctrl+滚轮 / + / - / 0) 全部保留, 下拉只是把"当前比例"与"常用档位"收在一处。
        _zoomMenu.Text = "100%";
        _zoomMenu.ToolTipText = "缩放比例（Ctrl+滚轮 / + - / 0 适应窗口）";
        _zoomMenu.DropDownDirection = ToolStripDropDownDirection.BelowRight;
        foreach (int percent in new[] { 50, 75, 100, 150, 200, 400 })
        {
            int target = percent;
            var item = new ToolStripMenuItem($"{percent}%");
            item.Click += (_, _) => SetZoomPercent(target);
            _zoomMenu.DropDownItems.Add(item);
        }
        _zoomMenu.DropDownItems.Add(new ToolStripSeparator());
        var fitItem = new ToolStripMenuItem("适应窗口（0）");
        fitItem.Click += (_, _) => ResetZoom();
        _zoomMenu.DropDownItems.Add(fitItem);
    }

    /// <summary>「导出」下拉: 四种文本格式 + 保存图片 + 贴到屏幕</summary>
    private void BuildExportMenu()
    {
        // 5 种导出格式收进下拉: 原先一字排开 5 个按钮, 工具栏宽达 639px,
        // 小截图时窗口最小宽度被撑到 655px, 观感很挤。收进下拉后只占 1 个按钮位。
        // 下拉按钮不能也叫「复制」: 首项是「复制全部」, 两个"复制"并排容易误读
        _copyMenu.Text = "导出";
        _copyMenu.ToolTipText = "选择导出格式";
        _copyMenu.DropDownDirection = ToolStripDropDownDirection.BelowRight;

        Bind(_copyCsv, "表格（制表符）", "按坐标重建为表格，可直接粘贴到 Excel",
            () => CopyText(OcrText.ToCsv(CurrentLines(), '\t')));
        Bind(_copyCsvComma, "CSV（逗号）", "导出为逗号分隔 CSV 文本",
            () => CopyText(OcrText.ToCsv(CurrentLines(), ',')));
        // 去硬换行的连贯文本: 屏幕上换行多是排版, 粘进文档/聊天时不想要这些断行
        Bind(_copyPlain, "段落（去换行）", "合并为连贯段落（去掉排版造成的硬换行）",
            () => CopyText(OcrText.ToPlainText(CurrentLines())));
        Bind(_copyMarkdown, "Markdown", "导出为 Markdown（段落间空行分隔）",
            () => CopyText(OcrText.ToMarkdown(CurrentLines())));

        // 贴文字: 把识别结果变成桌面便签, 不用先复制再找记事本。
        // 放在导出下拉里而不是工具栏 —— 工具栏宽度已经吃紧, 加按钮会撑破小截图的窗口。
        Bind(_pinText, "贴到屏幕", "把识别文本贴为置顶便签（有选中则贴选中）", PinTextToScreen);

        // 保存原图也是"导出"的一种: 并进下拉后工具栏少一个常驻按钮 (小截图窗口因此更窄)
        Bind(_saveImageItem, "保存图片", "保存截图为 PNG", SaveImage);

        _copyMenu.DropDownItems.AddRange(
        [
            _copyAll,
            new ToolStripSeparator(),
            _copyCsv,
            _copyCsvComma,
            _copyPlain,
            _copyMarkdown,
            new ToolStripSeparator(),
            _saveImageItem,
            _pinText,
        ]);
    }

    /// <summary>按钮的文本/提示/点击三连赋值 (11 个按钮各写一遍迟早漏一项)</summary>
    private static void Bind(ToolStripItem item, string text, string tip, Action onClick)
    {
        item.Text = text;
        item.ToolTipText = tip;
        item.Click += (_, _) => onClick();
    }

    /// <summary>计算当前窗口内能容纳的最大缩放比例 (不超过 100%)</summary>
    private float ComputeFitScale()
    {
        float dpi = DpiFactor();
        float availW = Math.Max(100f, (ClientSize.Width - 4) * dpi);
        // 图片视口 = 客户区 − 工具栏 − 译文面板: 面板不能靠压图片来腾地方
        float availH = Math.Max(100f, (ClientSize.Height - _toolStrip.Height - _panelHeight - 4) * dpi);
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
        // 默认锚点 = 图片视口中心 (工具栏与译文面板之间的那块区域)
        Point anchor = anchorClient ?? new Point(
            ClientSize.Width / 2,
            _toolStrip.Height + Math.Max(0, ClientSize.Height - _toolStrip.Height - _panelHeight) / 2);
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
        float viewH = Math.Max(0, (ClientSize.Height - _toolStrip.Height - _panelHeight) * newDpi);
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
        // 高度要带上译文面板: 面板是图片之外的常驻区域, 不能被缩放"吃掉"
        int newH = Math.Max(MinimumSize.Height,
            (int)Math.Round(_src.Height * _scale / dpi) + _toolStrip.Height + _panelHeight + 8);
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

        // 用户手动缩放窗口时, 面板要按新宽度重新换行 (只重排高度, 不再改窗口尺寸,
        // 否则"改尺寸 → 重算面板 → 再改尺寸"会互相追)。我们自己为面板加高时用 _syncingPanel 跳过。
        if (!_syncingPanel && _barText.Length > 0)
            _panelHeight = DesiredPanelHeight();

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
        _zoomMenu.Text = $"{(int)Math.Round(_scale * 100)}%";
    }

    /// <summary>按百分比设置缩放 (下拉项); 夹紧到"适应窗口"下限与 8 倍上限</summary>
    private void SetZoomPercent(int percent)
    {
        _scale = Math.Clamp(percent / 100f, _minScale, 8f);
        FitWindowToImage();
        ClampPan();
        UpdateZoomLabel();
        Invalidate();
    }

    /// <summary>适应窗口 (= 下拉「适应窗口」与 0 键)</summary>
    private void ResetZoom()
    {
        _scale = _minScale;
        FitWindowToImage();
        ClampPan();
        UpdateZoomLabel();
        Invalidate();
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
        // 垂直居中于"工具栏与译文面板之间": 面板出现时图片会往上让, 而不是被压住
        float viewTop = _toolStrip.Height * dpi;
        float viewH = Math.Max(0f, (ClientSize.Height - _toolStrip.Height - _panelHeight) * dpi);
        float y = viewTop + (viewH - h) / 2f + _panOffset.Y;
        return new RectangleF(x, y, w, h);
    }

    /// <summary>图片可用视口 (物理像素): 客户区减去顶部工具栏与底部译文面板</summary>
    private SizeF ImageViewport()
    {
        float dpi = DpiFactor();
        return new SizeF(
            Math.Max(0f, ClientSize.Width * dpi),
            Math.Max(0f, (ClientSize.Height - _toolStrip.Height - _panelHeight) * dpi));
    }

    /// <summary>夹紧平移偏移: 图片边缘不能进入视口内侧, 小图保持居中</summary>
    private void ClampPan() => _panOffset = OcrPan.Clamp(
        _panOffset, new SizeF(_src.Width * _scale, _src.Height * _scale), ImageViewport());

    /// <summary>
    /// 原图坐标 → 客户区逻辑坐标 (缩放 + 居中偏移 + DPI 的唯一换算)。
    /// 手写第二份会在非 1:1 缩放时出现"框画对了、字符高亮错位"。
    /// </summary>
    private PointF MapImageToClient(PointF p, RectangleF ir, float dpi)
        => new((p.X * _scale + ir.X) / dpi, (p.Y * _scale + ir.Y) / dpi);

    // 4 点多边形 (原图坐标 -> 客户区坐标); 保留给命中/字符高亮与离线交互断言使用
    private PointF[] BoxToClientPoints(PaddleOcrDetectionBox box)
    {
        var ir = ImageRect();
        float dpi = DpiFactor();
        return [.. OcrBox.Corners(box).Select(p => MapImageToClient(p, ir, dpi))];
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
            _quadScratch[i] = MapImageToClient(imageCorners[i], ir, dpi);
        return _quadScratch;
    }

    // ===== 绘制 =====

    /// <summary>渲染原图 + 识别框叠加: 选中=蓝色填充, 部分选中=加深高亮, 悬停=虚线, 其余=淡蓝边框</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        float dpi = DpiFactor();
        var ir = ImageRect();
        PaintImage(g, ir, dpi);
        PaintLineOverlays(g, ir, dpi);

        DrawRecognitionState(g);
        DrawTranslateHint(g);
        DrawTranslationBar(g);
        DrawStatus(g);
    }

    /// <summary>画布底色、边框与底图 (缩小时用双三次插值, 1:1 附近直接贴图)</summary>
    private void PaintImage(Graphics g, RectangleF ir, float dpi)
    {
        // ImageRect 返回物理像素坐标, 绘制需换算回逻辑像素 (DPI 感知)
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
    }

    /// <summary>每行检测框与选中/悬停/低置信度高亮</summary>
    private void PaintLineOverlays(Graphics g, RectangleF ir, float dpi)
    {
        if (!_ocrDone)
            return;

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

    protected override void OnPaintBackground(PaintEventArgs e) { /* OnPaint 内已填充背景 */ }

    /// <summary>
    /// 识别过程的三态: 进行中 / 无文字 / 失败。
    ///
    /// 为什么不能只靠状态栏那行小字: 窗口一出现图片就是完整的, 右下角 12px 的"正在识别…"
    /// 很容易被理解成"已经识别完, 只是没框到字"。空结果与失败同样需要一个明确的落点。
    /// 三态都画在图片视口中央, 不占工具栏, 也不参与导出。
    /// </summary>
    private void DrawRecognitionState(Graphics g)
    {
        if (_ocrRunning)
        {
            // 引擎在加载模型/切档位时会把状态报上来, 直接作为胶囊的副标题, 不再占用状态栏
            DrawStatePill(g, "正在识别\u2026",
                _serviceStatus.Length > 0 ? _serviceStatus : null, StateKind.Busy);
            return;
        }
        if (_ocrError is not null)
        {
            DrawStatePill(g, "识别失败 · 点击重试", CompactError(_ocrError), StateKind.Error);
            return;
        }
        if (_ocrDone && _lines.Length == 0)
            DrawStatePill(g, "未识别到文字", "可尝试放大截图，或切换到高精度模型", StateKind.Empty);
    }

    private enum StateKind
    {
        Busy,
        Empty,
        Error,
    }

    /// <summary>状态胶囊; Error 时整块可点 (命中矩形记在 <see cref="_retryRect"/>)</summary>
    private void DrawStatePill(Graphics g, string title, string? subtitle, StateKind kind)
    {
        using var titleFont = UiFont.Create(12f);
        using var subFont = UiFont.Create(11f);

        Size titleSize = TextRenderer.MeasureText(title, titleFont,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        Size subSize = subtitle is null
            ? Size.Empty
            : TextRenderer.MeasureText(subtitle, subFont,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        int maxW = Math.Max(140, ClientSize.Width - 24);
        int width = Math.Min(maxW, Math.Max(titleSize.Width, subSize.Width) + 40);
        int height = subtitle is null ? 44 : 66;

        // 居中于"工具栏与译文面板之间"的图片视口
        int viewTop = _toolStrip.Height;
        int viewH = Math.Max(0, ClientSize.Height - _toolStrip.Height - _panelHeight);
        var box = new Rectangle((ClientSize.Width - width) / 2, viewTop + (viewH - height) / 2, width, height);
        _retryRect = kind == StateKind.Error ? box : Rectangle.Empty;

        using (var bg = new SolidBrush(UiTheme.Surface))
        using (var path = UiShapes.RoundedRect(box, 8))
        {
            g.FillPath(bg, path);
            using var edge = new Pen(
                kind == StateKind.Error ? UiTheme.Danger : Color.FromArgb(255, 58, 63, 71), 1f);
            g.DrawPath(edge, path);
        }

        Color titleColor = kind == StateKind.Error ? UiTheme.Danger : UiTheme.TextPrimary;
        int titleY = subtitle is null ? box.Y + (height - titleSize.Height) / 2 : box.Y + 11;
        TextRenderer.DrawText(g, title, titleFont,
            new Rectangle(box.X + 12, titleY, box.Width - 24, titleSize.Height + 2), titleColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        if (subtitle is not null)
        {
            TextRenderer.DrawText(g, subtitle, subFont,
                new Rectangle(box.X + 14, box.Y + 34, box.Width - 28, 18), UiTheme.MutedText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        if (kind != StateKind.Busy)
            return;

        // 不定长进度条: 只表达"还在动", 不假装知道进度百分比
        int trackW = Math.Min(104, box.Width - 40);
        int trackX = box.X + (box.Width - trackW) / 2;
        int trackY = box.Bottom - 13;
        using (var track = new SolidBrush(Color.FromArgb(38, 255, 255, 255)))
            g.FillRectangle(track, trackX, trackY, trackW, 3);
        const int segW = 26;
        int segX = trackX + _statePhase * (trackW - segW) / 3;
        using var seg = new SolidBrush(UiTheme.Accent);
        g.FillRectangle(seg, segX, trackY, segW, 3);
    }

    /// <summary>错误信息只取首行并截断: 界面上不该出现多行异常栈</summary>
    private static string CompactError(string error)
    {
        int cut = error.IndexOfAny(['\r', '\n']);
        string line = (cut >= 0 ? error[..cut] : error).Trim();
        return line.Length <= 46 ? line : line[..46] + "\u2026";
    }

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
        using var font = UiFont.Create(11f);
        Size ts = TextRenderer.MeasureText(_statusText, font, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        // 译文面板在图片之外: 状态栏抬到面板上方 (仍落在图片区内), 两者不会叠在一起
        int bottomInset = _panelHeight > 0 ? _panelHeight + 8 : 0;
        var (box, textRect) = ComputeStatusLayout(ts, ClientSize, bottomInset);

        // 圆角与浮层同一档 (8): 早先是直角黑块, 与工具栏/徽标/胶囊的圆角语言不一致
        using (var bg = new SolidBrush(UiTheme.StatusSurface))
        using (var path = UiShapes.RoundedRect(box, 8))
            g.FillPath(bg, path);
        TextRenderer.DrawText(g, _statusText, font, textRect, UiTheme.TextPrimary,
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

    private static readonly Color BarTextColor = UiTheme.TextPrimary;
    private static readonly Color BarErrorColor = Color.FromArgb(255, 196, 120);

    /// <summary>译文条上的复制按钮文案; 测量与绘制共用, 改文案时宽度自动跟上</summary>
    private const string CopyBarLabel = "复制译文";

    /// <summary>
    /// 量出译文面板布局。绘制、命中、状态栏避让都从这里取, 不各自算一份。
    /// 面板高度由 <see cref="_panelHeight"/> 决定 (已按内容算好), 这里只负责切成矩形。
    /// </summary>
    private TranslationBarLayout.Layout MeasureTranslationBar()
    {
        var (title, copy, body) = MeasurePanelContent();
        return TranslationBarLayout.Compute(ClientSize, _panelHeight, title, copy, body);
    }

    /// <summary>
    /// 量标题 / 按钮 / 正文。
    /// 正文必须按"扣掉内边距后的实际宽度"换行: 按 int.MaxValue 量会得到一行, 高度就永远不增高。
    /// </summary>
    private (Size Title, Size Copy, Size Body) MeasurePanelContent()
    {
        using var titleFont = UiFont.Create(12f);
        using var bodyFont = UiFont.Create(13f);
        Size title = TextRenderer.MeasureText(_barTitle, titleFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        // 按钮文字必须写明"复制的是译文": 原文已在单击时进了剪贴板,
        // 两个"复制"看起来一样才让人以为冲突。
        Size copy = TextRenderer.MeasureText(CopyBarLabel, titleFont, new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        Size body = TextRenderer.MeasureText(_barText, bodyFont,
            new Size(TranslationBarLayout.ContentWidth(ClientSize.Width), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return (title, copy, body);
    }

    /// <summary>按内容算出的面板高度; 无译文时为 0, 超长时封顶 (正文会被裁切, 可复制)</summary>
    private int DesiredPanelHeight()
    {
        if (_barText.Length == 0)
            return 0;

        var (title, _, body) = MeasurePanelContent();
        int max = TranslationBarLayout.MaxPanelHeight(ClientSize.Height, _toolStrip.Height);
        int want = TranslationBarLayout.DesiredHeight(title, body);
        return Math.Clamp(want, Math.Min(TranslationBarLayout.MinHeight, max), max);
    }

    /// <summary>
    /// 让面板高度跟上内容, 并把窗口高度一起加减。
    ///
    /// 面板出现/变高 → 窗口向下长 (底边贴屏时先把窗口上移, 实在放不下才让图片视口变矮);
    /// 收起 → 减回当初为面板加的那部分。用户看到的窗口尺寸因此是可逆的。
    /// </summary>
    private void SyncPanelHeight()
    {
        int target = DesiredPanelHeight();
        int delta = target - _panelHeight;
        if (delta == 0)
            return;

        _panelHeight = target;

        if (delta > 0)
        {
            int room = PanelGrowthRoom();
            if (room < delta)
            {
                var wa = Screen.FromControl(this).WorkingArea;
                int shift = Math.Min(delta - room, Math.Max(0, Top - wa.Top));
                if (shift > 0)
                {
                    Top -= shift;
                    room += shift;
                }
            }

            int add = Math.Min(delta, room);
            if (add > 0)
            {
                _panelGrowth += add;
                ResizeForPanel(Height + add);
            }
        }
        else
        {
            // 只减回"当初为了面板加的那部分", 不吞用户自己拉大的尺寸
            int shrink = Math.Min(-delta, _panelGrowth);
            if (shrink > 0)
            {
                _panelGrowth -= shrink;
                ResizeForPanel(Math.Max(MinimumSize.Height, Height - shrink));
            }
        }

        Invalidate();
    }

    /// <summary>改窗口高度; 期间不让 OnClientSizeChanged 反过来重算面板高度</summary>
    private void ResizeForPanel(int height)
    {
        _syncingPanel = true;
        try
        {
            Size = new Size(Width, height);
        }
        finally
        {
            _syncingPanel = false;
        }
    }

    /// <summary>窗口下方还能长多高 (受所在屏工作区底边限制)</summary>
    private int PanelGrowthRoom()
    {
        var wa = Screen.FromControl(this).WorkingArea;
        return Math.Max(0, wa.Bottom - (Top + Height));
    }


    /// <summary>
    /// 画窗口底部的译文面板。
    ///
    /// 它是图片<b>之外</b>的一块区域 (图片视口已经扣掉面板高度), 所以这里可以放心铺满整宽:
    /// 顶部一条分隔线 + 略亮于画布的底色, 读起来是"窗口的下半部分", 而不是浮在截图上的条。
    /// </summary>
    private void DrawTranslationBar(Graphics g)
    {
        if (_panelHeight <= 0 || _barText.Length == 0)
            return;

        var lay = MeasureTranslationBar();

        using (var bg = new SolidBrush(UiTheme.Surface))
            g.FillRectangle(bg, lay.Box);
        // 与图片区之间的分隔线: 1px 足够, 深色主题下更细反而糊
        using (var edge = new Pen(Color.FromArgb(255, 48, 53, 60), 1f))
            g.DrawLine(edge, lay.Box.Left, lay.Box.Top, lay.Box.Right, lay.Box.Top);
        // 左侧强调条: 与深色底形成边界, 不额外引入新配色
        using (var strip = new SolidBrush(UiTheme.Accent))
            g.FillRectangle(strip, lay.Box.X, lay.Box.Y, 3, lay.Box.Height);

        using var titleFont = UiFont.Create(12f);
        using var bodyFont = UiFont.Create(13f);
        TextRenderer.DrawText(g, _barTitle, titleFont, lay.Title, UiTheme.MutedText,
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        // 「复制译文」: 拿到可复制内容时才点亮; 加载中/失败态置灰, 避免复制走错误文字
        bool canCopy = CanCopyBarTranslation;
        if (canCopy && _barCopyHover)
        {
            using var hover = new SolidBrush(Color.FromArgb(38, UiTheme.Accent));
            g.FillRectangle(hover, lay.Copy);
        }
        Color copyColor = !canCopy
            ? Color.FromArgb(110, UiTheme.MutedText)
            : _barCopyHover ? UiTheme.Accent : Color.FromArgb(210, 220, 232);
        TextRenderer.DrawText(g, CopyBarLabel, titleFont, lay.Copy, copyColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        // 正文高度被面板夹紧, 超出的部分必须裁掉: 否则长译文会压到窗口边缘
        GraphicsState state = g.Save();
        g.SetClip(lay.Text);
        TextRenderer.DrawText(g, _barText, bodyFont, lay.Text,
            _barError ? BarErrorColor : BarTextColor,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        g.Restore(state);

        // 关闭图标用矢量线而不是字体字形: 缺字体时字形会画出 0 像素且不报错 (工具栏图标踩过)
        using var closePen = new Pen(_barCloseHover ? UiTheme.Accent : UiTheme.MutedText, 1.6f);
        const int pad = 5;
        g.DrawLine(closePen, lay.Close.Left + pad, lay.Close.Top + pad,
            lay.Close.Right - pad, lay.Close.Bottom - pad);
        g.DrawLine(closePen, lay.Close.Right - pad, lay.Close.Top + pad,
            lay.Close.Left + pad, lay.Close.Bottom - pad);
    }

    /// <summary>只有成功拿到译文时才允许复制; 加载中/失败态的"复制"按钮置灰</summary>
    private bool CanCopyBarTranslation => _barText.Length > 0 && !_barError && !_barPending;

    private void SetTranslationBar(string title, string text, bool error, bool echo = false)
    {
        _barTitle = title;
        _barText = text;
        _barError = error;
        _barEcho = echo;
        _barCopyHover = false;
        _barCloseHover = false;
        // 内容变了 (例如"正在翻译…" → 多行译文) 面板高度也要跟着变, 窗口一起加高
        SyncPanelHeight();
        Invalidate();
    }

    private void HideTranslationBar()
    {
        if (_barText.Length == 0 && _panelHeight == 0)
            return;
        _barTitle = "";
        _barText = "";
        _barError = false;
        _barEcho = false;
        _barLine = -1;
        _barPending = false;
        _barCopyHover = false;
        _barCloseHover = false;
        // 用户主动收起时取消进行中的请求: 否则结果回来后又会把面板弹出来
        _clickTranslateCts?.Cancel();
        SyncPanelHeight(); // 高度归零并把窗口收回原尺寸
        Invalidate();
    }

    /// <summary>
    /// 译文面板命中: 关闭图标与「复制译文」优先于整块拖动。
    /// 复制按钮在不可复制时退化为普通面板区域 (拖动窗口), 不会静默复制错误内容。
    /// </summary>
    private TranslationBarHit HitTranslationBar(Point clientPoint)
    {
        if (_barText.Length == 0 || _panelHeight <= 0)
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

    // ===== 悬停「译」按钮 (翻译的独立入口) =====

    /// <summary>按钮底色: 比画布亮一档, 不抢识读, 又能在深色底上看出边界</summary>
    private static readonly Color HintSurface = Color.FromArgb(238, 30, 33, 38);

    /// <summary>
    /// 指定检测行的「译」按钮位置 (逻辑像素); 行无效/无文本/未识别完成时返回 null。
    /// 布局全部交给 <see cref="TranslateHoverButton"/>, 绘制与命中取同一份结果。
    /// </summary>
    private TranslateHoverButton.Placement? TranslatePlacement(int line)
    {
        if (!_ocrDone || line < 0 || line >= _lines.Length || string.IsNullOrEmpty(_lines[line].Text))
            return null;

        EnsureLineCorners();
        var ir = ImageRect();
        float dpi = DpiFactor();
        // MapCorners 复用同一个 4 点缓冲: 必须立刻取包围盒, 不要把返回值留到后面
        var pts = MapCorners(_lineCorners[line], ir, dpi);
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        for (int i = 0; i < 4; i++)
        {
            l = Math.Min(l, pts[i].X);
            t = Math.Min(t, pts[i].Y);
            r = Math.Max(r, pts[i].X);
            b = Math.Max(b, pts[i].Y);
        }

        // 译文面板可见时按钮让开它, 否则会画在面板上并被面板的命中抢先吃掉
        int bottomLimit = ClientSize.Height - _panelHeight;
        return TranslateHoverButton.Compute(
            RectangleF.FromLTRB(l, t, r, b), ClientSize, _toolStrip.Height, bottomLimit);
    }

    /// <summary>画悬停「译」: 只在指针所在块旁边出现, 移开即消失, 不占任何常驻空间</summary>
    private void DrawTranslateHint(Graphics g)
    {
        if (TranslatePlacement(_hover) is not { } pl)
            return;

        var box = pl.Button;
        using (var back = new SolidBrush(_translateHover ? UiTheme.Accent : HintSurface))
            g.FillEllipse(back, box);
        using (var pen = new Pen(Color.FromArgb(220, UiTheme.Accent), 1.2f))
            g.DrawEllipse(pen, box);

        using var font = UiFont.Create(12f);
        TextRenderer.DrawText(g, "译", font, box,
            _translateHover ? Color.White : Color.FromArgb(232, 236, 242),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
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
        // 插值点先算原图坐标再整体走 MapImageToClient; 旧写法只对偏移乘 scale, 非 1:1 时错位
        var (topA, topB) = OcrBox.TopEdge(b);
        var (botA, botB) = OcrBox.BottomEdge(b);
        PointF Lerp(PointF a, PointF c, float u) =>
            new(a.X + (c.X - a.X) * u, a.Y + (c.Y - a.Y) * u);
        return
        [
            MapImageToClient(Lerp(topA, topB, uA), ir, dpi),
            MapImageToClient(Lerp(topA, topB, uB), ir, dpi),
            MapImageToClient(Lerp(botA, botB, uB), ir, dpi),
            MapImageToClient(Lerp(botA, botB, uA), ir, dpi),
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

        var quads = new PointF[b - a][];
        for (int i = a; i < b; i++)
        {
            var box = glyph.Boxes[i];
            quads[i - a] =
            [
                MapImageToClient(new PointF(box.X1, box.Y1), ir, dpi),
                MapImageToClient(new PointF(box.X2, box.Y2), ir, dpi),
                MapImageToClient(new PointF(box.X3, box.Y3), ir, dpi),
                MapImageToClient(new PointF(box.X4, box.Y4), ir, dpi),
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
        UpdateIdleStatus();   // 同步「复制选中」的可用态
    }

    /// <summary>左键按下: 空白处=清除选中/拖动窗口, 文本块上=Shift 多选 或 进入字符级拖选</summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (TryBeginPan(e))
            return;

        // 右键菜单在松手时才弹 (见 OnMouseUp): 按下即弹会让这次松手落到菜单上,
        // 相当于直接选中了指针下的第一项。
        if (e.Button != MouseButtons.Left)
            return;

        // 失败态: 整块胶囊都是「点击重试」
        if (TryHandleRetryClick(e))
            return;

        _hintPressLine = -1; // 新的按下作废上一次未完成的「译」点击

        // 译文条盖在图片上方: 命中必须先于文字块, 否则点关闭/复制会选中它背后的文字
        if (HandleTranslationBarPress(e))
            return;

        int hit = HitTest(e.Location);

        if (TryBeginTranslatePress(hit, e.Location))
            return;

        HandleBlockPress(hit, e);
    }

    /// <summary>中键拖动, 或按住空格 + 左键: 平移放大后的图片, 不进入选词/拖窗口逻辑</summary>
    private bool TryBeginPan(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Middle && !(e.Button == MouseButtons.Left && _spaceHeld))
            return false;

        if (OcrPan.CanPan(new SizeF(_src.Width * _scale, _src.Height * _scale), ImageViewport()))
        {
            _panning = true;
            _panAnchorClient = e.Location;
            _panAnchorOffset = _panOffset;
            Cursor = Cursors.SizeAll;
        }
        return true;
    }

    /// <summary>失败胶囊整块可点: 点一下就重新识别</summary>
    private bool TryHandleRetryClick(MouseEventArgs e)
    {
        if (_ocrError is null || !_retryRect.Contains(e.Location))
            return false;
        StartOcr();
        return true;
    }

    /// <summary>译文条三态命中 (关闭 / 复制 / 条内拖动); 未命中返回 false 继续走文字块逻辑</summary>
    private bool HandleTranslationBarPress(MouseEventArgs e)
    {
        switch (HitTranslationBar(e.Location))
        {
            case TranslationBarHit.Close:
                HideTranslationBar();
                UpdateIdleStatus();
                return true;
            case TranslationBarHit.Copy:
                CopyText(_barText);
                return true;
            case TranslationBarHit.Body:
                // 条内其它位置按住可拖动窗口, 与点空白处一致
                BeginWindowDrag();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 「译」按钮必须先于文字块命中: 它可能压在块的边角上, 点它不能变成选词/拖选。
    /// 指针在按钮粘滞区里时 HitTest 已返回 -1, 这里用 _hover 兜住。
    /// </summary>
    private bool TryBeginTranslatePress(int hit, Point location)
    {
        int hintLine = hit >= 0 ? hit : _hover;
        if (hintLine < 0 || TranslatePlacement(hintLine) is not { } hint || !hint.Hover.Contains(location))
            return false;

        // 只记下意图, 松手时才真正翻译 (理由见 _hintPressLine)
        _hintPressLine = hintLine;
        return true;
    }

    /// <summary>文字块上的按下: 双击选词 / 空白拖动 / Shift 多选 / 字符拖选</summary>
    private void HandleBlockPress(int hit, MouseEventArgs e)
    {
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
            BeginWindowDrag();
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

    /// <summary>开始一次窗口拖动会话 (空白处 / 译文条空白区共用一个入口)</summary>
    private void BeginWindowDrag()
    {
        _draggingMove = true;
        _dragStartScreen = Cursor.Position;
        _dragStartLoc = Location;
    }

    /// <summary>鼠标移动: 拖动窗口 / 字符级拖选实时更新选区 / hover 提示</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 顺序即优先级: 会话中的拖动/框选必须先于 hover 处理
        if (HandleRetryHover(e)) return;
        if (HandlePanMove(e)) return;
        if (HandleWindowDragMove(e)) return;
        if (HandleCharDragMove(e)) return;
        if (HandleTranslationBarHover(e)) return;
        UpdateTextHover(e);
    }

    /// <summary>失败胶囊整块可点: 先给出"可点"的手型光标, 不再走 hover/拖动分支</summary>
    private bool HandleRetryHover(MouseEventArgs e)
    {
        if (_ocrError is null || !_retryRect.Contains(e.Location))
            return false;
        Cursor = Cursors.Hand;
        return true;
    }

    /// <summary>中键/空格平移会话; 任意非左/中键按下都结束会话</summary>
    private bool HandlePanMove(MouseEventArgs e)
    {
        if (!_panning)
            return false;

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
        return true;
    }

    /// <summary>拖动窗口 (空白处按下); 用绝对屏幕坐标避免客户端基准漂移</summary>
    private bool HandleWindowDragMove(MouseEventArgs e)
    {
        if (!_draggingMove)
            return false;

        if (e.Button == MouseButtons.Left)
        {
            Location = new Point(_dragStartLoc.X + Cursor.Position.X - _dragStartScreen.X,
                                _dragStartLoc.Y + Cursor.Position.Y - _dragStartScreen.Y);
            // 拖动中不做 hover 检测, 减少重绘
        }
        else
        {
            _draggingMove = false;
        }
        return true;
    }

    /// <summary>字符级拖选; 拖出文本块时保留最后一次高亮并复位会话</summary>
    private bool HandleCharDragMove(MouseEventArgs e)
    {
        if (!_charDrag)
            return false;

        if (e.Button != MouseButtons.Left)
        {
            _charDrag = false;
            return true;
        }

        if (_dragLine >= 0)
        {
            if (!IsInBox(_lines[_dragLine].Box,
                    (e.X * DpiFactor() - ImageRect().X) / _scale,
                    (e.Y * DpiFactor() - ImageRect().Y) / _scale))
            {
                // 拖出文本块: 保留最后一次 _partial 作为定格高亮, 但复位拖选状态,
                // 否则行号与"已移动"标记会留到下一次点击, 让 MouseUp 误判。
                _charDrag = false;
                _dragLine = -1;
                _charDragMoved = false;
                return true;
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
        return true;
    }

    /// <summary>译文条 hover: 覆盖下的文字块不参与 hover, 否则会误报背后的识别文本</summary>
    private bool HandleTranslationBarHover(MouseEventArgs e)
    {
        TranslationBarHit barHit = HitTranslationBar(e.Location);
        if (barHit != TranslationBarHit.None)
        {
            bool copyHover = barHit == TranslationBarHit.Copy;
            bool closeHover = barHit == TranslationBarHit.Close;
            if (_barCopyHover != copyHover || _barCloseHover != closeHover)
            {
                _barCopyHover = copyHover;
                _barCloseHover = closeHover;
                Invalidate();
            }
            Cursor = barHit is TranslationBarHit.Copy or TranslationBarHit.Close
                ? Cursors.Hand
                : Cursors.Default;
            if (_hover != -1)
            {
                _hover = -1;
                _translateHover = false;
                Invalidate();
            }
            return true;
        }

        if (_barCopyHover || _barCloseHover)
        {
            _barCopyHover = false;
            _barCloseHover = false;
            Invalidate();
        }
        return false;
    }

    /// <summary>文本块 hover 与「译」按钮粘滞/高亮</summary>
    private void UpdateTextHover(MouseEventArgs e)
    {
        int hit = HitTest(e.Location);
        // 指针移到「译」按钮上时, 块本身已不在指针下: 用按钮的粘滞区把行号留住,
        // 否则按钮会在指针刚碰到的瞬间消失 (绘制与命中都依赖 hover 行), 永远点不到。
        if (hit < 0 && TranslatePlacement(_hover) is { } sticky && sticky.Hover.Contains(e.Location))
            hit = _hover;

        var placement = TranslatePlacement(hit);
        bool onButton = placement?.Button.Contains(e.Location) == true;

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
        else if (onButton)
        {
            // 同一块内移到按钮上: 状态栏要说明这个圆钮是干什么的, 否则它只是块边一个奇怪的蓝点
            SetStatus("点击「译」翻译此块（不改剪贴板）");
        }

        if (onButton != _translateHover)
        {
            _translateHover = onButton;
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
        _hintPressLine = -1;
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

        // 右键菜单: 这是结果窗口里唯一没被占用的手势, 给"不想追着悬停按钮跑"的用户
        // 一条固定路径。放在松手而不是按下, 否则这次松手会被菜单吃掉。
        if (e.Button == MouseButtons.Right)
        {
            // 译文条自己已经带有复制/关闭, 右键不叠菜单 (否则条上会浮出"翻译全部"这种无关项)
            if (HitTranslationBar(e.Location) == TranslationBarHit.None)
                ShowResultMenu(e.Location);
            return;
        }

        // 「译」按钮: 按下只记录, 松手且仍在按钮粘滞区内才翻译 (移开等于取消)
        if (e.Button == MouseButtons.Left && _hintPressLine >= 0)
        {
            int hintLine = _hintPressLine;
            _hintPressLine = -1;
            _draggingMove = false;
            if (TranslatePlacement(hintLine) is { } pl && pl.Hover.Contains(e.Location))
                TranslateBlock(hintLine);
            return;
        }

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

            // 单击只做一件事: 选中并复制。翻译不再占用这个手势 ——
            // 它有自己的入口 (悬停「译」按钮 / 右键菜单 / T 键), 二者不再互相顶掉。
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
            // 翻译不再占用单击: T 是它自己的键 (单块贴底部条, 多行/全部开独立窗口)
            case Keys.T:
            case Keys.Control | Keys.T:
                TranslateCurrent();
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
                ResetZoom();
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

    /// <summary>
    /// 把识别文本贴成桌面便签 (文字贴图)。
    /// 有选中贴选中, 无选中贴全部 —— 与「翻译」按钮的选择语义保持一致。
    /// 字体所有权转移给贴图窗口, 这里不要再释放。
    /// </summary>
    private void PinTextToScreen()
    {
        string text = GetSelectedText();
        if (string.IsNullOrWhiteSpace(text))
            text = GetAllText();
        if (string.IsNullOrWhiteSpace(text))
            return;

        // 贴在本窗口左上方一点, 关掉结果窗口后便签仍在原位附近
        var location = new Point(Math.Max(0, Left + 40), Math.Max(0, Top + 40));
        new PinnedTextForm(text.Trim(), UiFont.Create(13f), location).Show();
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
        // 「复制选中」的可用态只在这里维护: 能不能点必须与"有没有选区"一致,
        // 不能可点、点了才在状态栏说"没有可复制的文本"。
        _copySelection.Enabled = _ocrDone && HasSelection();

        if (!_ocrDone)
            return;
        if (_lines.Length == 0)
        {
            // 空结果由画布中央的胶囊说明, 状态栏不再罗列"0 个文本块…"这种没有信息量的句子
            SetStatus(string.Empty);
            return;
        }
        if (HasSelection())
            SetStatus($"已选 {SelectedCharCount()} 字符 · Ctrl+C 复制");
        else
        {
            // 只有放大到超出视口时才提示平移, 平时不占用状态栏
            string panHint = _scale > _minScale + 0.001f ? " · 中键平移" : string.Empty;
            // 常驻只留信息量最高的"几块 + 单击行为"; 其余操作提示在工具栏 tooltip 里,
            // 不再用一整句"悬停译 / Shift 多选 / Ctrl+滚轮"长期压住截图底部。
            string clickHint = SettingsStore.Current.ClickToCopy ? "单击复制" : "单击选中";
            SetStatus($"{_lines.Length} 块 · {clickHint}" + panHint);
        }
    }

    /// <summary>
    /// 翻译的默认入口 (工具栏「翻译」/ T 键 / 右键「翻译」)。
    ///
    /// 呈现规则可感知: <b>恰好选中一个整块</b>时贴在图片下方 (轻量看译), 多块/部分选中/无选中
    /// 时开独立译文窗口 (读长文、复制全文)。单块不去开窗, 是因为看一行译文不需要多一个窗口。
    /// </summary>
    private void TranslateCurrent()
    {
        if (!_ocrDone)
            return;

        if (_selected.Count == 1 && _partial is null)
        {
            TranslateLine(_selected.First());
            return;
        }
        TranslateSelectionOrAll();
    }

    /// <summary>
    /// 翻译指定块 (悬停「译」/ 右键「翻译此块」)。选中它只为给出视觉上下文,
    /// 不写剪贴板 —— 翻译是"看一眼", 不该顺手改掉用户的剪贴板。
    /// </summary>
    private void TranslateBlock(int line)
    {
        if (!_ocrDone || line < 0 || line >= _lines.Length)
            return;

        ClearSelection();
        _selected.Add(line);
        Invalidate();
        TranslateLine(line);
    }

    /// <summary>
    /// 右键菜单: 结果窗口里唯一没被占用的手势, 给"不想追着悬停按钮跑"的用户一条固定路径。
    /// 单击已经固定为"选中 + 复制", 所以翻译必须有这样独立的显式入口。
    /// </summary>
    private void ShowResultMenu(Point client)
    {
        if (!_ocrDone)
            return;

        int hit = HitTest(client);
        var menu = new ContextMenuStrip
        {
            Font = _toolStrip.Font,
            ShowImageMargin = false,
        };

        if (hit >= 0)
        {
            // 右键即选中落点所在块 (只选中不复制): 菜单动作作用于"看到的这一块"
            ClearSelection();
            _selected.Add(hit);
            Invalidate();

            menu.Items.Add("复制此块", null, (_, _) => CopyText(_lines[hit].Text));
            menu.Items.Add("翻译此块", null, (_, _) => TranslateBlock(hit));
            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add("翻译选中/全部", null, (_, _) => TranslateCurrent());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("全选", null, (_, _) => SelectAll());
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(this, client);
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
                $"翻译需要联网：本次会把识别出的文字发送到 DeepSeek（{TranslateClient.Host}）进行翻译，不会上传截图。\n\n" +
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
    /// 翻译单个检测块, 结果贴在图片下方的面板 (悬停「译」/ 右键「翻译此块」/ 单块时按 T)。
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
        // 失败后允许重点重试; "疑似未翻译"也允许 —— 状态栏正提示用户再点一次。
        if (_barLine == line && (_barPending || (!_barError && !_barEcho)))
        {
            SetStatus(_barPending ? "正在翻译\u2026" : "译文已显示在图片下方");
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
                // 模型把原文原样吐回来时如实标注: 标题加后缀, 状态栏给出重试提示。
                // 静默显示英文会被当成"翻译功能坏了", 而且用户不知道该不该再点一次。
                SetTranslationBar(result.Echo ? title + " · 疑似未翻译" : title, result.Text,
                    error: false, echo: result.Echo);
                SetStatus(result.Echo
                    ? "译文可能仍是原文 · 再点一次「译」重试"
                    : "翻译完成 · 译文显示在图片下方");
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
                AtomicFile.SaveImage(_src, dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
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

        // 重试时先把上一轮结果与三态清干净: 新结果回来之前, 画面不能还显示旧框或旧失败态
        ResetOcrVisualState();

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
                    ApplyOcrSuccess(result.Lines);
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                this.TryBeginInvoke(() => ApplyOcrFailure(ex.Message));
            }
        });
    }

    /// <summary>识别开始前的状态复位 (重试入口共用): 三态、旧框、选区、可用态一起清</summary>
    private void ResetOcrVisualState()
    {
        _ocrRunning = true;
        _ocrError = null;
        _ocrDone = false;
        _serviceStatus = string.Empty;
        _lines = [];
        _lineCorners = [];
        _lineCornersSource = null;
        _lineGlyphs = [];
        _lineCumW = [];
        _lineRank = [];
        _retryRect = Rectangle.Empty;
        ClearSelection();
        _translateButton.Enabled = false;
        SetStatus("拖动空白处移动窗口");
        _stateTimer.Start();
        Invalidate();
    }

    /// <summary>识别成功: 接上新结果并恢复交互 (必须在 UI 线程调用)</summary>
    private void ApplyOcrSuccess(PaddleOcrLine[] lines)
    {
        _lines = lines;
        _wordOrder = SettingsStore.Current.SortReadingOrder;
        ClearSelection();
        _lineCumW = BuildCharWeights(_lines);
        _lineGlyphs = OcrGlyphLayout.BuildAll(_lines);
        _lineRank = OcrSelection.BuildReadingRanks(_lines, _wordOrder);
        _hover = -1;
        _ocrRunning = false;
        _stateTimer.Stop();
        _ocrError = null;
        _ocrDone = true;
        _translateButton.Enabled = true;
        _minScale = ComputeFitScale();
        if (_scale < _minScale) _scale = _minScale;
        UpdateZoomLabel();
        UpdateIdleStatus();
        Invalidate();
    }

    /// <summary>识别失败: 进入可重试的失败态 (必须在 UI 线程调用)</summary>
    private void ApplyOcrFailure(string message)
    {
        _ocrRunning = false;
        _stateTimer.Stop();
        _ocrError = message;
        _ocrDone = true;
        _lines = [];
        _translateButton.Enabled = false;
        SetStatus(string.Empty);   // 失败信息由中央胶囊承载, 状态栏不再重复
        Invalidate();
    }

    /// <summary>取消 OCR 与单击翻译任务、退订状态事件并释放位图</summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        OcrService.Instance.StatusChanged -= OnServiceStatus;
        _cts?.Cancel();
        _cts?.Dispose();
        _stateTimer.Stop();
        _stateTimer.Dispose();
        // 只取消不释放: 翻译任务的 finally 负责释放, 这里提前 Dispose 会和它竞争
        _clickTranslateCts?.Cancel();
        _src.Dispose();
        base.OnFormClosed(e);
    }
}
