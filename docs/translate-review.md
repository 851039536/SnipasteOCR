# SnipasteOCR F1 识别后 AI 翻译方案审查

日期: 2026-11-10 · 基线: `b620c3f`

**范围**: F1 框选识别完成后的「翻译」能力选型 —— 本地离线模型 vs DeepSeek API；
若采纳，还包括配置、Key 保护、窗口落点与隐私告知设计。

**不包含**: OCR 识别质量、标注功能、其它云服务商的横向对比（仅在结论处一句带过）、实际代码实现。

**方法**: 复核 `README.md` / `AGENTS.md` 的既有离线翻译结论；阅读 `OcrResultForm` / `AppSettings` /
`TrayController` / `SnipCoordinator` / `OcrText` 的相关代码；按 DeepSeek 官方文档核对接口
（核对日期 2026-11-10）；本地路径按体积、AOT/单文件、许可、质量、延迟、工程成本逐项评估；
成本按典型截图字数估算。

**结论**:

- **不推荐内嵌本地翻译模型**。不是技术上绝对做不到，而是「内置模型的体积/许可/工程成本」与
  「单文件约 163 MB、发布目录只有 exe + pdb」的产品定位直接冲突，质量还不如云端 LLM。
- **推荐 DeepSeek API**：默认关闭，只有用户点「翻译」才联网；译文用独立窗口展示；
  API Key 用 DPAPI 保护，不明文落盘。
- **保留本机推理端点作为后续扩展点**：不打包模型，用户自装 Ollama / LM Studio 时，
  同一套请求代码只需改 BaseUrl 即可指向 `127.0.0.1`。本期不做，但文档要承认这条路径存在。
- 本次评审后**已按推荐方案实施**（2026-11-10）：默认关闭、手动触发、独立译文窗口、
  固定 DeepSeek 官方端点、API Key 用 DPAPI 保护。下文保留评审依据与取舍，供后续改动参考。

---

## 摘要

| 等级 | 编号 | 结论 | 关键依据 |
| --- | --- | --- | --- |
| 高 | T-1 | 否决内嵌本地模型 | 现有 ONNX 引擎不支持 INT8、缺 Attention/LayerNorm；替代方案体积 0.6~2.4 GB、含原生 DLL、CC-BY-NC |
| 推荐 | T-2 | 采用 DeepSeek API，手动触发 | 无原生依赖、AOT 可承载、单次成本约 0.25~1 分 |
| 高 | T-3 | 必须处理「完全离线」承诺冲突 | README:3-5/157/245 与 dist 使用说明均写「不联网」 |
| 高 | T-4 | API Key 必须 DPAPI 保护 | `SettingsJsonContext` 会自动把新增 string 明文写入 settings.json |
| 中 | T-5 | 必须显式关闭思考模式 | 官方默认开启，`reasoning_content` 按输出 token 计费，成本/延迟放大 3~10 倍 |
| 中 | T-6 | 模型名与价格会漂移 | `deepseek-flash` 是别名；旧名兼容有期限 |
| 中 | T-7 | 工具栏按钮会撑宽最小窗口 | 最小宽度由工具栏首选宽度决定（`OcrResultForm.cs:116`），预计 +60px |
| 低 | T-8 | 超长文本需截断或分段 | 1M 上下文不是瓶颈，成本与等待时间才是 |

---

## 一、结论先行：本地 vs 云端

### 1.1 为什么「本地」不能简单回答「不可行」

`README.md:354-377` 与 `AGENTS.md:390-392` 已经记录了一次离线翻译评估：现有引擎
`Sdcb.SimdPaddleOCR.OnnxSharp` 实测跑不了标准 Transformer（INT8 不支持、缺
`LayerNormalization` / `Attention`）。这个结论只否定了**「继续用现有 OCR 引擎跑 NMT」**这一条路，
并不等于「本地方案全部不可行」。本次把本地拆成 5 条路径重新评估：

