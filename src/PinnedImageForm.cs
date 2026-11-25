using System.Drawing.Drawing2D;

namespace SnipasteOcr;

/// <summary>
/// 贴图窗口 (仿 Snipaste 的「贴图」): 把截图连同标注钉在屏幕上, 无边框、始终置顶。
///
/// 交互:
/// - 左键拖动移动位置;
/// - 滚轮缩放, <b>Ctrl+滚轮</b> 调透明度, 双击复位到 1:1;
/// - 右上角「×」关闭 (常驻, 鼠标移上去高亮); 右键同样关闭。
///
/// 为什么用 <c>WM_NCLBUTTONDOWN + HTCAPTION</c> 拖动而不是自己算鼠标偏移:
/// 自己实现要处理"鼠标移出窗口""按下后窗口被其它窗口遮挡""高 DPI 下坐标换算"等一堆边界,
/// 交给系统的标题栏拖拽逻辑则这些全部自动正确 (它内部用的是屏幕坐标与鼠标捕获)。
/// 这也是无边框窗口做拖动的标准做法 —— 除拖动外不引入任何窗口边框。
///
/// 窗口语义 (置顶/不激活/DPI 落尺寸) 在 <see cref="PinnedWindowBase"/> 里, 这里只负责"画一张图 + 缩放 + 关闭按钮"。
/// </summary>
public sealed class PinnedImageForm : PinnedWindowBase
{
    private const int CloseButtonSize = 18;
    private const int CloseButtonInset = 6;

    private readonly Bitmap _image;
    private float _zoom = 1f;

    /// <summary>指针是否正压在关闭按钮上 (只影响按钮高亮, 不改变可见性)</summary>
    private bool _closeHover;

    /// <summary>构造贴图窗口; 图像所有权转移给本窗口 (关闭时释放)</summary>
    /// <param name="image">已合成标注的成品图 (物理像素)</param>
    /// <param name="location">窗口左上角 (屏幕坐标)</param>
    public PinnedImageForm(Bitmap image, Point location)
    {
        _image = image;

        // 构造阶段先按 96 DPI 占位; 真实 DPI 在 OnLoad / OnDpiChanged 里按窗口所在屏落一次
        ClientSize = new Size(Math.Max(1, image.Width), Math.Max(1, image.Height));
        Location = location;
        Cursor = Cursors.SizeAll;

        PinnedRegistry.Register(this);
    }

    /// <summary>关闭按钮的命中矩形 (右上角, 逻辑像素)</summary>
    private Rectangle CloseButtonRect => new(
        Math.Max(2, ClientSize.Width - CloseButtonInset - CloseButtonSize),
        CloseButtonInset,
        CloseButtonSize,
        CloseButtonSize);

    /// <summary>当前缩放比例 (1 = 原始像素)</summary>
    public float Zoom => _zoom;

    protected override void RefreshPinnedSize()
        => ApplyImageSize(_image.Width, _image.Height, _zoom);

    /// <summary>悬停: 只更新关闭按钮的高亮 (整图重绘很贵, 只刷按钮附近)</summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        bool closeHover = CloseButtonRect.Contains(e.Location);
        if (closeHover == _closeHover)
            return;

