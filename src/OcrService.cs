using System.Drawing.Imaging;
using Sdcb.SimdPaddleOCR;
using Sdcb.SimdPaddleOCR.ModelProvider;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Medium;
using Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny;

namespace SnipasteOcr;

/// <summary>识别模型档位 (可在托盘菜单中切换)</summary>
public enum OcrModelProfile
{
    /// <summary>ChineseV6 Medium (132MB): 精度优先, 小字/模糊/形近字更准</summary>
    Medium = 0,
    /// <summary>ChineseV6 Tiny (6MB): 速度优先, 推理约快 9 倍</summary>
    Tiny = 1,
}

/// <summary>
/// OCR 服务: 懒加载 SimdPaddleOCR 引擎 (模型来自 NuGet 嵌入资源, 完全离线)。
/// 对 Bitmap 仅做瞬时 LockBits 复制, 推理在独立字节缓冲上进行,
/// 不持有位图锁, UI 线程可随时重绘。
/// 支持在 Medium / Tiny 两套模型间切换: 切换只置脏标记, 下一次识别时才真正加载,
/// 旧引擎在无并发推理时释放, 避免切换瞬间崩溃。
/// </summary>
public sealed class OcrService : IDisposable
{
    private static readonly Lazy<OcrService> _instance = new(() => new OcrService());
    public static OcrService Instance => _instance.Value;

    // 引擎初始化互斥锁 (双重检查: 初始化只执行一次)
    private readonly object _gate = new();
    // 懒加载的 OCR 引擎实例
    private PaddleOcrAll? _ocr;
    // 当前已加载引擎对应的档位
    private OcrModelProfile _loadedProfile;
    // 用户请求的档位 (用 int 存储以便 Volatile 原子读写); 与 _loadedProfile 不一致时下次识别重载
    private int _requestedProfile = (int)OcrModelProfile.Medium;

    /// <summary>状态提示 (可能在后台线程触发, 订阅方需自行切回 UI 线程)</summary>
    public event Action<string>? StatusChanged;

    /// <summary>档位变化通知 (切换生效后触发, 供托盘菜单刷新勾选状态)</summary>
    public event Action<OcrModelProfile>? ProfileChanged;

    /// <summary>当前档位</summary>
    public OcrModelProfile Profile => (OcrModelProfile)Volatile.Read(ref _requestedProfile);

    /// <summary>
    /// 切换识别模型档位。仅记录请求并置脏, 不立即加载 (加载耗时数百毫秒,
    /// 且应发生在后台推理线程上); 下一次 Recognize 时生效。
    /// </summary>
    public void SetProfile(OcrModelProfile profile)
    {
        if (Volatile.Read(ref _requestedProfile) == (int)profile)
            return;

        Volatile.Write(ref _requestedProfile, (int)profile);
        // 通知订阅方 (托盘勾选) —— 可能在任意线程, 订阅方需自行 marshal
        ProfileChanged?.Invoke(profile);
    }

    /// <summary>档位显示名 (菜单/状态栏用)</summary>
    public static string DisplayName(OcrModelProfile p) =>
        p == OcrModelProfile.Tiny ? "快速 (Tiny)" : "高精度 (Medium)";

    /// <summary>
    /// 识别位图中的文字。先瞬时拷贝像素, 再锁像素缓冲推理,
    /// 整个过程不持有位图锁, UI 线程可随时重绘
    /// </summary>
    public PaddleOcrResult Recognize(Bitmap image, CancellationToken cancellationToken)
    {
        // 瞬时锁定: 仅复制像素, 微秒级; 推理期间不持有位图锁
        byte[] pixels = Snapshot(image, out int width, out int height, out int stride);

        // 整个「取引擎 + 推理」过程都持有 _gate:
        // 否则切换档位时 Dispose() 可能正好在另一线程 Run() 的中途释放掉引擎内存。
        lock (_gate)
        {
            var ocr = EnsureInitializedLocked();
            cancellationToken.ThrowIfCancellationRequested();
            return ocr.Run(pixels.AsSpan(), width, height, stride, ImagePixelFormat.Bgra32);
        }
    }

    /// <summary>LockBits 只读锁定 + Marshal.Copy 像素快照, 立即解锁 (微秒级)</summary>
    private static byte[] Snapshot(Bitmap image, out int width, out int height, out int stride)
    {
        width = image.Width;
        height = image.Height;
        BitmapData data = image.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            byte[] pixels = new byte[stride * height];
            // 瞬时拷贝: Marshal.Copy 直接 Scan0 -> 托管数组, 无需 unsafe
            System.Runtime.InteropServices.Marshal.Copy(
                data.Scan0, pixels, 0, stride * height);
            return pixels;
        }
        finally
        {
            image.UnlockBits(data);
        }
    }

    /// <summary>
    /// 懒加载/切换 OCR 引擎; 首次调用需加载模型, 耗时较长。
    /// 调用方必须已持有 _gate (本方法不再重复加锁, 否则 lock 可重入但语义混乱)。
    /// 若请求档位与已加载档位不同, 先释放旧引擎再加载新模型。
    /// </summary>
    private PaddleOcrAll EnsureInitializedLocked()
    {
        var wanted = (OcrModelProfile)Volatile.Read(ref _requestedProfile);

        if (_ocr is not null && _loadedProfile == wanted)
            return _ocr;

        // 档位变了 (或首次加载): 释放旧引擎
        if (_ocr is not null)
        {
            StatusChanged?.Invoke("正在切换识别模型\u2026");
            _ocr.Dispose();
            _ocr = null;
        }
        else
        {
            StatusChanged?.Invoke("正在加载 OCR 引擎\u2026");
        }

        var options = new PaddleOcrOptions
        {
            // Medium 与 Tiny 的 bundle 都带 CLS 方向分类模型 (共用 TextLineOrientation 包),
            // 因此两者都可开启方向分类
            UseDirectionClassification = true,
            LineWorkerCount = 0, // min(ProcessorCount, 4)
        };

        PaddleOcrModelBundle bundle = wanted == OcrModelProfile.Tiny
            ? ChineseV6TinyModels.Default
            : ChineseV6MediumModels.Default;

        _ocr = PaddleOcrAll.Load(bundle, options);
        _loadedProfile = wanted;

        StatusChanged?.Invoke($"引擎就绪 ({DisplayName(wanted)}), 正在识别\u2026");
        return _ocr;
    }

    /// <summary>释放引擎占用的 SIMD 内存</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _ocr?.Dispose();
            _ocr = null;
        }
    }
}
