# KS1.3 - a killed engine's run must never list as running.
#
# The row in `runs.status` is what the last engine to write it believed. An engine that is killed never
# gets to write the correction, so the row says `running` for ever - four such rows on this machine are
# the whole of FU-F1-06, and every reading surface repeated them.
#
# This rig starts an engine on a throwaway plan, kills it the way a crash would, and then asks the
# FRESH BUILD what history says. The answer must be the reconciled word, with the raw column still
# visible beside it.
#
# Discipline: its own CONDUCTOR_STATE_HOME (so the operator's catalogue is untouched), CONDUCTOR_PLAN
# cleared, an explicit -p, and the only process it stops is the one it started - checked against
# Win32_Process.CommandLine first, never by name.
# Windows PowerShell 5.1 compatible, ASCII only.

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP ("ks1-3-kill-" + [guid]::NewGuid().ToString("N").Substring(0, 8))),
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

$rig = Join-Path $Root "rig"
$stateDir = Join-Path $rig ".conductor"
$stateHome = Join-Path $Root "state-home"
New-Item -ItemType Directory -Force -Path $rig, $stateDir, $stateHome | Out-Null

$plan = @{
    name     = "ks1-3-killed"
    repo     = $rig
    tracker  = "TRACKER.md"
    stateDir = $stateDir
    agent    = @{ command = "cmd"; args = @("/c", "echo", "{prompt}") }
    stages   = @(@{ id = "S1"; title = "the only stage"; sessions = 1 })
} | ConvertTo-Json -Depth 6
$planPath = Join-Path $rig "rig.plan.json"
[IO.File]::WriteAllText($planPath, $plan)
[IO.File]::WriteAllText((Join-Path $rig "TRACKER.md"), "# tracker`n`n| ID | Title | Status |`n|---|---|---|`n| S1.1 | a row | TODO |`n")

Write-Host "rig root  : $Root"
Write-Host "engine    : $FreshExe"
Write-Host "state home: $stateHome"
Write-Host ""

$catalogueBefore = Get-RealCatalogueCount

# --paused parks the engine with the run row written and no agent ever spawned - the shortest path to
# "a store with a running row and an engine about to die". Redirect to FILES: an unread pipe fills at
# 4KB and blocks the child for ever.
$outFile = Join-Path $rig "stdout.txt"
$errFile = Join-Path $rig "stderr.txt"
$prevPlan = $env:CONDUCTOR_PLAN
$prevHome = $env:CONDUCTOR_STATE_HOME
$env:CONDUCTOR_PLAN = ""
$env:CONDUCTOR_STATE_HOME = $stateHome
try {
    $p = Start-Process -FilePath $FreshExe -ArgumentList @("run", "-p", "`"$planPath`"", "--paused") `
        -WorkingDirectory $rig -PassThru -NoNewWindow `
        -RedirectStandardOutput $outFile -RedirectStandardError $errFile
}
finally {
    $env:CONDUCTOR_PLAN = $prevPlan
    $env:CONDUCTOR_STATE_HOME = $prevHome
}

$db = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $db = Get-ChildItem -Path $stateHome -Filter run.db -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($db) { break }
}
Start-Sleep -Seconds 4

# Trap 3. Two conductor runs share this machine. Prove this pid is OURS from its command line before
# going anywhere near it, and stop it by id - never by name, never anything we did not launch.
$proc = Get-CimInstance Win32_Process -Filter ("ProcessId = " + $p.Id) -ErrorAction SilentlyContinue
$cmdline = if ($proc) { $proc.CommandLine } else { "" }
Write-Host "pid $($p.Id) command line:"
Write-Host "  $cmdline"
if ($cmdline -notmatch [regex]::Escape($planPath)) {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    throw "refusing to trust pid $($p.Id): its command line does not name this rig's plan"
}
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
$p.WaitForExit(10000) | Out-Null
Start-Sleep -Seconds 1
Write-Host "engine killed (no chance to close the record)"
Write-Host ""

$lock = Join-Path $stateDir "conductor.lock"
Write-Host "engine lock left behind: $(Test-Path $lock)"

$json = & $FreshExe history --json --home $stateHome 2>&1 | Out-String
$payload = $json | ConvertFrom-Json
$runs = @($payload.runs)

Write-Host ""
Write-Host "history --json --home $stateHome"
foreach ($r in $runs) {
    Write-Host ("  runId=$($r.runId)  status=$($r.status)  storedStatus=$($r.storedStatus)  storeLive=$($r.storeLive)")
}
Write-Host ("  unreadable entries: " + @($payload.unreadable).Count)

$catalogueAfter = Get-RealCatalogueCount
Write-Host ""
Write-Host "real catalogue entries: before=$catalogueBefore after=$catalogueAfter"
Write-Host ""

# `--paused` parks the engine, so the row it leaves says `paused` rather than `running`. That is the
# same fact and the sharper case: KS0.2 widened "unfinished" from the literal `running` precisely
# because a park is a run nobody has ended, and a listing that repeated it was claiming the engine was
# still there to un-pause it.
$terminal = @("completed", "aborted", "closed")
$ok = $true
if ($runs.Count -ne 1) { Write-Host "FAIL: expected exactly one run, got $($runs.Count)"; $ok = $false }
else {
    if ($runs[0].status -ne "orphaned") { Write-Host "FAIL: status is '$($runs[0].status)', not the reconciled word"; $ok = $false }
    if (-not $runs[0].storedStatus) { Write-Host "FAIL: the raw column was not preserved"; $ok = $false }
    if ($terminal -contains $runs[0].storedStatus) { Write-Host "FAIL: the engine closed the record after all (storedStatus='$($runs[0].storedStatus)') - nothing was reconciled"; $ok = $false }
    if ($runs[0].storeLive -ne $false) { Write-Host "FAIL: storeLive is not false"; $ok = $false }
    if (-not $runs[0].runId) { Write-Host "FAIL: a run row with a blank runId"; $ok = $false }
}
if ($catalogueAfter -ne $catalogueBefore) { Write-Host "FAIL: this rig changed the REAL catalogue ($catalogueBefore -> $catalogueAfter)"; $ok = $false }

if ($ok) { Write-Host "PASS - the killed engine's run lists as orphaned, the raw column survives beside it, and the real catalogue is untouched"; exit 0 }
Write-Host "--- payload ---"
Write-Host $json
exit 1
