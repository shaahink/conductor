# SF1.2 live proof - the SQL console is GONE, and nothing that mattered went with it.
#
# A real run, in a scratch repo, with a real control plane. Then the four claims are made against
# that live run rather than against the source that produced it.
#
# What it proves, in order:
#   1. GET /report/query is 404 on a live control plane      (the endpoint is deleted, not disabled)
#   2. GET /scores still answers typed rows                  (SF1.1 survives the deletion)
#   3. `conductor report --query` is rejected by the FRESH   (the CLI half is gone too)
#      build, and plain `conductor report` still writes one
#   4. MCP run_query still returns rows from the same run.db (ad-hoc SQL lives where chat needs it)
#   5. no TabDev / QueryReport / report-query trace survives (asserted from face + engine source)
#
# Rules this script obeys (they are in the session prompt for a reason):
#   - it never touches C:/code/conductor/.conductor - the rig has its own repo, plan and state dir
#   - it drives src/Conductor/bin/Debug/net10.0/conductor.exe, NOT the conductor on PATH
#   - the OTHER conductor run on this machine holds 4317, so the rig asks for a far-away port AND
#     reads the port it actually got back out of the rig's own discovery file
#   - it shuts the rig down with POST /control abort, not by killing a conductor process
#   - ASCII only (Windows PowerShell 5.1)
#
# Usage: powershell -File tools/sf1/sf1-2-live-proof.ps1

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repoRoot 'src\Conductor\bin\Debug\net10.0\conductor.exe'
if (-not (Test-Path $exe)) { throw "fresh build not found at $exe - run: dotnet build Conductor.slnx" }

$rig = Join-Path $env:TEMP 'sarban-proofs\sf1-2'
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

Write-Host "=== SF1.2 live proof - the SQL console is gone from a RUNNING conductor ==="
Write-Host ("engine   : {0}" -f $exe)
Write-Host ("built    : {0}" -f (Get-Item $exe).LastWriteTime)
Write-Host ("rig      : {0}" -f $rig)
Write-Host ("PATH conductor (NOT used): {0}" -f (Get-Command conductor -ErrorAction SilentlyContinue).Source)
Write-Host ""

# ---------------------------------------------------------------- the rig

git -C $rig init -q -b main
git -C $rig config user.email "sf12@conductor.local"
git -C $rig config user.name  "sf12 proof"
Set-Content -Path (Join-Path $rig 'README.md') -Encoding ascii -Value "# sf1.2 scratch repo"
Set-Content -Path (Join-Path $rig '.gitignore') -Encoding ascii -Value @('.conductor/')
git -C $rig add -A
git -C $rig commit -q -m "chore: scratch scaffold" --no-gpg-sign

# The fake agent, carried over from the SF1.1 rig with its two hard-won lessons intact:
#   - read the prompt from the file the engine wrote, not the command line (bug #15: a long composed
#     prompt silently drops a cmd.exe child and the run still reports success)
#   - claim through `conductor task --done`, reading the LAST status per id out of the tracker (the
#     engine APPENDS its generated view below the seeded table, so a first-match scan reads the
#     frozen seed forever and the stage never completes)
$agentScript = Join-Path $rig 'agent.ps1'
Set-Content -Path $agentScript -Encoding ascii -Value @'
param([string]$Repo, [string]$StateDir, [string]$Exe, [string]$Prompt)
$ErrorActionPreference = 'Stop'

$logs = Join-Path $StateDir 'logs'
$promptFile = Get-ChildItem $logs -Filter 'session-*.prompt.md' -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime | Select-Object -Last 1
$prompt = if ($promptFile) { Get-Content $promptFile.FullName -Raw } else { $Prompt }

function Emit([string]$text) {
    $line = [ordered]@{ type = 'text'; session_id = 'rig'; part = [ordered]@{ text = $text } }
    Write-Output ($line | ConvertTo-Json -Compress -Depth 10)
}

if ($prompt -match 'VERIFICATION session') {
    # One clean pass: this rig is about the endpoint, not about the verdict engine. SF1.1's proof
    # already pinned the per-stage bar; all this needs is a real row on /scores.
    Emit (([ordered]@{ score = 91; verdict = 'PASS'; findings = @() }) | ConvertTo-Json -Compress -Depth 10)
    exit 0
}

$stamp = (Get-Date).ToUniversalTime().ToString('o')
Add-Content -Path (Join-Path $Repo 'work.txt') -Encoding ascii -Value "$stamp delivered"
git -C $Repo add -A | Out-Null
git -C $Repo commit -q -m "feat: deliver something the verifier can judge" --no-gpg-sign
$sha = (git -C $Repo rev-parse --short HEAD).Trim()

