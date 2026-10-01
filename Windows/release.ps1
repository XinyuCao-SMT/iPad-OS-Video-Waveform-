# release.ps1 —— Windows 版发版：一个版本一个文件夹 + 独立清单 + 独立 tag
#
# 与 iPad 版那套对齐（见 ../ROLLBACK.md）：
#   iPad：tag v1.x.y-xxx + legacy/* 分支 + dist/VideoScopePad-v1.x.y-xxx-unsigned.ipa + Release 附件
#   Windows：tag win-vx.y.z-xxx + dist/win-vx.y.z-xxx/VideoScopePad-win-vx.y.z-xxx-unsigned.exe
#            + MANIFEST.txt（SHA256 / 大小 / 源码提交 / 自检结果）+ 可选 Release 附件
#
# 用法：
#   powershell -ExecutionPolicy Bypass -File Windows\release.ps1 -Tag win-v0.1.0-monitor
#   powershell -ExecutionPolicy Bypass -File Windows\release.ps1 -Tag win-v0.1.1-fix -Note "修 xxx" -NoTag
#
# 为什么每版一个文件夹而不是覆盖 dist\VideoScopePad.exe：
#   回滚要能"拿起旧的那一个就跑"，而不是重新编译一遍（重新编译出来的不一定等于当时那一份）。
#   文件夹里连清单一起留着，几个月后还能核对「手里这个 exe 是不是当初那一版」。
#
# ⚠️ 本文件必须保持 **UTF-8 带 BOM**（Windows PowerShell 5.1 对无 BOM 的 UTF-8 按 GBK 解码，
#    下面的中文注释会整段乱码并导致解析失败）。改完确认前 3 字节是 EF BB BF。

param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Note = '',
    [switch]$SkipSelfCheck,
    [switch]$NoTag,
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $repoRoot

