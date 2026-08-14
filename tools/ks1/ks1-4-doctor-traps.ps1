# KS1.4 - seven seeded trap plans, one per new doctor lint.
#
# Each lint exists because the failure it catches has already cost a run here. This rig builds one
# plan per lint that carries exactly that failure, asks the FRESH BUILD's doctor about it, and
# demands two things of every answer: exit code 1, and the offending artifact named on stdout. Then
# it asks the same doctor about a plan carrying none of them and demands exit 0.
#
# The drift trap is asked twice, because half of that lint is knowing when NOT to fire: once with its
# engine live (red), and once with the same store, the same unfinished row and the same edited file
# after that engine has been killed (green - nothing is scheduling from a document nobody is holding).
#
# Discipline (promptExtra traps 0, 3, 4, 7, 13):
#   * its own CONDUCTOR_STATE_HOME and its own CONDUCTOR_RUN_DB, so no invocation can upsert the
#     operator's real catalogue - the count is printed before and after and must not move;
#   * CONDUCTOR_PLAN cleared and -p passed explicitly on every call;
#   * the only process it starts is the drift rig's own engine, stopped by pid after Win32_Process
#     confirms the command line names this rig's plan - never by name;
#   * no gate, hook or agent is ever executed: doctor resolves, it does not run.
# Windows PowerShell 5.1 compatible, ASCII only.

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP ("ks1-4-traps-" + [guid]::NewGuid().ToString("N").Substring(0, 8))),
    [string]$FreshExe = ""
)

$ErrorActionPreference = "Stop"

if (-not $FreshExe) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $FreshExe = Join-Path $repoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
}
if (-not (Test-Path $FreshExe)) { throw "no fresh build at $FreshExe - run dotnet build first" }

function Get-RealCatalogueCount {
    $p = Join-Path $env:LOCALAPPDATA "conductor\catalogue.json"
    if (-not (Test-Path $p)) { return 0 }
    try { $j = Get-Content $p -Raw -ErrorAction Stop | ConvertFrom-Json } catch { return -1 }
    if ($null -eq $j.entries) { return 0 }
    return @($j.entries).Count
}

$stateHome = Join-Path $Root "state-home"
New-Item -ItemType Directory -Force -Path $Root, $stateHome | Out-Null

$env:CONDUCTOR_PLAN = ""
$env:CONDUCTOR_STATE_HOME = $stateHome
$env:CONDUCTOR_RUN_DB = Join-Path $Root "scratch-run.db"

$catalogueBefore = Get-RealCatalogueCount
Write-Host "rig root  : $Root"
Write-Host "engine    : $FreshExe"
Write-Host "state home: $stateHome"
Write-Host "real catalogue entries before: $catalogueBefore"
Write-Host ""

$tracker = @(
    "# t",
    "",
    "## Handoff",
    "",
    "nothing pending.",
    "",
    "## Checkpoints",
    "",
    "| # | Checkpoint | Status | Commit | Evidence |",
    "|---|---|---|---|---|",
    "| S1.1 | the only row | TODO | - | - |",
    ""
) -join "`n"

# The escalation token is assembled, never written: the match that parks a run is a plain substring
# and a fixture carrying the literal would park the run reading this file (promptExtra trap 9).
$escalation = "HUMAN" + ":"

# doctor's pre-existing git check wants a repository; without one every trap plan fails for a reason
# that has nothing to do with the lint under test, and the clean case could never be green.
function Initialize-RigRepo {
    param([string]$Dir)
    # git writes advice to stderr, which a NativeCommandError under `Stop` turns into a thrown rig.
    $prev = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & git -C $Dir init -b main --quiet 2>&1 | Out-Null
        & git -C $Dir config core.autocrlf false 2>&1 | Out-Null
        & git -C $Dir config user.email "ks14@test" 2>&1 | Out-Null
        & git -C $Dir config user.name "KS1.4 Rig" 2>&1 | Out-Null
        & git -C $Dir add -A 2>&1 | Out-Null
        & git -C $Dir commit -m "rig" --no-gpg-sign --quiet 2>&1 | Out-Null
    }
    finally { $ErrorActionPreference = $prev }
}

