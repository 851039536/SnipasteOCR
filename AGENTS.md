# AGENTS.md — SnipasteOCR

仿 Snipaste 的截图 OCR / 标注小工具。.NET 10 WinForms + **NativeAOT**,发布为单个原生 exe(约 159 MB),
OCR 引擎与中文模型全部内嵌,**完全离线**。

用户文档见 [README.md](README.md)(功能、热键、模型档位、准确率实测数据)。本文件只写给改代码的 Agent。

---

## 仓库布局

```
src/                       产品代码 (唯一的出货工程, 也是唯一的 .slnx 成员)
  Program.cs               入口: 单实例 Mutex + 托盘 + 手动消息循环
  SnipCoordinator.cs       截图流程调度 (启动覆盖层 / 防止重复启动)
  SnipMode.cs              Ocr | Image
  SnipOverlayForm.cs       全屏覆盖层: 框选、遮罩、标注交互、结果合成
  Annotations/             标注引擎 (见下)
  OcrService.cs            OCR 引擎封装 (懒加载 / 档位切换 / 独立线程推理)
  OcrResultForm.cs         结果窗口: 框叠加、字符级拖选、缩放联动
  OcrText.cs               结果后处理: 阅读顺序排序、表格/CSV 重建 (纯函数)
  OcrBox.cs                检测框四角几何辅助 (5 处调用点共用)
  AppSettings.cs           配置持久化 + AOT 安全 JSON 源生成上下文
  HotKeyManager.cs         全局热键注册/重注册 (支持运行期换键)
  HotKeyForm.cs            热键设置对话框 (按键录制)
  TrayController.cs        Win32 托盘图标 + 菜单
  StartupRegistration.cs   开机自启 (写 HKCU 的 Run 键, 无管理员权限)
  NativeMethods.cs         Win32 P/Invoke (消息/热键/窗口/菜单/托盘/输入模拟)
tests/AnnotationTests/     离线自检, 136 项 (几何/命中/历史/渲染/工具栏/编辑框/OCR 后处理)
tests/InteractiveTests/    交互自检, 22 项 (真实窗口 + 真实按键, 需要桌面会话)
tools/DriveTest/           端到端驱动实际 exe (真实鼠标键盘事件 + 抓屏取证)
```

`src/Annotations/`:

| 文件 | 职责 |
| --- | --- |
| `Annotation.cs` | 单条标注模型: 几何 / 命中测试 / 克隆 / 平移 |
| `AnnotationTool.cs` | 工具枚举与展示元数据 |
| `AnnotationEngine.cs` | 渲染引擎 (六种工具 + 马赛克像素化 + 箭头几何) |
| `AnnotationHistory.cs` | 撤销/重做 (快照模型, 深度上限 100) |
| `AnnotationToolbar.cs` | 自绘工具栏 (图标全矢量) |
| `TextEditorOverlay.cs` | 文字输入**接收**载体(不负责显示) |
| `UiFont.cs` | 界面字体探测与回退 |

> README「项目结构」一节写的是 `SnipasteOcr/` 前缀,与实际的 `src/` 不一致 —— **以本文件为准**。

---

## 构建与验证

需要 .NET 10 SDK。所有命令用 PowerShell 执行。

```powershell
# 调试运行
dotnet build src/SnipasteOcr.csproj -c Debug

# AOT 发布 (win-x64 单文件原生 exe) —— 构建验证必须走这一步
dotnet publish src/SnipasteOcr.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true
# 产物: src/bin/Release/net10.0-windows/win-x64/publish/SnipasteOcr.exe
```

```powershell
# 离线自检 (136 项), 无需桌面会话 —— 改完必跑
dotnet run --project tests/AnnotationTests              # 期望输出「全部通过」, 退出码 0
dotnet run --project tests/AnnotationTests -- sample    # 额外输出六种标注样例图
# 交互自检 (22 项) —— 需要可用的桌面会话
dotnet run --project tests/InteractiveTests
# 端到端驱动实际 exe (可选)
dotnet run --project tools/DriveTest -- <SnipasteOcr.exe 完整路径>
```

