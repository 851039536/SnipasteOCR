using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SnipasteOcr;

/// <summary>
/// 翻译 API Key 的本地保护 (Windows DPAPI, CurrentUser)。
///
/// 为什么不直接把 Key 存进 settings.json: 设置文件在 %LOCALAPPDATA% 下, 同机其它进程
/// 可以直接读取。DPAPI 用当前 Windows 账户的密钥加密, 文件被复制到别的机器/账户后无法解密,
/// 这时界面提示重新输入即可 —— 绝不能让解密失败静默回退成明文保存。
///
/// 使用固定 entropy, 避免同账户下其它程序解出本程序的密文。
/// </summary>
internal static class ProtectedKey
{
    private const uint CryptProtectUiForbidden = 0x1;

    // 固定熵: 仅用于区分"本程序"的密文, 不是密钥本身
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SnipasteOCR.Translate.v1");

    /// <summary>加密为 Base64; 失败返回空串 (调用方必须当作"未保存"处理)</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
            return "";

        byte[] data = Encoding.UTF8.GetBytes(plain);
        byte[]? encrypted = Transform(data, protect: true);
        return encrypted is null ? "" : Convert.ToBase64String(encrypted);
    }

    /// <summary>解密 Base64 密文; 空串/格式错误/换账户解密失败均返回 false</summary>
    public static bool TryUnprotect(string protectedBase64, out string plain)
    {
        plain = "";
        if (string.IsNullOrEmpty(protectedBase64))
            return false;

        byte[] data;
        try
        {
            data = Convert.FromBase64String(protectedBase64);
        }
        catch
        {
            return false;
        }

        byte[]? decrypted = Transform(data, protect: false);
        if (decrypted is null)
            return false;

        try
        {
            plain = Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decrypted);
        }

        return plain.Length > 0;
    }

    /// <summary>只显示 Key 末 4 位; 太短时全部遮掉</summary>
    public static string MaskTail(string plain)
        => plain.Length <= 4 ? "****" : "****" + plain[^4..];

    /// <summary>
    /// 分配非托管缓冲并调用 DPAPI。输入数据在返回前清零 (减少 Key 在内存中的停留时间)。
    /// 输出缓冲由 DPAPI 用 LocalAlloc 分配, 必须用 LocalFree 释放。
    /// </summary>
    private static byte[]? Transform(byte[] input, bool protect)
    {
        IntPtr inputPtr = Marshal.AllocHGlobal(Math.Max(1, input.Length));
        IntPtr entropyPtr = Marshal.AllocHGlobal(Entropy.Length);
        try
        {
            if (input.Length > 0)
                Marshal.Copy(input, 0, inputPtr, input.Length);
            Marshal.Copy(Entropy, 0, entropyPtr, Entropy.Length);

            var inputBlob = new DataBlob { CbData = input.Length, PbData = inputPtr };
            var entropyBlob = new DataBlob { CbData = Entropy.Length, PbData = entropyPtr };

            DataBlob output;
            bool ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);

            if (!ok || output.PbData == IntPtr.Zero || output.CbData <= 0)
                return null;

            try
            {
                var result = new byte[output.CbData];
                Marshal.Copy(output.PbData, result, 0, output.CbData);
                return result;
            }
            finally
            {
                LocalFree(output.PbData);
            }
        }
        catch
        {
            // DPAPI 不可用/内存不足: 调用方会当作"未配置 Key", 不影响程序其它功能
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            Marshal.FreeHGlobal(inputPtr);
            Marshal.FreeHGlobal(entropyPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    // 注意: 这里用 DllImport 而不是 LibraryImport, 与 NativeMethods 的现有风格一致。
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        ref DataBlob pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