function New-TrapPlan {
    param([string]$Name, [scriptblock]$Mutate)

    $dir = Join-Path $Root $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    [IO.File]::WriteAllText((Join-Path $dir "TRACKER.md"), $tracker)
    Initialize-RigRepo $dir

    $plan = [ordered]@{
        version     = "1.0"
        planVersion = 1
        name        = "ks14-$Name"
        repo        = $dir
        tracker     = "TRACKER.md"
        agent       = [ordered]@{ command = "git"; args = @("-p", "{prompt}") }
        stages      = @(@{ id = "S1"; title = "the only stage"; sessions = 1 })
        gates       = @()
    }
    & $Mutate $plan $dir
    $path = Join-Path $dir "trap.plan.json"
    [IO.File]::WriteAllText($path, ($plan | ConvertTo-Json -Depth 8))
    return $path
}

$results = New-Object System.Collections.ArrayList

function Invoke-Doctor {
    param([string]$Label, [string]$PlanPath, [int]$ExpectExit, [string[]]$MustSay)

    Write-Host "--- $Label"
    Write-Host "    plan: $PlanPath"
    $out = & $FreshExe doctor -p $PlanPath --no-auth-check --no-update-check 2>&1 | Out-String
    $code = $LASTEXITCODE
    # Spectre wraps long lines; fold the output to one line so a named artifact split across a wrap
    # is still found. Strip ANSI first.
    $plain = ($out -replace "`e\[[0-9;]*m", "") -replace "\s+", " "
    Write-Host "    exit: $code (expected $ExpectExit)"

    $ok = ($code -eq $ExpectExit)
    foreach ($needle in $MustSay) {
        $flat = ($needle -replace "\s+", " ")
        if ($plain -notlike ("*" + $flat + "*")) {
            Write-Host "    MISSING from stdout: $needle"
            $ok = $false
        }
        else {
            Write-Host "    said: $needle"
        }
    }
    if (-not $ok) {
        Write-Host "--- doctor output ---"
        Write-Host $out
    }
    [void]$results.Add([pscustomobject]@{ Label = $Label; Ok = $ok })
    Write-Host ""
}

# ---------------------------------------------------------------- 1. gate-command path probe
$p = New-TrapPlan "gate-paths" {
    param($plan, $dir)
    $plan.gates = @(@{ name = "build"; command = "definitely-not-a-real-gate-xyz123 --version"; tier = "fast" })
}
Invoke-Doctor "1. gate-command path probe" $p 1 @("definitely-not-a-real-gate-xyz123", "'build'")

# ---------------------------------------------------------------- 2. hook dry-run
$p = New-TrapPlan "hooks" {
    param($plan, $dir)
    $plan.setup = [ordered]@{ command = "definitely-not-a-real-hook-xyz123 -x"; timeoutMinutes = 2 }
}
Invoke-Doctor "2. hook dry-run" $p 1 @("definitely-not-a-real-hook-xyz123", "plan.setup")

# ---------------------------------------------------------------- 3. checkpoint id vs tracker
$p = New-TrapPlan "checkpoint-ids" {
    param($plan, $dir)
    Add-Content -Path (Join-Path $dir "TRACKER.md") -Value "| S1-2 | a row the regex drops | TODO | - | - |"
}
Invoke-Doctor "3. checkpoint-id versus tracker" $p 1 @("S1-2", "stageIdPattern")

# ---------------------------------------------------------------- 5. composed-prompt argv length
# (4, plan drift, needs a live engine and runs last.)
$p = New-TrapPlan "argv" {
    param($plan, $dir)
    $plan.promptExtra = ("x" * 34000)
}
Invoke-Doctor "5. composed-prompt argv length" $p 1 @("32767-char ceiling", "stage 'S1'")

