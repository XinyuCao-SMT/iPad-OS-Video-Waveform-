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
