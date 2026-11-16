@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ==========================================
echo   AI 生图工作台 · 一键发布更新到 GitHub
echo ==========================================
echo.

where git >nul 2>nul
if errorlevel 1 (
    echo [错误] 未检测到 Git，请先安装 Git 后重试。
    pause
    exit /b 1
)

set /p MSG="请输入本次更新说明 (直接回车默认 常规更新): "
if "%MSG%"=="" set MSG=常规更新

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$vPath = 'version.json'; " ^
  "$v = Get-Content $vPath -Raw -Encoding UTF8 | ConvertFrom-Json; " ^
  "if ($v.version -match '^(MY)(\d+)\.(\d+)\.(\d+)$') { " ^
  "  $next = $Matches[1] + $Matches[2] + '.' + $Matches[3] + '.' + ([int]$Matches[4] + 1); " ^
  "  $v.version = $next; " ^
  "} " ^
  "$v.updatedAt = (Get-Date -Format 'yyyy-MM-dd'); " ^
  "$v.notes = '%MSG%'; " ^
  "$v | ConvertTo-Json -Depth 4 | Set-Content $vPath -Encoding UTF8; " ^
  "$html = Get-Content 'AI生图工作台.html' -Raw -Encoding UTF8; " ^
  "$html = $html -replace '(<span id=\"appVersionBadge\"[^>]*>\s*)MY\d+\.\d+\.\d+(\s*</span>)', ('${1}' + $v.version + '${2}'); " ^
  "[System.IO.File]::WriteAllText((Join-Path $PWD 'AI生图工作台.html'), $html, [System.Text.UTF8Encoding]::new($false)); " ^
  "Write-Host ('已自动升级版本号为: ' + $v.version)"

git add -A
git commit -m "%MSG%"
git branch -M main
git push -u origin main

if errorlevel 1 (
    echo.
    echo [失败] 推送到 GitHub 失败，请检查网络或 GitHub 登录凭证。
    pause
    exit /b 1
)

echo.
echo [成功] 已发布到 GitHub！其他用户端将自动检测到新版本并提示更新。
pause
