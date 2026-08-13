# KS0.3 / bug #27 - reproduction: a brand-new run.db must not log FOREIGN KEY constraint failed.
#
# run_state carries FOREIGN KEY (run_id) REFERENCES runs(run_id) (v5_events_and_state.sql), and the
# run loop saved state before it wrote the runs row. On a fresh store that first save is a swallowed
# SQLITE_CONSTRAINT: TryExecute logs it at Error and carries on, so every new run opened with a
# database error in its log and lost its first state write.
#
# Red  : the PUBLISHED engine on PATH, against a rig that has never run before.
# Green: the FRESH BUILD, same rig shape - no FK line anywhere, AND the run_state row is really there
#        (the fix has to be ordering, not a quieter log).
#
# The rig gets its own CONDUCTOR_STATE_HOME, so the operator's catalogue is untouched. Temp only.
# Windows PowerShell 5.1 compatible, ASCII only.

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP ("ks0-3-bug27-" + [guid]::NewGuid().ToString("N").Substring(0, 8))),
    [string]$FreshExe = "C:\code\conductor\src\Conductor\bin\Debug\net10.0\conductor.exe",
    [string]$Sqlite = "c:\adb\sqlite3.exe"
)

$ErrorActionPreference = "Stop"

# Everything this script reads is a file some process still has open - the engine's own log, and the
# redirect target of a process that has only just been stopped. Share the handle or read nothing.
function Read-Shared([string]$path) {
    if (-not (Test-Path $path)) { return "" }
    try {
        $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $sr = New-Object IO.StreamReader($fs)
        $text = $sr.ReadToEnd()
        $sr.Close(); $fs.Close()
        return $text
    }
    catch { return "" }
}

function Start-FreshRun([string]$exe, [string]$label) {
    $rig = Join-Path $Root $label
    $stateDir = Join-Path $rig ".conductor"
    $home_ = Join-Path $rig "state-home"
    New-Item -ItemType Directory -Force -Path $rig, $stateDir, $home_ | Out-Null

    $plan = @{
        name     = "ks0-3-$label"
        repo     = $rig
        tracker  = "TRACKER.md"
        stateDir = $stateDir
        agent    = @{ command = "cmd"; args = @("/c", "echo", "{prompt}") }
        stages   = @(@{ id = "S1"; title = "the only stage"; sessions = 1 })
    } | ConvertTo-Json -Depth 6
    $planPath = Join-Path $rig "rig.plan.json"
    [IO.File]::WriteAllText($planPath, $plan)
    [IO.File]::WriteAllText((Join-Path $rig "TRACKER.md"), "# tracker`n`n| ID | Title | Status |`n|---|---|---|`n| S1.1 | a row | TODO |`n")

    # --paused: the engine starts, saves state, and parks. No agent is ever spawned, which is the
    # shortest path through the ordering this bug lives in.
    # Redirect to FILES, not pipes: a paused engine keeps writing to stdout, and an unread pipe fills
    # at 4KB and blocks the child for ever. Measured the hard way.
    $outFile = Join-Path $rig "stdout.txt"
    $errFile = Join-Path $rig "stderr.txt"
    $prevPlan = $env:CONDUCTOR_PLAN
    $prevHome = $env:CONDUCTOR_STATE_HOME
    $env:CONDUCTOR_PLAN = ""                                  # trap 4: never inherit the driving plan
    $env:CONDUCTOR_STATE_HOME = $home_
    try {
        $p = Start-Process -FilePath $exe -ArgumentList @("run", "-p", "`"$planPath`"", "--paused") `
            -WorkingDirectory $rig -PassThru -NoNewWindow `
            -RedirectStandardOutput $outFile -RedirectStandardError $errFile
    }
    finally {
        $env:CONDUCTOR_PLAN = $prevPlan
        $env:CONDUCTOR_STATE_HOME = $prevHome
    }

    # Wait for the store to appear and settle, then stop the engine we started - by handle, never by
    # name, and never anything we did not launch ourselves.
    # Wait for the store to appear, then give the engine a moment to park. Deliberately NOT reading
    # the engine's log to decide: it holds those files open, and a read that throws would leave the
    # engine running for ever - which is exactly what happened the first time this was written.
    $db = $null
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $db = Get-ChildItem -Path $home_ -Filter run.db -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($db) { break }
    }
    Start-Sleep -Seconds 4
    # Stopped by the id we launched, never by name, and never a process we did not start ourselves.
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    $p.WaitForExit(10000) | Out-Null
    Start-Sleep -Seconds 1

    $stdout = Read-Shared $outFile
    $stderr = Read-Shared $errFile
    $logs = ""
    Get-ChildItem -Path (Join-Path $stateDir "logs") -Filter *.log -ErrorAction SilentlyContinue |
        ForEach-Object { $logs += (Read-Shared $_.FullName) }
    Get-ChildItem -Path $stateDir -Filter *.jsonl -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { $logs += (Read-Shared $_.FullName) }

    $all = $stdout + "`n" + $stderr + "`n" + $logs
    $fk = ([regex]::Matches($all, "FOREIGN KEY constraint failed")).Count

    $stateRows = "-"
    if ($db) { $stateRows = (& $Sqlite -readonly $db.FullName "select count(*) from run_state;" 2>$null) }

    return [pscustomobject]@{
        Exe = $exe; Db = $(if ($db) { $db.FullName } else { "(none)" })
        FkLines = $fk; RunStateRows = $stateRows
        Sample = ($all -split "`n" | Where-Object { $_ -match "FOREIGN KEY" } | Select-Object -First 1)
    }
}

Write-Host "rig root: $Root"
$published = (Get-Command conductor -ErrorAction SilentlyContinue).Source
if (-not $published) { throw "no published conductor on PATH to compare against" }

$red = Start-FreshRun $published "red-published"
$green = Start-FreshRun $FreshExe "green-fresh"

foreach ($r in @($red, $green)) {
    Write-Host ""
    Write-Host "  exe            : $($r.Exe)"
    Write-Host "  run.db         : $($r.Db)"
    Write-Host "  FK error lines : $($r.FkLines)"
    Write-Host "  run_state rows : $($r.RunStateRows)"
    if ($r.Sample) { Write-Host "  first          : $($r.Sample.Trim())" }
}

Write-Host ""
$ok = $true
if ($red.FkLines -lt 1) { Write-Host "UNEXPECTED: the published engine did not show the bug"; $ok = $false }
if ($green.FkLines -ne 0) { Write-Host "FAIL: this build still logs FOREIGN KEY constraint failed"; $ok = $false }
if ($green.RunStateRows -ne "1") { Write-Host "FAIL: the first run_state write did not survive (rows=$($green.RunStateRows))"; $ok = $false }

if ($ok) { Write-Host "PASS - red on the published engine, green on this build, and the state row is really there"; exit 0 }
exit 1