$stage = ''
if ($prompt -match 'checkpoint\(s\) of stage\s+([A-Za-z]{1,4}\d+)') { $stage = $Matches[1] }
if ($stage) {
    $statuses = @{}
    foreach ($line in (Get-Content (Join-Path $Repo 'TRACKER.md'))) {
        if ($line -match "^\s*\|\s*($stage\.\w+)\s*\|[^|]*\|\s*([^|]*?)\s*\|") { $statuses[$Matches[1]] = $Matches[2] }
    }
    $next = $statuses.Keys | Where-Object { $statuses[$_] -eq 'TODO' -or $statuses[$_] -eq 'IN PROGRESS' } |
            Sort-Object | Select-Object -First 1
    if ($next) { & $Exe task --done $next -c $sha -e "delivered by the sf1.2 rig agent" 2>&1 | Out-Null }
}
Emit "SESSION-RESULT: delivered one change; gates should be green."
exit 0
'@

$stateDir = Join-Path $rig '.conductor'
Set-Content -Path (Join-Path $rig 'TRACKER.md') -Encoding ascii -Value @"
# sql-console rig tracker

## Handoff
none.

| # | Checkpoint | Status | Commit | Evidence |
|---|---|---|---|---|
| A1.1 | the only thing | TODO | - | - |
"@

$planPath = Join-Path $rig 'nosql.plan.json'
$agentArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Slash $agentScript),
               '-Repo', (Slash $rig), '-StateDir', (Slash $stateDir), '-Exe', (Slash $exe),
               '-Prompt', '{prompt}')
$plan = [ordered]@{
    name    = 'nosql'
    repo    = (Slash $rig)
    tracker = 'TRACKER.md'
    agent   = [ordered]@{ command = 'powershell'; args = $agentArgs; provider = 'opencode' }
    # ownerGate keeps the run ALIVE once A1 is green instead of exiting: the control plane dies with
    # the process, and a proof that queries a dead port proves nothing. POST /control abort ends it.
    stages  = @(
        [ordered]@{ id = 'A1'; title = 'The only stage'; sessions = 1; ownerGate = $true }
    )
    gates   = @([ordered]@{ name = 'smoke'; command = 'git --version'; tier = 'fast'; timeoutMinutes = 2 })
    limits  = [ordered]@{ maxSessions = 8; maxRunCostUsd = 1.0; sessionTimeoutMinutes = 5 }
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
    -ArgumentList @('run', '-p', $planPath, '--headless', '--no-face', '--port', '4921') `
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
Write-Host ("control plane: {0} (asked for 4921, got {1})" -f $base, $cp.port)

# Wait until the run has actually verified something, so /scores has a row to serve.
$scores = $null
$deadline = (Get-Date).AddSeconds(300)
while ((Get-Date) -lt $deadline) {
    try { $scores = Invoke-RestMethod -Uri ($base + '/scores') -Method Get -TimeoutSec 10 } catch { $scores = $null }
    if ($scores -and @($scores.scores).Count -ge 1) { break }
    if ($proc.HasExited) { break }
    Start-Sleep -Milliseconds 500
}

# ---------------------------------------------------------------- (1) the endpoint is 404

# The EXACT query the deleted endpoint used to answer with a row. Invoke-WebRequest throws on 404, so
# the status code is read off the exception's response rather than guessed from the message text.
$sql = 'SELECT run_id, plan_name FROM runs'
$queryStatus = 0
$queryBody = ''
try {
    $r = Invoke-WebRequest -Uri ($base + '/report/query?sql=' + [uri]::EscapeDataString($sql)) `
        -Method Get -TimeoutSec 10 -UseBasicParsing
    $queryStatus = [int]$r.StatusCode
    $queryBody = $r.Content
} catch {
    if ($_.Exception.Response) { $queryStatus = [int]$_.Exception.Response.StatusCode }
}
Write-Host ""
Write-Host ("--- GET /report/query?sql={0} ---> HTTP {1} ---" -f $sql, $queryStatus) -ForegroundColor Cyan
Check "(1) GET /report/query is 404 on a live control plane" ($queryStatus -eq 404) `
    ("status {0}; body: {1}" -f $queryStatus, $queryBody)

# A write attempt gets the same 404 - not a 400 from a SELECT-only guard that is still running.
$writeStatus = 0
try {
    # The hostile payload, not a write: what is asserted below is that the control plane REFUSES it,
    # because the endpoint that would once have executed it is gone. runs-write-scan:allow
    $r = Invoke-WebRequest -Uri ($base + '/report/query?sql=' + [uri]::EscapeDataString('DELETE FROM runs')) `
        -Method Get -TimeoutSec 10 -UseBasicParsing
    $writeStatus = [int]$r.StatusCode
} catch {
    if ($_.Exception.Response) { $writeStatus = [int]$_.Exception.Response.StatusCode }
}
Check "(1) ...and a non-SELECT gets 404 too, not a 400 from a guard still standing" `
    ($writeStatus -eq 404) ("status {0}" -f $writeStatus)

# ---------------------------------------------------------------- (2) /scores survives

$rows = @()
if ($scores) { $rows = @($scores.scores) }
$rawScores = ''
try { $rawScores = (Invoke-WebRequest -Uri ($base + '/scores') -Method Get -TimeoutSec 10 -UseBasicParsing).Content } catch { }
Write-Host ""
Write-Host "--- raw GET /scores body ---" -ForegroundColor Cyan
Write-Host $rawScores
Write-Host ""
Check "(2) GET /scores still answers typed rows after the SQL endpoint's deletion" `
    ($rows.Count -ge 1) ("rows: {0}" -f $rows.Count)
