$ErrorActionPreference = 'Continue'
$root = 'C:\Users\shahi\AppData\Local\Temp\claude\C--Code-conductor\5ab9932e-feb0-46c0-8617-3506963986ab\scratchpad\v7'
$rig  = Join-Path $root 'qgate'
$exe  = 'C:\Code\conductor-lane-ks3\src\Conductor\bin\Debug\net10.0\conductor.exe'
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

$rj = $repo.Replace('\', '\\')
$plan = '{ "name":"ks34-qgate","repo":"' + $rj + '","tracker":"TRACKER.md","gatePolicy":"perPhase",' +
        '"agent":{"command":"git","args":["-p","{prompt}"]},' +
        '"gates":[{"name":"red-gate","command":"git rev-parse --verify refs/heads/definitely-not-a-branch","optional":false,"tier":"fast","timeoutMinutes":2}],' +
        '"limits":{"dnsHealthCheck":{"enabled":false},"authPreflight":false},' +
        '"stages":[{"id":"S1","title":"only","sessions":3}] }'
Set-Content (Join-Path $repo 'fixture.plan.json') $plan -Encoding ascii

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
$state = @"
{
  "planName": "ks34-qgate",
  "runId": "qg000000000000000000000000000001",
  "status": "Idle",
  "currentStage": "S1",
  "sessionCounter": 1,
  "attemptsThisStage": 0,
  "confirmedStages": [],
  "pendingPhaseGate": { "stageId": "S1", "stageStartHead": "HEAD" }
}
"@
Set-Content (Join-Path $repo '.conductor\state.json') $state -Encoding ascii

$env:CONDUCTOR_STATE_HOME = (Join-Path $rig 'home')
$env:CONDUCTOR_PLAN = ''
Write-Host "===== PREFLIGHT ====="
& $exe preflight -p (Join-Path $repo 'fixture.plan.json') --no-auth-check --no-update-check 2>&1 |
  Select-String "compose|READY|NOT READY" | Select-Object -First 4
Write-Host "preflight exit=$LASTEXITCODE"

Write-Host "===== LIVE RUN --once ====="
& $exe run -p (Join-Path $repo 'fixture.plan.json') --once --no-control-plane --no-face --headless *>&1 |
  Out-File (Join-Path $rig 'live.log') -Encoding utf8
Write-Host "run exit=$LASTEXITCODE"
Get-Content (Join-Path $rig 'live.log') | Select-String "session #|gate |queuing|CONFIRMED" |
  ForEach-Object { $_.Line } | Select-Object -First 10
Get-ChildItem (Join-Path $repo '.conductor\logs') -Filter '*.prompt.md' -EA SilentlyContinue | ForEach-Object {
  Write-Host ("{0}  {1} chars" -f $_.Name, [System.IO.File]::ReadAllText($_.FullName).Length)
}
