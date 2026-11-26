using System.Runtime.InteropServices;

namespace SnipasteOcr;

/// <summary>从剪贴板取图片的结果; 调用方必须按状态给出不同提示</summary>
public enum ClipboardImageStatus
{
    /// <summary>取到了图片, 所有权已转移给调用方</summary>
    Ok,

    /// <summary>剪贴板里确实没有图片 (或图片为空)</summary>
    NoImage,

    /// <summary>剪贴板正被其它进程占用 (CLIPBRD_E_CANT_OPEN), 稍后重试可能成功</summary>
    Busy,

    /// <summary>其它读取失败 (格式不支持 / GDI+ 失败等), 已带异常对象供调用方记录</summary>
    Failed,
}

/// <summary>
/// 剪贴板异常分类。
/// WinForms 的 Clipboard 会把"被占用"和"其它失败"都包成 ExternalException,
/// 不做 HResult 区分就只能一律说"被占用", 让用户对着一个治不好的提示反复重试。
/// </summary>
internal static class ClipboardErrors
{
    /// <summary>CLIPBRD_E_CANT_OPEN: 打开剪贴板失败 (被其它进程独占)</summary>
    public const int ClipbrdCantOpen = unchecked((int)0x800401D0);

    /// <summary>是否属于"剪贴板被占用"这一类可重试失败</summary>
    public static bool IsBusy(Exception? ex) => ex is not null && ex.HResult == ClipbrdCantOpen;
}

/// <summary>
/// 剪贴板图片读取 (唯一入口)。
///
/// 不先调 <c>ContainsImage()</c>: 那会多打开一次剪贴板, 无谓放大与其它进程的竞争窗口;
/// <c>GetImage()</c> 在无图时本来就返回 null。
/// 这里只做读取与分类, 不写日志 —— 保持可被离线自检单独链接, 由调用方记录。
/// </summary>
internal static class ClipboardImage
{
    /// <summary>
    /// 读取剪贴板图片。成功时 <paramref name="image"/> 归调用方所有;
    /// 失败时 <paramref name="error"/> 在非 Busy/NoImage 场景带回原始异常。
    /// </summary>
    public static ClipboardImageStatus TryGet(out Bitmap? image, out Exception? error)
    {
        image = null;
        error = null;

        Image? source = null;
        try
        {
            source = Clipboard.GetImage();
            if (source is null)
                return ClipboardImageStatus.NoImage;

            // GetImage 可能返回其它 Image 派生类; 统一成 Bitmap (同一实例则直接接管)
            image = source as Bitmap ?? new Bitmap(source);
            if (!ReferenceEquals(image, source))
                source.Dispose();
            source = null;
            return ClipboardImageStatus.Ok;
        }
        catch (Exception ex)
        {
            image?.Dispose();
            image = null;

            if (ClipboardErrors.IsBusy(ex))
                return ClipboardImageStatus.Busy;

            error = ex;
            return ClipboardImageStatus.Failed;
        }
        finally
        {
            source?.Dispose();
        }
    }
}
