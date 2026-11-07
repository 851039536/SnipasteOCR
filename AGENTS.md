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
  SnipGeometry.cs          框选几何唯一来源: 逻辑选区 → 导出物理矩形 / 有效选区判据
  Annotations/             标注引擎 (见下)
  OcrService.cs            OCR 引擎封装 (懒加载 / 档位切换 / 独立线程推理)
  OcrModelProfile.cs       模型档位枚举 (单独成文件, 便于设置自检不链接 OCR 引擎)
  OcrResultForm.cs         结果窗口: 框叠加、字符级拖选、缩放联动
  OcrText.cs               结果后处理: 阅读顺序排序、表格/CSV 重建 (纯函数)
  OcrBox.cs                检测框四角几何辅助 (5 处调用点共用)
  OcrSelection.cs          选词几何纯函数 (字符索引反解 / 多选阅读顺序名次)
  AppSettings.cs           配置持久化 + AOT 安全 JSON 源生成上下文
  HotKeyManager.cs         全局热键注册/重注册 (支持运行期换键)
  HotKeyForm.cs            热键设置对话框 (按键录制)
  TrayController.cs        Win32 托盘图标 + 菜单
  StartupRegistration.cs   开机自启 (写 HKCU 的 Run 键, 无管理员权限)
  SleepPreventer.cs        防休眠 (SetThreadExecutionState; 不移动鼠标)
  PinnedImageForm.cs       贴图窗口 (钉在屏幕上的置顶小窗, 可拖动/右键关闭)
  SnipSizeBadge.cs         框选实时尺寸徽标 (宽 × 高, 物理像素; 纯函数布局 + 自绘)
  NativeMethods.cs         Win32 P/Invoke (消息/热键/窗口/菜单/托盘/输入模拟)
tests/AnnotationTests/     离线自检, 237 项 (几何/命中/历史/渲染/工具栏/尺寸徽标/设置迁移/编辑框/OCR 后处理/选词几何)
tests/InteractiveTests/    交互自检, 61 项 (真实窗口 + 真实按键 + 拖拽实时尺寸 + 托盘菜单文案 + 结果窗口单击复制 + IME, 需要桌面会话)
tools/DriveTest/           端到端驱动实际 exe (真实鼠标键盘事件 + 抓屏取证 + 校验导出图)
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
# 离线自检 (237 项), 无需桌面会话 —— 改完必跑
dotnet run --project tests/AnnotationTests              # 期望输出「全部通过」, 退出码 0
dotnet run --project tests/AnnotationTests -- sample    # 额外输出六种标注样例图
# 交互自检 (61 项) —— 需要可用的桌面会话
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
| `SnipGeometry.ExportRect` | 实时尺寸徽标 **和** `RenderResult` 裁剪导出 **和** 无效选区兜底 |
| `SnipGeometry.MinDragSide` / `IsValidSelection` | 拖拽/松手判定 **和** 遮罩绘制 **和** 导出判据 |
| `SnipSizeBadge.Segments`(私有,内部共用) | 徽标文本度量 **和** 分段绘制 |
| `OcrSelection.CharIndexFromU` | 字符级拖选的光标索引 **和** 离线断言(唯一的中线判定实现) |
| `OcrSelection.BuildReadingRanks` | 多选/全选复制顺序 **和** 离线断言(内部复用 `OcrText.SortReadingOrder`) |

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
- **尺寸徽标报的数必须来自 `ExportRect`**:早期实现各写一份 `round(宽 × 缩放)`,且只在纵向判断了
  "上方放不下就塞进选区",横向没夹紧 —— 贴着屏幕右边框选时标签右半截被屏幕裁掉。
  现在布局统一由 `SnipSizeBadge.ComputeLayout` 夹紧四边(底部不放 —— 那里是工具栏),
  `SnipSizeCheck` 断言"任何选区位置下徽标都完整落在客户区内"。
