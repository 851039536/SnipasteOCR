<#
.SYNOPSIS
    SnipasteOCR 的一键 AOT 发布脚本。

.DESCRIPTION
    把 README「从源码构建」里的手工步骤固化下来, 并自动处理几处实测踩过的坑:

      1. 残留文件导致 LNK1104 —— AOT 的最后一步 link.exe 打不开
         src/bin/Release/.../native/SnipasteOcr.exe 时, 绝大多数情况不是代码问题,
         而是上次构建残留(实测是一个 46MB 的 .pdb)或 exe 仍在运行占用文件。
         本脚本发布前自动清理并结束占用进程。
      2. 单实例互斥 —— SnipasteOcr 启动时有 Mutex, 旧实例在跑会导致产物无法覆盖,
         也让发布后的手工验收毫无意义。清理阶段一并结束。
      3. AVX2 指令集 —— IlcInstructionSet 已固定在 csproj 里, 本脚本不覆盖它。
         这是 SimdPaddleOCR 在 NativeAOT 下启用 SIMD 内核的硬性要求, 去掉会慢一个数量级。
      4. 绝不使用 -p:PublishSingleFile=true —— 单文件是 AOT 本身产出的,
         模型靠嵌入资源加载, 另加该开关会破坏模型加载。

    脚本不做任何"聪明"的隐式操作: 只发布、校验产物、按需自检与打包。

.PARAMETER Configuration
    构建配置。默认 Release。AOT 单文件只在 Release 下发布才有意义。

.PARAMETER OutputDir
    打包输出目录。默认 'dist' —— 即双击/无参数运行时也会真的打出 zip, 避免"以为没打包成功"。
    产物为该目录下的 SnipasteOCR-<日期>\ 子目录, 并生成同名 zip。
    只想要 publish 目录里的 exe、不打包时传 -OutputDir '' 显式关闭。

.PARAMETER SkipClean
    跳过清理步骤。仅在你确定没有残留与占用时使用 —— 跳过能省几十秒,
    但 LNK1104 会原样复现。

.PARAMETER SelfTest
    发布后运行离线自检 (tests/AnnotationTests, 136 项, 无需桌面会话)。
    产物能不能跑不等于逻辑没坏, 建议正式出包时开启。

.PARAMETER IncludePdb
    打包时一并带上 .pdb (46MB 调试符号)。默认不带 —— 分发不需要它,
    只有在需要收集线上崩溃栈时才用。

.PARAMETER NoZip
    打包时只复制文件到输出目录, 不生成 zip。

.NOTES
    Windows 下 .ps1 双击默认是用记事本打开, 并不会执行。
    要"双击就发布", 请双击同目录的 publish.cmd (或在资源管理器里右键
    publish.ps1 -> 使用 PowerShell 运行)。

.EXAMPLE
    # 最常用: 双击 publish.cmd 等效于下面这条 (发布 + 打包到 dist)
    .\scripts\publish.ps1

.EXAMPLE
    # 发布 + 自检 + 打包 (正式出包)
    .\scripts\publish.ps1 -SelfTest

.EXAMPLE
    # 只要发布产物, 不打包
    .\scripts\publish.ps1 -OutputDir '' -SelfTest
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    # 默认 dist: 双击运行(不带任何参数)也要真的打包出 zip。
    # 踩过的坑: 原先默认空值 -> 双击后只发布不打包, 用户以为"没打包成功"。
    # 只想要发布产物、不打包时传 -OutputDir '' 显式关闭。
    [string]$OutputDir = 'dist',
    [switch]$SkipClean,
    [switch]$SelfTest,
    [switch]$IncludePdb,
    [switch]$NoZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# 路径: 以脚本自身位置推导仓库根, 不依赖调用者的当前目录
# (README 里所有命令都要求在仓库根执行, 但脚本不该有这个约束)
# ---------------------------------------------------------------------------
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot 'src\SnipasteOcr.csproj'
$Rid         = 'win-x64'
$PublishDir  = Join-Path $RepoRoot "src\bin\$Configuration\net10.0-windows\$Rid\publish"
$ExePath     = Join-Path $PublishDir 'SnipasteOcr.exe'

