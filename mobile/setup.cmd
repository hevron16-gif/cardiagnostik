@echo off
setlocal enabledelayedexpansion
echo ============================================
echo   CarDiagnosticApp - Setup
echo ============================================
echo.

:: Check Windows App Runtime 1.7
powershell -Command "$p = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.7_*_x64__8wekyb3d8bbwe' -ErrorAction SilentlyContinue; if ($p) { exit 0 } else { exit 1 }" >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo [OK] Windows App Runtime 1.7 already installed.
    goto :run
)

echo [MISSING] Windows App Runtime 1.7 not found.
echo.
echo Downloading installer (approx. 100 MB)...
echo.

set "DOWNLOAD_URL=https://aka.ms/windowsappsdk/1.7/windowsappruntimeinstall-x64.exe"
set "INSTALLER=%TEMP%\WinAppRuntime-1.7-x64.exe"

powershell -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri '%DOWNLOAD_URL%' -OutFile '%INSTALLER%' -UseBasicParsing" 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Failed to download Windows App Runtime.
    echo Please download and install manually:
    echo   https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads
    echo.
    pause
    exit /b 1
)

echo.
echo [INFO] Running Windows App Runtime installer...
echo This requires administrator privileges.
echo If prompted by UAC, click YES.
echo.
%INSTALLER% --quiet
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [WARNING] Installer returned exit code %ERRORLEVEL%.
    echo Trying to continue anyway...
)

echo.
echo [OK] Installation complete.
del "%INSTALLER%" 2>nul

:run
echo.
echo Starting CarDiagnosticApp...
start "" "%~dp0CarDiagnosticApp.exe"

endlocal