# ---------------------------------------------------------------- 6. brace sweep
$p = New-TrapPlan "braces" {
    param($plan, $dir)
    $t = Join-Path $dir "templates"
    New-Item -ItemType Directory -Force -Path $t | Out-Null
    [IO.File]::WriteAllText((Join-Path $t "session.md"), "You are a DELIVER session for {planNam}.`n")
    $plan.templatesDir = "templates"
}
Invoke-Doctor "6. brace sweep over templatesDir" $p 1 @("session.md", "{planNam}")

# ---------------------------------------------------------------- 7. escalation-token sweep
$p = New-TrapPlan "escalation" {
    param($plan, $dir)
    $t = Join-Path $dir "templates"
    New-Item -ItemType Directory -Force -Path $t | Out-Null
    [IO.File]::WriteAllText((Join-Path $t "fix.md"), "You are a FIX session.`nAsk with $escalation when blocked.`n")
    $plan.templatesDir = "templates"
    $plan.stages[0].notes = "If you are stuck, write $escalation in the handoff."
}
Invoke-Doctor "7. escalation-token sweep" $p 1 @("stage 'S1' notes", "templates/fix.md")

# ---------------------------------------------------------------- the clean plan
$clean = New-TrapPlan "clean" { param($plan, $dir) }
Invoke-Doctor "0. a plan carrying none of them" $clean 0 @("gate-paths", "plan-drift", "escalation")

# ---------------------------------------------------------------- 4. plan drift (needs a LIVE engine)
# Drift is a claim about a run something is still scheduling FROM, and KS1.3's shared rule decides
# which runs those are: an unfinished row with no engine behind it is orphaned, not drifting. So this
# trap needs both halves - a run that recorded loading v2, AND an engine still holding the store - and
# it proves the other half in the same breath: once that engine is gone, the same store, the same
# stale row and the same edited file are green.
#
# The third edit is written with the same length and the same last-write time as the second, because
# the engine's own boundary check is a (mtime, length) stamp (RunLoop.Reload.cs:37-38). That is not a
# trick played on the engine; it is the state the engine cannot see by itself - and the state it is in
# for the whole of any live session anyway, since the boundary check only runs BETWEEN sessions.
$driftDir = Join-Path $Root "drift"
New-Item -ItemType Directory -Force -Path $driftDir | Out-Null
[IO.File]::WriteAllText((Join-Path $driftDir "TRACKER.md"), $tracker)
Initialize-RigRepo $driftDir
$driftDb = Join-Path $driftDir "run.db"

function Write-DriftPlan {
    param([int]$Version)
    $plan = [ordered]@{
        version     = "1.0"
        planVersion = $Version
        name        = "ks14-drift"
        repo        = $driftDir
        tracker     = "TRACKER.md"
        agent       = [ordered]@{ command = "git"; args = @("-p", "{prompt}") }
        stages      = @(@{ id = "S1"; title = "the only stage"; sessions = 1 })
        gates       = @()
    }
    [IO.File]::WriteAllText((Join-Path $driftDir "trap.plan.json"), ($plan | ConvertTo-Json -Depth 8))
}
$driftPlanPath = Join-Path $driftDir "trap.plan.json"
Write-DriftPlan 1