- **徽标还要避开工具栏**:`PositionToolbar` 在选区贴底时会把工具栏翻转到选区上方,而工具栏是子控件,
  会整块盖住窗体自绘内容 —— 早期徽标正好落在同一位置,只剩 2px 可见。因此 `OnPaint` 必须把
  `_toolbar.Bounds` 传给 `ComputeLayout`,重叠时依次让到工具栏上方 → 选区内侧 → 工具栏下方。
- **字符光标索引必须允许等于文本长度**:`OcrSelection.CharIndexFromU` 返回 `0..n`(`n` = 行尾光标),
  按字符中线判定左右半格。踩过的坑:旧实现循环只推到 `n-1`,拖到行尾永远少选最后一个字符,
  且 `cur == t.Length` 的贴边收尾分支成了死代码。拖选复制与高亮共用 `_partial`,修一处两者同步修好。
- **清空选中只能走 `ClearSelection`**:`_selected`(整行)与 `_partial`(行内范围)必须同清。
  只清其一会留下"画面上没有高亮、`Ctrl+C` 却还能复制"的隐藏选区;Shift 取消最后一行时也要清掉
  指向已取消行的 `_partial`。`HasSelection` 只认 `_selected`,因为 `_partial` 必然伴随它。
- **多选复制的顺序必须按阅读顺序名次**:`GetSelectedText` 不能再按 `_lines` 检测顺序排序。
  检测顺序在多栏截图里可能右栏在前,直接按索引拼接会与「复制全部」不一致。
  名次由 `OcrSelection.BuildReadingRanks` 复用 `OcrText.SortReadingOrder` 生成,排序规则不得另写一份。
- **单击复制只在"未拖动的单击检测块"生效**:拖选(`_charDragMoved`)、`Shift` 多选(不进 `_charDrag`)、
  空白单击都不写剪贴板;复制范围就是当前检测块,不要合并同一视觉行(表格会粘列)。
  `OnMouseUp` 里复制后**不要**再调 `UpdateIdleStatus`,否则「已复制 N 字符」会立刻被「已选 N 字符」覆盖。
- **`OcrService.StatusChanged` 必须退订**:结果窗口用具名方法订阅,`OnFormClosed` 里 `-=`。
  匿名 lambda 无法退订,每开一次窗口就会在单例事件链上留下一个捕获 `this` 的委托,窗体无法回收。

### 坐标系与 DPI

- 标注几何**一律存位图物理像素**(与 `SnipOverlayForm._screen` 同坐标系),不是窗口客户区逻辑坐标。
  绘制/命中/字符定位三套坐标统一经 DPI 换算(Per-Monitor DPI v2)。
- 覆盖层预览用 `Graphics.ScaleTransform` 缩放绘制,导出时用 `TranslateTransform` 平移 ——
  同一份数据、同一个渲染引擎,因此预览与导出**像素级一致**。
- **文字标注用字形轮廓绘制,不要退回 `TextRenderer`**:`TextRenderer`(GDI)**不跟随 `Graphics` 的
  世界变换** —— 导出时 `TranslateTransform(-裁剪原点)` 会让文字按屏幕坐标落点、被画到裁剪图外
  ("预览有字、导出/识别结果里没有");高 DPI 预览的 `ScaleTransform` 同理(错位且字号偏大)。
  它还受系统 ClearType 影响:"彩色文字 + 白描边"会混出蓝/橙彩边;用 8 次偏移伪造描边更会叠出粉边。
  因此 `AnnotationEngine.DrawText` 改为 `GraphicsPath.AddString` 取轮廓,先 `DrawPath` 真描边再
  `FillPath` 填充;`MeasureText` 用同一套 GDI+ 字体与排版参数(`StringFormat.GenericTypographic`),
  光标/命中框因此与绘制一致。`RenderChecks` 守着:导出平移后文字仍在图内、预览缩放后位置正确、
  文字区域没有偏蓝像素。
