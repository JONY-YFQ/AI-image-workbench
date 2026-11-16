@echo off
chcp 65001 >nul
setlocal

set "VERSION_ROOT=%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference = 'Stop'; $root = $env:VERSION_ROOT; $files = Get-ChildItem -LiteralPath $root -Filter '*.html' -File; $pattern = 'MY(\d+)\.(\d+)\.(\d+)'; $newVer = $null; foreach ($f in $files) { $txt = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8); $m = [regex]::Match($txt, $pattern); if ($m.Success) { if ($null -eq $newVer) { $major = [int]$m.Groups[1].Value; $minor = [int]$m.Groups[2].Value; $patch = [int]$m.Groups[3].Value + 1; if ($patch -ge 100) { $patch = 0; $minor += 1 }; if ($minor -ge 100) { $minor = 0; $major += 1 }; $newVer = ('MY{0}.{1}.{2}' -f $major, $minor, $patch); Write-Host ('Current version: ' + $m.Value + ' -> New version: ' + $newVer) }; $updated = [regex]::Replace($txt, $pattern, $newVer); [IO.File]::WriteAllText($f.FullName, $updated, (New-Object System.Text.UTF8Encoding($false))); Write-Host ('Updated file: ' + $f.Name) } }; if ($null -eq $newVer) { throw 'No MYx.y.z version found in any HTML file.' }"

if errorlevel 1 (
  echo.
  echo Version update failed!
  pause
  exit /b 1
)

echo.
echo Version updated successfully!
pause
