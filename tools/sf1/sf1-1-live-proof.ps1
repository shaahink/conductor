# SF1.1 live proof - verifier scores come off a real endpoint, not a canned SELECT.
#
# A real run, in a scratch repo, with a real control plane, producing REAL verifier score rows -
# then GET /scores is fetched over real HTTP and asserted against what the run actually decided.
#
# What it proves, in order:
#   1. GET /scores exists on a live run and answers a typed body     (the endpoint is real)
#   2. a failing verdict's findings arrive as a LIST, not one blob   (the DTO does the splitting)
#   3. `passed` matches the run's OWN verdict, per stage             (the bar is the engine's, not 80)
#   4. a 92 that FAILED a stage with a stricter dial says so         (the case a naive client gets wrong)
#   5. the Report tab needs no SQL: /report/query is never called    (asserted from the face source)
#
# Rules this script obeys (they are in the session prompt for a reason):
#   - it never touches C:/code/conductor/.conductor - the rig has its own repo, plan and state dir
#   - it drives src/Conductor/bin/Debug/net10.0/conductor.exe, NOT the conductor on PATH
#   - the OTHER conductor run on this machine holds 4317, so the rig asks for a far-away port AND
#     reads the port it actually got back out of the rig's own discovery file
#   - it shuts the rig down with POST /control abort, not by killing a conductor process
#   - ASCII only (Windows PowerShell 5.1)
#
# Usage: powershell -File tools/sf1/sf1-1-live-proof.ps1

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repoRoot 'src\Conductor\bin\Debug\net10.0\conductor.exe'
if (-not (Test-Path $exe)) { throw "fresh build not found at $exe - run: dotnet build Conductor.slnx" }

$rig = Join-Path $env:TEMP 'sarban-proofs\sf1-1'
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

Write-Host "=== SF1.1 live proof - GET /scores, from a run that really verified something ==="
Write-Host ("engine   : {0}" -f $exe)
Write-Host ("built    : {0}" -f (Get-Item $exe).LastWriteTime)
Write-Host ("rig      : {0}" -f $rig)
Write-Host ("PATH conductor (NOT used): {0}" -f (Get-Command conductor -ErrorAction SilentlyContinue).Source)
Write-Host ""

# ---------------------------------------------------------------- the rig

git -C $rig init -q -b main
git -C $rig config user.email "sf11@conductor.local"
git -C $rig config user.name  "sf11 proof"
Set-Content -Path (Join-Path $rig 'README.md') -Encoding ascii -Value "# sf1.1 scratch repo"
Set-Content -Path (Join-Path $rig '.gitignore') -Encoding ascii -Value @('.conductor/')
git -C $rig add -A
git -C $rig commit -q -m "chore: scratch scaffold" --no-gpg-sign

# The fake agent. The plan validator REQUIRES a {prompt} placeholder in the agent args (measured: a
# plan without one is rejected at load), so it takes one - but it prefers the copy the engine wrote to
# disk, because bug #15 says a long composed prompt on a command line silently drops the child, and a
# verify prompt is the longest one this rig produces.
#
# As a verifier it returns a DIFFERENT verdict each time, because the whole point of the DTO is the
# cases a three-column SELECT could not express:
#   verify 1 -> 72 with two findings   (stage A1, bar 80) -> the run FAILS it
#   verify 2 -> 92 clean               (stage A1, bar 80) -> the run PASSES it
#   verify 3 -> 92 clean               (stage A2, bar 95) -> the run FAILS it, at the same score
$agentScript = Join-Path $rig 'agent.ps1'
Set-Content -Path $agentScript -Encoding ascii -Value @'
param([string]$Repo, [string]$StateDir, [string]$Exe, [string]$Prompt)
$ErrorActionPreference = 'Stop'

# The engine writes session-NNN.prompt.md BEFORE it starts the agent, so the file is the reliable
# copy; the argument is the fallback.
$logs = Join-Path $StateDir 'logs'
$promptFile = Get-ChildItem $logs -Filter 'session-*.prompt.md' -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime | Select-Object -Last 1
$prompt = if ($promptFile) { Get-Content $promptFile.FullName -Raw } else { $Prompt }

function Emit([string]$text) {
    $line = [ordered]@{ type = 'text'; session_id = 'rig'; part = [ordered]@{ text = $text } }
    Write-Output ($line | ConvertTo-Json -Compress -Depth 10)
}