if ($rows.Count -ge 1) {
    $s0 = $rows[0]
    Check "(2) ...carrying the engine's own threshold and passed verdict" `
        ($s0.threshold -gt 0 -and $null -ne $s0.passed) `
        ("session #{0}: score {1} / threshold {2} / passed {3}" -f $s0.sessionNumber, $s0.score, $s0.threshold, $s0.passed)
}

# ---------------------------------------------------------------- (3) the CLI half is gone

# Through the FRESH build, not the conductor on PATH: testing a deleted option through the published
# engine would only prove the published engine still HAS it.
#
# SF1.2 measured this as INERT rather than REJECTED, and said why: bug #17 - Program.cs never called
# Spectre's UseStrictParsing, so every verb silently accepted and ignored every unknown option, and
# the deleted flag looked like it still worked. K7.2 landed that one line, so the assertion this rig
# always wanted (see the header: "rejected by the FRESH build") is now the one it can make. The bar
# goes UP here, not down: exit non-zero and the bad option named, instead of exit 0 doing nothing.
$queryOut = (& $exe report -p $planPath --query $sql 2>&1 | Out-String)
$queryExit = $LASTEXITCODE
Write-Host ("--- fresh build: conductor report --query ---> exit {0} ---" -f $queryExit) -ForegroundColor Cyan
Write-Host $queryOut
Check "(3) the fresh build REJECTS report --query - deleted, and no longer silently swallowed" `
    (($queryExit -ne 0) -and ($queryOut -match 'query') -and `
     ($queryOut -notmatch 'Query result') -and ($queryOut -notmatch 'plan_name') -and `
     ($queryOut -notmatch 'report written to')) `
    ("exit {0} - expected a non-zero exit naming the unknown option" -f $queryExit)

# And the option is gone from the code, not merely unreachable: a Settings property left behind would
# come back the moment someone re-added a branch for it.
$reportCmdCode = ((Get-Content (Join-Path $repoRoot 'src\Conductor\Commands\ReportCommand.cs')) |
                  Where-Object { $_ -notmatch '^\s*(//|///)' }) -join "`n"
Check "(3) ...and ReportCommand has no Query setting and no RunQuery left to call" `
    (($reportCmdCode -notmatch 'Query') -and ($reportCmdCode -notmatch 'CommandOption')) ''

# ...and the verb it belonged to still does its actual job.
$reportOut = (& $exe report -p $planPath 2>&1 | Out-String)
$reportExit = $LASTEXITCODE
$reportPath = Join-Path $stateDir 'REPORT.md'
Check "(3) ...and plain `conductor report` still writes a report" `
    ($reportExit -eq 0 -and (Test-Path $reportPath)) `
    ("exit {0}; {1}" -f $reportExit, $reportOut.Trim())

# ---------------------------------------------------------------- (4) MCP run_query survives

. (Join-Path $repoRoot 'tools\lib\run-query.ps1')
$mcpRows = Invoke-ConductorQuery -Exe $exe -StateDir $stateDir -Sql "SELECT number, stage_id, kind FROM sessions ORDER BY number"
Write-Host ""
Write-Host "--- MCP run_query: SELECT number, stage_id, kind FROM sessions ---" -ForegroundColor Cyan
Write-Host $mcpRows
Check "(4) MCP run_query still reads the same run.db the deleted endpoint used to" `
    ($mcpRows -notmatch 'query failed' -and $mcpRows -notmatch 'no rows' -and $mcpRows -match 'stage_id') ''
