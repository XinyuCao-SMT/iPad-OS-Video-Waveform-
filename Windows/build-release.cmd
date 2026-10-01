@echo off
rem ---------------------------------------------------------------
rem Build a single-file, self-contained VideoScopePad.exe
rem
rem   Output: dist\VideoScopePad.exe   (one file, copy it anywhere)
rem
rem   Self-contained = the target PC does NOT need .NET installed.
rem   Single file     = the shaders are embedded resources, so no
rem                     Render\Shaders folder has to travel with the exe.
rem   (The only OS dependency is d3dcompiler_47.dll, which ships with
rem    Windows 10/11 itself - used to compile the HLSL at startup.)
rem
rem   The exe is not committed: dist\ is in .gitignore.
rem ---------------------------------------------------------------
setlocal
cd /d "%~dp0.."

set OUT=dist
set RID=win-x64

echo Publishing (Release, single file, self-contained)...
dotnet publish Windows\VideoScopePad.App\VideoScopePad.App.csproj ^
  -c Release -r %RID% --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -o "%OUT%\%RID%"
if errorlevel 1 (
  echo.
  echo Publish failed - see the errors above.
  pause
  exit /b 1
)

copy /y "%OUT%\%RID%\VideoScopePad.App.exe" "%OUT%\VideoScopePad.exe" >nul
if errorlevel 1 (
  echo Copy failed.
  pause
  exit /b 1
)

for %%F in ("%OUT%\VideoScopePad.exe") do (
  echo.
  echo Done: %%~fF
  echo Size: %%~zF bytes
)
echo.
echo Share this single file. On first run Windows SmartScreen may warn
echo (unsigned exe) - "More info" -^> "Run anyway".
exit /b 0
