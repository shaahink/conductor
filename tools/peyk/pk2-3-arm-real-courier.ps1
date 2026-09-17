# PK2.3 - arm the instruments on the REAL courier, and the reading procedure for the window (PK6.1).
#
#   -ReadOnly   reads only: the presence record, the process behind it, the task's last run and its
#               XML triggers, `courier status` through this tree's build, the courier.log tail, the
#               installed conductor.exe's hash. This IS the PK6.1 reading procedure; it changes nothing.
#   (default)   the same reads, then `tools/install.ps1 -CourierOnly` ONCE (owner's go recorded in the
#               tracker handoff, 2026-09-17), then the reads again, a loopback hello and one protocol-2
#               push to chat id 0 - the courier carries it to the messenger with the real token and the
#               messenger answers "chat not found", so no message exists anywhere, and the refusal is
#               journaled in courier.log as the proof the push went through the new courier.
#
# It never touches conductor.exe, the courier's home, its config or its token. Before arming it
# exports the task's XML to TEMP (never into the repo: it names the machine's user) and, if the
# install fails, puts the task back on that XML and starts it again. ASCII only (Windows PowerShell 5.1).

param(
    [switch]$ReadOnly,
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\conductor"),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$TaskName = "Conductor Courier"
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
function Native($exe, [string[]]$argv) {
    $ErrorActionPreference = "Continue"
    $out = & $exe @argv 2>&1 | ForEach-Object { "$_" }
    $script:nativeExit = $LASTEXITCODE
    return $out
}
function Sha256($path) {
    $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { return ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($fs))).Replace("-", "") }
    finally { $fs.Dispose() }
}

$courierHome = if ($env:CONDUCTOR_STATE_HOME) { Join-Path $env:CONDUCTOR_STATE_HOME "courier" } else { Join-Path $env:LOCALAPPDATA "conductor\courier" }
$presencePath = Join-Path $courierHome "courier.run.json"
$logPath = Join-Path $courierHome "courier.log"
$engineExe = Join-Path $InstallDir "conductor.exe"
$courierExe = Join-Path $InstallDir "courier\conductor-courier.exe"
$freshEngine = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"

