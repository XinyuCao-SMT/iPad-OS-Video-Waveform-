<#
.SYNOPSIS
    One-click HLSL build check for the Windows port of VideoScopePad.

.DESCRIPTION
    Runs: dotnet run --project <repo>\Windows\tools\compile-shaders\compile-shaders.csproj
    That tool compiles every entry point of Render\Shaders\ScopeKernels.hlsl and
    Render\Shaders\DisplayShaders.hlsl with the system d3dcompiler_47.dll
    (cs_5_0 / vs_5_0 / ps_5_0). On failure it prints the full HLSL compiler errors
    and this script forwards the non-zero exit code unchanged.

    NOTE: this script is intentionally ASCII-only. Windows PowerShell 5.1 reads
    BOM-less .ps1 files using the system ANSI code page (GBK on a Chinese system),
    so non-ASCII text here would be mangled and can even break parsing. All Chinese
    output comes from the C# tool, which is compiled as UTF-8.

.PARAMETER WindowsDir
    Optional: the Windows directory inside the repository. By default the tool
    locates it by walking up from its own executable directory.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File Windows\tools\compile-shaders.ps1
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $WindowsDir
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'compile-shaders\compile-shaders.csproj'
if (-not (Test-Path -LiteralPath $project)) {
    Write-Error "Cannot find the compile-shaders project: $project"
    exit 2
}

$dotnetArgs = @('run', '--project', $project)
if ($WindowsDir -and $WindowsDir.Count -gt 0) {
    $dotnetArgs += '--'
    $dotnetArgs += $WindowsDir
}

Write-Host "==> dotnet $($dotnetArgs -join ' ')" -ForegroundColor Cyan
& dotnet @dotnetArgs
$code = $LASTEXITCODE

if ($code -eq 0) {
    Write-Host "==> all shader entry points compiled successfully" -ForegroundColor Green
} else {
    Write-Host "==> shader compilation FAILED (exit code $code)" -ForegroundColor Red
}
exit $code
