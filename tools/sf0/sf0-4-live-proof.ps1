# SF0.4 live proof - an open bug outlives the RUN that found it.
#
# Two real runs, one scratch repo, one run.db - the exact shape that lost eleven bugs when the Sarban
# core run ended and the face run started. Run A files a bug and completes; run B is a different plan
# in the same repo, so it gets a new run id and, before this fix, an empty ledger.
#
# What it proves, in order:
#   1. run A's epilogue names the bug it is leaving behind        (SF0.4 a: "run ended says how many")
#   2. run A's RUN-SUMMARY.md carries the open-bug ledger         (SF0.4 a: the documented export)
#   3. run B's `bug list` shows run A's row, attributed           (SF0.4 a: a new run sees it)
#   4. run B's session PROMPT ON DISK contains run A's bug        (the ledger exists to reach the agent)
#   5. run B can close run A's row with `bug fix`                 (a row nothing can close is not carried)
#
# Rules this script obeys (they are in the session prompt for a reason):
#   - it never touches C:/code/conductor/.conductor - the rig has its own repo, plan and state dir
#   - it drives src/Conductor/bin/Debug/net10.0/conductor.exe, NOT the conductor on PATH (that one is
#     the published engine driving the session)
#   - --no-control-plane, so it cannot collide with the OTHER conductor run live on this machine
#   - ASCII only (Windows PowerShell 5.1)
#
# Usage: powershell -File tools/sf0/sf0-4-live-proof.ps1

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repoRoot 'src\Conductor\bin\Debug\net10.0\conductor.exe'
if (-not (Test-Path $exe)) { throw "fresh build not found at $exe - run: dotnet build Conductor.slnx" }

$rig = Join-Path $env:TEMP 'sarban-proofs\sf0-4'
if (Test-Path $rig) { Remove-Item $rig -Recurse -Force }
New-Item -ItemType Directory -Force $rig | Out-Null

$pass = 0
$fail = 0
function Check([string]$what, [bool]$ok, [string]$detail) {
    if ($ok) { $script:pass++; Write-Host ("  PASS  " + $what) -ForegroundColor Green }
    else     { $script:fail++; Write-Host ("  FAIL  " + $what) -ForegroundColor Red }
    if ($detail) { Write-Host ("        " + $detail) -ForegroundColor DarkGray }
}
function Slash([string]$p) { return ($p -replace '\\', '/') }

Write-Host "=== SF0.4 live proof - open bugs outlive the run that found them ==="
Write-Host ("engine   : {0}" -f $exe)
Write-Host ("built    : {0}" -f (Get-Item $exe).LastWriteTime)
Write-Host ("rig      : {0}" -f $rig)
Write-Host ("PATH conductor (NOT used): {0}" -f (Get-Command conductor -ErrorAction SilentlyContinue).Source)
Write-Host ""

# ---------------------------------------------------------------- the rig

git -C $rig init -q -b main
git -C $rig config user.email "sf04@conductor.local"
git -C $rig config user.name  "sf04 proof"
Set-Content -Path (Join-Path $rig 'README.md') -Encoding ascii -Value "# sf0.4 scratch repo"
Set-Content -Path (Join-Path $rig '.gitignore') -Encoding ascii -Value @('.conductor/')
git -C $rig add -A
git -C $rig commit -q -m "chore: scratch scaffold" --no-gpg-sign

# The fake agent: marks its checkpoint DONE, commits it, and (run A only) files a bug it is not
# fixing. Token-free. It is handed the tracker to edit and the plan to file against.
$agentScript = Join-Path $rig 'agent.ps1'
Set-Content -Path $agentScript -Encoding ascii -Value @'
param([string]$Repo, [string]$Exe, [string]$Tracker, [string]$Plan, [string]$BugTitle, [string]$Prompt)
$t = Join-Path $Repo $Tracker
(Get-Content $t -Raw) -replace 'TODO', 'DONE' | Set-Content -Path $t -Encoding ascii
git -C $Repo add -A | Out-Null
git -C $Repo commit -q -m "feat: deliver the checkpoint" --no-gpg-sign
if ($BugTitle) { & $Exe bug new -p $Plan $BugTitle --detail "filed by the run that is about to end" --severity high | Out-Null }
exit 0
'@

function New-Rig([string]$name, [string]$tracker, [string]$checkpoint, [string]$bugTitle) {
    Set-Content -Path (Join-Path $rig $tracker) -Encoding ascii -Value @"
# $name tracker

## Handoff
none.

| # | Checkpoint | Status | Commit | Evidence |
|---|---|---|---|---|
| $checkpoint | the one thing | TODO | - | - |
"@
    $planPath = Join-Path $rig "$name.plan.json"
    $agentArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Slash $agentScript),
                   '-Repo', (Slash $rig), '-Exe', (Slash $exe), '-Tracker', $tracker,
                   '-Plan', (Slash $planPath), '-BugTitle', $bugTitle, '-Prompt', '{prompt}')
    $plan = [ordered]@{
        name    = $name
        repo    = (Slash $rig)
        tracker = $tracker
        agent   = [ordered]@{ command = 'powershell'; args = $agentArgs; provider = 'opencode' }
        stages  = @([ordered]@{ id = ($checkpoint -split '\.')[0]; title = 'The only stage'; sessions = 2 })
        gates   = @([ordered]@{ name = 'smoke'; command = 'git --version'; tier = 'fast'; timeoutMinutes = 2 })
        limits  = [ordered]@{ maxSessions = 4; maxRunCostUsd = 1.0; sessionTimeoutMinutes = 5 }
        report  = [ordered]@{ commit = $false; push = $false }
        batteryCollapse = $false
        # One deliver session must be able to finish the plan, or the run parks at the session cap and
        # never reaches CompletePlan - which is where RUN-SUMMARY.md is written. (Incidentally this
        # also drives SF0.1's fix: before it, this key was read by nothing.)
        verifyEachDelivery = $false
    }
    ($plan | ConvertTo-Json -Depth 20) | Set-Content -Path $planPath -Encoding ascii
    return $planPath
}