if ($prompt -match 'VERIFICATION session') {
    $counterFile = Join-Path $Repo 'verify-count.txt'
    $n = 0
    if (Test-Path $counterFile) { $n = [int](Get-Content $counterFile -Raw).Trim() }
    $n = $n + 1
    Set-Content -Path $counterFile -Encoding ascii -Value $n

    if ($n -eq 1) {
        $verdict = [ordered]@{
            score    = 72
            verdict  = 'WARN'
            findings = @(
                'gate cache key ignores the tier, so a full-tier pass satisfies a fast-tier check',
                'no test covers the cache miss path'
            )
        }
    } elseif ($n -le 3) {
        $verdict = [ordered]@{ score = 92; verdict = 'PASS'; findings = @() }
    } else {
        $verdict = [ordered]@{ score = 99; verdict = 'PASS'; findings = @() }
    }
    Emit ($verdict | ConvertTo-Json -Compress -Depth 10)
    exit 0
}

# Deliver / fix: do a token of real work and commit it, so the verdict has a diff to judge.
$stamp = (Get-Date).ToUniversalTime().ToString('o')
Add-Content -Path (Join-Path $Repo 'work.txt') -Encoding ascii -Value "$stamp delivered"
git -C $Repo add -A | Out-Null
git -C $Repo commit -q -m "feat: deliver something the verifier can judge" --no-gpg-sign
$sha = (git -C $Repo rev-parse --short HEAD).Trim()

# CLAIM it. Without this the stage never completes and the run re-verifies A1 forever - measured:
# the first attempt at this rig spent all eight sessions on A1 and never entered A2. The claim goes
# through the same one channel a real session uses; CONDUCTOR_PLAN is already in this process's
# environment, so no -p is needed (and passing one would prove less).
$stage = ''
if ($prompt -match 'checkpoint\(s\) of stage\s+([A-Za-z]{1,4}\d+)') { $stage = $Matches[1] }
if ($stage) {
    # The next open row of this stage - A1 has two checkpoints on purpose, so it runs two
    # deliver+verify pairs and the wire ends up carrying a failing AND a passing verdict for the same
    # stage. LAST status per id wins: the engine APPENDS its generated view to the tracker, leaving
    # the seeded table above it frozen at TODO forever. Reading the first match instead re-claimed
    # A1.1 on every session and the run looped on A1 until the cap.
    $statuses = @{}
    foreach ($line in (Get-Content (Join-Path $Repo 'TRACKER.md'))) {
        if ($line -match "^\s*\|\s*($stage\.\w+)\s*\|[^|]*\|\s*([^|]*?)\s*\|") { $statuses[$Matches[1]] = $Matches[2] }
    }
    $next = $statuses.Keys | Where-Object { $statuses[$_] -eq 'TODO' -or $statuses[$_] -eq 'IN PROGRESS' } |
            Sort-Object | Select-Object -First 1
    if ($next) { & $Exe task --done $next -c $sha -e "delivered by the sf1.1 rig agent" 2>&1 | Out-Null }
}
Emit "SESSION-RESULT: delivered one change; gates should be green."
exit 0
'@

$stateDir = Join-Path $rig '.conductor'
Set-Content -Path (Join-Path $rig 'TRACKER.md') -Encoding ascii -Value @"
# scores rig tracker

## Handoff
none.

| # | Checkpoint | Status | Commit | Evidence |
|---|---|---|---|---|
| A1.1 | the first thing | TODO | - | - |
| A1.2 | the second thing | TODO | - | - |
| A2.1 | the strict thing | TODO | - | - |
"@

$planPath = Join-Path $rig 'scores.plan.json'
$agentArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Slash $agentScript),
               '-Repo', (Slash $rig), '-StateDir', (Slash $stateDir), '-Exe', (Slash $exe),
               '-Prompt', '{prompt}')
