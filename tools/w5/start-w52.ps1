<#
.SYNOPSIS
  W5.2 -- start the real-model unattended proof run. This one costs money.

.DESCRIPTION
  Everything W5.2 needs before `conductor run` is safe to leave alone, in one command, so that
  none of it is a step anyone has to remember:

    1  refuse to start on a dirty working tree (uncommitted work would be swept into the agent's
       commits and attributed to the run)
    2  put the run on its OWN branch, never on `feat/foreman` -- a real model committing unattended
       to the branch that is about to merge into master is the one outcome with no cheap undo
    3  give that branch an upstream, because the session prompt instructs the agent to push and a
       push with no upstream fails -- which reads to the agent as a problem to solve and burns
       sessions on it
    4  build the engine if needed, then run `conductor doctor` and STOP on any failure
    5  show what the run is allowed to spend and which rails are armed, and ask once

  Then it starts the run and gets out of the way.

  Why a branch and not a worktree: every other plan in `plans/` points `repo` at the main checkout,
  and `PlanConfig.Load` validates that the path exists. A worktree would need the plan edited before
  the run and reverted after, which is one more thing to get wrong at 2am.

  ASCII ONLY (Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI and one stray byte tears
  the next string literal).

.PARAMETER Branch
  Branch to run on. Default `w52-proof`. Created from the current HEAD if it does not exist.

.PARAMETER NoRemote
  Do not create/track an upstream. The agent's `git push` will then fail harmlessly, but expect it
  to spend some tokens working out why.

.PARAMETER Headless
  Run without the Face, appending to `.conductor/w52-run.log`. Use this if you want to close the
  terminal. (Closing it is now survivable either way -- W3.3 parks the run cleanly -- but a parked
  run is not a finished one, and W5.2 wants a finished one.)

.PARAMETER Yes
  Skip the confirmation prompt.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/w5/start-w52.ps1
#>
[CmdletBinding()]
param(
    [string]$Branch = "w52-proof",
    [switch]$NoRemote,
    [switch]$Headless,
    [switch]$Yes
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$planPath = Join-Path $repoRoot "plans\conductor-w52.plan.json"
$exe = Join-Path $repoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"

function Say($t, $c = "Gray") { Write-Host $t -ForegroundColor $c }
function Step($t) { Write-Host ""; Write-Host ("=== " + $t + " ===") -ForegroundColor Cyan }
function Die($t) { Write-Host ""; Write-Host ("STOP: " + $t) -ForegroundColor Red; exit 1 }

if (-not (Test-Path $planPath)) { Die "plan not found: $planPath" }

# --- 1. clean tree ------------------------------------------------------------------------------
Step "working tree"
$dirty = git -C $repoRoot status --porcelain
if ($dirty) {
    Say "uncommitted changes:" Yellow
    $dirty | ForEach-Object { Say ("  " + $_) DarkGray }
    Die "commit or stash first. An unattended run commits whatever it finds in the tree, and you will not be able to tell its work from yours afterwards."
}
Say "clean" Green

# --- 2. the run's own branch --------------------------------------------------------------------
Step "branch"
$current = (git -C $repoRoot rev-parse --abbrev-ref HEAD).Trim()
Say ("currently on " + $current)
$exists = (git -C $repoRoot branch --list $Branch)
if ($exists) {
    git -C $repoRoot checkout $Branch --quiet
    Say ("switched to existing " + $Branch) Green
} else {
    git -C $repoRoot checkout -b $Branch --quiet
    Say ("created " + $Branch + " from " + $current) Green
}

if (-not $NoRemote) {
    $hasUpstream = git -C $repoRoot rev-parse --abbrev-ref --symbolic-full-name "@{u}" 2>$null
    if (-not $hasUpstream) {
        Say ("publishing " + $Branch + " so the agent's 'git push' has somewhere to go...")
        git -C $repoRoot push -u origin $Branch --quiet
        if ($LASTEXITCODE -ne 0) { Die "could not publish $Branch. Re-run with -NoRemote to proceed without an upstream." }
    }
    Say ("upstream: " + (git -C $repoRoot rev-parse --abbrev-ref --symbolic-full-name "@{u}")) Green
} else {
    Say "no upstream (-NoRemote): the agent's pushes will fail" Yellow
}

# --- 3. engine ----------------------------------------------------------------------------------
Step "engine"
if (-not (Test-Path $exe)) {
    Say "building..."
    & dotnet build (Join-Path $repoRoot "src\Conductor\Conductor.csproj") -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { Die "engine build failed" }
}
Say $exe Green

# --- 4. doctor ----------------------------------------------------------------------------------
Step "conductor doctor"
$doctor = & $exe doctor -p $planPath 2>&1
$doctorText = ($doctor | Out-String)
$doctor | ForEach-Object { Write-Host $_ }
if ($doctorText -match "(?m)(\d+)\s+fail" -and [int]$matches[1] -gt 0) {
    Die "doctor reported failures. Fix them before spending money -- an unattended run cannot."
}
# The auth preflight is the one worth reading twice: a dead credential is the single most common way
# an overnight run turns into an empty log (W3.2 exists because of exactly that).
if ($doctorText -match "auth") { Say "auth checked above -- if it did not pass, run 'claude setup-token' first" Yellow }

# --- 5. what it may spend -----------------------------------------------------------------------
Step "what you are authorising"
$plan = Get-Content $planPath -Raw | ConvertFrom-Json
Say ("  plan        " + $plan.name)
Say ("  branch      " + $Branch + "   (NOT feat/foreman)")
Say ("  model       " + $plan.agent.model + "    advisor: fable-5")
Say ("  budget cap  `$" + $plan.limits.maxRunCostUsd + "   -- the run PARKS at this, it does not overrun")
Say ("  session cap " + $plan.limits.maxSessions + " sessions")
Say ("  rails       session timeout " + $plan.limits.sessionTimeoutMinutes + "m - stall " + $plan.limits.stallMinutes + "m (+" + $plan.limits.stallGraceMinutes + "m grace) - auth preflight on")
Say ("  gates       every session: build - face-build - test - face-test - ratchet")
Say ("  work        4 checkpoints across 3 stages (four open followup rows)")
Write-Host ""
Say "This is the proof run: do NOT intervene once it starts. Rescuing it mid-run means it did not" Yellow
Say "pass -- stop it, fix what bled, and start again. That is the brief's 're-run until clean'." Yellow

if (-not $Yes) {
    Write-Host ""
    $answer = Read-Host "Start the run and spend real money? (type 'yes')"
    if ($answer -ne "yes") { Say "not started." ; exit 0 }
}

# --- 6. go --------------------------------------------------------------------------------------
Step "conductor run"
Set-Location $repoRoot
if ($Headless) {
    $log = Join-Path $repoRoot ".conductor\w52-run.log"
    Say ("headless -- tail " + $log)
    & $exe run -p $planPath --headless --no-face 2>&1 | Tee-Object -FilePath $log -Append
} else {
    Say "leave this window open until the run finishes (closing it parks the run cleanly, but parked is not finished)"
    & $exe run -p $planPath
}
$code = $LASTEXITCODE
Step "run ended"
Say ("exit " + $code)
Say "Next: hand the run to a session for the audit -- conductor report -p plans/conductor-w52.plan.json --query ... writes docs/workgraph/W5-AUDIT.md"
exit $code
