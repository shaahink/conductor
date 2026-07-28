<#
.SYNOPSIS
  Drive the Conductor engine end-to-end with ZERO model spend.

.DESCRIPTION
  Conductor is a long-running orchestrator whose whole job is to spawn AGENT
  sessions. You cannot smoke-test it against a real agent without burning
  tokens. This driver stands in a fake agent (tools/fake-agent.ps1, which
  speaks opencode's stream-json wire format) and drives the ENGINE through a
  full session loop against a hermetic throwaway repo:

     build engine -> scaffold scratch git repo + toy plan + tracker
     -> conductor doctor      (health check)
     -> conductor run --dry-run   (prints the next prompt, spawns nothing)
     -> conductor run --headless --max-sessions N   (the real loop, fake agent)
     -> conductor status   (read the result back from run.db)

  It asserts the loop reached a terminal state and wrote .conductor/REPORT.md,
  then prints a PASS/FAIL summary. Nothing touches the real conductor plans in
  plans/, and no network / model calls are made.

  ASCII only (Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI and
  a stray non-ASCII byte tears a string literal). fake-agent.ps1 has the same
  rule -- keep it that way.

.PARAMETER Exe
  Path to conductor.exe. Default: the repo's fresh Debug build
  (src/Conductor/bin/Debug/net10.0/conductor.exe). Built automatically if
  missing unless -NoBuild is passed.

.PARAMETER Sessions
  Max agent sessions the loop runs (default 2).

.PARAMETER Mode
  fake-agent scenario: success | no-commits | true-red | stall | limit
  (default success). See tools/fake-agent.ps1.

.PARAMETER Keep
  Keep the scratch repo (prints its path) instead of deleting it.

.EXAMPLE
  pwsh -File .claude/skills/run-conductor/driver.ps1
.EXAMPLE
  pwsh -File .claude/skills/run-conductor/driver.ps1 -Mode true-red -Keep
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$Sessions = 2,
    [ValidateSet("success", "no-commits", "true-red", "stall", "limit")]
    [string]$Mode = "success",
    [switch]$Keep,
    [switch]$NoBuild
)
$ErrorActionPreference = "Stop"

# --- locate the repo root (skill lives at <repo>/.claude/skills/run-conductor) -------------------
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$fakeAgent = Join-Path $repo "tools\fake-agent.ps1"
if (-not (Test-Path $fakeAgent)) { throw "fake-agent not found at $fakeAgent" }

function Section($t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }
$slash = { param($p) $p -replace '\\', '/' }   # JSON/plan paths want forward slashes

# --- 1. build (or reuse) the engine --------------------------------------------------------------
if (-not $Exe) { $Exe = Join-Path $repo "src\Conductor\bin\Debug\net10.0\conductor.exe" }
if (-not (Test-Path $Exe)) {
    if ($NoBuild) { throw "engine exe not found at $Exe and -NoBuild was passed" }
    Section "build engine"
    & dotnet build (Join-Path $repo "src\Conductor\Conductor.csproj") -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "engine build failed (exit $LASTEXITCODE)" }
}
Write-Host ("engine: {0}" -f $Exe)