$plan = [ordered]@{
    name    = 'scores'
    repo    = (Slash $rig)
    tracker = 'TRACKER.md'
    agent   = [ordered]@{ command = 'powershell'; args = $agentArgs; provider = 'opencode' }
    # A2's own dial disagrees with a hardcoded 80: a client deriving pass/fail itself calls the 92
    # below a PASS, and this run calls it a FAIL. Only the engine knows.
    stages  = @(
        # Two checkpoints, so A1 gets TWO verify sessions: one that fails its bar and one that clears
        # it. A stage with a single checkpoint is verified once and the wire never carries the pair.
        [ordered]@{ id = 'A1'; title = 'The default-bar stage'; sessions = 2 },
        # ownerGate keeps the run ALIVE once A2 is green instead of exiting: the control plane dies
        # with the process, and the first version of this rig raced its own run to the finish and
        # queried a port nothing was listening on. A parked run waits forever, which is exactly what
        # a proof needs - and POST /control abort below is what ends it.
        [ordered]@{ id = 'A2'; title = 'The strict stage'; sessions = 1; ownerGate = $true; qa = [ordered]@{ mode = 'everySession'; verifierThreshold = 95 } }
    )
    gates   = @([ordered]@{ name = 'smoke'; command = 'git --version'; tier = 'fast'; timeoutMinutes = 2 })
    limits  = [ordered]@{ maxSessions = 14; maxRunCostUsd = 1.0; sessionTimeoutMinutes = 5 }
    report  = [ordered]@{ commit = $false; push = $false }
    batteryCollapse = $false
    verifyEachDelivery = $true
}
($plan | ConvertTo-Json -Depth 20) | Set-Content -Path $planPath -Encoding ascii

# ---------------------------------------------------------------- run it, with a control plane

# 4317 belongs to the OTHER conductor run on this machine. Ask for something far away, then believe
# only the discovery file - the engine scans forward when a port is taken.
$outFile = Join-Path $rig 'run.out.log'
$proc = Start-Process -FilePath $exe `
    -ArgumentList @('run', '-p', $planPath, '--headless', '--no-face', '--port', '4911') `
    -RedirectStandardOutput $outFile -RedirectStandardError (Join-Path $rig 'run.err.log') `
    -PassThru -NoNewWindow
Write-Host ("run started: pid {0} (this script's own child)" -f $proc.Id)

$discovery = Join-Path $stateDir 'control-plane.json'
$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path $discovery) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
if (-not (Test-Path $discovery)) {
    Write-Host (Get-Content $outFile -Raw)
    throw "the rig's control plane never published a discovery file"
}
$cp = Get-Content $discovery -Raw | ConvertFrom-Json
$base = $cp.baseUrl
Write-Host ("control plane: {0} (asked for 4911, got {1})" -f $base, $cp.port)

function Get-Scores {
    try {
        return Invoke-RestMethod -Uri ($base + '/scores') -Method Get -TimeoutSec 10
    } catch {
        return $null
    }
}

# Wait until the run has verified BOTH stages. Poll the endpoint itself - if it cannot answer, this
# proof has already failed.
$scores = $null
$deadline = (Get-Date).AddSeconds(300)
while ((Get-Date) -lt $deadline) {
    $scores = Get-Scores
    if ($scores -and (@($scores.scores) | Where-Object { $_.stageId -eq 'A2' })) { break }
    if ($proc.HasExited) { break }
    Start-Sleep -Milliseconds 500
}

$rawBody = ''
try { $rawBody = (Invoke-WebRequest -Uri ($base + '/scores') -Method Get -TimeoutSec 10 -UseBasicParsing).Content } catch { }
if ($rawBody) {
    Set-Content -Path (Join-Path $rig 'scores-response.json') -Encoding ascii -Value $rawBody
    Write-Host ""
    Write-Host "--- raw GET /scores body ---" -ForegroundColor Cyan
    Write-Host $rawBody
    Write-Host ""
}

# ---------------------------------------------------------------- the assertions

$rows = @()
if ($scores) { $rows = @($scores.scores) }

Check "(1) GET /scores answered a typed body on a live run" ($rows.Count -ge 3) `
    ("rows: {0}" -f $rows.Count)