function Write-Step([string]$Text) { Write-Host "`n=== $Text ===" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "  [OK] $Text"   -ForegroundColor Green }
function Write-Warn([string]$Text) { Write-Host "  [警告] $Text" -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
# 0. 前置检查
# ---------------------------------------------------------------------------
Write-Step '检查前置条件'

if (-not (Test-Path $ProjectPath)) {
    throw "找不到工程文件: $ProjectPath`n(脚本应位于 <仓库根>\scripts\ 下)"
}
Write-Ok "工程: $ProjectPath"

$sdkVersion = (& dotnet --version 2>&1)
if ($LASTEXITCODE -ne 0) { throw "dotnet 不可用, 请先安装 .NET 10 SDK。" }
Write-Ok "dotnet SDK: $sdkVersion"

# link.exe 由 MSVC 提供; AOT 最后一步依赖它。缺失时的报错较晦涩, 这里提前提示。
$linkFound = Get-Command link.exe -ErrorAction SilentlyContinue
if (-not $linkFound) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $msvc = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
        if ($msvc) { $linkFound = "VS: $msvc" }
    }
}
if ($linkFound) {
    Write-Ok "MSVC 链接器可用"
} else {
    Write-Warn "未能确认 MSVC 链接器 (link.exe)。若 AOT 失败, 请安装 Visual Studio 的「使用 C++ 的桌面开发」工作负载。"
}

# ---------------------------------------------------------------------------
# 1. 清理: 结束占用进程 + 删除残留构建输出
#    这两步是 LNK1104 (无法打开 ...native\SnipasteOcr.exe) 的根因
# ---------------------------------------------------------------------------
if (-not $SkipClean) {
    Write-Step '清理占用与残留'

    $procs = @(Get-Process -Name 'SnipasteOcr' -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        Write-Host "  发现 $($procs.Count) 个正在运行的 SnipasteOcr, 正在结束…"
        foreach ($p in $procs) {
            try   { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
            catch { Write-Warn "无法结束 PID $($p.Id): $($_.Exception.Message)" }
        }
        Start-Sleep -Milliseconds 800
        $left = @(Get-Process -Name 'SnipasteOcr' -ErrorAction SilentlyContinue).Count
        if ($left -gt 0) { throw "仍有 $left 个 SnipasteOcr 进程无法结束, 请手工处理后再试。" }
        Write-Ok '已结束旧实例'
    } else {
        Write-Ok '没有正在运行的实例'
    }

    $binDir = Join-Path $RepoRoot "src\bin\$Configuration"
    if (Test-Path $binDir) {
        Remove-Item $binDir -Recurse -Force
        Write-Ok "已清理 $binDir"
    } else {
        Write-Ok '无需清理 (bin 目录不存在)'
    }
} else {
    Write-Step '清理'
    Write-Warn '已按 -SkipClean 跳过; 若报 LNK1104 请去掉该参数重试'
}

# ---------------------------------------------------------------------------
# 2. 发布
# ---------------------------------------------------------------------------
Write-Step 'AOT 发布'
$publishArgs = @(
    'publish', $ProjectPath,
    '-c', $Configuration,
    '-r', $Rid,
    '--self-contained',
    '-p:PublishAot=true'
)
Write-Host "  dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    throw @"
发布失败 (exit $LASTEXITCODE)。
若上面出现 LNK1104「无法打开文件 …native\SnipasteOcr.exe」:
  1) 确认没有 SnipasteOcr 进程在跑;
  2) 删除 src\bin\$Configuration 后重试 (去掉 -SkipClean 即可自动完成);
  3) 不要改用 -p:PublishSingleFile=true —— 会破坏模型加载。
"@
}
Write-Ok '发布成功'

# ---------------------------------------------------------------------------
# 3. 校验产物
# ---------------------------------------------------------------------------
Write-Step '校验产物'

if (-not (Test-Path $ExePath)) { throw "发布报告成功, 但找不到产物: $ExePath" }

$exeItem = Get-Item $ExePath
$exeMB   = [math]::Round($exeItem.Length / 1MB, 1)
Write-Ok "SnipasteOcr.exe  $exeMB MB  ($($exeItem.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')))"

# 单文件判定: 同目录若出现大量托管 dll, 说明不是 AOT 单文件产出, 应视为异常
$strayDll = @(Get-ChildItem $PublishDir -Filter '*.dll' -File -ErrorAction SilentlyContinue)
if ($strayDll.Count -gt 0) {
    Write-Warn "publish 目录下有 $($strayDll.Count) 个 .dll, 疑似不是 AOT 单文件产出:"
    $strayDll | Select-Object -First 5 | ForEach-Object { Write-Host "      $($_.Name)" -ForegroundColor DarkGray }
} else {
    Write-Ok '单文件产出 (publish 目录无附带 dll)'
}

