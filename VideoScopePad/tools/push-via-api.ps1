# push-via-api.ps1
#
# Why this exists: on some networks github.com:443 (the git-over-HTTPS endpoint) is
# blocked/reset while api.github.com stays reachable. `git push` then fails with
# "Failed to connect to github.com:443" no matter how often you retry.
#
# This script uploads the local commit to GitHub using ONLY the REST API
# (api.github.com), which is reachable in that situation:
#   1. read the remote ref sha of the target branch
#   2. diff that sha against the local HEAD to find changed/added/deleted files
#   3. POST /git/blobs for every changed file
#   4. POST /git/trees with base_tree = remote tree
#   5. POST /git/commits with parent = remote sha
#   6. PATCH /git/refs/heads/<branch> (fast-forward only)
#
# Credentials come from the same store `git push` uses (`git credential fill`),
# so nothing new is configured and no account setting is touched.
#
# ⚠️ 维护这个脚本时注意两件事（都是实测踩过的）：
#   1) 本文件必须保持 **UTF-8 带 BOM**。Windows PowerShell 5.1 对「无 BOM 的 UTF-8」
#      会按系统 ANSI 代码页（简中是 GBK）解码，下面的中文注释会整段变成乱码，
#      进而导致解析失败（报 "Missing closing '}'"/"Unexpected token"）。
#      用某些编辑器/AI 工具改完这个文件后，记得确认前 3 字节仍是 EF BB BF：
#        $b = [IO.File]::ReadAllBytes($p); $b[0..2]  # 应该是 239,187,191
#   2) 取 token 不能用 PowerShell 管道喂 `git credential fill`（多行会被丢掉，
#      git 报 "refusing to work with credential missing protocol field"），
#      也不能用 cmd 的 2>&1（$ErrorActionPreference='Stop' 会把 stderr 变成终止错误）。
#      现在用的是「输入写临时文件 + Start-Process -RedirectStandardInput」。
#   3) 用户 profile 里可能有名为 git 的函数包装，所以要
#      `Get-Command git -CommandType Application` 才拿得到真正的可执行文件路径。
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/push-via-api.ps1
#   powershell -ExecutionPolicy Bypass -File tools/push-via-api.ps1 -Branch main -DryRun

