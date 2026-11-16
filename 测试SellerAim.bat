@echo off
chcp 65001 >nul
title Seller Aim 连通性测试
cd /d "%~dp0"

set "NODE_EXE=node"
where node >nul 2>nul
if errorlevel 1 (
  if exist "%ProgramFiles%\nodejs\node.exe" set "NODE_EXE=%ProgramFiles%\nodejs\node.exe"
  if exist "%LocalAppData%\Programs\nodejs\node.exe" set "NODE_EXE=%LocalAppData%\Programs\nodejs\node.exe"
)

if not exist ".env" (
  if exist ".env.example" (
    echo [提示] 尚未找到 .env 文件。
  )
)

echo 正在运行 Seller Aim 模型网关连通性自检...
echo.
"%NODE_EXE%" "%~dp0test-selleraim.js" %*
echo.
pause
