# KS3.4 round 8 - the three branches round 7 refuted, drill vs real launch on identical fixtures.
# Each rig gets its own scratch repo, its own CONDUCTOR_STATE_HOME, an explicit -p and a cleared
# CONDUCTOR_PLAN, and it drives THIS TREE'S FRESH BUILD - never the conductor on PATH.
# Usage:  pwsh -File run-round8.ps1 [-Exe <path to fresh conductor.exe>] [-Root <scratch dir>]
param(
  [string]$Exe  = 'C:\code\conductor\src\Conductor\bin\Debug\net10.0\conductor.exe',
  [string]$Root = (Join-Path $env:TEMP 'ks3-4-v8')
)
$ErrorActionPreference = 'Continue'
if (-not (Test-Path $Exe)) { throw "fresh build not found: $Exe" }

function New-Rig {
  param([string]$Name, [string]$PlanJson, [string]$StateJson)
  $rig = Join-Path $Root $Name
  if (Test-Path $rig) { Remove-Item -Recurse -Force $rig }
  New-Item -ItemType Directory -Force -Path (Join-Path $rig 'home') | Out-Null
  $repo = Join-Path $rig 'repo'
  New-Item -ItemType Directory -Force -Path $repo | Out-Null
  Push-Location $repo
  git init -q 2>$null | Out-Null
  git config user.email r@e.com; git config user.name r
  Set-Content README.md 'x' -Encoding ascii
  git add -A | Out-Null; git commit -q -m s | Out-Null
  Pop-Location

  Set-Content (Join-Path $repo 'fixture.plan.json') ($PlanJson.Replace('@REPO@', $repo.Replace('\', '\\'))) -Encoding ascii
  $tracker = @"
# f

## Handoff

nothing pending.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|---|---|---|---|
| S1.1 | row | DONE | abc1234 | evidence.md |
"@
  Set-Content (Join-Path $repo 'TRACKER.md') $tracker -Encoding ascii
  New-Item -ItemType Directory -Force -Path (Join-Path $repo '.conductor') | Out-Null
  Set-Content (Join-Path $repo '.conductor\state.json') $StateJson -Encoding ascii
  return @{ Rig = $rig; Repo = $repo; Plan = (Join-Path $repo 'fixture.plan.json') }
}

function Invoke-Rig {
  param([hashtable]$R, [string]$Title)
  $env:CONDUCTOR_STATE_HOME = (Join-Path $R.Rig 'home')
  $env:CONDUCTOR_PLAN = ''
  Write-Host ""
  Write-Host "############ $Title ############"
  Write-Host "===== DRILL: conductor preflight ====="
  $pre = & $Exe preflight -p $R.Plan --no-auth-check --no-update-check 2>&1
  $exit = $LASTEXITCODE
  # unwrapped, for the evidence artifact - Write-Host below is clipped to the console width
  $pre | ForEach-Object { $_.ToString() } | Out-File (Join-Path $R.Rig 'preflight.txt') -Encoding utf8
  $pre | ForEach-Object { $_.ToString() } | Select-String -Pattern 'compose|READY|^\s+-' -Raw |
    Select-Object -First 12 | ForEach-Object { Write-Host $_ }
  Write-Host "preflight exit=$exit"

  Write-Host "===== LAUNCH: conductor run --once ====="
  & $Exe run -p $R.Plan --once --no-control-plane --no-face --headless *>&1 |
    Out-File (Join-Path $R.Rig 'live.log') -Encoding utf8
  Write-Host "run exit=$LASTEXITCODE"
  Get-Content (Join-Path $R.Rig 'live.log') |
    Select-String 'session #|scheduling|queuing|NOT confirmed|gate red-gate|phase gate|complete' |
    ForEach-Object { $_.Line } | Select-Object -First 12 | ForEach-Object { Write-Host $_ }
  Write-Host "--- prompt files the launch actually wrote ---"
  Get-ChildItem (Join-Path $R.Repo '.conductor\logs') -Filter '*.prompt.md' -EA SilentlyContinue |
    ForEach-Object { Write-Host ("{0}  {1} chars" -f $_.Name, [System.IO.File]::ReadAllText($_.FullName).Length) }
}

$agent = '"agent":{"command":"git","args":["-p","{prompt}"]},'
$lim   = '"limits":{"dnsHealthCheck":{"enabled":false},"authPreflight":false},'
$redGate = '"gates":[{"name":"red-gate","command":"git rev-parse --verify refs/heads/definitely-not-a-branch","optional":false,"tier":"fast","timeoutMinutes":2}],'
$stage = '"stages":[{"id":"S1","title":"only","sessions":3}] }'

# (1) round 7 finding 1: perPhase + audit enabled, not parallel - the scheduling composes an Audit session.
$a = New-Rig -Name 'phaseaudit' `
  -PlanJson ('{ "name":"ks34-phase","repo":"@REPO@","tracker":"TRACKER.md","gatePolicy":"perPhase",' + $agent +
             '"audit":{"enabled":true,"enableParallel":false},' + $lim + $stage) `
  -StateJson '{ "planName":"ks34-phase","runId":"ph000000000000000000000000000001","status":"Idle","currentStage":"S1","sessionCounter":1,"attemptsThisStage":0,"confirmedStages":[] }'
Invoke-Rig -R $a -Title 'RIG 1 - scheduled auto-fix audit (round 7 finding 1)'

# (2) round 7 finding 2: every row DONE + a red required gate - the completion battery composes a Fix session.
$b = New-Rig -Name 'compred' `
  -PlanJson ('{ "name":"ks34-compred","repo":"@REPO@","tracker":"TRACKER.md",' + $agent + $redGate + $lim + $stage) `
  -StateJson '{ "planName":"ks34-compred","runId":"cr000000000000000000000000000001","status":"Idle","currentStage":"S1","sessionCounter":1,"attemptsThisStage":0 }'
Invoke-Rig -R $b -Title 'RIG 2 - red completion battery (round 7 finding 2)'

# (3) round 7 finding 3: a queued pendingPhaseGate + a red required gate - same, at the phase gate.
$c = New-Rig -Name 'qgatered' `
  -PlanJson ('{ "name":"ks34-qgate","repo":"@REPO@","tracker":"TRACKER.md","gatePolicy":"perPhase",' + $agent + $redGate + $lim + $stage) `
  -StateJson '{ "planName":"ks34-qgate","runId":"qg000000000000000000000000000001","status":"Idle","currentStage":"S1","sessionCounter":1,"attemptsThisStage":0,"confirmedStages":[],"pendingPhaseGate":{"stageId":"S1","stageStartHead":"HEAD"} }'
Invoke-Rig -R $c -Title 'RIG 3 - red queued phase gate (round 7 finding 3)'

Write-Host ""
Write-Host "rigs under $Root"
