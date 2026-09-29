# SnipasteOCR

仿 Snipaste 的截图 OCR 小工具:按热键框选屏幕区域,本地离线识别文字,支持部分选词、复制、保存。

基于 .NET 10 WinForms + NativeAOT,发布后是**单个原生 exe**,无需安装 .NET 运行时;OCR 引擎 [SimdPaddleOCR](https://github.com/Sdcb/SimdPaddleOCR) 与中文识别模型全部内嵌,识别过程**完全离线**。

## 功能

- **F1** — 截屏 OCR:框选区域后弹出结果窗口
- **F2** — 截屏标注:框选后选区下方浮出标注工具栏,可绘制**矩形 / 椭圆 / 箭头 / 画笔 / 马赛克 / 文字**,确认后以 PNG 复制到剪贴板
- **热键可自定义**(托盘菜单 → 热键设置),用于避开被 PixPin / IDE 等占用的 F1/F2
- **可在托盘菜单切换识别模型**(高精度 / 快速),选择会被记住
- **结果支持阅读顺序排序与表格导出**(复制表格 / 复制 CSV),方便从截图里的表格取数
- 支持鼠标右键托盘菜单触发,热键被占用时会提示
- 识别结果窗口:
  - 按截图原始尺寸 1:1 显示,缩放时窗口跟随图片大小联动
  - 单击选中整个文本块,在文本块内**按住拖动可部分选词**(像选网页文字一样)
  - `Ctrl+C` 复制选中内容(整行或选中的字符范围),`Ctrl+A` 全选
  - `Ctrl+滚轮` / `+` / `-` 缩放,`0` 适应窗口,`Esc` 关闭
  - 一键复制全部 / **复制表格** / **复制 CSV** / 保存截图为 PNG

## 截图标注(F2)

框选完成后,选区下方会自动浮出工具栏(贴近屏幕边缘时自动翻转到选区上方)。

| 工具 | 快捷键 | 操作 |
| --- | --- | --- |
| 矩形 | `1` | 按住拖拽出矩形框 |
| 椭圆 | `2` | 按住拖拽出椭圆框 |
| 箭头 | `3` | 从起点拖向目标,箭头指向终点 |
| 画笔 | `4` | 按住自由绘制(轨迹自动去抖) |
| 马赛克 | `5` | 按住拖出打码区域,块平均像素化,适合遮挡密码/隐私 |
| 文字 | `6` | 点击定位后就地输入,回车确认、`Esc` 取消 |

**样式**:点色块循环切换 8 种颜色,点线宽按钮切换 3 档粗细。

**快捷键**:`Ctrl+Z` 撤销、`Ctrl+Y` 重做、`Delete` 清空全部标注、`0` 退回纯框选、`回车`/双击确认、`Esc` 退出当前工具、`右键` 先清空标注再退出。

**确认动作**:与原来一致 —— F2 模式下标注会**烧录进图片**一起复制到剪贴板(不做成独立图层);
工具栏上的「保存」按钮可另存为 PNG 到 `图片\SnipasteOCR\`。

> 标注只在选区内可见与生效,与最终导出一致 —— 预览看到什么,导出的就是什么。
> 马赛克直接从原图取样,不会把下方的其它标注也糊进去。

## 配置

设置保存在 `%LOCALAPPDATA%\SnipasteOCR\settings.json`,全部可在托盘菜单里改:

| 字段 | 说明 | 默认 |
| --- | --- | --- |
| `modelProfile` | 识别模型档位 (0=高精度, 1=快速) | 0 |
| `hotKeyOcr` / `hotKeyImage` | 两个热键的虚拟键码 | F1 (0x70) / F2 (0x71) |
| `hotKeyModifiers` | 热键修饰符 (Ctrl=2, Alt=1, Shift=4, Win=8, 可相加) | 0 |
| `sortReadingOrder` | 结果是否按阅读顺序重排 | true |

> 配置读写失败 (文件损坏/无权限) 时自动回退默认值,不影响启动。
> NativeAOT 下 JSON 使用**源生成**序列化 (`SettingsJsonContext`),否则会被裁剪器移除。


## 系统要求

| 项目 | 要求 |
| --- | --- |
| 系统 | Windows 10 1809+ / Windows 11 (x64) |
| CPU | 需支持 AVX2(2011 年后的 Intel/AMD 桌面/移动 CPU 基本都支持) |

> AVX2 是 SimdPaddleOCR 在 NativeAOT 下启用 SIMD 内核的硬性要求,否则推理速度会差一个数量级,因此发布时固定编译为 AVX2 指令集。

## 快速开始(发布产物)

发布后的 `SnipasteOcr.exe` 单文件即可运行(约 159 MB,内含高精度 + 快速两套模型):

```
SnipasteOcr.exe      # 启动后驻留系统托盘
```

启动后按 **F1** 框选区域,稍等识别完成即可在结果窗口中选择、复制文字。

## 识别模型切换

内置两套中文模型,**右键托盘图标 → 识别模型** 即可切换,切换立即生效(下一次识别时加载):

| 档位 | 模型 | 模型体积 | 速度 | 适用场景 |
| --- | --- | --- | --- | --- |
| 高精度(默认) | PP-OCRv6 Medium | 132 MB | 基准 | 小字、模糊、形近字、表格与代码 |
| 快速 | PP-OCRv6 Tiny | 6 MB | 约快 9 倍 | 正常字号的印刷体、段落、英文数字 |

实测字符准确率(困难样本,越小越难):

| 场景 | Medium | Tiny |
| --- | --- | --- |
| 极小字 / 表格 | 100% | 83% |
| 密集代码 | 100% | 92% |
| 缩放后略模糊 | 100% | 89% |
| 形近字(未末土士己已巳) | 93% | 86% |
| 正常字号印刷体 / 段落 / 英文 | 100% | 100% |
| **平均** | **99.0%** | **95.8%** |

> 结论:屏幕截图通常很清晰,Tiny 在正常字号下完全够用;但**小字和模糊场景** Medium 明显更准。
> 因为两者都打包进 exe,体积为两者之和(约 159 MB),换来的是随时切换、无需重新发布。

切换只需登记请求,真正的模型加载发生在其后的第一次识别(后台线程),不会卡住界面。


## 从源码构建

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```powershell
# 调试运行
dotnet build SnipasteOcr -c Debug

# AOT 发布 (win-x64 单文件原生 exe)
dotnet publish SnipasteOcr -c Release -r win-x64 --self-contained -p:PublishAot=true
```

产物位于 `SnipasteOcr/bin/Release/net10.0-windows/win-x64/publish/`,其中的语言资源目录(`cs`、`de`、`zh-Hans` 等)可删除,不影响运行。

### 标注引擎自检

标注引擎的几何、命中测试、撤销栈与渲染都有离线自检 (`tests/AnnotationTests`,链接产品源码而非复制,保证验证对象即实际代码):

```powershell
cd tests/AnnotationTests
dotnet run                  # 67 项检查: 几何/命中/历史/像素级渲染
dotnet run -- sample        # 额外输出一张六种标注的样例图, 便于目视确认
```

覆盖点包括反向拖拽的矩形归一化、椭圆包围盒死角的精确排除、箭头的头宽大于线体、马赛克的 block 级量化且不引入偏色、`clip`/`skip` 参数生效、退化标注不绘制、撤销栈快照与原对象隔离等。

## 项目结构

```
SnipasteOcr/
├── Program.cs            # 入口: 手动消息循环 + 热键注册 + 配置恢复
├── SnipCoordinator.cs    # 截图流程调度
├── SnipMode.cs           # 截图模式枚举 (OCR / 仅截图)
├── SnipOverlayForm.cs    # 全屏截图覆盖层: 框选、遮罩、标注交互、结果合成
├── Annotations/          # 标注引擎
│   ├── Annotation.cs         # 单条标注模型 (几何/命中测试/克隆)
│   ├── AnnotationTool.cs     # 工具枚举与展示元数据
│   ├── AnnotationEngine.cs   # 渲染引擎 (六种工具 + 马赛克像素化)
│   ├── AnnotationHistory.cs  # 撤销/重做 (快照模型)
│   ├── AnnotationToolbar.cs  # 自绘工具栏 (工具/颜色/线宽/撤销/保存)
│   └── TextEditorOverlay.cs  # 就地文字编辑框 (所见即所得)
├── OcrService.cs         # OCR 引擎封装 (懒加载、模型档位切换、独立线程推理)
├── OcrResultForm.cs      # 结果窗口: 框叠加、字符级拖选、缩放联动
├── OcrText.cs            # 结果后处理: 阅读顺序排序、表格/CSV 重建
├── AppSettings.cs        # 配置持久化 + AOT 安全的 JSON 源生成上下文
├── HotKeyManager.cs      # 全局热键注册/重注册 (支持运行期换键)
├── HotKeyForm.cs         # 热键设置对话框 (按键录制)
├── TrayController.cs     # Win32 托盘图标 + 模型档位/热键菜单
└── NativeMethods.cs      # Win32 P/Invoke
```

## 技术要点

- **NativeAOT + 裁剪**: `IsAotCompatible=true`、`IlcInstructionSet=avx2`;OCR 引擎与模型通过程序集嵌入资源加载,需在 `TrimmerRootAssembly` 中显式保留(**两套模型程序集都要保留**,否则运行时切换会找不到资源),`PublishAot` 默认开启。
- **AOT 安全的 JSON**: 配置用 `JsonSerializerContext` **源生成**序列化。直接用反射式 `JsonSerializer` 会在裁剪后运行时失败 —— 这是 NativeAOT 最常见的坑之一。
- **模型档位切换**: `OcrService` 用 `_requestedProfile`(int,`Volatile` 读写)记录请求、`_loadedProfile` 记录实际已加载档位,两者不一致时在下一次识别中释放旧引擎并加载新模型。**取引擎与推理在同一个锁内完成**,避免切换时 `Dispose()` 打断另一线程正在进行的 `Run()`。
- **热键可换不丢**: `HotKeyManager` 换键时**先注册新键,成功后才注销旧键**,失败则回滚保留原热键,避免"换失败导致两个热键都没了"。
- **表格重建**: 结果块的包围盒先按纵向重叠聚类成行、再按左边界容差聚类成列,填充网格后输出制表符/逗号分隔文本;缺失单元格留空以保持列对齐。
- **Per-Monitor DPI v2**: 截图是物理像素,窗口客户区是逻辑像素,绘制/命中/字符定位三套坐标统一经 DPI 换算,保证 100% 缩放下像素对齐。
- **字符级选词**: PaddleOCR 只输出整行文本与整行检测框(四边形)。选词按字符宽度权重(汉字 1.0 / ASCII 0.6 / 空格 0.35)将字符投影到检测框主轴上,鼠标位置经投影反解出行内字符索引;复制内容直接按字符索引切原文,结果精确。
- **推理不锁图**: 识别前对位图做瞬时 `LockBits` 拷贝,推理在独立字节缓冲上进行,UI 线程可随时重绘。
- **标注坐标系统一**: 标注几何 **一律存物理像素**,与底图 `_screen` 同坐标系。覆盖层预览用 `Graphics.ScaleTransform` 缩放绘制,导出时用 `TranslateTransform` 平移 —— 同一份数据、同一个渲染引擎 (`AnnotationEngine`),因此**预览与导出像素级一致**,不会因 DPI 缩放错位。
- **马赛克实现**: 对目标区域 `LockBits` 取原始像素,按 10×10 分块求平均色写入小位图,再用 `NearestNeighbor` 放大绘制得到硬边像素块。**直接从原图取样**而非从"已绘制的画面"取样,因此马赛克不会把其下方的其它标注一起糊进去。
- **撤销/重做**: 采用「快照 + 列表」而非命令模式。标注数量是手工量级 (通常 < 50),每次操作深拷贝一份列表代价可忽略,换来实现简单且不会出现逆操作写错的问题;深度上限 100。
- **工具栏自绘**: 不用 `ToolStrip` —— 覆盖层无边框且工具栏需跟随选区移动、贴近屏幕边缘时自动翻转,自绘才能精确控制尺寸与外观;图标全部矢量绘制,不依赖图片资源,AOT 友好且任意 DPI 清晰。
- **文字标注就地编辑**: 自绘 `TextEditorOverlay` 而非 `TextBox`,以做到无边框 + 透明底 + 带描边文字,与最终合成效果一致。注意覆盖层设了 `KeyPreview = true`,编辑期间必须同时在 `ProcessCmdKey`/`ProcessDialogKey` 让行,否则输入的数字会被当作工具快捷键吞掉。

## 已知限制

- 中文识别为主,英文等西文字符可用但非最优
- 字符级选区按宽度比例估算,选区边缘与真实字符边界可能有 1~2 像素偏差(不影响复制内容)
- 仅支持 x64 Windows;发布产物与编译机架构绑定,换架构需重新 publish

## 关于「截屏翻译」的可行性结论

曾评估加入离线截屏翻译,结论是**当前技术栈下不可行**,记录备查:

本项目的 `Sdcb.SimdPaddleOCR.OnnxSharp` 虽含通用 ONNX 加载 API(`Model.LoadOnnx` / `InferenceSession`),
但实测**无法执行通用的 Transformer 翻译模型**:

| 验证对象 | 结果 |
| --- | --- |
| 本项目自带 OCR 模型 (正对照) | 4/4 加载成功 —— 证明探针正确 |
| NLLB `int8_encoder_model.onnx` | 失败:`InvalidDataException: ONNX TensorProto has no name` |
| NLLB `int8_encoder_kv_model.onnx` | 失败:`NotSupportedException: Unsupported ONNX tensor data type 3` |

根因:该引擎的 `DType` 只声明 `F32/I32/I64/U8`(且把 I64 编为 3,与 ONNX 规范中 `3=INT8` 冲突),
即**不支持 INT8 量化张量**,而主流可离线分发的 NMT 模型多为 INT8 量化。
另扫描其算子表,**缺少 `LayerNormalization` 与 `Attention`**,标准 Transformer 无法运行。

其他候选同样不合适:

- `LMSupply.Translator`:包内不含模型,运行时从 HuggingFace 联网下载(违背离线)
- `LocalAI.Translation` (NLLB):模型约 **1.5 GB**,依赖 `Microsoft.ML.OnnxRuntime.DirectML` **原生 DLL**(破坏单文件);且模型为 CC BY-NC 4.0 **仅限非商业**

若将来要做,可行方向是:自备**非量化、且 LayerNorm 已分解为基础算子**的 ONNX 翻译模型,
或用纯卷积式 NMT 小模型,并同样以嵌入资源方式打包。

## 依赖

- [Sdcb.SimdPaddleOCR](https://github.com/Sdcb/SimdPaddleOCR) 1.4.2 — 纯 C# 的 PaddleOCR 推理引擎
- [Sdcb.SimdPaddleOCR.Models.ChineseV6Medium](https://www.nuget.org/packages/Sdcb.SimdPaddleOCR.Models.ChineseV6Medium) 1.0.0 — 中文 det+rec+字典(嵌入资源,132 MB)
- [Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny](https://www.nuget.org/packages/Sdcb.SimdPaddleOCR.Models.ChineseV6Tiny) 1.0.0 — 轻量中文 det+rec+字典(嵌入资源,6 MB)
- [Sdcb.SimdPaddleOCR.Models.TextLineOrientation](https://www.nuget.org/packages/Sdcb.SimdPaddleOCR.Models.TextLineOrientation) 1.0.0 — 方向分类 (CLS) 模型,两套 bundle 共用

## License

MIT,(依赖项目开源协议保持原协议)
