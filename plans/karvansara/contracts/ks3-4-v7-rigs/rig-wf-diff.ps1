$ErrorActionPreference = 'Continue'
$root = 'C:\Users\shahi\AppData\Local\Temp\claude\C--Code-conductor\5ab9932e-feb0-46c0-8617-3506963986ab\scratchpad\v7'
$rig  = Join-Path $root 'wfdiff'
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
$plan = '{ "name":"ks34-wf","repo":"' + $rj + '","tracker":"TRACKER.md",' +
        '"agent":{"command":"git","args":["-p","{prompt}"]},' +
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
| S1.1 | row | TODO | - | - |
"@
Set-Content (Join-Path $repo 'TRACKER.md') $tracker -Encoding ascii

New-Item -ItemType Directory -Force -Path (Join-Path $repo '.conductor') | Out-Null
$state = @"
{
  "planName": "ks34-wf",
  "runId": "wf000000000000000000000000000001",
  "status": "Idle",
  "currentStage": "S1",
  "sessionCounter": 1,
  "attemptsThisStage": 1,
  "workflowStepIndices": { "S1": 1 }
}
"@
$statePath = Join-Path $repo '.conductor\state.json'
Set-Content $statePath $state -Encoding ascii
Copy-Item $statePath (Join-Path $rig 'state.backup.json')

$env:CONDUCTOR_STATE_HOME = (Join-Path $rig 'home')
$env:CONDUCTOR_PLAN = ''
Write-Host "===== PREFLIGHT ====="
& $exe preflight -p (Join-Path $repo 'fixture.plan.json') --no-auth-check --no-update-check 2>&1 | Select-String "compose|READY" | Select-Object -First 3

Write-Host "===== DRY RUN ====="
$dry = & $exe run -p (Join-Path $repo 'fixture.plan.json') --dry-run --no-control-plane --no-face --headless 2>&1 | Out-String
$dry | Out-File (Join-Path $rig 'dryrun.txt') -Encoding utf8
$marker = $dry.IndexOf('with prompt: ---')
if ($marker -ge 0) {
  $p = $dry.Substring($marker + 'with prompt: ---'.Length)
  $p = $p -replace "^(\r?\n)+", ""
  Set-Content (Join-Path $rig 'dry.prompt.txt') $p -NoNewline
  Write-Host ("dry prompt (trimmed of log framing): {0} chars" -f $p.Length)
}
($dry -split "`n" | Select-String "DRY RUN") | Select-Object -First 2

# restore state (dry run must not have changed it, but be safe)
Copy-Item (Join-Path $rig 'state.backup.json') $statePath -Force
Write-Host "===== LIVE RUN --once ====="
& $exe run -p (Join-Path $repo 'fixture.plan.json') --once --no-control-plane --no-face --headless 2>&1 | Select-String "session #\d+ start" | Select-Object -First 2
Get-ChildItem (Join-Path $repo '.conductor\logs') -Filter '*.prompt.md' -ErrorAction SilentlyContinue | ForEach-Object {
  $raw = [System.IO.File]::ReadAllText($_.FullName)
  Write-Host ("{0}  {1} chars (File.ReadAllText)" -f $_.Name, $raw.Length)
  Copy-Item $_.FullName (Join-Path $rig 'live.prompt.txt') -Force
}
