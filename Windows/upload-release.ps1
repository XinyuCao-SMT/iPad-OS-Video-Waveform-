# upload-release.ps1 —— 把发版产物传到 GitHub Release
#
# 为什么单独一个脚本：github.com:443 在国内链路经常被拦（git push 直接 reset），
# 但 api.github.com 与 uploads.github.com 是通的，所以改用 REST API 上传。
#
# 用法（release.ps1 -Upload 会自动调用）：
#   powershell -ExecutionPolicy Bypass -File Windows\upload-release.ps1 `
#       -Tag win-v0.14.0-syncstd -Asset dist\win-v0.14.0-syncstd\xxx.exe -Note "说明"
#
# 行为：
#   1. 从 git 凭据库取 token（与 push-via-api.ps1 同一套办法）
#   2. 若该 tag 的 Release 已存在则复用，否则创建（名字 = tag，正文 = Note）
#   3. 上传资产（同名资产已存在则跳过，避免 422）
#   4. 打印 Release 页面地址
#
# ⚠️ 本文件必须保持 **UTF-8 带 BOM**，否则 Windows PowerShell 5.1 会按 GBK 解码中文注释导致解析失败。

param(
    [Parameter(Mandatory = $true)][string]$Tag,
    [Parameter(Mandatory = $true)][string]$Asset,
    [string]$Note = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if (-not (Test-Path $Asset)) { throw "找不到资产文件：$Asset" }
$assetPath = (Resolve-Path $Asset).Path
$assetName = Split-Path $assetPath -Leaf
$assetBytes = [System.IO.File]::ReadAllBytes($assetPath)
Write-Host ("  资产：{0}（{1:N1} MB）" -f $assetName, ($assetBytes.Length / 1MB))

# ---------------------------------------------------------------- 凭据
$gitExe = (Get-Command git -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $gitExe -and (Test-Path 'D:\Software\Git\cmd\git.exe')) { $gitExe = 'D:\Software\Git\cmd\git.exe' }
if (-not $gitExe) { throw '找不到 git 可执行文件' }

$inFile = Join-Path $env:TEMP 'vsp-upload-cred-in.txt'
$outFile = Join-Path $env:TEMP 'vsp-upload-cred-out.txt'
$errFile = Join-Path $env:TEMP 'vsp-upload-cred-err.txt'
[System.IO.File]::WriteAllText($inFile, "protocol=https`nhost=github.com`n`n", (New-Object System.Text.UTF8Encoding($false)))
Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue
try {
    $sp = @{
        FilePath               = $gitExe
        ArgumentList           = 'credential fill'
        RedirectStandardInput  = $inFile
        RedirectStandardOutput = $outFile
        RedirectStandardError  = $errFile
        NoNewWindow            = $true
        Wait                   = $true
    }
    Start-Process @sp
    $cred = Get-Content $outFile
} finally {
    Remove-Item $inFile, $outFile, $errFile -Force -ErrorAction SilentlyContinue
}

$user = ($cred | Where-Object { $_ -like 'username=*' }) -replace '^username=', ''
$token = ($cred | Where-Object { $_ -like 'password=*' }) -replace '^password=', ''
if (-not $token) { throw '取不到 GitHub 凭据（先在命令行 git push 一次完成登录）' }

$repoUrl = (& git remote get-url origin) -replace '\.git$', ''
$repoName = ($repoUrl -split '/')[-1]
$slug = "$user/$repoName"
$headers = @{
    Authorization = "Bearer $token"
    'User-Agent'  = 'vsp-release'
    Accept        = 'application/vnd.github+json'
}

# ---------------------------------------------------------------- 1) 取或建 Release
$releaseId = $null
$releaseUrl = $null
try {
    $existing = Invoke-RestMethod -Uri "https://api.github.com/repos/$slug/releases/tags/$Tag" -Headers $headers -Method GET
    $releaseId = $existing.id
    $releaseUrl = $existing.html_url
    Write-Host "  Release 已存在，复用它：$releaseUrl"
} catch {
    Write-Host '  创建 Release…'
    $body = [ordered]@{
        tag_name   = $Tag
        name       = $Tag
        body       = $Note
        draft      = $false
        prerelease = $false
    } | ConvertTo-Json -Depth 4 -Compress
    $jsonPath = Join-Path $env:TEMP 'vsp-release.json'
    [System.IO.File]::WriteAllText($jsonPath, $body, (New-Object System.Text.UTF8Encoding($false)))
    try {
        $created = Invoke-RestMethod -Uri "https://api.github.com/repos/$slug/releases" -Headers $headers `
            -Method POST -InFile $jsonPath -ContentType 'application/json; charset=utf-8'
        $releaseId = $created.id
        $releaseUrl = $created.html_url
        Write-Host "  已创建：$releaseUrl"
    } finally {
        Remove-Item $jsonPath -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- 2) 上传资产（同名则跳过）
$alreadyThere = $false
try {
    $assets = Invoke-RestMethod -Uri "https://api.github.com/repos/$slug/releases/$releaseId/assets" -Headers $headers -Method GET
    # ⚠️ 不要写成 ($assets | Where-Object {...}) -ne $null：空结果集在 PowerShell 里的比较结果不可靠，
    #    会误判成"已存在"而跳过上传（第一次就是这么没传上去的，Release 里空着）。逐个显式判断。
    if ($assets) {
        foreach ($item in $assets) {
            if ($item.name -eq $assetName) { $alreadyThere = $true; break }
        }
    }
} catch {
    $alreadyThere = $false
}

if ($alreadyThere) {
    Write-Host "  资产已存在，跳过上传：$assetName"
} else {
    Write-Host '  上传资产…（69 MB 左右，可能要走一会儿）'
    $uploadUrl = "https://uploads.github.com/repos/$slug/releases/$releaseId/assets?name=$assetName"

    # ⚠️ Invoke-RestMethod 传 69 MB 会报 "The underlying connection was closed"（PS 5.1 的长 POST 不稳），
    #    改用系统自带的 curl.exe（Win10 1803+ 内置）。token 放临时 config 文件，不出现在命令行里。
    $cfgPath = Join-Path $env:TEMP 'vsp-curl-upload.cfg'
    $cfgLines = @(
        'silent',
        'show-error',
        'fail-with-body',
        'request = "POST"',
        ('header = "Authorization: Bearer ' + $token + '"'),
        'header = "Accept: application/vnd.github+json"',
        'header = "User-Agent: vsp-release"',
        'header = "Content-Type: application/octet-stream"',
        ('data-binary = "@' + ($assetPath -replace '\\', '/') + '"'),
        ('url = "' + $uploadUrl + '"')
    )
    [System.IO.File]::WriteAllLines($cfgPath, $cfgLines, (New-Object System.Text.UTF8Encoding($false)))
    try {
        $uploadOutput = & curl.exe --config $cfgPath 2>&1
        $uploadCode = $LASTEXITCODE
    } finally {
        Remove-Item $cfgPath -Force -ErrorAction SilentlyContinue
    }
    if ($uploadCode -ne 0) {
        throw "资产上传失败（curl 退出码 $uploadCode）：$uploadOutput"
    }
    Write-Host '  已上传（curl 返回 0）'
}

Write-Host ''
Write-Host "发版完成：$releaseUrl"
Write-Host "  直接下载：https://github.com/$slug/releases/download/$Tag/$assetName"