| 路径 | 体积增量 | 单文件 / AOT | 许可 | 译文质量 | 延迟 | 工程成本 | 结论 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| L1 继续用 `Sdcb.SimdPaddleOCR.OnnxSharp` 跑 NMT | 模型另计 | 无新增原生依赖 | 取决于模型 | — | — | 需改引擎 | **不可行**（算子/量化缺失，已有实测） |
| L2 自带 `Microsoft.ML.OnnxRuntime` + NLLB | 0.6~2.4 GB | **多出原生 DLL**，需自解压 | NLLB 多为 CC-BY-NC | 中上 | CPU 1~5 s | 1~2 人月 | 不推荐 |
| L3 纯 C# 词典 / 规则翻译 | 10~20 MB | 无风险 | 词典需逐项核 | 仅词级，段落不可用 | <50 ms | 0.5 人月 | 不推荐 |
| L4 纯 C# 重写 Transformer / 卷积 NMT | 100~300 MB | 可控 | 可商用模型少 | 中 | 2~5 s | 3~6 人月 | 不推荐 |
| L5 不打包模型，走本机 OpenAI 兼容端点 | **0** | 无风险（同一 HTTP 通道） | 取决于用户自装模型 | 高（7B 以上） | 1~3 s（GPU）/ 10 s+（CPU） | ≈0 | **可行，本期不做** |

体积数据为量级估算：NLLB-200-distilled-600M fp32 约 2.4 GB、int8 约 0.6 GB；
Opus-MT 单方向 fp32 约 300 MB；`Microsoft.ML.OnnxRuntime` 的原生库约 15~50 MB（视构建而定）。

### 1.2 逐条判断

- **L1 不可行的根因不是「模型太大」，而是引擎能力不足**：`DType` 只声明 `F32/I32/I64/U8`，
  且把 I64 编为 3（与 ONNX 规范中 `3=INT8` 冲突）；算子表缺 `LayerNormalization` 与 `Attention`。
  要继续走这条路，必须先替换或大幅扩展推理引擎，等于重做 L2/L4。
- **L2 与产品定位冲突**：发布目录现在只有 `SnipasteOcr.exe` 与 `.pdb`（`README.md:266-267`），
  引入原生 ONNX Runtime 后必须随包分发 DLL 或自解压，AOT 单文件的「一个 exe 就能跑」被破坏。
  再叠加 0.6~2.4 GB 模型与 CC-BY-NC 许可，商业分发直接不可行。
- **L3 只能算术语表**：词典/规则无法处理语序、否定、术语一致性，达不到用户对「AI 翻译」的预期。
- **L4 的成本远高于收益**：3~6 人月只是把质量做到「中等」，而云端 API 的接入成本是几天。
- **L5 才是真正值得保留的「本地」选项**：模型跑在用户自己的 Ollama / LM Studio 里，本程序不打包、
  不分发模型，零体积、零许可风险；而且它与 DeepSeek 共用同一套 OpenAI 兼容请求/解析代码，
  后续只需增加一个 BaseUrl 配置项。**本期按用户决定固定 DeepSeek，但文档明确这条路径存在**，
  避免用户已经装了本机模型却被产品否认。

### 1.3 一句话结论

> **不要内嵌翻译模型；推荐 DeepSeek API；本机端点作为零代码增量的后续扩展点。**

如果未来「完全离线」变成不可妥协的硬需求，正确做法不是往 exe 里塞模型，而是把 BaseUrl
指向用户自装的本地推理服务 —— 这既保住了单文件，也把模型体积与许可责任移出了本产品。

---

## 二、DeepSeek 接口核对（2026-11-10）

### 2.1 官方事实

| 项目 | 事实 | 对实现的影响 |
| --- | --- | --- |
| Base URL | `https://api.deepseek.com`，OpenAI 兼容 | 请求/响应结构可按 OpenAI Chat Completions 理解 |
| 端点 | `POST /chat/completions` | 只用一个端点，无需 SDK |
| 认证 | `Authorization: Bearer <API Key>` | Key 由用户自备；程序不内置任何 Key |
| 模型 | `deepseek-flash`（V4.1 Flash；旧名 `deepseek-v4-flash` 仍可调用）与 `deepseek-v4-pro` | **不要把版本号写进 UI 文案**；默认值放配置，允许用户改 |
| 上下文 / 输出 | 1M tokens / 最大 384K tokens | 截图文本远小于上限，瓶颈是成本与等待时间 |
| 思考模式 | **默认开启**；关闭参数 `{"thinking":{"type":"disabled"}}`；思考内容在 `reasoning_content` | 翻译必须关闭；解析时只取 `choices[0].message.content` |
| 价格（元/百万 tokens） | flash：输入缓存命中 0.02/0.04、未命中 1/2、输出 4/8；pro：0.15/0.30、4.5/9、13.5/27 | 数字为「优惠时段/高峰时段」，具体时段以官网为准 |
| 错误码 | 400 格式错、401 认证错、402 余额不足、422 参数错、429 限流、500/503 故障或繁忙 | 401/402/422 不重试；429/503 最多退避重试一次 |