if ($rows.Count -ge 3) {
    # Newest session first, matching /sessions.
    $numbers = $rows | ForEach-Object { $_.sessionNumber }
    $sorted = @($numbers | Sort-Object -Descending)
    Check "(1) ...newest session first" `
        ((($numbers -join ',') -eq ($sorted -join ','))) ("sessionNumber order: " + ($numbers -join ','))

    $failing = $rows | Where-Object { $_.score -eq 72 } | Select-Object -First 1
    Check "(2) the failing verdict's findings arrive as a LIST of two, not one blob" `
        ($failing -and @($failing.findings).Count -eq 2) `
        $(if ($failing) { "findings: " + (@($failing.findings) -join ' || ') } else { 'no score-72 row' })
    $joined = if ($failing) { @($failing.findings) -join '' } else { "`n" }
    Check "(2) ...each finding is its own string, with no embedded newline" `
        (-not $joined.Contains("`n")) ''

    Check "(3) the failing verdict says passed=false against the default bar of 80" `
        ($failing -and $failing.passed -eq $false -and $failing.threshold -eq 80) `
        $(if ($failing) { "score {0} / threshold {1} / passed {2}" -f $failing.score, $failing.threshold, $failing.passed } else { '' })

    $a1pass = $rows | Where-Object { $_.stageId -eq 'A1' -and $_.score -eq 92 } | Select-Object -First 1
    Check "(3) ...and the 92 on the SAME stage says passed=true" `
        ($a1pass -and $a1pass.passed -eq $true -and $a1pass.threshold -eq 80) `
        $(if ($a1pass) { "score {0} / threshold {1} / passed {2}" -f $a1pass.score, $a1pass.threshold, $a1pass.passed } else { 'no A1 92 row' })

    # THE ONE A NAIVE CLIENT GETS WRONG: same score, different stage, opposite answer.
    $a2 = $rows | Where-Object { $_.stageId -eq 'A2' } | Select-Object -First 1
    Check "(4) the SAME score of 92 on the stricter stage says passed=false at its own bar of 95" `
        ($a2 -and $a2.score -eq 92 -and $a2.threshold -eq 95 -and $a2.passed -eq $false) `
        $(if ($a2) { "stage {0}: score {1} / threshold {2} / passed {3}" -f $a2.stageId, $a2.score, $a2.threshold, $a2.passed } else { 'no A2 row' })

    # And the engine's own log has to agree - a DTO that disagreed with the run would be a new lie.
    $runLog = Get-Content $outFile -Raw
    Check "(4) ...and the run's own log says exactly that" `
        ($runLog -match 'verifier failed \(92/95\)') `
        $(($runLog -split "`n" | Select-String 'verifier (passed|failed)' | ForEach-Object { $_.ToString().Trim() }) -join ' | ')
}

# ---------------------------------------------------------------- shut the rig down

# POST /control abort, not Stop-Process: this machine runs more than one conductor and killing one by
# pid is how you take out somebody else's work. Stop-Process stays as a last resort, on the pid THIS
# script started, after checking its command line really points at the rig.
try {
    $headers = @{ 'X-Conductor-Token' = $cp.token }
    Invoke-RestMethod -Uri ($base + '/control') -Method Post -Headers $headers `
        -ContentType 'application/json' -Body '{"command":"abort"}' -TimeoutSec 10 | Out-Null
    Write-Host "sent POST /control abort to the rig"
} catch {
    Write-Host ("abort post failed: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
}
if (-not $proc.WaitForExit(60000)) {
    $cmdline = (Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f $proc.Id) -ErrorAction SilentlyContinue).CommandLine
    if ($cmdline -and $cmdline.Contains('sarban-proofs')) {
        Write-Host ("rig did not exit; stopping pid {0} - command line confirms the rig: {1}" -f $proc.Id, $cmdline) -ForegroundColor Yellow
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    } else {
        Write-Host ("rig pid {0} did not exit and its command line is NOT the rig - leaving it alone" -f $proc.Id) -ForegroundColor Red
    }
}

# ---------------------------------------------------------------- the face no longer needs SQL

# Assert on CODE, not prose: the first cut of this check grepped the whole file for "SELECT" and went
# red on a comment explaining that the SELECT is gone. Comment lines are stripped first.
$reportCode = ((Get-Content (Join-Path $repoRoot 'face-go\internal\tui\tab_report.go')) |
               Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
$connSrc = Get-Content (Join-Path $repoRoot 'face-go\internal\tui\conn.go') -Raw
Check "(5) the Report tab's CODE holds no SQL at all" `
    (-not ($reportCode -match 'SELECT') -and -not ($reportCode -match 'scoresSQL') `
     -and -not ($reportCode -match 'QueryReport')) ''
Check "(5) ...and its scores fetch calls FetchScores, not QueryReport" `
    (($connSrc -match 'cmdFetchScores[\s\S]{0,400}source\.FetchScores\(\)')) ''

# ---------------------------------------------------------------- verdict

Write-Host ""
Write-Host ("=== SF1.1 live proof: {0} passed, {1} failed ===" -f $pass, $fail)
Write-Host ("rig kept for inspection: {0}" -f $rig)
if ($fail -gt 0) { exit 1 }
exit 0
