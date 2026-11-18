using System.Drawing.Imaging;

namespace SnipasteOcr;

/// <summary>
/// 原子写入: 先写同目录的临时文件, 成功后再移动覆盖目标。
///
/// 为什么需要: 直接 <c>File.WriteAllBytes</c> / <c>Bitmap.Save</c> 到目标路径时,
/// 一旦中途失败 (磁盘满 / 无权限 / 文件被占用), 会留下一个<b>被截断的半成品</b> ——
/// 用户看到的是一张打不开的图片或一个损坏的表格, 比"保存失败"更糟。
/// 同目录临时文件保证 <c>File.Move</c> 这一步是原子的: 要么完整替换, 要么目标保持原样。
///
/// 是本期所有保存路径的统一入口 (截图保存 / 历史 / XLSX 导出), 不得各自再写一份。
/// </summary>
public static class AtomicFile
{
    /// <summary>原子写入字节数组</summary>
    /// <param name="path">目标路径 (目录不存在会自动创建)</param>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(directory);

        string temporary = TemporaryPathIn(directory);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            // 移动成功后临时文件已不存在; 失败时这里负责清理, 不让目录里堆垃圾
            TryDelete(temporary);
        }
    }

    /// <summary>原子保存图片 (按显式指定的编码格式, 不依赖文件扩展名)</summary>
    public static void SaveImage(Image image, string path, ImageFormat format)
    {
        string full = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(full) ?? ".";
        Directory.CreateDirectory(directory);

        string temporary = TemporaryPathIn(directory);
        try
        {
            // 显式传 ImageFormat: 临时文件名是 .tmp, 不能靠扩展名推断编码
            image.Save(temporary, format);
            File.Move(temporary, full, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>在目标目录内生成临时文件名 (同目录才能保证 Move 是同一卷上的原子改名)</summary>
    private static string TemporaryPathIn(string directory)
        => Path.Combine(directory, ".snipasteocr-" + Guid.NewGuid().ToString("N") + ".tmp");

    /// <summary>尽力删除临时文件; 删除失败不影响主流程 (下次写入会另起新名)</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 被其它进程短暂占用: 忽略。留下一个 .tmp 不会破坏任何已有文件。
        }
    }
}
