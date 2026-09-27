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
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/push-via-api.ps1
#   powershell -ExecutionPolicy Bypass -File tools/push-via-api.ps1 -Branch main -DryRun

param(
    [string]$Repo   = 'XinyuCao-SMT/iPad-OS-Video-Waveform-',
    [string]$Branch = 'main',
    [string]$LocalRef = 'HEAD',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $repoRoot

# ---------------------------------------------------------------- credentials
function Get-GitHubToken {
    $payload = "protocol=https`nhost=github.com`n`n"
    $raw = $payload | git credential fill 2>$null
    foreach ($line in $raw) {
        if ($line -match '^password=(.+)$') { return $Matches[1] }
    }
    throw 'could not read a GitHub token from the git credential store'
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
            $json  = $Body | ConvertTo-Json -Depth 12 -Compress
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
if (-not $haveRemoteObject) {
    Write-Host "remote tip $remoteSha is not in this clone; matching by tree sha"
    $diffBase = $null
    foreach ($candidate in (& git rev-list -n 100 $LocalRef)) {
        $t = (& git rev-parse "$candidate^{tree}").Trim()
        if ($t -eq $baseTree) { $diffBase = $candidate.Trim(); break }
    }
    if (-not $diffBase) {
        throw ("remote tip {0} (tree {1}) has no local equivalent - run 'git fetch origin' when github.com is reachable" -f $remoteSha, $baseTree)
    }
    Write-Host "  local equivalent of the remote tree: $diffBase"
} else {
    # make sure the local commit really descends from the remote tip (fast-forward)
    & git merge-base --is-ancestor $remoteSha $localSha
    if ($LASTEXITCODE -ne 0) { throw "local $localSha is not a fast-forward of remote $remoteSha; push manually" }
}

# ---------------------------------------------------------------- diff
$diffLines = & git diff --name-status $diffBase $localSha
if (-not $diffLines) { throw 'no diff between remote tip and local HEAD' }

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

Write-Host ''
Write-Host 'NOTE: the new commit sha differs from the local one (the API builds the'
Write-Host 'commit server-side). Re-sync with:  git fetch origin ; git reset --hard origin/'$Branch