# A parked run waits for its owner forever and would hang this script (measured: the first attempt
# parked at the session cap and sat there). Run out of process with a hard wall, and stop ONLY the
# pid this function started.
function Invoke-Run([string]$planPath, [string]$tag) {
    $outFile = Join-Path $rig "$tag.out.log"
    $p = Start-Process -FilePath $exe -ArgumentList @('run', '-p', $planPath, '--no-control-plane') `
                       -RedirectStandardOutput $outFile -RedirectStandardError (Join-Path $rig "$tag.err.log") `
                       -PassThru -NoNewWindow
    if (-not $p.WaitForExit(180000)) {
        Write-Host ("run '{0}' did not exit in 180s - stopping pid {1} (this script's own child)" -f $tag, $p.Id) -ForegroundColor Yellow
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    return (Get-Content $outFile -Raw)
}

# ---------------------------------------------------------------- run A: files a bug, then ends

Write-Host "--- run A ('alpha') - a run that ends with an open bug ---" -ForegroundColor Cyan
$planA = New-Rig 'alpha' 'TRACKER-A.md' 'A1.1' 'bg status crashes on an uninspectable pid'
$outA = Invoke-Run $planA 'runA'
Write-Host ($outA.Trim())
Write-Host ""

# (1) the epilogue says how many are open and where to read them.
Check "(1) run A's epilogue names the open bug it is leaving behind" `
    ($outA -match 'open bugs:\s*1 open bug\(s\)') `
    (($outA -split "`n" | Select-String 'open bugs:').ToString().Trim())
Check "(1) ...and says the command that lists them" ($outA -match 'conductor bug list -p') ''

# (2) the documented export.
$summaryA = Join-Path $rig '.conductor\RUN-SUMMARY.md'
$sumA = if (Test-Path $summaryA) { Get-Content $summaryA -Raw } else { '' }
Check "(2) run A's RUN-SUMMARY.md has an open-bug section" ($sumA -match '## Open bugs at run end') $summaryA
Check "(2) ...naming the bug by title" ($sumA -match 'bg status crashes on an uninspectable pid') ''
if ($sumA) { Copy-Item $summaryA (Join-Path $rig 'RUN-SUMMARY-A.md') -Force }

# ---------------------------------------------------------------- run B: a NEW run, same repo

Write-Host ""
Write-Host "--- run B ('beta') - a different plan in the same repo, so a NEW run id ---" -ForegroundColor Cyan
$planB = New-Rig 'beta' 'TRACKER-B.md' 'B1.1' ''
$outB = Invoke-Run $planB 'runB'
Write-Host ($outB.Trim())
Write-Host ""

# Run B filed nothing of its own, so everything it reports open is inherited.
Check "(1b) run B's epilogue counts the row it inherited, and says so" `
    ($outB -match 'open bugs:\s*1 open bug\(s\) \(1 carried from an earlier run in this repo\)') ''

$listB = & $exe bug list -p $planB 2>&1 | Out-String
Write-Host $listB

# (3) a new run sees the previous run's open row, attributed to the plan that filed it.
Check "(3) run B's bug list shows run A's open bug" ($listB -match 'bg status crashes') ''
Check "(3) ...attributed to the plan that filed it" ($listB -match 'alpha') ''
Check "(3) ...and says they are carried forward" ($listB -match 'carried forward from an earlier run') ''

# (4) THE GATE: the row reached the agent, not just the CLI. Asserted against the file on disk.
$promptB = Get-ChildItem (Join-Path $rig '.conductor\logs') -Filter '*.prompt.md' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime | Select-Object -Last 1
$promptText = if ($promptB) { Get-Content $promptB.FullName -Raw } else { '' }
Check "(4) run B's session prompt ON DISK contains run A's bug" `
    ($promptText -match 'bg status crashes on an uninspectable pid') `
    $(if ($promptB) { $promptB.FullName } else { 'no prompt.md found' })
Check "(4) ...marked as carried, so 'do NOT re-file' still reads correctly" `
    ($promptText -match 'carried from an earlier run') ''

# (5) a carried row is closable from the run that fixes it.
$bugId = 0
if ($listB -match '(?m)^\W*(\d+)\W+\w+\W+open\W+\w+\W+.*bg status crashes') { $bugId = [int]$Matches[1] }
if ($bugId -eq 0) {
    # fall back to the only open bug in the rig
    $ids = [regex]::Matches($listB, '\|\s*(\d+)\s*\|') | ForEach-Object { [int]$_.Groups[1].Value }
    if ($ids.Count -gt 0) { $bugId = $ids[0] }
}
Write-Host ("closing carried bug #{0} from run B" -f $bugId)
$fixOut = & $exe bug fix -p $planB $bugId 2>&1 | Out-String
Write-Host $fixOut.Trim()
Check "(5) run B closed a bug run A filed" ($fixOut -match 'fixed') $fixOut.Trim()
$listAfter = & $exe bug list -p $planB 2>&1 | Out-String
Check "(5) ...and it is gone from the open list" ($listAfter -notmatch 'bg status crashes') $listAfter.Trim()

# ---------------------------------------------------------------- verdict

Write-Host ""
Write-Host ("=== SF0.4 live proof: {0} passed, {1} failed ===" -f $pass, $fail)
Write-Host ("rig kept for inspection: {0}" -f $rig)
if ($fail -gt 0) { exit 1 }
exit 0