$releaseDir = Join-Path $repoRoot ("dist\" + $Tag)
$exeName = "VideoScopePad-$Tag-unsigned.exe"
$exePath = Join-Path $releaseDir $exeName
$manifestPath = Join-Path $releaseDir 'MANIFEST.txt'

Write-Host "发版：$Tag"

# ---------------------------------------------------------------- 1) 编译发布
if (Test-Path $releaseDir) {
    Write-Host "  ⚠ 目标文件夹已存在，先清掉旧内容：$releaseDir"
    Remove-Item $releaseDir -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseDir | Out-Null

Write-Host '  发布单文件 exe（Release / 自包含 / win-x64）…'
& dotnet publish 'Windows\VideoScopePad.App\VideoScopePad.App.csproj' `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o (Join-Path $releaseDir 'publish') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

Copy-Item (Join-Path $releaseDir 'publish\VideoScopePad.App.exe') $exePath -Force
Remove-Item (Join-Path $releaseDir 'publish') -Recurse -Force

# ---------------------------------------------------------------- 2) 版本与哈希
# ProductVersion 会被 .NET 拼成 "0.1.0+<git sha>"（SourceLink 干的），
# 这里只取前半段当版本号；提交哈希在下面单独记一行，清单更好读。
$version = ((Get-Item $exePath).VersionInfo.ProductVersion -split '\+')[0]
$sha256 = (Get-FileHash $exePath -Algorithm SHA256).Hash
$size = (Get-Item $exePath).Length
$commit = (& git rev-parse HEAD).Trim()
$commitShort = (& git rev-parse --short HEAD).Trim()
$dirty = (@(& git status --porcelain).Count -gt 0)

Write-Host ("  exe：{0}（{1:N1} MB）" -f $exeName, ($size / 1MB))
Write-Host ("  SHA256：{0}" -f $sha256)

# ---------------------------------------------------------------- 3) 自检（在空目录里跑，证明确实自包含）
$selfCheckLines = @()
if (-not $SkipSelfCheck) {
    $probeDir = Join-Path $env:TEMP ("vsp-release-check-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $probeDir | Out-Null
    Copy-Item $exePath (Join-Path $probeDir 'VideoScopePad.exe')
    try {
        Write-Host '  独立自检（把 exe 单独拷到空目录里跑）…'
        Push-Location $probeDir
        & (Join-Path $probeDir 'VideoScopePad.exe') --snapshot (Join-Path $probeDir 'check.png') `
            --frames 60 --source synthetic --width 1280 --height 720 | Out-Null
        $code = $LASTEXITCODE
        Pop-Location
        $reportPath = Join-Path $probeDir 'check.png.report.txt'
        if (Test-Path $reportPath) {
            $selfCheckLines = Get-Content $reportPath -Encoding UTF8 | Where-Object { $_ -match '^\s*[✓✗]' }
        }
        Write-Host ("  自检退出码：{0}（{1} 项断言）" -f $code, $selfCheckLines.Count)
        if ($code -ne 0) {
            # 失败时把报告留档再抛：否则只看到一句"未通过"，还得手动把那一版重跑一遍才查得出原因
            $failPath = Join-Path $releaseDir 'SELFCHECK-FAILED.txt'
            if (Test-Path $reportPath) { Copy-Item $reportPath $failPath -Force }
            throw "独立自检未通过（退出码 $code）—— 不发出这一版；报告已留档：$failPath"
        }
    } finally {
        Remove-Item $probeDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- 4) 清单
$manifest = @()
$manifest += "VideoScopePad（Windows 版）发版清单"
$manifest += "======================================"
$manifest += ""
$manifest += "版本号    : $version"
$manifest += "发版标签  : $Tag"
$manifest += "文件名    : $exeName"
$manifest += "大小      : $size 字节（$([math]::Round($size / 1MB, 1)) MB）"
$manifest += "SHA256    : $sha256"
$manifest += "源码提交  : $commit$([string]::Format('{0}', $(if ($dirty) { '（工作区有未提交改动）' } else { '' })))"
$manifest += "构建时间  : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$manifest += "构建机    : $env:COMPUTERNAME"
$manifest += ""
$manifest += "说明      : $Note"
$manifest += ""
$manifest += "怎么用    : 直接把 $exeName 拷到目标机器双击即可（自包含，目标机不用装 .NET；"
$manifest += "            唯一系统依赖是 Win10/11 自带的 d3dcompiler_47.dll）。未签名 exe 首次运行"
$manifest += "            会有 SmartScreen 提示 →「更多信息」→「仍要运行」。"
$manifest += ""
$manifest += "独立自检结果（把 exe 单独拷到空目录里跑 --snapshot，证明自包含且着色器已内嵌）："
if ($selfCheckLines.Count -gt 0) { $manifest += $selfCheckLines } else { $manifest += "  （本次跳过）" }
$manifest += ""
$manifest += "回滚      : 见 Windows\ROLLBACK-Windows.md（把旧版文件夹里的 exe 拿出来直接替换即可）"
$manifest | Set-Content -Path $manifestPath -Encoding UTF8

# ---------------------------------------------------------------- 5) 打 tag
if (-not $NoTag) {
    & git rev-parse --verify --quiet "refs/tags/$Tag" | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  ⚠ tag $Tag 已存在，跳过创建（要重打请先 git tag -d $Tag）"
    } else {
        $message = if ($Note) { "$Tag`n`n$Note" } else { $Tag }
        & git -c user.name=agent -c user.email=agent@local tag -a $Tag -m $message $commit
        Write-Host "  已打 tag：$Tag → $commitShort"
    }
}

# ---------------------------------------------------------------- 6) 可选：传到 GitHub Release
if ($Upload) {
    Write-Host '  上传到 GitHub Release（走 API，github.com:443 不通时也能用）…'
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'upload-release.ps1') `
        -Tag $Tag -Asset $exePath -Note $Note
}

Write-Host ''
Write-Host "完成：$releaseDir"
Write-Host "  • 旧版本文件夹都留在 dist\ 下，一个都没动 —— 回滚就是把它里面的 exe 拿出来用"
Write-Host "  • 清单：$manifestPath"