**改动的验证门槛**:

- 纯逻辑 / 几何 / 渲染 → `AnnotationTests` 必须全绿。
- 任何涉及**焦点、键盘输入、可见性**的行为 → 必须跑 `InteractiveTests`。
  **反射直接调用控件方法会绕过真实的焦点链与绘制路径**,会掩盖 bug,不能替代真实窗口验证。
- 影响 AOT 裁剪或模型资源的改动 → 必须真跑一次 `publish` 并运行产物,**不能只看 `build` 通过**。

---

## 硬性约束(踩过坑,别重犯)

### NativeAOT / 裁剪

- **JSON 必须源生成**:配置走 `SettingsJsonContext`(`JsonSerializerContext`)。直接用反射式
  `JsonSerializer` 会在裁剪后运行时失败 —— 这是 NativeAOT 最常见的坑。新增设置字段必须给默认值,
  旧配置文件反序列化后仍要可用。
- **模型程序集必须全部保留**:`TrimmerRootAssembly` 里 Medium 与 Tiny **两个**都要写,
  少一个则运行时切换档位找不到嵌入资源。改 `csproj` 后要真的跑一遍发布验证切换。
- **`IlcInstructionSet=avx2` 不能改**:SimdPaddleOCR 官方要求,x64 AOT 不指定会裁掉 SIMD 内核,
  速度差一个数量级。
- **不用字体画图标**:工具栏图标一律**矢量绘制**。踩过的坑:原先用 `↶`/`↷`(U+21B6/U+21B7)
  配 `Segoe UI Symbol`,该字体在目标机器不存在且这两个码位在本机 229 个字体族中都没有字形,
  渲染出 **0 像素**、按钮空白且无任何报错。

### 字体

- 所有字体经 `UiFont.Create(...)` 创建,不要直接 `new Font("Microsoft YaHei UI", ...)` ——
  字体缺失时 WinForms 会**静默回退**到 Microsoft Sans Serif,中文显示不出来还不报错。
  新增字体名要加进 `UiFont` 的候选列表。

### 单一来源(本项目的核心不变量)

同一份逻辑写两遍,就会在容差/阈值漂移后出现"预览与导出不一致""列表与表格不一致"。以下几何与规则
**必须复用,不得各自实现一份**:

| 唯一来源 | 覆盖的调用点 |
| --- | --- |
| `OcrText.ClusterRows`(私有,内部共用) | 阅读顺序排序 **和** 表格/CSV 重建 **和** 段落/Markdown 导出的分行 |
| `OcrText.BuildParagraphs`(私有,内部共用) | `ToPlainText` **和** `ToMarkdown` 的段落构造与行内接续 |
| `OcrBox`(`Corners`/`TopEdge`/`BottomEdge`/`AxisAligned`/`Contains`) | 画框、命中测试、字符范围高亮、包围盒、轴对齐 |
| `AnnotationEngine.ArrowGeometry.TryCreate` | 标注箭头 **和** 工具栏箭头图标 |
| `AnnotationEngine.OutlineColorFor` | 文字标注描边 **和** 正在输入的编辑框描边 |
| `AnnotationEngine.Draw` | 覆盖层实时预览 **和** 最终合成导出 |
| `AnnotationToolbar.ComputeLayout`(私有,内部共用) | 工具栏绘制 **和** 宽度测量(`MeasurePreferredWidth`) |

- `OcrBox.TopEdge` 与 `BottomEdge` 的取点顺序必须**同向**(`X1→X2` 与 `X4→X3`),
  否则按同一参数 `u` 插值出的高亮四边形会自交。
- `ArrowGeometry.TryCreate` 在起止点重合(`len < 1px`)时**返回 null 而非放行 NaN**,
  调用方必须判空 —— 否则 GDI+ 会静默画出垃圾或抛异常。