param(
    [string]$Repo   = 'XinyuCao-SMT/iPad-OS-Video-Waveform-',
    [string]$Branch = 'main',
    [string]$LocalRef = 'HEAD',
    # 显式指定 diff 基点（本地提交 sha）：**当远端 tip 不在本 clone 里时，强烈建议给**。
    # 语义 = 「远端当前的内容 == 这个本地提交的内容」。给了就跳过下面的自动匹配，
    # 因为自动匹配基于**整棵树**的 sha 相等，而先前用本脚本上传过的提交树里，
    # 换行（CRLF/LF）与本地 git 规范化后的树不一致 —— 永远匹配不上，
    # 于是会退化成「用 HEAD^ 当基点」，那在「一次要推多个提交」时会算错差异。
    # ⚠️ 参数名不能叫 DiffBase：PowerShell 变量名大小写不敏感，
    #    会和脚本内部的 $diffBase 撞成同一个变量（实测直接炸）。
    [string]$BaseCommit = '',
    # 顺带把本地标签推到远端（给个通配前缀，默认空 = 不推）。
    # 标签对回滚很关键：源码回滚靠的就是 tag，远端没有 tag 那台机器就回不去。
    # 例：-Tags 'win-v*'
    [string]$Tags = '',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repoRoot

# ---------------------------------------------------------------- credentials
function Get-GitHubToken {
    # ⚠️ 踩过的坑（2026-02 实测）：
    #   1) PowerShell 管道把多行字符串喂给原生 git → git 报
    #      "refusing to work with credential missing protocol field"（payload 里明明有 protocol）。
    #   2) [Diagnostics.Process] + RedirectStandardInput（管道 stdin）→ 同样拿不到输出。
    #   3) cmd /c "... 2>&1" 在 $ErrorActionPreference='Stop' 下会把 stderr 变成终止错误。
    #   只有「输入写文件 + 用文件重定向喂进去、stdout/stderr 也各自重定向到文件」最稳。
    # 注意：用户的 PowerShell profile 里可能有名为 git 的函数/别名包装，
    # 那样 Get-Command git 拿不到可执行文件路径（.Source 为空），
    # Start-Process 会报 "ParameterArgumentValidationError"。所以只认 Application。
    $gitExe = (Get-Command git -CommandType Application -ErrorAction SilentlyContinue |
               Select-Object -First 1).Source
    if (-not $gitExe -and (Test-Path 'D:\Software\Git\cmd\git.exe')) {
        $gitExe = 'D:\Software\Git\cmd\git.exe'
    }
    if (-not $gitExe -or -not (Test-Path $gitExe)) {
        throw "找不到 git 可执行文件（Get-Command git -CommandType Application 返回空）"
    }
    $inFile = Join-Path $env:TEMP 'vsp-cred-in.txt'
    $outFile = Join-Path $env:TEMP 'vsp-cred-out.txt'
    $errFile = Join-Path $env:TEMP 'vsp-cred-err.txt'

    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($inFile, "protocol=https`nhost=github.com`n`n", $utf8)
    Remove-Item $outFile, $errFile -Force -ErrorAction SilentlyContinue

    try {
        # 用 splat 传参，避免反引号续行（续行符后面粘上空格就会解析失败）
        $spArgs = @{
            FilePath               = $gitExe
            ArgumentList           = 'credential fill'
            RedirectStandardInput  = $inFile
            RedirectStandardOutput = $outFile
            RedirectStandardError  = $errFile
            NoNewWindow            = $true
            Wait                   = $true
        }
        Start-Process @spArgs

        $stdout = if (Test-Path $outFile) { Get-Content $outFile -Raw } else { '' }
        foreach ($line in ($stdout -split "`r?`n")) {
            if ($line -match '^password=(.+)$') { return $Matches[1] }
        }

        $stderr = if (Test-Path $errFile) { (Get-Content $errFile -Raw) } else { '' }
        throw ("could not read a GitHub token from the git credential store. stderr: " + $stderr.Trim())
    } finally {
        Remove-Item $inFile, $outFile, $errFile -Force -ErrorAction SilentlyContinue
    }
}
$token = Get-GitHubToken
Write-Host ("token: {0}...({1} chars)" -f $token.Substring(0, 4), $token.Length)

# ---------------------------------------------------------------- api helper
function Invoke-GitHub {
    param([string]$Method, [string]$Path, $Body, [int]$Retries = 5)

    $uri = "https://api.github.com$Path"
    $headers = @{
        Authorization          = "Bearer $token"
        Accept                 = 'application/vnd.github+json'
        'User-Agent'           = 'VideoScopePad-push'
        'X-GitHub-Api-Version' = '2022-11-28'
    }
    $lastError = $null
    for ($i = 1; $i -le $Retries; $i++) {
        try {
            if ($null -eq $Body) {
                return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -TimeoutSec 90
            }
            # $Body 给字符串时按「已经是 JSON」发出去：
            # PS 5.1 的 ConvertTo-Json 会把**嵌套哈希表**序列化成 "{sha => …, type => …}"
            # 这种 PowerShell 自己的字符串形式（建 tag 对象时被 GitHub 以 422 打回：
            # "For 'properties/object', {...} is not a string"）。所以嵌套结构自己拼 JSON。
            $json  = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 12 -Compress }
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
            return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers `
                -Body $bytes -ContentType 'application/json; charset=utf-8' -TimeoutSec 180
        } catch {
            $lastError = $_
            $code = $null
            if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
            # 4xx other than rate limiting will never succeed on retry
            if ($code -and $code -ge 400 -and $code -lt 500 -and $code -ne 429) { throw }
            Write-Host ("  api retry {0}/{1} ({2})" -f $i, $Retries, $_.Exception.Message)
            Start-Sleep -Seconds (5 * $i)
        }
    }
    throw $lastError
}

# ---------------------------------------------------------------- local state
$localSha = (git rev-parse $LocalRef).Trim()
# git log returns one array element per line in PowerShell: join it back into a string,
# otherwise the API rejects the commit message with
# "For 'properties/message', [...] is not a string".
$localMsg = ((git log -1 --pretty=%B $LocalRef) | ForEach-Object { $_ -replace "`r", '' }) -join "`n"
$localMsg = $localMsg.TrimEnd()
Write-Host "local  $LocalRef = $localSha"

$remoteRef = Invoke-GitHub GET "/repos/$Repo/git/ref/heads/$Branch"
$remoteSha = $remoteRef.object.sha
Write-Host "remote $Branch = $remoteSha"

if ($remoteSha -eq $localSha) { Write-Host 'already up to date, nothing to do'; exit 0 }

$remoteCommit = Invoke-GitHub GET "/repos/$Repo/git/commits/$remoteSha"
$baseTree = $remoteCommit.tree.sha

# The remote tip may be a commit this clone has never seen: a previous run of THIS
# script builds the commit server-side, so its sha exists only on GitHub. In that case
# `git merge-base --is-ancestor` cannot work, so fall back to matching by tree sha:
# find a local commit whose tree is identical to the remote tree, and use it as the
# diff base (the new commit still gets the real remote sha as its parent).
$diffBase = $remoteSha
# `git rev-parse --verify --quiet` exits non-zero and prints NOTHING when the object is
# missing. (`git cat-file -e` writes to stderr, which PowerShell turns into a terminating
# error while $ErrorActionPreference is 'Stop'.)
& git rev-parse --verify --quiet "$remoteSha^{commit}" | Out-Null
$haveRemoteObject = ($LASTEXITCODE -eq 0)

if (-not [string]::IsNullOrWhiteSpace($BaseCommit)) {
    # 显式基点优先：一次推多个提交时，务必用「内容等于远端 tip 的那个本地提交」，
    # 否则算出来的差异会漏掉前面几个提交的文件（远端树就少文件了）。
    $explicit = (& git rev-parse --verify --quiet "$BaseCommit^{commit}")
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($explicit)) {
        throw ("-BaseCommit {0} is not a commit in this clone" -f $BaseCommit)
    }
    $diffBase = $explicit.Trim()
    Write-Host ("explicit diff base: {0} (assumed to hold the same content as the remote tip)" -f $diffBase)
}
elseif (-not $haveRemoteObject) {
    Write-Host "remote tip $remoteSha is not in this clone; matching by tree sha"
    $diffBase = $null
    foreach ($candidate in (& git rev-list -n 100 $LocalRef)) {
        $t = (& git rev-parse "$candidate^{tree}").Trim()
        if ($t -eq $baseTree) { $diffBase = $candidate.Trim(); break }
    }
    if (-not $diffBase) {
        # 找不到 tree 完全一致的本地提交时（常见原因：git 的换行规范化让本地提交的
        # 树字节与工作区/服务端不一致），退一步用**上一个本地提交**做 diff 基点：
        # 只要「远端当前状态 = 上一个本地提交的内容」，上传这份差异拼出来的树就是对的。
        & git rev-parse --verify --quiet "HEAD^" | Out-Null
        if ($LASTEXITCODE -eq 0) {
            $diffBase = (& git rev-parse "HEAD^").Trim()
            Write-Host "  WARNING: no local tree matched the remote tree;"
            Write-Host "           using the previous local commit $diffBase as the diff base"
            Write-Host "           (fine when the remote tip holds the same content as that commit)"
        } else {
            throw ("remote tip {0} (tree {1}) has no local equivalent and there is no parent commit - run 'git fetch origin' when github.com is reachable" -f $remoteSha, $baseTree)
        }
    } else {
        Write-Host "  local equivalent of the remote tree: $diffBase"
    }
} else {
    # make sure the local commit really descends from the remote tip (fast-forward)
    & git merge-base --is-ancestor $remoteSha $localSha
    if ($LASTEXITCODE -ne 0) { throw "local $localSha is not a fast-forward of remote $remoteSha; push manually" }
}

# ---------------------------------------------------------------- diff
$diffLines = & git diff --name-status $diffBase $localSha
# 没有差异时：如果这次只是想推标签（-Tags），那就继续往下走；
# 否则确实是"没东西可推"，直接报错更清楚。
if (-not $diffLines -and [string]::IsNullOrWhiteSpace($Tags)) {
    throw 'no diff between remote tip and local HEAD'
}
$nothingToPush = -not $diffLines

$changes = @()
foreach ($line in $diffLines) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $parts  = $line -split "`t"
    $status = $parts[0]
    $path   = $parts[1]
    if ($status -like 'R*') { $path = $parts[2] }   # rename -> destination
    $changes += [pscustomobject]@{ Status = $status; Path = $path }
}

Write-Host ("changed files: {0}" -f $changes.Count)
$tree = @()
foreach ($c in $changes) {
    switch -Wildcard ($c.Status) {
        'D' {
            Write-Host ("  delete {0}" -f $c.Path)
            $tree += @{ path = $c.Path; mode = '100644'; type = 'blob'; sha = $null }
        }
        default {
            $full = Join-Path $repoRoot ($c.Path -replace '/', '\')
            if (-not (Test-Path $full)) { throw "missing file: $full" }
            $bytes = [System.IO.File]::ReadAllBytes($full)
            $b64   = [System.Convert]::ToBase64String($bytes)
            $blob  = Invoke-GitHub POST "/repos/$Repo/git/blobs" @{ content = $b64; encoding = 'base64' }
            Write-Host ("  {0} {1} ({2} bytes)" -f $c.Status, $c.Path, $bytes.Length)
            $tree += @{ path = $c.Path; mode = '100644'; type = 'blob'; sha = $blob.sha }
        }
    }
}

if ($DryRun) { Write-Host 'dry run: stopping before tree/commit/ref update'; exit 0 }

# ---------------------------------------------------------------- commit + ref
if ($nothingToPush) {
    # 只推标签的场景（源码早就推过了）：远端 tip 保持不变
    Write-Host '没有源码差异：跳过提交，只处理标签'
    $commit = @{ sha = $remoteSha }
} else {
    $newTree = Invoke-GitHub POST "/repos/$Repo/git/trees" @{ base_tree = $baseTree; tree = $tree }
    $commit  = Invoke-GitHub POST "/repos/$Repo/git/commits" @{
        message = $localMsg
        tree    = $newTree.sha
        parents = @($remoteSha)
    }
    Write-Host "new commit = $($commit.sha)"

    Invoke-GitHub PATCH "/repos/$Repo/git/refs/heads/$Branch" @{ sha = $commit.sha; force = $false } | Out-Null

    $check = Invoke-GitHub GET "/repos/$Repo/git/ref/heads/$Branch"
    Write-Host ("remote $Branch is now {0}" -f $check.object.sha)
    if ($check.object.sha -ne $commit.sha) { throw 'ref update verification failed' }
}

Write-Host ''
Write-Host 'NOTE: the new commit sha differs from the local one (the API builds the'
Write-Host 'commit server-side. Re-sync with:  git fetch origin ; git reset --hard origin/'$Branch

# ---------------------------------------------------------------- tags
# 标签也要能推上去：源端回滚靠的就是 tag。做法是官方那套
#   ① POST /git/tags   建一个 annotated tag 对象（指向某个 commit）
#   ② POST /git/refs   建 refs/tags/<名字>
# 已存在的标签直接跳过（不覆盖 —— 覆盖标签会让"回滚到那一版"变得不可信）。
if (-not [string]::IsNullOrWhiteSpace($Tags)) {
    Write-Host ''
    Write-Host "推送标签（匹配 $Tags）："
    $localTags = @(& git tag --list $Tags)
    foreach ($tag in $localTags) {
        $existing = $null
        try {
            $existing = Invoke-GitHub GET "/repos/$Repo/git/ref/tags/$tag"
        } catch {
            $existing = $null
        }
        if ($existing) {
            Write-Host "  $tag 已存在，跳过"
            continue
        }

        $targetSha = (& git rev-list -n 1 $tag).Trim()
        # 标签要指向**远端存在的**提交。本仓库走 API 推送、提交 sha 由服务端生成，
        # 所以本地 HEAD 那个 sha 远端往往没有 —— 这时改指"内容等价的那个远端提交"：
        #   本次推了源码 → 指向这次生成的新提交；只推标签 → 指向远端当前 tip。
        $target = $targetSha
        if ($targetSha -eq $localSha) {
            $target = if ($nothingToPush) { $remoteSha } else { $commit.sha }
            Write-Host "  $tag 指向本地 HEAD；改指远端等价提交 $($target.Substring(0,8))"
        }

        $tagMessage = (& git tag -l --format='%(contents)' $tag) -join "`n"
        if ([string]::IsNullOrWhiteSpace($tagMessage)) { $tagMessage = $tag }

        # ⚠️ 这里建的是**轻量标签**（ref 直接指向提交），不是 annotated tag 对象。
        #    实测 POST /git/tags 在这个 token 上一直返回 422（响应体还是空的），
        #    而 POST /git/refs 直接指提交一次就成 —— 回滚只需要「tag 名字 → 提交」这一层，
        #    注释信息本地 tag 里有就够了。真要 annotated 对象，等 github.com:443 通了用
        #    `git push origin --tags` 推一次即可。
        Invoke-GitHub POST "/repos/$Repo/git/refs" (
            '{"ref":"refs/tags/' + $tag + '","sha":"' + $target + '"}') | Out-Null
        Write-Host "  ✓ $tag → $($target.Substring(0,8))（轻量标签）"
    }
}
