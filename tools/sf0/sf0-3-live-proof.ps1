# SF0.3 live proof - bugs 5, 12, 13 driven through the FRESH build against a scratch rig.
#
# Rules this script obeys (they are in the session prompt for a reason):
#   - it never touches C:/code/conductor/.conductor - the rig has its own repo, plan and state dir
#   - it drives src/Conductor/bin/Debug/net10.0/conductor.exe, NOT the conductor on PATH (that one is
#     the published engine driving the session)
#   - it kills only the pid it started itself, and only after checking that pid is its own child
#   - ASCII only (Windows PowerShell 5.1)
#
# Usage: powershell -File tools/sf0/sf0-3-live-proof.ps1

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repoRoot 'src\Conductor\bin\Debug\net10.0\conductor.exe'
if (-not (Test-Path $exe)) { throw "fresh build not found at $exe - run: dotnet build Conductor.slnx" }

$rig = Join-Path $env:TEMP 'sarban-proofs\sf0-3'
if (Test-Path $rig) { Remove-Item $rig -Recurse -Force }
New-Item -ItemType Directory -Force $rig | Out-Null

Write-Host "=== SF0.3 live proof ==="
Write-Host ("engine   : {0}" -f $exe)
Write-Host ("built    : {0}" -f (Get-Item $exe).LastWriteTime)
Write-Host ("rig      : {0}" -f $rig)
Write-Host ("PATH conductor (NOT used): {0}" -f (Get-Command conductor -ErrorAction SilentlyContinue).Source)
Write-Host ""

# ---------------------------------------------------------------- the rig

Set-Content -Path (Join-Path $rig 'TRACKER.md') -Encoding utf8 -Value @'
# Proof

## Handoff
none.

| # | Checkpoint | Status | Commit | Evidence |
|---|---|---|---|---|
| P0.1 | item | TODO | | |
'@

Set-Content -Path (Join-Path $rig 'session.md') -Encoding utf8 -Value 'noop {stage}.'
# ASCII, not utf8: Set-Content -Encoding utf8 emits a BOM here, and cmd.exe reads the BOM as part of
# the first command ("'@echo' is not recognized"). Measured while writing this script.
Set-Content -Path (Join-Path $rig 'agent.cmd') -Encoding ascii -Value "@echo off`r`nexit /b 0`r`n"

$planPath = Join-Path $rig 'proof.plan.json'
$agent = (Join-Path $rig 'agent.cmd') -replace '\\', '/'
$repoFwd = $rig -replace '\\', '/'
Set-Content -Path $planPath -Encoding utf8 -Value @"
{
  "name": "sf03proof",
  "repo": "$repoFwd",
  "tracker": "TRACKER.md",
  "agent": { "command": "cmd.exe", "args": ["/c", "$agent", "{prompt}"], "provider": "claude", "output": "stream-json" },
  "stages": [ { "id": "P0", "title": "Proof", "sessions": 1 } ],
  "gates": [ { "name": "smoke", "command": "echo ok", "tier": "fast", "timeoutMinutes": 1 } ],
  "limits": { "maxSessions": 1 },
  "report": { "commit": false }
}
"@

Write-Host "--- creating a real run in the rig (one no-op session) ---"
& $exe run -p $planPath --once 2>&1 | Select-Object -Last 6
$runDb = Join-Path $rig '.conductor\run.db'
if (-not (Test-Path $runDb)) { throw "the rig run did not create $runDb" }
Write-Host ""

# ---------------------------------------------------------------- bug 12

Write-Host "--- bug 12: a PIPED bg start must return while its child still runs ---"
Set-Content -Path (Join-Path $rig 'slow.cmd') -Encoding ascii -Value "@echo off`r`necho slow-child-started`r`nping -n 61 127.0.0.1 >nul`r`necho slow-child-done`r`n"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
# The pipe is the whole point: piping is what used to block until the child exited.
$out = & $exe bg start -p $planPath --purpose slowjob -- cmd.exe /c (Join-Path $rig 'slow.cmd') | Out-String
$sw.Stop()
Write-Host $out.Trim()
Write-Host ("PIPED bg start returned in {0:N2}s (child sleeps 60s; before the fix this blocked for the full 60)" -f $sw.Elapsed.TotalSeconds)
if ($sw.Elapsed.TotalSeconds -ge 15) { Write-Host "FAIL: bug 12 is NOT fixed" } else { Write-Host "PASS: the caller's stdout is no longer held by the detached child" }
Write-Host ""

$childPid = [int](( $out | Select-String -Pattern 'PID=(\d+)' ).Matches[0].Groups[1].Value)
Write-Host ("tracked pid: {0}" -f $childPid)

# ---------------------------------------------------------------- bug 13

Write-Host ""
Write-Host "--- bug 13: bg logs must read a log the child is STILL writing ---"
Start-Sleep -Seconds 2   # let the shell create the redirect target
& $exe bg logs -p $planPath $childPid
Write-Host ("bg logs exit code: {0}" -f $LASTEXITCODE)
if ($LASTEXITCODE -eq 0) { Write-Host "PASS: a live log is readable" } else { Write-Host "FAIL: bug 13 is NOT fixed" }
Write-Host ""

# ---------------------------------------------------------------- bug 5

Write-Host "--- bug 5: bg status must survive a pid it cannot inspect ---"
$sqlite = (Get-Command sqlite3 -ErrorAction SilentlyContinue).Source
if ($sqlite) {
    $runId = & $sqlite $runDb "select run_id from pids limit 1;"
    $stamp = (Get-Date).ToUniversalTime().AddMinutes(-5).ToString('o')
    # pid 4 is the Windows System process: it exists and cannot be opened by anyone.
    & $sqlite $runDb "insert into pids (pid, purpose, stage_id, session_number, started_utc, run_id) values (4, 'bg:uninspectable', 'P0', 1, '$stamp', '$runId');"
    Write-Host "injected pid 4 (Windows System, uninspectable) into the rig's pids table"
} else {
    Write-Host "sqlite3 not on PATH - skipping the pid-4 injection (the suite covers it)"
}
& $exe bg status -p $planPath
Write-Host ("bg status exit code: {0}" -f $LASTEXITCODE)
if ($LASTEXITCODE -eq 0) { Write-Host "PASS: bg status printed a table instead of a Win32 stack trace" } else { Write-Host "FAIL" }
Write-Host ""

# ---------------------------------------------------------------- cleanup

Write-Host "--- cleanup: stopping ONLY the child this script started ---"
$proc = Get-CimInstance Win32_Process -Filter "ProcessId=$childPid" -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host ("pid {0} command line: {1}" -f $childPid, $proc.CommandLine)
    if ($proc.CommandLine -like "*sarban-proofs*") {
        & $exe bg stop -p $planPath $childPid
        Write-Host ("bg stop exit code: {0}" -f $LASTEXITCODE)
    } else {
        Write-Host "REFUSING to stop pid $childPid - its command line is not this rig's. Leave it alone."
    }
} else {
    Write-Host "child already exited"
}
Write-Host ""
Write-Host "=== end of proof ==="