- **工具栏布局只有 `ComputeLayout` 一份**:`OnPaint` 与 `MeasurePreferredWidth` 都从它取坐标。
  踩过的坑:两边曾各写一套 `x += ...` 累加公式,结果宽度与实际布局不符 ——
  尾部凭空多出 27px 空白,收紧公式后又反过来溢出 2px。**新增按钮必须同时改 `ComputeLayout`**,
  且 `ToolbarCheck` 有断言守着"尾部留白 == 7px"与"宽度 ≥ 取消按钮右边界"。
- 结果窗口的导出按钮(`复制段落`/`复制 MD`)必须走 `OcrText.ToPlainText`/`ToMarkdown`,
  二者共用 `BuildParagraphs`;若各写一份行内接续规则,同一屏文字用两种格式导出会得到不同断句。

### 坐标系与 DPI

- 标注几何**一律存位图物理像素**(与 `SnipOverlayForm._screen` 同坐标系),不是窗口客户区逻辑坐标。
  绘制/命中/字符定位三套坐标统一经 DPI 换算(Per-Monitor DPI v2)。
- 覆盖层预览用 `Graphics.ScaleTransform` 缩放绘制,导出时用 `TranslateTransform` 平移 ——
  同一份数据、同一个渲染引擎,因此预览与导出**像素级一致**。

### 窗口 / 焦点(WinForms 的坑)

- **覆盖层必须自己抢前台**:`Form.Show()` 只显示、**不激活**。必须在 `OnShown` 里用
  `AttachThreadInput` 挂到当前前台线程再 `SetForegroundWindow`(后台进程直接调用会被前台锁定拒绝),
  否则表现为"能框选、能点工具栏,但打字没反应"。
- **覆盖层设了 `KeyPreview = true`**:文字编辑期间必须同时在 `ProcessCmdKey`/`ProcessDialogKey`
  让行,否则输入的数字会被当作工具快捷键吞掉。
- **输入文字的显示不走子控件**:覆盖层在 `OnPaint` 里整屏绘制底图,`SupportsTransparentBackColor`
  依赖的"父窗口先画背景、子控件再叠加"协作在此**不成立**,透明子控件会被父窗口整屏绘制覆盖
  (实测打字前后 **0 像素**差异)。因此文字显示由覆盖层自己的 `DrawEditingText` 完成,
  `TextEditorOverlay` 被放到屏幕外、仅承担接收键盘输入(焦点与可见性无关)。

### 并发 / OCR

- `OcrService` 的 **取引擎与推理必须在同一个 `lock (_gate)` 内**完成,
  否则切换档位时 `Dispose()` 会打断另一线程正在进行的 `Run()`。
- 档位切换只写 `_requestedProfile`(`Volatile`,int)并置脏,真正加载发生在**下一次识别的后台线程**上,
  不阻塞 UI。
- **推理不锁图**:识别前对位图做瞬时 `LockBits` 拷贝到 `byte[]`,推理在独立缓冲上进行,
  不持有位图锁,UI 线程可随时重绘。
- `HotKeyManager` 换键时**先注册新键(用 probe id),成功后才注销旧键**,失败则回滚保留原热键,
  避免"换失败导致两个热键都没了"。

### 设置读写

设置落盘到 `%LOCALAPPDATA%\SnipasteOCR\settings.json`。所有异常一律吞掉并回退默认值 ——
**设置读写失败绝不能让程序崩溃或无法启动**。新增字段走 `AppSettings` + `SettingsStore.Update`。

### 开机自启 (`StartupRegistration`)

- 写 **HKCU** 的 `Software\Microsoft\Windows\CurrentVersion\Run`,不用 HKLM(HKLM 需要管理员权限,
  会触发 UAC,与"单文件绿色工具"的定位冲突)。