# --- 2. scaffold a hermetic scratch repo ---------------------------------------------------------
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("conductor-driver-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
Section "scratch repo"
Write-Host $scratch

Push-Location $scratch
try {
    git init -q
    git config user.email "driver@conductor.local"
    git config user.name  "conductor driver"

    # tracker: a Handoff block (no HUMAN: token -> won't park) + a checkpoint table whose
    # TODO rows match fake-agent's flip regex ( | <id> | ... | TODO | | | ).
    $tracker = @"
# Demo Tracker (resume here)

## Handoff  (overwrite this block)
last: (none) -- scratch tracker authored by the run-conductor driver.
stage: **T0 NOT STARTED**.
gate: not run.
next: **T0.1** -- deliver the first toy checkpoint.

## Checkpoints

Status in TODO / IN PROGRESS / DONE / BLOCKED.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| T0.1 | first toy checkpoint | TODO | | |
| T0.2 | second toy checkpoint | TODO | | |
"@
    Set-Content -Path (Join-Path $scratch "demo-START.md") -Value $tracker -Encoding ascii

    # toy plan: agent = fake-agent (no tokens); gate = `git --version` (always green, offline).
    $plan = [ordered]@{
        version      = "1.0"
        name         = "Driver Smoke"
        repo         = (& $slash $scratch)
        tracker      = "demo-START.md"
        pauseOnBlocked = $true
        agent        = [ordered]@{
            command  = "powershell"
            args     = @(
                "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", (& $slash $fakeAgent),
                "-Repo", (& $slash $scratch),
                "-Mode", $Mode,
                "-Prompt", "{prompt}"
            )
            provider = "opencode"
        }
        stages       = @(
            [ordered]@{ id = "T0"; title = "Toy stage"; sessions = 2 }
        )
        gates        = @(
            [ordered]@{ name = "git"; command = "git --version"; tier = "fast" }
        )
        gatePolicy   = "perSession"
        audit        = [ordered]@{ enabled = $false }
        report       = [ordered]@{ commit = $false; push = $false }
    }
    $planPath = Join-Path $scratch "toy.plan.json"
    ($plan | ConvertTo-Json -Depth 8) | Set-Content -Path $planPath -Encoding ascii

    git add -A; git commit -q -m "chore: scratch scaffold for conductor driver"

    # --- 3. doctor (health check, no run) --------------------------------------------------------
    Section "conductor doctor"
    & $Exe doctor -p $planPath
    if ($LASTEXITCODE -ne 0) { Write-Host ("doctor exit {0}" -f $LASTEXITCODE) -ForegroundColor Yellow }

    # --- 4. dry-run (prints next prompt, spawns nothing) -----------------------------------------
    Section "conductor run --dry-run"
    & $Exe run -p $planPath --dry-run 2>&1 | Select-Object -First 20

    # --- 5. the real loop, driven by the fake agent ----------------------------------------------
    Section ("conductor run --headless --max-sessions {0}  (mode={1})" -f $Sessions, $Mode)
    $runLog = & $Exe run -p $planPath --headless --max-sessions $Sessions 2>&1
    $runLog | ForEach-Object { Write-Host $_ }

    # --- 6. read the result back -----------------------------------------------------------------
    Section "conductor status"
    & $Exe status -p $planPath --no-llm 2>&1 | Select-Object -First 25

    # --- 7. assertions ---------------------------------------------------------------------------
    Section "assertions"
    $report = Join-Path $scratch ".conductor\REPORT.md"
    $ok = $true
    function Check($label, $cond) {
        if ($cond) { Write-Host ("  PASS  {0}" -f $label) -ForegroundColor Green }
        else { Write-Host ("  FAIL  {0}" -f $label) -ForegroundColor Red; $script:ok = $false }
    }
    Check "engine ran a session loop (state dir created)" (Test-Path (Join-Path $scratch ".conductor"))
    Check "REPORT.md written"                             (Test-Path $report)
    Check "run state persisted (run.db)"                  (Test-Path (Join-Path $scratch ".conductor\run.db"))
    Check "run produced output"                           ($runLog -and $runLog.Count -gt 0)
    if ($Mode -eq "success") {
        # the fake agent hand-edits the tracker; the engine must independently discard the claim.
        Check "engine discarded the direct tracker edit"  ([bool]($runLog -match "discarded"))
    }

    Write-Host ""
    if ($ok) { Write-Host "DRIVER: PASS" -ForegroundColor Green }
    else { Write-Host "DRIVER: FAIL" -ForegroundColor Red }

    if ($Keep) {
        Write-Host ""
        Write-Host ("scratch kept: {0}" -f $scratch) -ForegroundColor Yellow
        Write-Host ("  plan:   {0}" -f $planPath)
        Write-Host ("  report: {0}" -f $report)
    }
}
finally {
    Pop-Location
    if (-not $Keep) { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
}

if (-not $ok) { exit 1 }