# 两套模型程序集必须都被嵌入, 否则运行时切换档位会找不到资源
# 这里通过 exe 体积做粗判: 两套中文模型合计约 138MB, 缺失会显著变小
if ($exeItem.Length -lt 100MB) {
    Write-Warn "产物仅 $exeMB MB, 明显小于预期(约 162 MB) —— 可能缺少模型程序集或裁剪过度。"
} else {
    Write-Ok '体积符合预期 (两套中文模型已内嵌)'
}

# ---------------------------------------------------------------------------
# 4. 自检 (可选)
# ---------------------------------------------------------------------------
if ($SelfTest) {
    Write-Step '离线自检 (136 项)'
    & dotnet run --project (Join-Path $RepoRoot 'tests\AnnotationTests') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "自检未通过 (exit $LASTEXITCODE)" }
    Write-Ok '自检通过'
} else {
    Write-Step '自检'
    Write-Host '  已跳过 (加 -SelfTest 开启)' -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# 5. 打包 (可选)
# ---------------------------------------------------------------------------
if ($OutputDir) {
    Write-Step '打包'

    $OutputDir = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $RepoRoot $OutputDir }
    $stamp     = Get-Date -Format 'yyyyMMdd'
    $pkgName   = "SnipasteOCR-$stamp"
    $pkgDir    = Join-Path $OutputDir $pkgName

    if (Test-Path $pkgDir) { Remove-Item $pkgDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $pkgDir | Out-Null

    Copy-Item $ExePath $pkgDir
    Write-Ok "已复制 SnipasteOcr.exe"

    if ($IncludePdb) {
        $pdb = Join-Path $PublishDir 'SnipasteOcr.pdb'
        if (Test-Path $pdb) { Copy-Item $pdb $pkgDir; Write-Ok '已附带 SnipasteOcr.pdb' }
    }

    # 附一份极简说明, 收包的人不必回头翻仓库
    $readme = @"
SnipasteOCR ($stamp)
====================

运行: 直接双击 SnipasteOcr.exe, 程序驻留系统托盘, 无需安装 .NET 运行时。

  F1  截图 OCR   —— 框选区域, 弹出识别结果
  F2  截图标注   —— 框选后可画矩形/椭圆/箭头/画笔/马赛克/文字, 结果复制到剪贴板

右键托盘图标可切换识别模型(高精度 / 快速)与修改热键。

系统要求: Windows 10 1809+ / 11 (x64), CPU 需支持 AVX2。
识别全程离线, 模型已内嵌, 不联网。
"@
    Set-Content -Path (Join-Path $pkgDir '使用说明.txt') -Value $readme -Encoding UTF8
    Write-Ok '已生成 使用说明.txt'

    if (-not $NoZip) {
        $zipPath = Join-Path $OutputDir "$pkgName.zip"
        if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

        # 这里刻意不用 Compress-Archive: Windows PowerShell 5.1 的实现
        # 对 162MB 的单个文件会误报 "being used by another process"
        # (实测此时文件可独占打开, 且 .NET ZipFile 能正常压缩), 属于该 cmdlet 自身缺陷。
        # 改用 .NET 的 ZipFile, 行为稳定且更快。
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $pkgDir, $zipPath,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false)   # $false = 不把 pkgDir 本身作为顶层目录打进 zip

        $zipMB = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
        Write-Ok "已生成 $zipPath  ($zipMB MB)"
    }

    Write-Host ""
    Write-Host "  输出目录: $pkgDir" -ForegroundColor White
} else {
    Write-Step '打包'
    Write-Host '  已跳过 (加 -OutputDir <目录> 开启)' -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
Write-Step '完成'
Write-Host "  产物: $ExePath" -ForegroundColor White
Write-Host ""
Write-Host '  提醒: 发布只证明能编译并产出 exe。首次出包请手工验收一遍 ——' -ForegroundColor DarkGray
Write-Host '        托盘图标出现 -> F1 框选出文字 -> 右键托盘切换一次模型档位' -ForegroundColor DarkGray
Write-Host '        (切档位能验证 Medium/Tiny 两套模型程序集都被正确保留)' -ForegroundColor DarkGray
