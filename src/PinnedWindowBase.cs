using System.ComponentModel;
using SnipasteOcr.Native;

namespace SnipasteOcr;

/// <summary>
/// 贴图窗口的公共行为: 无边框 / 置顶 / 不抢焦点 / 标题栏拖拽 / DPI 尺寸 / 鼠标穿透。
///
/// 三种贴图 (图片、文字、色块) 只有"画什么"和"尺寸怎么算"不同, 窗口语义完全一致 ——
/// 抽基类是为了让 DPI 与拖拽这两块踩过坑的逻辑只存在一份。
///
/// DPI 的关键约束 (来自 PinnedImageForm 的实测):
/// - <see cref="ClientSize"/> 只能在 <c>OnLoad</c> / <c>OnDpiChanged</c> 落,
///   不能在 <c>OnHandleCreated</c> 设 —— CreateHandle 之后 WinForms 还会按旧的非客户区
///   再改一次尺寸 (300x200 会被改回 300x239)。
/// - DPI 因子必须取窗口自身的 <see cref="Control.DeviceDpi"/>, 不能用
///   <c>Graphics.FromHwnd(IntPtr.Zero)</c> (那是桌面 DC, 永远返回主屏 DPI)。
/// </summary>
public abstract class PinnedWindowBase : Form
{
    private bool _clickThrough;
    private bool _dpiReady;

    protected PinnedWindowBase()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;              // 贴图的意义就是浮在其它窗口之上
        ShowInTaskbar = false;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    /// <summary>窗口所在屏的 DPI 因子 (1.0 = 96 DPI)</summary>
    protected float DpiFactor { get; private set; } = 1f;

    /// <summary>
    /// 鼠标穿透。
    ///
    /// 开启后本窗口收不到任何鼠标消息, 用户<b>无法再右键关闭它</b> ——
    /// 因此只能由托盘的「取消全部穿透」统一恢复, 见 <see cref="PinnedRegistry"/>。
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value)
                return;
            _clickThrough = value;
            if (IsHandleCreated)
                ApplyClickThrough();
        }
    }

    /// <summary>贴图是"看"的, 不该把焦点从用户正在输入的程序抢走</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= (int)User32.WS_EX_NOACTIVATE;
            if (_clickThrough)
                cp.ExStyle |= (int)User32.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 贴图窗口同样用 Win11 的 8px 圆角, 与结果窗口/弹层一致 (Win10 忽略)
        SnipasteOcr.Native.Dwmapi.TryRoundCorners(Handle);
        DpiFactor = DeviceDpi / 96f;
        _dpiReady = true;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshPinnedSize();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        DpiFactor = e.DeviceDpiNew / 96f;
        RefreshPinnedSize();
    }

    /// <summary>按当前 DPI 重新落一次尺寸; 子类必须实现为"幂等"</summary>
    protected abstract void RefreshPinnedSize();

    /// <summary>把物理像素 + 缩放换算成客户区逻辑尺寸</summary>
    protected void ApplyImageSize(int physicalWidth, int physicalHeight, float zoom)
    {
        if (!_dpiReady)
            return;
        ClientSize = PinnedImageLayout.ClientSize(physicalWidth, physicalHeight, DpiFactor, zoom);
    }

    /// <summary>按逻辑像素落客户区尺寸 (文字/色块贴图的内容本身就是按逻辑像素排版的)</summary>
    protected void ApplyLogicalSize(Size logical)
    {
        if (!_dpiReady)
            return;
        ClientSize = new Size(Math.Max(1, logical.Width), Math.Max(1, logical.Height));
    }

    /// <summary>
    /// 左键: 交给系统做窗口拖动。
    /// 先 ReleaseCapture 释放 WinForms 自己的鼠标捕获, 否则系统的拖拽循环收不到鼠标消息。
    /// </summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left)
        {
            User32.ReleaseCapture();
            User32.SendMessage(Handle, (uint)User32.WM_NCLBUTTONDOWN, (IntPtr)User32.HTCAPTION, IntPtr.Zero);
        }
        else if (e.Button == MouseButtons.Right)
        {
            OnRightClick();
        }
    }

    /// <summary>右键行为; 默认关闭贴图 (与既有行为一致)</summary>
    protected virtual void OnRightClick() => Close();

    private void ApplyClickThrough()
    {
        long style = User32.GetWindowLongPtr(Handle, User32.GWL_EXSTYLE).ToInt64();
        if (_clickThrough)
            style |= User32.WS_EX_TRANSPARENT;
        else
            style &= ~(long)User32.WS_EX_TRANSPARENT;

        User32.SetWindowLongPtr(Handle, User32.GWL_EXSTYLE, (IntPtr)style);

        // 改扩展样式后必须走一次 SetWindowPos, 否则系统不会重新计算命中测试
        User32.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_FRAMECHANGED);
    }
}