$mcpWrite = Invoke-ConductorQuery -Exe $exe -StateDir $stateDir -Sql "DELETE FROM sessions"
Check "(4) ...and it is still SELECT-only" ($mcpWrite -match '(?i)only SELECT') $mcpWrite

# ---------------------------------------------------------------- shut the rig down

# POST /control abort, not Stop-Process: this machine runs more than one conductor and killing one by
# pid is how you take out somebody else's work. A one-stage plan can finish and exit on its own while
# the checks above are still running - the control plane dies with the process, so a failed abort
# there is expected, not a fault. Every check that needs a live plane (1 and 2) runs before this.
if ($proc.HasExited) { Write-Host "the rig run already finished on its own - nothing to abort" }
else {
try {
    $headers = @{ 'X-Conductor-Token' = $cp.token }
    Invoke-RestMethod -Uri ($base + '/control') -Method Post -Headers $headers `
        -ContentType 'application/json' -Body '{"command":"abort"}' -TimeoutSec 10 | Out-Null
    Write-Host "sent POST /control abort to the rig"
} catch {
    Write-Host ("abort post failed: {0}" -f $_.Exception.Message) -ForegroundColor Yellow
}
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

# ---------------------------------------------------------------- (5) no trace in the source

# Comment lines are stripped first: the SF1.1 rig went red on a comment EXPLAINING that the SELECT is
# gone, which is the difference between measuring code and grepping prose.
function Code([string]$relPath) {
    $full = Join-Path $repoRoot $relPath
    if (-not (Test-Path $full)) { return '' }
    return ((Get-Content $full) | Where-Object { $_ -notmatch '^\s*(//|#)' }) -join "`n"
}
$faceFiles = Get-ChildItem (Join-Path $repoRoot 'face-go') -Recurse -Filter '*.go' |
             Where-Object { $_.FullName -notmatch '\\testdata\\' }
$hits = @()
foreach ($f in $faceFiles) {
    $code = ((Get-Content $f.FullName) | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
    foreach ($sym in @('TabDev', 'QueryReport', 'QueryResultDto', 'MsgReportResult', 'report/query')) {
        if ($code -match [regex]::Escape($sym)) { $hits += ("{0}: {1}" -f $f.Name, $sym) }
    }
}
Check "(5) no face-go CODE mentions TabDev, QueryReport, QueryResultDto, MsgReportResult or report/query" `
    ($hits.Count -eq 0) (($hits | Select-Object -First 6) -join ' | ')

$engineHits = @()
foreach ($rel in @('src\Conductor\Core\Http\ControlPlaneServer.cs',
                   'src\Conductor\Core\Http\ControlPlaneServer.Endpoints.cs',
                   'src\Conductor\Core\Http\ControlPlaneDto.cs',
                   'src\Conductor\Commands\ReportCommand.cs')) {
    $code = Code $rel
    foreach ($sym in @('report/query', 'WriteQueryAsync', 'QueryResultDto', 'QueryRowDto', '--query')) {
        if ($code -match [regex]::Escape($sym)) { $engineHits += ("{0}: {1}" -f (Split-Path $rel -Leaf), $sym) }
    }
}
Check "(5) ...and no engine CODE routes, serves or parses a SQL query for a report" `
    ($engineHits.Count -eq 0) (($engineHits | Select-Object -First 6) -join ' | ')

# The tab strip is the owner-visible half of the deletion.
$modelSrc = Code 'face-go\internal\tui\model.go'
$tabNamesLine = ($modelSrc -split "`n" | Where-Object { $_ -match 'var tabNames' }) -join ''
$tabKeyLine = ($modelSrc -split "`n" | Where-Object { $_ -match 'var tabKey' }) -join ''
Check "(5) ...the face is down to twelve tabs, with `d` left unbound" `
    (($tabNamesLine -notmatch '"Dev"') -and ($tabKeyLine -notmatch '"d"')) `
    (($tabNamesLine.Trim() + " || " + $tabKeyLine.Trim()))

# ---------------------------------------------------------------- verdict

Write-Host ""
Write-Host ("=== SF1.2 live proof: {0} passed, {1} failed ===" -f $pass, $fail)
Write-Host ("rig kept for inspection: {0}" -f $rig)
if ($fail -gt 0) { exit 1 }
exit 0
