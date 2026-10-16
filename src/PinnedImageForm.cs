using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 贴图窗口 (仿 Snipaste 的「贴图」): 把截图连同标注钉在屏幕上, 无边框、始终置顶。
///
/// 交互 (与用户确认过的范围一致):
/// - 左键拖动移动位置 (含标注的成品图, 位置自由摆放);
/// - 右键关闭贴图。
///
/// 为什么用 <c>WM_NCLBUTTONDOWN + HTCAPTION</c> 拖动而不是自己算鼠标偏移:
/// 自己实现要处理"鼠标移出窗口""按下后窗口被其它窗口遮挡""高 DPI 下坐标换算"等一堆边界,
/// 交给系统的标题栏拖拽逻辑则这些全部自动正确 (它内部用的是屏幕坐标与鼠标捕获)。
/// 这也是无边框窗口做拖动的标准做法 —— 除拖动外不引入任何窗口边框。
/// </summary>
public sealed class PinnedImageForm : Form
{
    private readonly Bitmap _image;

    /// <summary>构造贴图窗口; 图像所有权转移给本窗口 (关闭时释放)</summary>
    /// <param name="image">已合成标注的成品图 (物理像素)</param>
    /// <param name="location">窗口左上角 (屏幕坐标)</param>
    public PinnedImageForm(Bitmap image, Point location)
    {
        _image = image;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        // 贴图的意义就是"浮在其它窗口之上", 因此必须置顶
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // 尺寸换算: 传入的是<b>物理像素</b>, 而 WinForms 的 ClientSize 是<b>逻辑像素</b>。
        // 本程序是 PerMonitorV2, 在 125%/150% 缩放下两者不等 —— 若直接把物理像素当逻辑像素,
        // 贴图会被放大 1.25/1.5 倍, 看起来"糊了"且与截图尺寸对不上。
        // 这里除以 DPI 因子, 使贴图在任何缩放比例下都是 1:1 显示 (与 OcrResultForm 同一套换算)。
        float dpi = DpiFactor();
        ClientSize = new Size(
            (int)Math.Round(image.Width / dpi),
            (int)Math.Round(image.Height / dpi));

        Location = location;
        Cursor = Cursors.SizeAll;
    }

    /// <summary>物理像素/逻辑像素比例 (与 OcrResultForm 的换算保持一致)</summary>
    private static float DpiFactor()
    {
        using var g = Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96f;
    }

    /// <summary>
    /// 显示时不激活窗口: 贴图是"看"的, 不该把焦点从用户正在输入的程序抢走。
    /// 这是只读属性, 只能重写 (直接赋值会编译失败)。
    /// </summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>创建参数: 不激活 + 不占任务栏按钮, 且不被父窗口裁剪</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000;   // WS_EX_NOACTIVATE
            return cp;
        }
    }

    /// <summary>
    /// 左键按下: 交给系统做窗口拖动。
    /// 先 ReleaseCapture 释放 WinForms 自己的鼠标捕获, 否则系统的拖拽循环收不到鼠标消息。
    /// </summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left)
        {
            User32.ReleaseCapture();
            User32.SendMessage(Handle, User32.WM_NCLBUTTONDOWN, (IntPtr)User32.HTCAPTION, IntPtr.Zero);
        }
        else if (e.Button == MouseButtons.Right)
        {
            Close();
        }
    }

    /// <summary>绘制贴图内容 (1:1, 不做缩放, 保证与截图所见一致)</summary>
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
