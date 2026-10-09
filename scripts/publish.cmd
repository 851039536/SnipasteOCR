@echo off
REM ===========================================================================
REM  SnipasteOCR 一键发布 —— 双击运行本文件 (不要双击 publish.ps1)
REM
REM  为什么要这个 .cmd:
REM    Windows 下双击 .ps1 默认是"用记事本打开", 根本不会执行;
REM    即使关联到 PowerShell, 控制台窗口也会在脚本结束时立刻关闭,
REM    报错一闪而过, 表现就是"双击了但好像什么也没发生 / 没打包成功"。
REM    本文件负责: 调用 publish.ps1 -> 结束后 pause 留屏 -> 返回真实退出码。
REM
REM  用法: 直接双击 = 发布 + 打包到 dist\ (与 .\scripts\publish.ps1 等效)
REM        需要自检时双击后在窗口里手动跑: publish.ps1 -SelfTest
REM ===========================================================================
setlocal

REM 以脚本自身位置定位, 不依赖调用者的当前目录
set "SCRIPT=%~dp0publish.ps1"

if not exist "%SCRIPT%" (
    echo [错误] 找不到 "%SCRIPT%"
    goto :end
)

echo 正在调用 %SCRIPT% %*
echo.

REM -ExecutionPolicy Bypass: 避免本机执行策略禁止运行未签名脚本
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
set "RC=%ERRORLEVEL%"

:end
echo.
if not defined RC set "RC=1"
if "%RC%"=="0" (
    echo [完成] 退出码 0
) else (
    echo [失败] 退出码 %RC% —— 请向上翻看错误信息
)
echo.
pause
exit /b %RC%