- **贴图窗口的尺寸也要换算**:`PinnedImageForm` 收到的是**物理像素**位图,而 `ClientSize` 是
  **逻辑像素**。必须除以 DPI 因子(与 `OcrResultForm.DpiFactor` 同一套),否则 150% 缩放下贴图被放大
  1.5 倍且只画出左上角一部分。绘制时同样要按 `ClientSize` 尺寸还原,不能直接 `DrawImageUnscaled`。

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
- **贴图窗口拖动用 `WM_NCLBUTTONDOWN + HTCAPTION`,不要自己算鼠标偏移**:
  自己实现要处理"鼠标移出窗口/被遮挡/高 DPI 换算"一堆边界,交给系统的标题栏拖拽逻辑则全部自动正确
  (它内部用屏幕坐标与鼠标捕获)。调用前必须 `ReleaseCapture()`,否则 WinForms 自己的鼠标捕获
  会让系统的拖拽循环收不到消息。贴图另设 `WS_EX_NOACTIVATE` 且重写 `ShowWithoutActivation => true`,
  避免钉住时抢走用户正在输入程序的焦点 —— 注意 `ShowWithoutActivation` 是**只读属性, 只能重写**,
  直接赋值编译不过。
- **贴图按钮只加在 F3(图片)模式**:OCR 模式的产物是文字,钉一张原图没有意义。
  实现上由 `AnnotationToolbar.ShowPin` 控制:隐藏时必须同时从 `ComputeLayout` 与命中去掉,
  不能只改可见性 —— 否则会留下一个"可点但 `PinToScreen` 直接 return"的假按钮。

### 并发 / OCR

- `OcrService` 的 **取引擎与推理必须在同一个 `lock (_gate)` 内**完成,
  否则切换档位时 `Dispose()` 会打断另一线程正在进行的 `Run()`。
- 档位切换只写 `_requestedProfile`(`Volatile`,int)并置脏,真正加载发生在**下一次识别的后台线程**上,
  不阻塞 UI。
- **推理不锁图**:识别前对位图做瞬时 `LockBits` 拷贝到 `byte[]`,推理在独立缓冲上进行,
  不持有位图锁,UI 线程可随时重绘。
- `HotKeyManager` 换键时**先注册新键(用 probe id),成功后才注销旧键**,失败则回滚保留原热键,
  避免"换失败导致两个热键都没了"。
- **虚拟键码别手算**:`VK_F1 = 0x70` 起算,所以 `VK_F3 = 0x72`、`VK_F4 = 0x73`。
  踩过的坑:默认值和「恢复默认」都曾把 **F3 写成 `0x73`(实际是 F4)** ——
  表现为「按 F3 毫无反应,按 F4 反而截图」。因为显示走的是 `HotKeyManager.KeyName`(由 vk 反推,
  所以界面老老实实显示"F4"),代码注释与 README 却写着 F3,两边对不上、排查方向被完全带偏。
  `KeyName` 是唯一可信的名字来源;**写死键码时必须同时核对 `KeyName` 的输出**。
- 改**默认热键**不只改 `AppSettings` 的初值 —— 错误值可能已经**落盘**到老用户的 `settings.json`,
  必须像 `SettingsStore.Load` 里那样做一次性迁移,否则老用户升级后症状依旧。
  迁移判据要收窄(这里要求整组值等于旧版默认组合),**别无条件改写**,否则会悄悄抹掉用户刻意的选择。
- **两个热键的修饰键必须各存一份**:早期 `HotKeyForm` 只把第一个框的修饰键交出去,而 `RegisterAll`
  对两项共用同一组 —— 第二个框可以显示 `F3`、实际注册的却是 `Ctrl+F3`。现在
  `hotKeyModifiers` / `hotKeyImageModifiers` 各自独立;新字段用 **-1 表示"旧配置没有这一项"**,
  由 `SettingsStore.Normalize` 继承旧值。**首次运行与配置损坏回退也要走 Normalize**,
  否则 -1 会被当成位掩码传给 `RegisterHotKey`(注册全部失败)。
- **托盘菜单与提示里的按键文案必须动态生成**:菜单项和 `szTip` 曾写死 `F1/F3`,改键后界面与事实不符。
  用 `ModifyMenu`(按命令 ID) + `Shell_NotifyIcon(NIM_MODIFY)` 刷新,**不要重建菜单** ——
  `ShowMenu` 依赖菜单项 ID 稳定来还原命令。

