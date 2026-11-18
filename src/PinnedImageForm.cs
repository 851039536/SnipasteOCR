namespace SnipasteOcr;

/// <summary>
/// 贴图窗口 (仿 Snipaste 的「贴图」): 把截图连同标注钉在屏幕上, 无边框、始终置顶。
///
/// 交互:
/// - 左键拖动移动位置;
/// - 滚轮缩放, <b>Ctrl+滚轮</b> 调透明度, 双击复位到 1:1;
/// - 右键关闭。
///
/// 为什么用 <c>WM_NCLBUTTONDOWN + HTCAPTION</c> 拖动而不是自己算鼠标偏移:
/// 自己实现要处理"鼠标移出窗口""按下后窗口被其它窗口遮挡""高 DPI 下坐标换算"等一堆边界,
/// 交给系统的标题栏拖拽逻辑则这些全部自动正确 (它内部用的是屏幕坐标与鼠标捕获)。
/// 这也是无边框窗口做拖动的标准做法 —— 除拖动外不引入任何窗口边框。
///
/// 窗口语义 (置顶/不激活/DPI 落尺寸) 在 <see cref="PinnedWindowBase"/> 里, 这里只负责"画一张图 + 缩放"。
/// </summary>
public sealed class PinnedImageForm : PinnedWindowBase
{
    private readonly Bitmap _image;
    private float _zoom = 1f;

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

    /// <summary>当前缩放比例 (1 = 原始像素)</summary>
    public float Zoom => _zoom;

    protected override void RefreshPinnedSize()
        => ApplyImageSize(_image.Width, _image.Height, _zoom);

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

    /// <summary>绘制贴图内容</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        // 位图是物理像素、客户区是逻辑像素, 高 DPI 下必须还原到逻辑尺寸绘制才是 1:1;
        // 若直接 DrawImageUnscaled, 在 150% 下只会画出左上角约 2/3 的内容。
        e.Graphics.DrawImage(_image, new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));

        // 1px 描边: 贴图常叠在内容相近的背景上 (白底截图贴到白底文档上会"消失"),
        // 一圈细边让它始终能看出边界。
        using var pen = new Pen(Color.FromArgb(90, 0, 0, 0));
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }

    /// <summary>释放贴图位图</summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _image.Dispose();
        base.OnFormClosed(e);
    }
}
