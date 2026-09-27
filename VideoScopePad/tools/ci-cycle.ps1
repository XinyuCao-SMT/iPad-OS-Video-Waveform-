<#
    ci-cycle.ps1 —— 一条命令跑完一轮「推送 -> 云端构建 -> 等结果 -> 汇总错误」

    用法（在仓库任意位置）：
        pwsh -File VideoScopePad/tools/ci-cycle.ps1
        pwsh -File VideoScopePad/tools/ci-cycle.ps1 -SkipPush          # 只重跑构建，不推送
        pwsh -File VideoScopePad/tools/ci-cycle.ps1 -TimeoutMinutes 20

    说明：
      * 凭据用 git credential fill 从本机已缓存的 GitHub 登录里取，不落盘、不打印。
      * github.com 在国内链路经常抖（api.github.com 通、github.com 不通），所以推送会重试。
      * 构建失败时自动把日志里的 error: 行抓回来，不用手工翻 CI 页面。
#>

[CmdletBinding()]
param(
    [switch]$SkipPush,
    [int]$MaxPushAttempts = 40,
    [int]$TimeoutMinutes = 16
)

$ErrorActionPreference = 'Continue'
$env:GIT_TERMINAL_PROMPT = '0'

# 仓库根 = 本脚本所在目录的上两级
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $repoRoot

$owner = $null
$repoName = $null
$token = $null
$headers = $null
$repoSlug = $null

function Get-GitHubAuth {
    $credOut = ("protocol=https`nhost=github.com`n`n" | git credential fill 2>&1)
    $script:owner = ($credOut | Where-Object { $_ -like 'username=*' }) -replace '^username=',''
    $script:token = ($credOut | Where-Object { $_ -like 'password=*' }) -replace '^password=',''
    if (-not $script:token) { throw '取不到 GitHub 凭据，请先在命令行执行一次 git push 完成登录' }

    # 从 origin 里解析出仓库名
    $url = (git remote get-url origin) -replace '\.git$',''
    $script:repoName = ($url -split '/')[-1]
    $script:repoSlug = "$script:owner/$script:repoName"
    $script:headers = @{
        Authorization = "Bearer $script:token"
        'User-Agent' = 'vsp-ci-cycle'
        Accept = 'application/vnd.github+json'
    }
}

function Invoke-GitHub {
    param([string]$Uri, [string]$Method = 'GET', $Body = $null)
    # 注意：不要用 $args 当变量名，它是 PowerShell 的自动变量
    $reqArgs = @{ Uri = $Uri; Headers = $script:headers; Method = $Method; TimeoutSec = 40 }
    if ($Body) {
        $reqArgs.Body = ($Body | ConvertTo-Json -Compress)
        $reqArgs.ContentType = 'application/json'
    }
    Invoke-RestMethod @reqArgs
}

Write-Host "仓库: $repoRoot"

# ---------- 1) 推送 ----------
if (-not $SkipPush) {
    $ahead = git log --oneline origin/main..HEAD 2>$null
    if (-not $ahead) {
        Write-Host '[1/4] 没有待推送的提交，跳过推送'
    } else {
        Write-Host "[1/4] 待推送 $((($ahead | Measure-Object).Count)) 个提交，开始推送（链路不稳会自动重试）"
        $pushed = $false
        for ($i = 1; $i -le $MaxPushAttempts; $i++) {
            $out = git push 2>&1
            if ($LASTEXITCODE -eq 0) {
                Write-Host "      第 $i 次尝试推送成功"
                $pushed = $true
                break
            }
            if ($i % 5 -eq 1) { Write-Host "      第 $i 次失败: $((($out | Select-Object -Last 1).ToString()).Trim())" }
            Start-Sleep -Seconds 20
        }
        if (-not $pushed) {
            Write-Host '      推送始终失败，本地提交完好，稍后再试。'
            exit 1
        }
    }
}

# ---------- 2) 触发 ----------
Write-Host '[2/4] 触发云端构建'
Get-GitHubAuth
Invoke-GitHub -Uri "https://api.github.com/repos/$repoSlug/actions/workflows/build-ipa.yml/dispatches" `
    -Method Post -Body @{ ref = 'main'; inputs = @{ configuration = 'Release' } } | Out-Null
Start-Sleep -Seconds 15

$runs = Invoke-GitHub -Uri "https://api.github.com/repos/$repoSlug/actions/runs?per_page=2"
$run = $runs.workflow_runs | Select-Object -First 1
$runId = $run.id
Write-Host "      运行 #$($run.run_number)  提交=$($run.head_sha.Substring(0,7))"
Write-Host "      $($run.html_url)"

# ---------- 3) 等待 ----------
Write-Host "[3/4] 等待构建完成（最长 $TimeoutMinutes 分钟）"
$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
while ((Get-Date) -lt $deadline) {
    try { $run = Invoke-GitHub -Uri "https://api.github.com/repos/$repoSlug/actions/runs/$runId" } catch { }
    if ($run.status -eq 'completed') { break }
    Start-Sleep -Seconds 15
}

Write-Host "      状态=$($run.status)  结论=$($run.conclusion)"
$jobList = Invoke-GitHub -Uri "https://api.github.com/repos/$repoSlug/actions/runs/$runId/jobs"
foreach ($j in $jobList.jobs) {
    Write-Host "      job: $($j.name) [$($j.conclusion)]"
    foreach ($s in $j.steps) { Write-Host ("        {0,2}. {1,-30} {2}" -f $s.number, $s.name, $s.conclusion) }
}

if ($run.conclusion -eq 'success') {
    Write-Host ''
    Write-Host '[4/4] 构建成功 —— 去运行页面底部 Artifacts 下载 IPA 即可：'
    Write-Host "      $($run.html_url)"
    exit 0
}

# ---------- 4) 抓错误 ----------
Write-Host '[4/4] 构建失败，抓取错误摘要'
$failedJob = ($jobList.jobs | Where-Object { $_.conclusion -eq 'failure' } | Select-Object -First 1)
if (-not $failedJob) { $failedJob = $jobList.jobs | Select-Object -First 1 }

try {
    $resp = Invoke-WebRequest -Uri "https://api.github.com/repos/$repoSlug/actions/jobs/$($failedJob.id)/logs" `
        -Headers $headers -TimeoutSec 60 -UseBasicParsing
    $text = if ($resp.Content -is [byte[]]) { [System.Text.Encoding]::UTF8.GetString($resp.Content) } else { $resp.Content }
    $logPath = Join-Path $env:TEMP 'vsp-ci-log.txt'
    Set-Content -Path $logPath -Value $text -Encoding utf8
    $lines = $text -split "`r?`n"

    Write-Host ''
    Write-Host '---- error 行（去重）----'
    $errs = $lines | Where-Object { $_ -match 'error:' } |
        ForEach-Object { ($_ -replace '^\S+Z\s+', '').Trim() } | Select-Object -Unique
    if ($errs) { $errs | ForEach-Object { Write-Host "  $_" } } else { Write-Host '  （没有 error: 行）' }

    Write-Host ''
    Write-Host "完整日志: $logPath"
} catch {
    Write-Host "      拉取日志失败: $($_.Exception.Message)"
}

exit 1
