namespace SnipasteOcr;

/// <summary>
/// 无边框窗口的圆角。
///
/// Win11 有原生的 8px 窗口圆角, 但 FormBorderStyle.None 的窗口默认不参与,
/// 需要显式声明 <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>; 该属性带抗锯齿, 观感最好。
///
/// Win10 没有这个能力 (DwmSetWindowAttribute 直接失败, 窗口仍是直角)。为了让
/// "圆角"这条视觉语言在 Win10 上也成立, 这里退回 <see cref="Form.Region"/> 硬裁剪:
/// 边缘是 1px 台阶而不是抗锯齿曲线, 但在 8px 半径下几乎看不出来;
/// Region 只在窗口尺寸/DPI 变化时重建, 不参与每次重绘。
///
/// 不用于贴图窗口: 贴图展示的是截图原像素, 圆角会把图片四角裁掉。
/// </summary>
internal static class WindowChrome
{
    /// <summary>圆角半径 (逻辑像素, 与 Win11 的默认值一致)</summary>
    public const int Radius = 8;

    /// <summary>已挂过事件的窗体 (用弱表而不是 Tag, 避免占用业务字段)</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, object> Attached = new();

    /// <summary>当前系统是否原生支持窗口圆角 (Win11 = build 22000+)</summary>
    public static bool NativeRoundedCorners => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// 给无边框窗体挂上圆角: 句柄创建、尺寸变化、DPI 变化各应用一次。
    /// 幂等, 可重复调用; 同一个窗体只注册一轮事件。
    /// </summary>
    public static void Attach(Form form)
    {
        // 只处理无边框窗体: 带边框的窗口由系统绘制非客户区, 在 Win10 上用 Region 硬裁
        // 会把边框线一起切掉 (四角像缺了一块), 而 Win11 本来就会给普通窗口自动加圆角。
        if (form.FormBorderStyle != FormBorderStyle.None)
            return;

        if (!Attached.TryGetValue(form, out _))
        {
            Attached.Add(form, new object());
            form.HandleCreated += (_, _) => Apply(form);
            form.SizeChanged += (_, _) =>
            {
                // Win11 由 DWM 维护, 尺寸变化不需要我们插手
                if (!NativeRoundedCorners)
                    Apply(form);
            };
            form.DpiChanged += (_, _) => Apply(form);
        }

        if (form.IsHandleCreated)
            Apply(form);
    }

    /// <summary>按当前尺寸与 DPI 应用圆角 (最大化时取消, 避免四角被切)</summary>
    private static void Apply(Form form)
    {
        if (!form.IsHandleCreated || form.IsDisposed)
            return;

        if (form.WindowState == FormWindowState.Maximized)
        {
            form.Region?.Dispose();
            form.Region = null;
            return;
        }

        if (NativeRoundedCorners)
        {
            Native.Dwmapi.TryRoundCorners(form.Handle);
            return;
        }

        int radius = (int)Math.Round(Radius * form.DeviceDpi / 96f);
        using var path = RoundedPath(new Rectangle(0, 0, form.Width, form.Height), radius);
        var region = new Region(path);
        var previous = form.Region;
        form.Region = region;
        previous?.Dispose();
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