function Read-State($label) {
    Section "read: $label at $(Utc)"
    $s = @{}
    $s.presence = if (Test-Path $presencePath) { Get-Content $presencePath -Raw | ConvertFrom-Json } else { $null }
    if ($s.presence) {
        "  presence: pid $($s.presence.pid), protocol $($s.presence.protocol), engine $($s.presence.engine), port $($s.presence.port), task $($s.presence.taskName)"
        "            exe $($s.presence.exe)"
        "            startedUtc $($s.presence.startedUtc), lastPollUtc $($s.presence.lastPollUtc)"
        $proc = Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f $s.presence.pid)
        if ($proc) {
            $parent = Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f $proc.ParentProcessId)
            "  process:  $($proc.ExecutablePath) | args: $($proc.CommandLine.Replace($proc.ExecutablePath, '').Trim().Trim('`"').Trim()) | parent $($parent.Name)"
        } else { "  process:  pid $($s.presence.pid) is not running" }
        $s.proc = $proc
    } else { "  presence: none" }

    $row = Native "schtasks.exe" @("/Query", "/TN", $TaskName, "/V", "/FO", "CSV", "/NH") | Select-Object -First 1
    if ($nativeExit -eq 0 -and $row) {
        $f = $row -split '","'
        $s.task = @{ status = $f[3]; lastRun = $f[5]; lastResult = $f[6].Trim('"') }
        "  task:     status $($f[3]); last run $($f[5]); last result $($f[6].Trim('"'))"
    } else { "  task:     not registered ($($row))" }
    $xmlText = (Native "schtasks.exe" @("/Query", "/TN", $TaskName, "/XML")) -join "`n"
    if ($nativeExit -eq 0) {
        [xml]$xml = $xmlText
        $ns = New-Object Xml.XmlNamespaceManager $xml.NameTable
        $ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task")
        $s.interval = ($xml.SelectSingleNode("//t:Triggers/t:TimeTrigger/t:Repetition/t:Interval", $ns)).InnerText
        $s.boundary = ($xml.SelectSingleNode("//t:Triggers/t:TimeTrigger/t:StartBoundary", $ns)).InnerText
        $s.logon = [bool]$xml.SelectSingleNode("//t:Triggers/t:LogonTrigger", $ns)
        $s.policy = ($xml.SelectSingleNode("//t:Settings/t:MultipleInstancesPolicy", $ns)).InnerText
        $s.command = ($xml.SelectSingleNode("//t:Actions/t:Exec/t:Command", $ns)).InnerText
        $s.arguments = ($xml.SelectSingleNode("//t:Actions/t:Exec/t:Arguments", $ns)).InnerText
        $s.xml = $xmlText
        "  task xml: logon trigger $($s.logon); keep-alive $(if ($s.interval) { "TimeTrigger every $($s.interval) from $($s.boundary)" } else { 'NONE' }); $($s.policy)"
        "            exec $($s.command) $($s.arguments)"
    }

    if (Test-Path $freshEngine) {
        $psi = New-Object System.Diagnostics.ProcessStartInfo $freshEngine
        $psi.Arguments = "courier status --json --task-name `"$TaskName`""
        $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
        $psi.EnvironmentVariables.Remove("CONDUCTOR_PLAN")
        $sp = [System.Diagnostics.Process]::Start($psi); $so = $sp.StandardOutput.ReadToEnd(); $null = $sp.StandardError.ReadToEnd(); $sp.WaitForExit()
        try { $s.vitals = ($so | ConvertFrom-Json).vitals } catch { $s.vitals = $null }
        "  status:   life=$($s.vitals.life)  $($s.vitals.describe)$(if ($s.vitals.taskLastRun) { '  task last run ' + $s.vitals.taskLastRun })"
    }

    $s.engineHash = Sha256 $engineExe
    $s.engineWrite = (Get-Item $engineExe).LastWriteTimeUtc.ToString("u")
    "  conductor.exe: sha256 $($s.engineHash.Substring(0, 16))..., written $($s.engineWrite)"
    if (Test-Path $courierExe) { "  conductor-courier.exe: written $((Get-Item $courierExe).LastWriteTimeUtc.ToString('u'))" }
    if ($env:CONDUCTOR_PID) {
        $e = Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f $env:CONDUCTOR_PID)
        $s.engineAlive = [bool]$e
        "  driving engine pid $($env:CONDUCTOR_PID): $(if ($e) { $e.ExecutablePath } else { 'NOT RUNNING' })"
    }
    "  courier.log tail:"
    if (Test-Path $logPath) { Get-Content $logPath -Tail 10 | ForEach-Object { "    $_" } }
    $script:last = $s
}

"PK2.3 $(if ($ReadOnly) { 'read-out' } else { 'arming' }) at $(Utc) on commit $(git rev-parse --short HEAD)"
Read-State "before"; $before = $script:last
if ($ReadOnly) { return }

# ---- arm -------------------------------------------------------------------------------------------
$backup = Join-Path $env:TEMP ("pk23-courier-task-backup-{0}.xml" -f (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmssZ"))
[IO.File]::WriteAllText($backup, $before.xml, (New-Object Text.UnicodeEncoding($false, $true)))
Section "arm: tools/install.ps1 -CourierOnly (task backup at $backup)"
$armStart = Utc
$ErrorActionPreference = "Continue"
$installOut = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\install.ps1") -CourierOnly -InstallDir $InstallDir -CourierTaskName $TaskName 2>&1 | ForEach-Object { "$_" }
$installExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"
$installOut | ForEach-Object { "  | $_" }
"  install.ps1 -CourierOnly exit $installExit ($armStart -> $(Utc))"
if ($installExit -ne 0) {
    Section "ROLLBACK: the install failed - the task goes back on its previous definition"
    Native "schtasks.exe" @("/Create", "/TN", $TaskName, "/XML", $backup, "/F") | ForEach-Object { "  $_" }
    Native "schtasks.exe" @("/Run", "/TN", $TaskName) | ForEach-Object { "  $_" }
    Read-State "after rollback"
    "ARMING FAILED - task restored to the previous exe; see above"
    exit 1
}

$armed = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    $p = if (Test-Path $presencePath) { try { Get-Content $presencePath -Raw | ConvertFrom-Json } catch { $null } } else { $null }
    if ($p -and $p.exe -eq $courierExe -and $p.lastPollUtc) { $armed = $p; break }
}
$armedAt = if ($armed) { ([DateTimeOffset]::Parse($armed.startedUtc)).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") } else { Utc }

Read-State "after arming"; $after = $script:last
# MEASURED at the arming: 12 s saw no new beat - one real poll is a 30 s long-poll plus the interval.
Start-Sleep -Seconds 45
Read-State "45 s later"; $later = $script:last

# ---- the loopback: hello and one protocol-2 push that creates no message ----------------------------
Section "loopback: hello and a protocol-2 push to chat 0"
$secret = ([IO.File]::ReadAllText((Join-Path $courierHome "courier.secret"))).Trim()
$base = "http://127.0.0.1:$($later.presence.port)"
$hello = $null; $ack = $null
try {
    $hello = (Invoke-WebRequest -UseBasicParsing -Uri "$base/hello" -Headers @{ "X-Conductor-Courier" = $secret } -TimeoutSec 10).Content | ConvertFrom-Json
    "  hello: protocol $($hello.protocol), pid $($hello.pid), engine $($hello.engine), lastPollUtc $($hello.lastPollUtc)"
} catch { "  hello failed: $($_.Exception.Message)" }
$pushAt = Utc
$body = @{ chatId = "0"; text = "PK2.3 arming probe - addressed to no chat, never delivered"; protocol = 2; severity = "Quiet"; origin = "pk2.3-arming-probe" } | ConvertTo-Json
try {
    $resp = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$base/push" -Headers @{ "X-Conductor-Courier" = $secret } -ContentType "application/json" -Body $body -TimeoutSec 30
    $ack = $resp.Content | ConvertFrom-Json
} catch {
    $r = $_.Exception.Response
    # MEASURED at the arming: 5.1 has already drained the response stream on a non-2xx answer; the
    # body survives in ErrorDetails.
    if ($_.ErrorDetails.Message) { $ack = $_.ErrorDetails.Message | ConvertFrom-Json }
    elseif ($r) { "  push answered $([int]$r.StatusCode) with no readable body" }
    else { "  push failed: $($_.Exception.Message)" }
}
"  push at $pushAt -> accepted $($ack.accepted); detail: $($ack.detail)"
Start-Sleep -Seconds 2
$pushLine = Get-Content $logPath -Tail 20 | Where-Object { $_ -like "*push to chat 0 failed*" } | Select-Object -Last 1
"  courier.log: $pushLine"

# ---- verdicts --------------------------------------------------------------------------------------
Section "checks"
Check "install.ps1 -CourierOnly exited 0" ($installExit -eq 0) ""
Check "conductor.exe untouched (sha256 and write time)" (($before.engineHash -eq $later.engineHash) -and ($before.engineWrite -eq $later.engineWrite)) ("{0} / {1}" -f $before.engineWrite, $later.engineWrite)
Check "the engine driving this run is still alive" ([bool]$later.engineAlive) $env:CONDUCTOR_PID
Check "the task runs the courier's own binary" ($later.command -eq $courierExe) $later.command
Check "the task XML carries the five-minute keep-alive" ($later.interval -eq "PT5M") ("{0} from {1}" -f $later.interval, $later.boundary)
Check "the task XML keeps the logon trigger and IgnoreNew" ($later.logon -and $later.policy -eq "IgnoreNew") $later.policy
Check "the presence names the courier binary and this task" (($later.presence.exe -eq $courierExe) -and ($later.presence.taskName -eq $TaskName)) $later.presence.exe
Check "the running process is that binary" ($later.proc -and $later.proc.ExecutablePath -eq $courierExe) $later.proc.ExecutablePath
Check "courier status (this tree) reads alive" ($later.vitals.life -eq "alive") $later.vitals.describe
Check "the heartbeat advances" ($after.presence.lastPollUtc -and $later.presence.lastPollUtc -and ([DateTimeOffset]::Parse($later.presence.lastPollUtc) -gt [DateTimeOffset]::Parse($after.presence.lastPollUtc))) ("{0} -> {1}" -f $after.presence.lastPollUtc, $later.presence.lastPollUtc)
Check "the hello speaks protocol 2 with a heartbeat" ($hello -and $hello.protocol -eq 2 -and $hello.lastPollUtc) ""
Check "the protocol-2 push reached the messenger through the new courier" ($ack -and ($ack.accepted -or "$($ack.detail)" -match "chat not found|Bad Request")) $ack.detail
Check "and the courier journaled it" ([bool]$pushLine) ""

Section "ARMED"
"  armed at (courier start, UTC): $armedAt"
"  courier pid: $($later.presence.pid)"
"  courier engine stamp: $($later.presence.engine)"
"  task: $TaskName -> $($later.command) $($later.arguments); keep-alive $($later.interval)"
"  reading procedure (PK6.1): powershell -NoProfile -ExecutionPolicy Bypass -File tools/peyk/pk2-3-arm-real-courier.ps1 -ReadOnly"
"    then in courier.log after the arming time: every 'courier run starting' line is a (re)start; the line before"
"    it says how the previous one ended - 'courier run stopped: exit N', 'courier process exit ...', 'courier run DIED"
"    (unhandled ...)', 'courier received SIGTERM/SIGHUP ...' - or, when nothing was journaled, the next start's"
"    'previous courier pid N died silently; last poll T; task last-run result R'. Keep-alive starts land on a"
"    five-minute mark; R reads 267009 when the task itself restarted it (the exit code is then not kept)."
Section "summary"
$results
$failed = @($results | Where-Object { $_ -like "FAIL*" }).Count
"{0} checks, {1} failed, finished {2}" -f $results.Count, $failed, (Utc)
if ($failed -gt 0) { exit 1 }
