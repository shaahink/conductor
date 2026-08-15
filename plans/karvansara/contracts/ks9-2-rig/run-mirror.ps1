# KS9.2 - the live mirror against a REAL GitHub, with a real outage in the middle.
#
# The rig has its own CONDUCTOR_STATE_HOME, its own scratch git repo, an explicit -p and a cleared
# CONDUCTOR_PLAN, and it drives THIS TREE'S FRESH BUILD - never the conductor on PATH, which is the
# published engine driving the session that wrote this.
#
# The outage is a process-boundary one on purpose: CONDUCTOR_GITHUB_API is read per request, but a
# running engine cannot have its environment changed from outside, so pass 1 runs entirely against a
# dead port and pass 2 runs entirely against api.github.com. That is the STRONGER shape anyway - it
# proves the cursor survived a process death, not just a retry inside one.
#
# Usage:  powershell -File run-mirror.ps1 [-Exe <fresh conductor.exe>] [-Repo owner/name]
param(
  [string]$Exe  = 'C:\code\conductor\src\Conductor\bin\Debug\net10.0\conductor.exe',
  [string]$Repo = 'shaahink/conductor-sync-scratch-ks92',
  [string]$Root = (Join-Path $env:TEMP 'ks9-2-mirror')
)
$ErrorActionPreference = 'Continue'
if (-not (Test-Path $Exe)) { throw "fresh build not found: $Exe" }

$token = (& gh auth token).Trim()
if (-not $token) { throw 'no gh token' }

# ---------------------------------------------------------------- the rig
if (Test-Path $Root) { Remove-Item -Recurse -Force $Root }
$home_ = Join-Path $Root 'home'
$repoDir = Join-Path $Root 'repo'
New-Item -ItemType Directory -Force -Path $home_, $repoDir | Out-Null
Push-Location $repoDir
git init -q 2>$null | Out-Null
git config user.email r@e.com; git config user.name r
Set-Content README.md 'scratch' -Encoding ascii
git add -A | Out-Null; git commit -q -m seed | Out-Null
Pop-Location

$planJson = '{ "name":"ks92-mirror","repo":"@REPO@","tracker":"TRACKER.md",' +
  '"agent":{"command":"git","args":["-p","{prompt}"]},' +
  '"limits":{"dnsHealthCheck":{"enabled":false},"authPreflight":false},' +
  '"github":{"enabled":true,"repo":"@GH@","labelPrefix":"conductor","runHistoryIssue":true},' +
  '"stages":[{"id":"S1","title":"the mirrored stage","sessions":3}] }'
Set-Content (Join-Path $repoDir 'fixture.plan.json') `
  ($planJson.Replace('@REPO@', $repoDir.Replace('\','\\')).Replace('@GH@', $Repo)) -Encoding ascii

$tracker = @"
# ks92 mirror rig

## Handoff

nothing pending.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|---|---|---|---|
| S1.1 | the first row | DONE | abc1234 | evidence.md |
| S1.2 | the second row | TODO | - | - |
| S1.3 | the third row | TODO | - | - |
"@
Set-Content (Join-Path $repoDir 'TRACKER.md') $tracker -Encoding ascii

$plan = Join-Path $repoDir 'fixture.plan.json'
$env:CONDUCTOR_STATE_HOME = $home_
$env:CONDUCTOR_PLAN = ''
$env:CONDUCTOR_GITHUB_TOKEN = $token

function Invoke-Pass {
  param([string]$Name, [string]$Api)
  if ($Api) { $env:CONDUCTOR_GITHUB_API = $Api } else { Remove-Item Env:CONDUCTOR_GITHUB_API -EA SilentlyContinue }
  $log = Join-Path $Root ("$Name.log")
  Write-Host ""
  Write-Host "############ $Name  (api=$(if($Api){$Api}else{'api.github.com'})) ############"
  & $Exe run -p $plan --once --no-control-plane --no-face --headless *>&1 | Out-File $log -Encoding utf8
  Write-Host "run exit=$LASTEXITCODE"
  Get-Content $log | Select-String 'github mirror|session #|complete' |
    ForEach-Object { $_.Line } | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" }
}

function Show-Board {
  param([string]$Label)
  # trap 13: never --jq from PowerShell with quotes in the filter - fetch raw and ConvertFrom-Json.
  $issues = (& gh api "repos/$Repo/issues?state=all&per_page=100") | ConvertFrom-Json
  Write-Host "  [$Label] issues=$($issues.Count)"
  foreach ($i in $issues | Sort-Object number) {
    $labels = ($i.labels | ForEach-Object { $_.name }) -join ','
    Write-Host ("    #{0} [{1}] {2}  <{3}>  comments={4}" -f $i.number, $i.state, $i.title, $labels, $i.comments)
  }
}

Write-Host "===== BEFORE ====="
Show-Board 'before'

Invoke-Pass -Name 'pass1-network-dead' -Api 'http://127.0.0.1:9'
Show-Board 'after pass 1 (network dead)'

Invoke-Pass -Name 'pass2-reconnected' -Api ''
Show-Board 'after pass 2 (reconnected)'

Invoke-Pass -Name 'pass3-steady' -Api ''
Show-Board 'after pass 3 (steady state)'

Write-Host ""
Write-Host "===== request counts the engine logged ====="
Get-ChildItem $Root -Filter '*.log' | Sort-Object Name | ForEach-Object {
  $n = ($_.Name)
  Get-Content $_.FullName | Select-String 'github mirror' | ForEach-Object { Write-Host "  [$n] $($_.Line)" }
}
Write-Host ""
Write-Host "rig at $Root"