- 用 `Environment.ProcessPath` 取自身路径,**不要用 `Assembly.Location`** ——
  单文件/AOT 下它返回空串(见 ILC 的 IL3000 告警),写进注册表就成了无效项。
- 写入时路径**必须用引号包裹**,否则含空格的路径会被系统按第一个空格截断。
- 与 `SettingsStore` 同一约定:**任何异常一律吞掉并返回 false**,注册表被策略锁定时只是自启不生效,
  绝不能影响程序其它功能。
- **勾选状态以注册表为准,不以设置文件为准**:用户可能在任务管理器里手工禁用它,
  这时若还显示"已勾选"就是在骗人。启动时以设置文件为准去**修注册表**(单向),不要反向覆盖设置。

---

## 代码风格

- 全部注释、XML doc、`Console` 输出、提交信息用**中文**;标识符用英文。
- 文件头/类型/方法写 XML doc 说明**为什么这么做**,尤其是反直觉的取舍和踩过的坑
  (现有代码普遍记录"曾经怎么错、为什么改成现在这样"),新增代码请沿用这个习惯。
- 私有字段 `_camelCase`,常量 `PascalCase`,集合用集合表达式 `[]` / `[.. x]`。
- `Nullable` + `ImplicitUsings` 全程开启。`AllowUnsafeBlocks` 已开,但优先用
  `Marshal.Copy` 而非 `unsafe`(见 `OcrService.Snapshot`)。
- 文件名空间:`src/Annotations/*` 用 `SnipasteOcr.Annotations`,`NativeMethods.cs` 用 `SnipasteOcr.Native`,
  其余 `SnipasteOcr`。
- 自检工程**链接产品源码**而非复制(`<Compile Include="..\..\src\..." Link="..." />`),
  保证验证对象就是出货代码。新加的待验证文件要同步加进 `AnnotationTests.csproj`
  (或用 `InteractiveTests.csproj` 的通配 `..\..\src\**\*.cs`)。
- 测试断言用具名 `CheckRunner`,不要各写一份 `int passed` + `Check(...)` 样板。
- 断言应先断言行数/长度再做逐项索引,否则实现漂移时会抛 `IndexOutOfRange` 让整个自检崩掉,
  而不是报一条 FAIL。
- **测试辅助函数本身也要写对**:`SplitLines` 曾把 `Split('\n', RemoveEmptyEntries)` 写在
  `TrimEnd('\r')` **之前**,于是 CRLF 文本里的空行被切成 `"\r"`、在去空时存活下来,
  凭空多出一行 —— 断言失败指向的是辅助函数而非产品代码。判空与去尾字符的顺序要对。

### OCR 素材来源

`OcrService.Recognize(Bitmap, CancellationToken)` 接受**任意位图**,不限于截图。
因此"识别剪贴板图片"这类扩展**不需要改 `OcrService`**,只需新增取图路径并复用 `OcrResultForm`
(见 `SnipCoordinator.StartFromClipboard`) —— 这样结果窗口的选词/复制/导出行为与截图路径天然一致。

- `Clipboard.GetImage()` 可能因剪贴板被其它进程独占而抛异常,必须兜住并返回 false,
  由调用方给出提示(否则用户点了菜单没反应,不知道原因)。

---

## 已知限制

- 仅 x64 Windows,发布产物与编译机架构绑定;中文识别为主,西文可用但非最优。
- 字符级选区按宽度权重估算(汉字 1.0 / ASCII 0.6 / 空格 0.35),边缘与真实字符边界可能有 1~2px 偏差。
- **不要尝试加离线截屏翻译**:已在 README 记录评估结论 —— 当前技术栈不可行。
  `Sdcb.SimdPaddleOCR.OnnxSharp` 的 `DType` 不支持 INT8 量化张量,算子表缺 `LayerNormalization`
  与 `Attention`;主流可离线分发的 NMT 模型要么体积过大、要么依赖原生 DLL 破坏单文件、要么仅限非商业。
