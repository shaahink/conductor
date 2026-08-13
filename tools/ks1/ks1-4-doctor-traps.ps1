# KS1.4 - seven seeded trap plans, one per new doctor lint.
#
# Each lint exists because the failure it catches has already cost a run here. This rig builds one
# plan per lint that carries exactly that failure, asks the FRESH BUILD's doctor about it, and
# demands two things of every answer: exit code 1, and the offending artifact named on stdout. Then
# it asks the same doctor about a plan carrying none of them and demands exit 0.
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

# ---------------------------------------------------------------- 4. plan drift (needs a real run)
# Drift is defined against what a run RECORDED loading, so the fixture needs an engine that actually
# loaded the plan: start one paused, edit the file so it reloads at the session boundary, stop it,
# then edit the file again. The run is left unfinished, which is the only state drift matters in.
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
    $reloaded = $false
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if ((Test-Path $outFile) -and ((Get-Content $outFile -Raw) -match "plan reloaded")) { $reloaded = $true; break }
    }
    Write-Host "--- 4. plan drift"
    Write-Host "    engine recorded a reload: $reloaded"

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

    Write-DriftPlan 3                     # the edit nothing has loaded
    Invoke-Doctor "4. plan drift" $driftPlanPath 1 @("v2", "v3", "plan reload")
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
Write-Host ("PASS - " + $results.Count + " doctor invocations, seven traps red with the artifact named, the clean plan green, real catalogue untouched")
exit 0