> 模型名与价格是本次核对时的事实，官方会调整。文档与代码都应把「核对日期」写清楚，
> 默认模型放配置项，不把 `V4.1` 之类版本号硬编码进界面文案。

### 2.2 请求设计建议

- **非流式**：单屏文本的响应通常在数秒内，非流式可以让解析与错误处理保持简单；
  流式（SSE）只在前端需要逐字显示时才值得引入，本期不需要。
- **关闭思考**：`{"thinking":{"type":"disabled"}}`。这是费用与延迟的关键开关，
  漏掉会让一次翻译多出大量推理 token，且用户看到的等待时间明显变长。
- **提示词**：
  - system：专业翻译；只输出译文；保留段落、列表与换行结构；不解释、不加引号；
    若原文已经是目标语言，原样返回。
  - user：目标语言 + 原文。
- **参数**：`temperature` 0.1~0.3；`max_tokens` 取「输入字符数 × 2 + 256」并与 384K 取小；
  不需要 `response_format`。
- **超时与取消**：单次请求 20 s 超时；窗口关闭或用户取消时通过 `CancellationToken` 中止。
- **解析**：`content` 可能为 `null` 或空串，必须判空并转成「服务未返回译文」；
  `reasoning_content` 不展示、不拼接。
- **错误映射**：把状态码转成中文可操作提示（Key 无效 / 余额不足 / 请求太频繁 / 网络不可用），
  不要把原始 JSON 或异常堆栈直接甩给用户。
- **不记录**：请求体、响应体、Key 都不写入日志或临时文件。

### 2.3 成本估算

以「一屏 400~800 汉字」估算：输入约 500~1000 tokens（含提示词），输出约 500~1000 tokens。

| 场景 | flash 单次 | 1000 次 |
| --- | --- | --- |
| 优惠时段、思考关闭 | 约 0.25~0.5 分 | 约 2.5~5 元 |
| 高峰时段、思考关闭 | 约 0.5~1 分 | 约 5~10 元 |
| 开启思考（输出放大 3~10 倍） | 约 1~8 分 | 约 10~80 元 |

结论：正常使用时成本可以忽略；**真正的风险是「忘记关闭思考模式」**，而不是单价。

---

## 三、NativeAOT / 单文件影响

- `HttpClient` / `SocketsHttpHandler` 在 NativeAOT 下可用；项目当前没有任何网络代码，
  但 `System.Net.Http` 已在隐式 using 中，不需要新增原生依赖。
- **JSON 必须源生成**：新增翻译请求/响应 DTO 时，必须挂到 `JsonSerializerContext` 上
  （按 DTO 根类型追加 `[JsonSerializable]`）。仓库现有约定见 `AppSettings.cs:66-72` 与
  `AGENTS.md:125-126`；反射式 `JsonSerializer` 在裁剪后会在运行时失败。
- 发布产物应仍然只有 `SnipasteOcr.exe` 与 `.pdb`；HTTP/JSON 代码预计带来 **1~4 MB** 体积增量，
  必须真跑 `scripts/publish.cmd` 并运行产物确认，不能只看 `dotnet build`。
- 代理：`SocketsHttpHandler` 默认使用系统代理。需要验证公司网络/代理环境下是否可达，
  失败时给出「网络不可用或代理拦截」而不是裸异常。
- `InvariantGlobalization=true` 不影响 HTTP 与中文提示词；但**不要用 `CultureInfo`
  判断源语言/目标语言**，语言选择应显式配置。

---

## 四、隐私与「完全离线」承诺

这是本方案最大的产品风险，优先级高于技术实现。

### 4.1 现状

- `README.md:3-5`：本地离线识别；翻译为可选联网（默认关闭）。
- `README.md:178`、`:266-267`：单文件约 163 MB，发布目录只有 exe 与 pdb。
- `dist/SnipasteOCR-20261109/使用说明.txt:12`：「识别全程离线, 模型已内嵌, 不联网。」
- `AGENTS.md:390-392`：不要尝试加离线截屏翻译。