        _closeHover = closeHover;
        Cursor = closeHover ? Cursors.Hand : Cursors.SizeAll;
        var rect = CloseButtonRect;
        rect.Inflate(3, 3);
        Invalidate(rect);
    }

    /// <summary>离开窗口: 复位关闭按钮的高亮与光标</summary>
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_closeHover)
            return;
        _closeHover = false;
        Cursor = Cursors.SizeAll;
        var rect = CloseButtonRect;
        rect.Inflate(3, 3);
        Invalidate(rect);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        // 关闭按钮必须先于拖动拦截: 否则点「×」会变成"开始拖窗口"。
        // 命中是逻辑像素, 与绘制同一个矩形, 不存在"看得见点不到"。
        if (e.Button == MouseButtons.Left && CloseButtonRect.Contains(e.Location))
        {
            Close();
            return;
        }
        base.OnMouseDown(e);   // 左键交给系统拖动, 右键关闭 (见基类)
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (e.Delta == 0)
            return;

        // Ctrl+滚轮 = 透明度 (与常见看图工具一致); 普通滚轮 = 缩放
        if ((ModifierKeys & Keys.Control) != 0)
        {
            Opacity = PinnedImageLayout.StepOpacity(Opacity, Math.Sign(e.Delta));
            return;
        }

        float next = PinnedImageLayout.StepZoom(_zoom, e.Delta);
        if (Math.Abs(next - _zoom) < 0.0001f)
            return;

        // 以光标为锚点缩放: 只改尺寸不动位置会让内容从左上角"长出去", 看起来像画面在跑
        Point cursorScreen = PointToScreen(e.Location);
        float previous = _zoom;
        _zoom = next;
        RefreshPinnedSize();
        Location = PinnedImageLayout.AnchorLocation(Location, cursorScreen, previous, _zoom);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left)
            return;

        if (Math.Abs(_zoom - 1f) < 0.0001f)
            return;

        _zoom = 1f;
        RefreshPinnedSize();
    }

    /// <summary>绘制贴图内容 + 边框 + 关闭按钮</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // 位图是物理像素、客户区是逻辑像素, 高 DPI 下必须还原到逻辑尺寸绘制才是 1:1;
        // 若直接 DrawImageUnscaled, 在 150% 下只会画出左上角约 2/3 的内容。
        g.DrawImage(_image, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));

        g.SmoothingMode = SmoothingMode.AntiAlias;
        DrawEdge(g);
        DrawCloseButton(g);
    }

    /// <summary>
    /// 贴图的边框。
    ///
    /// 旧实现只有一圈 35% 黑边 —— 贴一张深色截图时它就"消失"了, 用户以为没有边框。
    /// 改成<b>内白外黑各 1px</b> 的双色边: 白底截图贴到白文档、深色截图贴到深色编辑器,
    /// 总有一侧能看出来。代价是内容最外圈被盖住 1px (贴图是查看用途, 可以接受)。
    /// </summary>
    private void DrawEdge(Graphics g)
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        if (w < 3 || h < 3)
            return;

        using (var dark = new Pen(Color.FromArgb(120, 0, 0, 0), 1f))
            g.DrawRectangle(dark, 0, 0, w - 1, h - 1);
        using (var light = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
            g.DrawRectangle(light, 1, 1, w - 3, h - 3);
    }

    /// <summary>
    /// 右上角的「×」关闭按钮。
    ///
    /// 为什么常驻而不是悬停才出现: 贴图没有标题栏和按钮, "怎么关"完全靠用户猜 ——
    /// 用户实测反馈"没有关闭按钮"。常驻半透明圆钮在内容上只占 18px, 交互损失远小于
    /// "发现不了怎么关"; 悬停时才变实。
    /// </summary>
    private void DrawCloseButton(Graphics g)
    {
        var r = CloseButtonRect;
        if (r.Right > ClientSize.Width || r.Bottom > ClientSize.Height)
            return;

        using (var back = new SolidBrush(Color.FromArgb(_closeHover ? 225 : 150, 18, 20, 24)))
            g.FillEllipse(back, r);
        if (_closeHover)
        {
            using var ring = new Pen(Color.FromArgb(180, 255, 255, 255), 1f);
            g.DrawEllipse(ring, r);
        }

        using var pen = new Pen(Color.FromArgb(_closeHover ? 255 : 215, 255, 255, 255), 1.7f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        const int pad = 5;
        g.DrawLine(pen, r.Left + pad, r.Top + pad, r.Right - pad, r.Bottom - pad);
        g.DrawLine(pen, r.Right - pad, r.Top + pad, r.Left + pad, r.Bottom - pad);
    }

    /// <summary>释放贴图位图</summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _image.Dispose();
        base.OnFormClosed(e);
    }
}
