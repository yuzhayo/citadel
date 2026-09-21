@echo off
setlocal

cd /d "%~dp0"

where pwsh.exe >nul 2>nul
if errorlevel 1 (
  echo [Citadel] FAILED: pwsh.exe tidak ditemukan.
  pause
  exit /b 1
)

pwsh.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Commit-Push-Release.ps1" -Message "%~1"
if errorlevel 1 (
  echo [Citadel] FAILED. Lihat error di atas.
  pause
  exit /b 1
)

echo [Citadel] Commit, push, and release completed.
exit /b 0