只要加入云端翻译，**「不联网」这句话就不再无条件成立**。

### 4.2 必须同时满足的约束

1. **默认关闭**：不点「翻译」不发任何请求；OCR 主路径零改动。
2. **首次知情同意**：第一次点「翻译」时明确告知「截图中的文字将上传到 `api.deepseek.com`」，
   并说明只上传文字、不上传图片；用户拒绝则不发送。
3. **同意状态持久化且进程级共享**：剪贴板识别路径复用同一窗体（`SnipCoordinator.cs:34-62`），
   不能每开一个窗口就重复弹窗。
4. **只发文本**：发送 OCR 后的纯文本，不发送截图、检测框、文件名或设备信息。
5. **不落盘**：请求/响应/Key 不写日志；不缓存译文。
6. **文案同步**：README 与使用说明改为「识别全程离线；翻译为可选的联网功能，默认关闭」，
   并链接本评审文档。

### 4.3 建议的告知文案（示例）

> 翻译需要联网：本次会把识别出的**文字**发送到 DeepSeek（api.deepseek.com）进行翻译，
> 不会上传截图。识别本身仍然全程离线。是否继续？

按钮：`继续翻译` / `取消`。取消后本次不发送，设置中随时可以清除已同意状态与 API Key。

---

## 五、API Key 存储

### 5.1 风险

`AppSettings` 的字段由 `SettingsJsonContext` 按根类型自动纳入（`AppSettings.cs:66-72`）。
如果直接新增 `public string ApiKey { get; set; } = "";`，Key 会**明文**写进
`%LOCALAPPDATA%\SnipasteOCR\settings.json`，任何能读该文件的进程/备份都能拿到。

### 5.2 推荐方案：DPAPI

- 用 `CryptProtectData`（CurrentUser）加密后 Base64 存入 `TranslateApiKeyProtected`；
  P/Invoke 放在 `NativeMethods` 或独立小类中，与仓库现有 Win32 调用风格一致。
  也可以使用 `System.Security.Cryptography.ProtectedData` NuGet 包，但会新增依赖，需重新做 AOT 验证。
- 解密失败（换机器、换 Windows 账户、文件被拷贝）时提示「请重新输入 API Key」，
  **不得静默回退到明文**。
- 设置对话框只显示掩码（末 4 位），提供「清除 Key」；输入框用 `PasswordChar`。
- Key 只保存在内存期间用于请求，不进入异常消息、状态栏或日志。

### 5.3 备选：明文存储

如果为了首版最小化而选择明文，必须在 README 与设置界面明确标注风险。
本评审**不推荐**：DPAPI 的实现量很小，而明文 Key 是本方案唯一会造成实际损失的安全项。

---

## 六、落点设计（已实施）

### 6.1 配置字段

| 字段 | 类型 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `TranslateApiKeyProtected` | string | `""` | DPAPI 加密后的 Base64 |
| `TranslateModel` | string | `"deepseek-flash"` | 允许用户跟随官方改名 |
| `TranslateTargetLanguage` | string | `"自动（中↔英）"` | 目标语言；`自动` 时中文译英文、英文译中文；可固定为 English / 简体中文 / 日本語 / 한국어 / 自定义 |
| `TranslateConsentGiven` | bool | `false` | 首次知情同意 |

均为简单类型，`SettingsJsonContext` 无需新增根类型；旧配置缺少字段时反序列化会保留初值，
`SettingsStore.Normalize` 不需要特判。

### 6.2 结果窗口

- 在「导出」下拉之后加一个「翻译」按钮：`OcrResultForm.cs:265` 附近；
  不要塞进「导出」下拉，可发现性太差。
- **已知副作用**：窗口最小宽度由工具栏首选宽度决定（`OcrResultForm.cs:116`），
  新增按钮会把小截图窗口撑宽约 60px。实现后要重新看一遍小截图观感，
  并确认 `InteractiveTests` 中没有依赖旧宽度的断言。
- 交互语义与复制一致：**有选区译选区，无选区译全部**。
  - 选区直接取 `GetSelectedText()` 的结果（`OcrResultForm.cs:1208`），不要回到 `_lines` 重拼。
  - 全部文本走 `OcrText.ToPlainText(CurrentLines(), joinAll: true)`（`OcrText.cs:264`），
    **不得新写断句/接续规则**，否则「复制段落」与译文断句会不一致。
