# PK2.1 live proof - the courier's heartbeat, alive/dead from `courier status`, and the death record.
#
# What it proves, in order:
#   1. a scratch courier (this tree's conductor-courier.exe) writes lastPollUtc into its presence
#      record and keeps advancing it
#   2. this tree's `conductor courier status` reads it as `alive`
#   3. Stop-Process kills it; the next status reads `dead` with a last-seen time, and the scheduler's
#      last result for the task the dead courier named
#   4. the next start journals `previous courier pid N died silently; last poll T; task last-run
#      result R` in courier.log, with R read from a REAL scheduled task
#   5. negative control: a courier that exits through its own finally (a --once poll) clears its
#      record, and the start after it journals no death
#
# Scratch only: its own state home and courier port under TEMP, a fake token, a Bot API base URL
# nothing listens on, and a scratch scheduled task whose only action is `cmd /c exit 3` - it starts
# no courier, reads no environment and is deleted at the end. The real courier, its home, its task
# and its token are only ever READ (the real task's last run time before and after, to show it was
# not touched). ASCII only (Windows PowerShell 5.1).

param(
    [string]$OutDir   = (Join-Path $env:TEMP "pk21-rig"),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$env:CONDUCTOR_PLAN = $null
Set-Location $RepoRoot

function Section($title) { ""; "==== $title ====" }
function Utc() { (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") }
$results = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    $verdict = "FAIL"
    if ($ok) { $verdict = "PASS" }
    $line = "{0}  {1}  {2}" -f $verdict, $name, $detail
    [void]$results.Add($line)
    $line
}
function FreePort() {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $l.Start()
    $p = $l.LocalEndpoint.Port; $l.Stop(); return $p
}
# Native calls run under "Continue" and are judged by exit code (5.1 turns native stderr into a throw).
function Native($exe, [string[]]$argv) {
    $ErrorActionPreference = "Continue"
    $out = & $exe @argv 2>&1 | ForEach-Object { "$_" }
    $script:nativeExit = $LASTEXITCODE
    return $out
}
function LastRunOf($taskName) {
    $row = Native "schtasks.exe" @("/Query", "/TN", $taskName, "/V", "/FO", "CSV", "/NH") | Select-Object -First 1
    if ($script:nativeExit -ne 0 -or -not $row) { return $null }
    $f = $row -split '","'
    return @{ time = $f[5]; result = $f[6].Trim('"') }
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null
"PK2.1 proof at $(Utc) on commit $(git rev-parse --short HEAD)"

if (-not $NoBuild) {
    $ErrorActionPreference = "Continue"
    $b = & dotnet build Conductor.slnx -clp:ErrorsOnly 2>&1
    if ($LASTEXITCODE -ne 0) { $b; throw "build failed" }
    $ErrorActionPreference = "Stop"
}
$bin = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0"
$engine  = Join-Path $bin "conductor.exe"
$courier = Join-Path $bin "conductor-courier.exe"
"  engine  $engine  $((Get-Item $engine).LastWriteTimeUtc.ToString('u'))"
"  courier $courier  $((Get-Item $courier).LastWriteTimeUtc.ToString('u'))"

$realTask = "Conductor Courier"
$realBefore = LastRunOf $realTask
"  real task (read only) before: last run $($realBefore.time), last result $($realBefore.result)"

# ---- scratch world -------------------------------------------------------------------------
$stateHome = Join-Path $OutDir "state-home"
$courierDir = Join-Path $stateHome "courier"
New-Item -ItemType Directory -Path $courierDir | Out-Null
$deadApi = FreePort
$courierPort = FreePort
$settings = @{
    projects = @(@{ plan = "pk21-scratch"; repo = $OutDir })
    chats = @(@{ chatId = "770000001"; profile = "admin" })
    pollIntervalSeconds = 2
    apiBaseUrl = "http://127.0.0.1:$deadApi"
} | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $courierDir "courier.json") -Value $settings -Encoding ASCII
$presencePath = Join-Path $courierDir "courier.run.json"
$logPath = Join-Path $courierDir "courier.log"
"  scratch state home $stateHome, courier port $courierPort, Bot API http://127.0.0.1:$deadApi (nothing listens)"

function Scratch-Env($psi) {
    $psi.EnvironmentVariables["CONDUCTOR_STATE_HOME"] = $stateHome
    $psi.EnvironmentVariables["CONDUCTOR_COURIER_PORT"] = "$courierPort"
    $psi.EnvironmentVariables["CONDUCTOR_TELEGRAM_TOKEN"] = "111111:pk21-scratch-token"
    $psi.EnvironmentVariables.Remove("CONDUCTOR_PLAN")
}
function Start-Courier([string]$argv) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $courier
    $psi.Arguments = $argv
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    Scratch-Env $psi
    return [System.Diagnostics.Process]::Start($psi)
}
function Status() {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $engine
    $psi.Arguments = "courier status --json --task-name `"$scratchTask`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    Scratch-Env $psi
    $p = [System.Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEnd(); $null = $p.StandardError.ReadToEnd(); $p.WaitForExit()
    return ($out | ConvertFrom-Json)
}
function Read-Presence() {
    if (-not (Test-Path $presencePath)) { return $null }
    try { return (Get-Content $presencePath -Raw | ConvertFrom-Json) } catch { return $null }
}

# A real scheduled task with a real last result, and nothing in it that can reach a courier.
$scratchTask = "pk21-scratch-" + [Guid]::NewGuid().ToString("N").Substring(0, 8)
Section "0. scratch scheduled task $scratchTask (action: cmd /c exit 3)"
$created = Native "schtasks.exe" @("/Create", "/TN", $scratchTask, "/TR", "cmd.exe /c exit 3", "/SC", "ONCE", "/ST", "23:59", "/F")
$created | ForEach-Object { "  $_" }
Check "scratch task registered" ($nativeExit -eq 0) ("exit=" + $nativeExit)
$null = Native "schtasks.exe" @("/Run", "/TN", $scratchTask)
$ran = $null
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    $ran = LastRunOf $scratchTask
    if ($ran -and $ran.result -eq "3") { break }
}
"  scratch task last run $($ran.time), last result $($ran.result)"
Check "scratch task ran and the scheduler recorded exit 3" ($ran -and $ran.result -eq "3") ("result=" + $ran.result)

$proc = $null
try {
    # ---- 1. heartbeat --------------------------------------------------------------------------
    Section "1. a scratch courier writes lastPollUtc and keeps advancing it"
    $proc = Start-Courier "--task-name `"$scratchTask`""
    "  started pid $($proc.Id) at $(Utc)"
    $first = $null
    for ($i = 0; $i -lt 60 -and -not $first; $i++) {
        Start-Sleep -Milliseconds 500
        $p = Read-Presence
        if ($p -and $p.pid -eq $proc.Id -and $p.lastPollUtc) { $first = $p }
    }
    if (-not $first) { throw "no heartbeat within 30s" }
    Start-Sleep -Seconds 5
    $second = Read-Presence
    "  presence pid $($first.pid) engine $($first.engine) port $($first.port)"
    "  lastPollUtc first read  $($first.lastPollUtc)"
    "  lastPollUtc 5s later    $($second.lastPollUtc)"
    Check "presence carries lastPollUtc" ([bool]$first.lastPollUtc) $first.lastPollUtc
    Check "heartbeat advances" ([DateTimeOffset]::Parse($second.lastPollUtc) -gt [DateTimeOffset]::Parse($first.lastPollUtc)) ("{0} -> {1}" -f $first.lastPollUtc, $second.lastPollUtc)
    Check "bug #97: engine is the build stamp, not 0.0.0.0" ($first.engine -notmatch '^\d+\.0\.0\.0$') $first.engine

    # ---- 2. alive ------------------------------------------------------------------------------
    Section "2. courier status (this tree) reads alive"
    $s = Status
    "  $(Utc) life=$($s.vitals.life)  $($s.vitals.describe)"
    Check "status reads alive" ($s.vitals.life -eq "alive") $s.vitals.describe

    # ---- 3. killed -> dead -----------------------------------------------------------------------
    Section "3. Stop-Process, then status reads dead with a time"
    $deadPid = $proc.Id
    $lastBeat = (Read-Presence).lastPollUtc
    Stop-Process -Id $deadPid -Force
    $proc.WaitForExit(10000) | Out-Null
    $killedAt = Utc
    "  killed pid $deadPid at $killedAt; its last heartbeat was $lastBeat"
    Check "record left behind by the killed courier" (Test-Path $presencePath) $presencePath
    $s = Status
    "  $(Utc) life=$($s.vitals.life)  $($s.vitals.describe)"
    "  lastSeenUtc $($s.vitals.lastSeenUtc)  taskLastRun $($s.vitals.taskLastRun)"
    Check "status reads dead" ($s.vitals.life -eq "dead") $s.vitals.describe
    Check "dead carries the last-seen time" ($s.vitals.describe -match 'dead \(last seen \d{4}-\d\d-\d\d \d\d:\d\d:\d\dZ') $s.vitals.describe
    Check "dead is last seen at the last heartbeat" ([DateTimeOffset]::Parse($s.vitals.lastSeenUtc) -eq [DateTimeOffset]::Parse($lastBeat)) ("{0} vs {1}" -f $s.vitals.lastSeenUtc, $lastBeat)
    Check "status names the task's last result" ("$($s.vitals.taskLastRun)" -like "3 (0x00000003)*") $s.vitals.taskLastRun

    # ---- 4. the death record ------------------------------------------------------------------
    Section "4. the next start journals the death record"
    $proc = Start-Courier "--task-name `"$scratchTask`""
    "  restarted pid $($proc.Id) at $(Utc)"
    $line = $null
    for ($i = 0; $i -lt 40 -and -not $line; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $logPath) {
            $line = Select-String -Path $logPath -SimpleMatch "previous courier pid $deadPid died silently" | Select-Object -Last 1
        }
    }
    "  courier.log: $($line.Line)"
    Check "death record journaled" ([bool]$line) "previous courier pid $deadPid died silently"
    Check "death record names the last poll" ("$($line.Line)" -match "last poll \d{4}-\d\d-\d\d \d\d:\d\d:\d\dZ") ""
    Check "death record carries the scheduler's result for the dead courier's task" ("$($line.Line)" -match [regex]::Escape("task last-run result 3 (0x00000003) at ") + ".*" + [regex]::Escape("[task $scratchTask]")) ""
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 500; $p = Read-Presence; if ($p -and $p.pid -eq $proc.Id -and $p.lastPollUtc) { break } }
    $s = Status
    "  $(Utc) life=$($s.vitals.life)  $($s.vitals.describe)"
    Check "the new courier reads alive" ($s.vitals.life -eq "alive") $s.vitals.describe
    Stop-Process -Id $proc.Id -Force
    $proc.WaitForExit(10000) | Out-Null
    $proc = $null

    # ---- 5. negative control -------------------------------------------------------------------
    Section "5. negative control: a courier that leaves through its finally leaves no death behind"
    $once = Start-Courier "--once --task-name `"$scratchTask`""
    $once.WaitForExit(60000) | Out-Null
    "  --once exited $($once.ExitCode); presence file present: $(Test-Path $presencePath)"
    Check "--once cleared the record (the dead one's successor journaled it, then its own finally ran)" (-not (Test-Path $presencePath)) ""
    $before = @(Select-String -Path $logPath -SimpleMatch "died silently").Count
    $proc = Start-Courier "--task-name `"$scratchTask`""
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Milliseconds 500; $p = Read-Presence; if ($p -and $p.pid -eq $proc.Id -and $p.lastPollUtc) { break } }
    $after = @(Select-String -Path $logPath -SimpleMatch "died silently").Count
    "  'died silently' lines before start: $before, after: $after"
    Check "no death record after a clean exit" ($after -eq $before) ("{0} -> {1}" -f $before, $after)
}
finally {
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force; $proc.WaitForExit(10000) | Out-Null }
    $null = Native "schtasks.exe" @("/Delete", "/TN", $scratchTask, "/F")
    "  scratch task deleted (exit $nativeExit)"
}

Section "courier.log (scratch)"
Get-Content $logPath | ForEach-Object { "  $_" }

Section "the real courier was only read"
$realAfter = LastRunOf $realTask
"  real task after: last run $($realAfter.time), last result $($realAfter.result)"
Check "real task not restarted by the rig" ($realAfter.time -eq $realBefore.time) ("{0} / {1}" -f $realBefore.time, $realAfter.time)

Section "summary"
$results
$failed = @($results | Where-Object { $_ -like "FAIL*" }).Count
"{0} checks, {1} failed, finished {2}" -f $results.Count, $failed, (Utc)
if ($failed -gt 0) { exit 1 }
