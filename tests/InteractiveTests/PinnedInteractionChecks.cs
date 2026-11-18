// 交互验证: 贴图窗口的缩放 / 鼠标穿透 / 注册表管理。
//
// 撑穿与缩放都要真的改窗口扩展样式并让系统重算命中测试, 纯离屏测不出来。
// 这里构造真实窗口, 反射调用消息处理函数 (与真实滚轮同一条路径)。
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using SnipasteOcr;
using SnipasteOcr.Native;

namespace SnipasteOcr.Tests;

internal static class PinnedInteractionChecks
{
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>把断言并入调用方的收集器</summary>
    public static void Run(CheckRunner runner)
    {
        void Check(bool cond, string name) => runner.Check(cond, name);

        var source = new Bitmap(200, 100);
        using (var g = Graphics.FromImage(source))
            g.Clear(Color.SteelBlue);

        using var pin = new PinnedImageForm(source, new Point(200, 200));
        pin.Show();
        Application.DoEvents();

        // ===== 1. 初始尺寸按窗口自身 DPI 换算 =====
        float dpi = pin.DeviceDpi / 96f;
        var expected = PinnedImageLayout.ClientSize(200, 100, dpi, 1f);
        Check(pin.ClientSize == expected,
            $"贴图初始尺寸按 DPI 换算 (实得 {pin.ClientSize}, 期望 {expected})");
        Check(Math.Abs(pin.Zoom - 1f) < 0.0001f, "初始缩放为 1:1");

        var wheel = typeof(PinnedImageForm).GetMethod("OnMouseWheel", Priv)!;

        void Roll(int delta) => wheel.Invoke(pin, [new MouseEventArgs(MouseButtons.None, 0, 20, 20, delta)]);

        // ===== 2. 滚轮放大 =====
        Roll(120);
        Application.DoEvents();
        Check(Math.Abs(pin.Zoom - 1.1f) < 0.001f, $"滚轮向上放大 10% (实得 {pin.Zoom})");
        Check(pin.ClientSize.Width > expected.Width, "放大后客户区变宽");

        // ===== 3. 缩小回去, 尺寸应回到初始值 =====
        Roll(-120);
        Application.DoEvents();
        Check(Math.Abs(pin.Zoom - 1f) < 0.001f, $"滚轮向下缩回 1:1 (实得 {pin.Zoom})");
        Check(pin.ClientSize == expected, $"回到 1:1 后尺寸复原 (实得 {pin.ClientSize})");

        // ===== 4. 缩放夹紧 =====
        for (int i = 0; i < 40; i++)
            Roll(120);
        Check(Math.Abs(pin.Zoom - PinnedImageLayout.MaxZoom) < 0.001f,
            $"连滚放大夹在上限 (实得 {pin.Zoom})");

        for (int i = 0; i < 120; i++)
            Roll(-120);
        Check(Math.Abs(pin.Zoom - PinnedImageLayout.MinZoom) < 0.001f,
            $"连滚缩小夹在下限 (实得 {pin.Zoom})");

        // ===== 5. 鼠标穿透: 扩展样式位真的被置上/清除 =====
        pin.ClickThrough = true;
        long style = User32.GetWindowLongPtr(pin.Handle, User32.GWL_EXSTYLE).ToInt64();
        Check((style & User32.WS_EX_TRANSPARENT) != 0, "开启穿透后 WS_EX_TRANSPARENT 置位");
        Check(PinnedRegistry.AnyClickThrough, "注册表能查到穿透状态");

        pin.ClickThrough = false;
        style = User32.GetWindowLongPtr(pin.Handle, User32.GWL_EXSTYLE).ToInt64();
        Check((style & User32.WS_EX_TRANSPARENT) == 0, "取消穿透后样式位被清除");

        // ===== 6. 托盘"取消全部穿透"是穿透后唯一的逃生口 =====
        pin.ClickThrough = true;
        int restored = PinnedRegistry.ClearClickThrough();
        Check(restored >= 1 && !pin.ClickThrough,
            $"一键取消全部穿透 (恢复 {restored} 个)");

        // ===== 7. 关闭全部 =====
        int before = PinnedRegistry.Count;
        int closed = PinnedRegistry.CloseAll();
        Check(closed == before && before >= 1, $"关闭全部贴图 (关闭 {closed} / 之前 {before})");
        Check(PinnedRegistry.Count == 0, "关闭后注册表清空");
    }
}