- 识别未完成时按钮禁用；翻译中显示状态（复用 `SetStatus`），不阻塞窗口拖动/缩放。
- 超长文本：单次上限约 8000 字符；超出时提示「已截断，可选中部分后再翻译」。
  不做自动分段（会增加请求数、成本与拼接错误）。

### 6.3 独立翻译窗口

- 新建 `TranslateForm`：原文与译文两块只读文本（上下或左右），带「复制译文」「关闭」；
  `Esc` 关闭，复用 `UiFont` 与深色主题。
- 不修改 `OcrResultForm` 的自绘图片视口与缩放/命中逻辑 —— 这是选择独立窗口而不是右侧分栏的
  主要原因：改动面小、不会影响现有坐标与 DPI 逻辑。
- 窗口关闭时取消进行中的请求。

### 6.4 网络层

- 独立 `TranslateClient`，持有一个静态 `HttpClient`（不要每次 `new`），
  请求体构造与响应解析做成可离线测试的纯函数。
- 不挂到 `OcrService` 单例上，避免污染「识别状态」事件链与模型档位锁。
- 错误映射与重试策略集中在客户端，UI 只负责展示结果与状态。

### 6.5 设置入口

- 托盘菜单新增「翻译设置…」（`TrayController.cs:116-128`；命令 ID 从 `0x100A` 起；
  事件声明与 `ShowMenu` 分支同步补齐）。
- 必须新开一个小 Form：纯 Win32 菜单无法输入 Key，`HotKeyForm` 没有文本框，不能复用。
- 设置项：API Key（掩码 + 清除）、模型名、目标语言、已同意状态（可撤销）、
  「测试连接」可选。

---

## 七、风险清单

| 等级 | 风险 | 影响 | 缓解 |
| --- | --- | --- | --- |
| 高 | Key 明文落盘 | Key 泄露、被盗刷 | DPAPI；掩码显示；清除按钮；不写日志 |
| 高 | 「完全离线」承诺冲突 | 用户信任受损 | 默认关闭；首次知情同意；README/使用说明同步改口径 |
| 高 | 思考模式默认开启 | 成本与延迟放大 3~10 倍 | 请求里显式 `{"thinking":{"type":"disabled"}}` |
| 中 | 模型名/价格漂移 | 默认模型失效或估算失真 | 模型名放配置；文档记录核对日期；UI 不写版本号 |
| 中 | 代理/TLS 拦截 | 请求失败且提示不清 | 20 s 超时；把异常映射成「网络不可用/代理拦截」 |
| 中 | 429 / 503 | 偶发失败 | 最多退避重试一次；其余直接提示 |
| 中 | AOT/裁剪未实测 | 发布后才暴露 JSON/HTTP 问题 | 真跑 publish + 运行产物；响应 DTO 全部源生成 |
| 中 | 剪贴板路径重复弹同意 | 体验差 | 同意状态进程级共享、持久化 |
| 中 | 超长截图 | 费用高、等待久 | 8000 字符上限；提示改用选区 |
| 低 | 断网/未配置 Key | 点了没反应 | 明确提示并引导到设置；不阻塞 OCR |
| 低 | 剪贴板写入失败 | 复制译文失败 | 沿用现有复制失败提示模式 |

---

## 八、验收门槛与回归项（已实施）

### 8.1 离线自检（不联网）

- 请求体构造：端点、模型、`thinking` 关闭、`max_tokens`、`temperature` 正确。
- 响应解析：正常译文、`content=null`、空 `choices`、非 JSON、错误 JSON。
- 错误映射：401/402/422/429/503 分别落到预期中文提示；429/503 只重试一次。
- 截断：超过 8000 字符时的提示与发送内容一致。
- Key：DPAPI 往返、换账户解密失败提示、掩码只显示末 4 位。
- 文本来源：全部文本与选中文本都复用 `OcrText.ToPlainText` / `GetSelectedText` 的语义。
- 自动方向：中文→English、英文→简体中文、中英混排、显式目标语言不被自动覆盖。

### 8.2 交互自检

- 识别完成前「翻译」按钮禁用，完成后启用。
- 点击后弹出独立窗口，焦点正确；`Esc` 关闭并取消请求。
- 首次点击弹知情同意；拒绝后不发送；同意后不再重复弹。
- 断网、错误 Key、余额不足分别给出可操作提示，窗口不崩。

