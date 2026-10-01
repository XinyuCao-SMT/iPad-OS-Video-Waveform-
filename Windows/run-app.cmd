@echo off
rem ---------------------------------------------------------------
rem VideoScopePad (Windows) launcher
rem   Double-click this file to start the live monitor window.
rem   It builds first (no-op when already built), then starts the app.
rem
rem   Why a .cmd: the .exe lives several folders deep
rem   (Windows\VideoScopePad.App\bin\Debug\net8.0-windows\), and it must be
rem   rebuilt after source changes. This keeps "just run it" to one double-click.
rem   Details and design notes: Windows\README-Windows.md
rem ---------------------------------------------------------------
setlocal
cd /d "%~dp0.."

rem A running instance locks bin\...\VideoScopePad.Win.dll, which makes the build fail
rem with MSB3027 ("file is being used by another process"). Close it first.
tasklist /FI "IMAGENAME eq VideoScopePad.App.exe" 2>nul | find /I "VideoScopePad.App.exe" >nul
if not errorlevel 1 (
  echo Closing the running VideoScopePad instance...
  taskkill /IM VideoScopePad.App.exe /F >nul 2>&1
  rem give Windows a moment to release the file handles
  ping -n 2 127.0.0.1 >nul
)

echo Building...
dotnet build Windows\VideoScopePad.App\VideoScopePad.App.csproj -v q --nologo
if errorlevel 1 (
  echo.
  echo Build failed - see the errors above.
  pause
  exit /b 1
)

echo Starting VideoScopePad...
start "" "Windows\VideoScopePad.App\bin\Debug\net8.0-windows\VideoScopePad.App.exe" %*
exit /b 0