### 设置读写

设置落盘到 `%LOCALAPPDATA%\SnipasteOCR\settings.json`。所有异常一律吞掉并回退默认值 ——
**设置读写失败绝不能让程序崩溃或无法启动**。新增字段走 `AppSettings` + `SettingsStore.Update`。

新增字段若需要"旧配置没有时继承另一个字段",在 `SettingsStore.Normalize` 里做,并保证
**三条加载路径(文件存在 / 首次运行 / 反序列化失败)**都经过它;判据只看哨兵值,不要无条件改写用户显式存过的值。

布尔开关类新字段(如 `ClickToCopy`)直接给属性初值即可:旧 JSON 缺字段时反序列化会保留初值,
显式存过的 `false` 也不会被覆盖。结果窗口每次单击都读 `SettingsStore.Current`,因此托盘里切换开关后,
已经打开的结果窗口会立即响应,不需要重启或重建窗口。

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

### 防休眠 (`SleepPreventer`)

- **只用 `SetThreadExecutionState`,禁止用"定时微移鼠标"**:模拟鼠标移动会被本程序**自己的全屏
  覆盖层**反噬 —— F1/F3 框选与标注期间光标被偷偷移动会破坏框选;用户正在拖拽时还会被抢走鼠标。
  官方 API 不产生任何输入事件、不移动光标,是同样目的下唯一正确的入口。
- **必须由常驻线程调用**:该状态是**线程级**的,线程一退出就失效。所以只在 `Program.Main`
  (主消息循环线程) 上调用。若图省事丢进 `Task.Run` 或定时器线程,防休眠会随该线程结束悄悄失效 ——
  表现为"开了但没用"且没有任何报错。
- **三个标志一个都不能少**:`ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED`。
  只给 `ES_SYSTEM_REQUIRED` 时显示器照样息屏,用户看到的就是"屏幕黑了",等于没做。
  关闭时**只传 `ES_CONTINUOUS`**(不是 0) —— 传 0 在部分系统上不会清除而是被忽略,变成"关不掉"。
- **返回值是"调用前的旧状态",不是新状态**,别拿它直接当成功与否的判据。实测:
  开启返回 `0x80000000`、关闭返回 `0x80000003`、传 0 返回 `0x80000000` —— 后两者明显不是
  "本次请求的新状态"。判定只需 `result != 0`(0 = 失败)。
- **勾选以实际生效为准**:`SleepPreventer.IsActive` 记录真实结果,调用失败就不勾选、并把设置改回
  false —— 与开机自启同一套"不骗人"约定。
- **能力边界如实写在文案里**:只防空闲触发的休眠/息屏;**拦不住**合盖、电源键、开始菜单睡眠、
  屏保/策略锁屏。因此菜单文案是"防止电脑休眠",不要写成"保持屏幕常亮"这类做不到的承诺。
- 与 `SettingsStore`/`StartupRegistration` 同约定:**任何异常一律吞掉并返回 false**,不影响其它功能。

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
- **输入端到端工具必须检查调用结果**:DriveTest 的 `INPUT` 结构体曾少了联合体内存(x64 应为 40 字节),
  `SendInput` 一直静默失败(返回 0),但日志照常打印"已输入文字" —— 端到端验证形同虚设,
  真正的导出 bug 因此被掩盖。注入类 API 的返回值/错误码必须检查,否则"测试通过"没有意义。
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
- 文字标注用 GDI+ 字形轮廓绘制,**不做字体链接**:字体族缺字形时(如 emoji)不会自动回退到其它字体,
  与系统"缺字自动补字体"不同。中文由 `UiFont` 选定的字体覆盖,不受影响。
- **不要尝试加离线截屏翻译**:已在 README 记录评估结论 —— 当前技术栈不可行。
  `Sdcb.SimdPaddleOCR.OnnxSharp` 的 `DType` 不支持 INT8 量化张量,算子表缺 `LayerNormalization`
  与 `Attention`;主流可离线分发的 NMT 模型要么体积过大、要么依赖原生 DLL 破坏单文件、要么仅限非商业。