### 8.3 发布验证

- `scripts/publish.cmd` 真跑 AOT；发布目录仍只有 `SnipasteOcr.exe` 与 `.pdb`。
- 运行产物完成一次真实翻译；记录体积增量。
- 记录 AOT 告警；不能以 `dotnet build` 通过代替发布验证。

---

## 九、当前决定与未决问题

已决定（本次实施）：

1. 默认自动方向（中文→英文 / 英文→简体中文）；可在翻译设置里固定 English / 简体中文 / 日文 / 韩文或自定义。
2. 不展示 token 费用账单，只在翻译窗口状态栏显示本次 token 用量。
3. 知情同意一次同意长期有效，可在翻译设置中撤销。
4. 本期不暴露 BaseUrl，固定 DeepSeek；本机 Ollama 支持记为后续扩展项。
5. 不分配翻译快捷键。

仍待产品确认：

1. 截图文字可能包含个人信息/商业信息，是否需要企业版直接禁用该功能。
2. 是否需要流式输出（当前非流式，单屏文本数秒内返回）。

---

## 十、资料与代码引用

- `README.md:354-377` — 离线翻译既有评估（INT8、算子、NLLB/LMSupply）。
- `AGENTS.md:390-392` — 「不要尝试加离线截屏翻译」的约束来源。
- `src/AppSettings.cs:66-72` — 源生成 JSON 上下文；新增简单字段自动纳入。
- `src/AppSettings.cs:102-193` — `Load` / `Normalize` / `Save` / `Update`。
- `src/OcrResultForm.cs:116` — 最小窗口宽度由工具栏首选宽度决定。
- `src/OcrResultForm.cs:156-273` — 工具栏构建；`:265` 为「导出」按钮插入点。
- `src/OcrResultForm.cs:1208` — `GetSelectedText`。
- `src/OcrResultForm.cs:1312-1356` — OCR 完成回调（翻译按钮启用的时机）。
- `src/SnipCoordinator.cs:34-62` — 剪贴板识别路径复用同一结果窗口。
- `src/TrayController.cs:16-128` — 托盘命令 ID 与菜单构建。
- `src/OcrText.cs:264` — `ToPlainText(lines, joinAll)`；译文文本来源。
- `src/TranslateClient.cs` — 请求构造/响应解析/错误映射/截断；`TranslateJsonContext` 源生成。
- `src/ProtectedKey.cs` — DPAPI 加密（`CryptProtectData` + 固定 entropy）。
- `src/TranslateForm.cs` / `src/TranslateSettingsForm.cs` — 译文窗口与设置对话框。
- `src/ClipboardText.cs` — 剪贴板换行统一入口（结果窗口与译文窗口共用）。
- `tests/AnnotationTests/TranslateCheck.cs` — 56 项离线断言（请求体/解析/错误/重试/截断/DPAPI）。
- `src/SnipasteOcr.csproj` — `PublishAot`、`InvariantGlobalization`、裁剪根程序集。
- `scripts/publish.cmd` — AOT 发布与 `-SelfTest` 入口。
- DeepSeek 官方文档：`https://api-docs.deepseek.com/`（首次调用、模型与价格、思考模式、错误码），
  核对日期 2026-11-10。

## 十一、实施结果

- 离线自检：`dotnet run --project tests/AnnotationTests`，**400 项全部通过**（新增翻译 56 项）。
- 交互自检：`dotnet run --project tests/InteractiveTests`，**130 项全部通过**（新增翻译窗口与设置 10 项）。
- AOT 发布：`scripts/publish.ps1 -OutputDir '' -SelfTest` 成功；publish 目录仍只有
  `SnipasteOcr.exe` 与 `.pdb`；exe 为 171,136,000 字节（约 163 MiB），较实施前增加约 0.8 MB；
  启动冒烟测试 3 秒后进程仍在运行。
- 未做真实 API 调用（当前无 Key）：请求体、响应解析、错误映射与重试策略由固定 JSON 离线断言覆盖，
  界面由注入假翻译结果验证。首次实机使用时需确认 401/402 等提示与真实响应一致。
- 产品入口：结果窗口工具栏「翻译」；托盘菜单「翻译设置…」。
- 隐私：识别全程离线不变；首次点翻译弹知情同意；只发送 OCR 文字；Key 用 DPAPI 加密。
