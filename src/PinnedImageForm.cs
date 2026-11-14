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

    /// <summary>窗口所在屏的 DPI 因子; 构造阶段按 96 DPI 估算, 句柄创建后按 DeviceDpi 校正</summary>
    private float _dpi = 1f;

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
        // 构造阶段先按 96 DPI 估算; 真实 DPI 必须取窗口自身 (DeviceDpi), 见 OnHandleCreated。
        ClientSize = new Size(Math.Max(1, image.Width), Math.Max(1, image.Height));
        Location = location;
        Cursor = Cursors.SizeAll;
    }

    /// <summary>
    /// 句柄创建后先记录窗口实际所在屏的 DPI。
    ///
    /// 这里<b>不能</b>立刻设置 ClientSize: <see cref="Form.CreateHandle"/> 在 OnHandleCreated
    /// 之后还会执行 ApplyClientSize/UpdateStylesCore, 按旧的非客户区再改一次尺寸
    /// (实测 300x200 会被改回 300x239)。真正落尺寸放在 <see cref="OnLoad"/>。
    ///
    /// 也不能用 <c>Graphics.FromHwnd(IntPtr.Zero)</c>: 那是桌面 DC, 永远返回主屏 DPI。
    /// 主屏 100% + 副屏 150% 时, 副屏上的贴图会按 100% 计算, 结果小一圈。
    /// </summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _dpi = DeviceDpi / 96f;
    }

    /// <summary>样式与客户区在 CreateHandle 末端才最终确定, Load 时按 1:1 落一次尺寸</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyDpi(DeviceDpi / 96f);
    }

    /// <summary>贴图被拖到另一块缩放比例不同的屏幕时, 保持 1:1 物理像素大小</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyDpi(e.DeviceDpiNew / 96f);
    }

    /// <summary>按 DPI 因子把物理像素图片换算成客户区逻辑尺寸 (左上角不动)</summary>
    private void ApplyDpi(float dpi)
    {
        _dpi = dpi <= 0 ? 1f : dpi;
        ClientSize = new Size(
            Math.Max(1, (int)Math.Round(_image.Width / _dpi)),
            Math.Max(1, (int)Math.Round(_image.Height / _dpi)));
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
