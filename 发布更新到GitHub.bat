@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion
cd /d "%~dp0"

echo ============================================================
echo   AI 生图工作台 · 一键发布更新到 GitHub
echo ============================================================
echo.

where git >nul 2>nul
if errorlevel 1 (
    echo [错误] 未检测到 Git，请先安装 Git 后重试。
    goto KEEP_OPEN
)

set "MSG="
set /p MSG="请输入本次更新说明 (直接回车默认 常规更新): "
if "%MSG%"=="" set "MSG=常规更新"
set "PUBLISH_MSG=%MSG%"

echo.
echo [1/4] 正在更新本地版本号 (version.json 与 HTML)...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop'; " ^
  "$msg = $env:PUBLISH_MSG; if (-not $msg) { $msg = '常规更新' }; " ^
  "$vPath = Join-Path $PWD 'version.json'; " ^
  "$v = Get-Content $vPath -Raw -Encoding UTF8 | ConvertFrom-Json; " ^
  "if ($v.version -match '^(MY)(\d+)\.(\d+)\.(\d+)$') { " ^
  "  $v.version = $Matches[1] + $Matches[2] + '.' + $Matches[3] + '.' + ([int]$Matches[4] + 1); " ^
  "} " ^
  "$v.updatedAt = (Get-Date -Format 'yyyy-MM-dd'); " ^
  "$v.notes = $msg; " ^
  "$utf8NoBom = New-Object System.Text.UTF8Encoding($false); " ^
  "[System.IO.File]::WriteAllText($vPath, ($v | ConvertTo-Json -Depth 4), $utf8NoBom); " ^
  "$htmlPath = Join-Path $PWD 'AI生图工作台.html'; " ^
  "$html = [System.IO.File]::ReadAllText($htmlPath, $utf8NoBom); " ^
  "$html = [regex]::Replace($html, '(<span id=\"appVersionBadge\"[^>]*>\s*)MY\d+\.\d+\.\d+(\s*</span>)', ('${1}' + $v.version + '${2}')); " ^
  "[System.IO.File]::WriteAllText($htmlPath, $html, $utf8NoBom); " ^
  "Write-Host ('      已升级版本号为: ' + $v.version)"

if errorlevel 1 (
    echo [失败] 更新版本号失败，请检查文件是否被占用。
    goto KEEP_OPEN
)

for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "(Get-Content 'version.json' -Raw -Encoding UTF8 | ConvertFrom-Json).version"`) do set "NEW_VER=%%v"

echo.
echo [2/4] 正在提交改动到本地 Git...
git add -A
git commit -m "%MSG%"
if errorlevel 1 (
    echo [提示] 本地没有检测到新的改动或提交未完成，继续校验远端状态...
)

echo.
echo [3/4] 正在推送到 GitHub (main 分支)...
git branch -M main
git push -u origin main
if errorlevel 1 (
    echo.
    echo ============================================================
    echo   [推送失败] 未能成功上传到 GitHub！
    echo   请检查网络连接、代理或 GitHub 登录凭证。
    echo ============================================================
    goto KEEP_OPEN
)

echo.
echo [4/4] 正在向 GitHub 反查确认是否已真正上传...
for /f "delims=" %%h in ('git rev-parse HEAD') do set "LOCAL_HASH=%%h"
set "REMOTE_HASH="
for /f "tokens=1" %%r in ('git ls-remote origin refs/heads/main') do set "REMOTE_HASH=%%r"

echo.
if /i "%LOCAL_HASH%"=="%REMOTE_HASH%" (
    echo ============================================================
    echo   [确认成功] 本次更新已真正推送到 GitHub！
    echo   当前版本号: %NEW_VER%
    echo   更新说明  : %MSG%
    echo   本地提交  : %LOCAL_HASH%
    echo   GitHub远端: %REMOTE_HASH%  [完全一致]
    echo   仓库地址  : https://github.com/JONY-YFQ/AI-image-workbench
    echo ============================================================
    echo   其他用户点击「检查更新」或每小时自动检测时即可收到此更新。
) else (
    echo ============================================================
    echo   [警告] 推送后本地与 GitHub 远端提交号不一致，请核实！
    echo   本地提交  : %LOCAL_HASH%
    echo   GitHub远端: %REMOTE_HASH%
    echo ============================================================
)

:KEEP_OPEN
echo.
echo 请确认以上信息无误后，直接点击窗口右上角 [X] 手动关闭。
:WAIT_FOREVER
set "DUMMY="
set /p DUMMY=""
goto WAIT_FOREVER