$outFile = Join-Path $driftDir "stdout.txt"
$errFile = Join-Path $driftDir "stderr.txt"
$prevDb = $env:CONDUCTOR_RUN_DB
$env:CONDUCTOR_RUN_DB = $driftDb
try {
    $proc = Start-Process -FilePath $FreshExe `
        -ArgumentList @("run", "-p", "`"$driftPlanPath`"", "--paused", "--headless", "--no-control-plane") `
        -WorkingDirectory $driftDir -PassThru -NoNewWindow `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile

    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $driftDb) { break }
    }
    Start-Sleep -Seconds 3
    Write-DriftPlan 2                     # the edit the engine picks up at the session boundary
    $v2 = Get-Item $driftPlanPath
    $stampV2 = $v2.LastWriteTimeUtc
    $lengthV2 = $v2.Length
    $reloaded = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if ((Test-Path $outFile) -and ((Get-Content $outFile -Raw) -match "plan reloaded")) { $reloaded = $true; break }
    }
    Write-Host "--- 4. plan drift"
    Write-Host "    engine recorded a reload: $reloaded"

    Write-DriftPlan 3                     # the edit the engine's stamp check cannot see
    $v3 = Get-Item $driftPlanPath
    if ($v3.Length -ne $lengthV2) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        throw "the v3 edit changed the file length ($lengthV2 -> $($v3.Length)); the engine would reload it and there would be no drift to find"
    }
    [IO.File]::SetLastWriteTimeUtc($driftPlanPath, $stampV2)
    Start-Sleep -Seconds 2                # two boundary ticks: if it were going to reload, it would have
    # The console echo, not the structured line: every log line is written twice and counting both
    # would double every reload.
    $reloadLines = @(Get-Content $outFile | Where-Object { $_ -match "^\[\d{2}:\d{2}:\d{2}\] plan reloaded at session boundary" })
    Write-Host "    reloads recorded: $($reloadLines.Count) - last: $($reloadLines[-1])"
    $engineLock = Join-Path $driftDir ".conductor\conductor.lock"
    Write-Host "    engine lock held: $(Test-Path $engineLock)"
    if ($reloadLines.Count -ne 1 -or $reloadLines[-1] -notmatch "v2,") {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        throw "the engine is not executing v2 after $($reloadLines.Count) reload(s); the v3 edit was seen after all and this trap proves nothing"
    }

    # The trap itself: engine live, run unfinished, file ahead of what that run loaded.
    Invoke-Doctor "4. plan drift (engine live)" $driftPlanPath 1 @("v2", "v3", "plan reload")

    # Trap 3: prove the pid is ours from its command line before going near it.
    $cim = Get-CimInstance Win32_Process -Filter ("ProcessId = " + $proc.Id) -ErrorAction SilentlyContinue
    $cmdline = if ($cim) { $cim.CommandLine } else { "" }
    Write-Host "    pid $($proc.Id) command line: $cmdline"
    if ($cmdline -notmatch [regex]::Escape($driftPlanPath)) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        throw "refusing to trust pid $($proc.Id): its command line does not name this rig's plan"
    }
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    $proc.WaitForExit(10000) | Out-Null
    Start-Sleep -Seconds 1

    # And the half the first delivery of this checkpoint got wrong: the killed engine leaves the row
    # saying paused for ever, and NOTHING is scheduling from the stale document. Same store, same
    # stale row, same edited file - green, and the lint says which word it reconciled to.
    Write-Host "    engine lock after the kill: $(Test-Path $engineLock) (a killed engine cannot release it)"
    Invoke-Doctor "4b. the same drift once the engine is gone" $driftPlanPath 0 @("orphaned")
}
finally {
    $env:CONDUCTOR_RUN_DB = $prevDb
}

# ---------------------------------------------------------------- verdict
$catalogueAfter = Get-RealCatalogueCount
Write-Host "real catalogue entries after: $catalogueAfter"
Write-Host ""

$bad = @($results | Where-Object { -not $_.Ok })
if ($catalogueAfter -ne $catalogueBefore) {
    Write-Host "FAIL: this rig changed the REAL catalogue ($catalogueBefore -> $catalogueAfter)"
    exit 1
}
if ($bad.Count -gt 0) {
    foreach ($b in $bad) { Write-Host ("FAIL: " + $b.Label) }
    exit 1
}
Write-Host ("PASS - " + $results.Count + " doctor invocations, seven traps red with the artifact named, the clean plan green, drift green again once its engine is gone, real catalogue untouched")
exit 0
