$ErrorActionPreference = 'Continue'
$root = 'C:\Users\shahi\AppData\Local\Temp\claude\C--Code-conductor\5ab9932e-feb0-46c0-8617-3506963986ab\scratchpad\v7'
$rig  = Join-Path $root 'compred'
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
$plan = '{ "name":"ks34-compred","repo":"' + $rj + '","tracker":"TRACKER.md",' +
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
  "planName": "ks34-compred",
  "runId": "cr000000000000000000000000000001",
  "status": "Idle",
  "currentStage": "S1",
  "sessionCounter": 1,
  "attemptsThisStage": 0
}
"@
$statePath = Join-Path $repo '.conductor\state.json'
Set-Content $statePath $state -Encoding ascii
Copy-Item $statePath (Join-Path $rig 'state.backup.json')

$env:CONDUCTOR_STATE_HOME = (Join-Path $rig 'home')
$env:CONDUCTOR_PLAN = ''
Write-Host "===== PREFLIGHT ====="
& $exe preflight -p (Join-Path $repo 'fixture.plan.json') --no-auth-check --no-update-check 2>&1 | Out-String
Write-Host "preflight exit=$LASTEXITCODE"

Write-Host "===== DRY RUN ====="
& $exe run -p (Join-Path $repo 'fixture.plan.json') --dry-run --no-control-plane --no-face --headless 2>&1 | Select-String "DRY RUN|complete|gate" | Select-Object -First 6
Copy-Item (Join-Path $rig 'state.backup.json') $statePath -Force

Write-Host "===== LIVE RUN --once ====="
& $exe run -p (Join-Path $repo 'fixture.plan.json') --once --no-control-plane --no-face --headless 2>&1 |
  Select-String "session #|gate|complete|fix|NeedsHuman" | Select-Object -First 15
Write-Host "run exit=$LASTEXITCODE"
Get-ChildItem (Join-Path $repo '.conductor\logs') -Filter '*.prompt.md' -EA SilentlyContinue | ForEach-Object {
  Write-Host ("{0}  {1} chars" -f $_.Name, [System.IO.File]::ReadAllText($_.FullName).Length)
}
