# PK2.2 live proof - last words, the keep-alive trigger measured, and a run that restarts a dead courier.
#
# What it proves, in order:
#   A. exit journaling on this tree's conductor-courier.exe: Environment.Exit(0) and an unhandled throw
#      on a timer thread each leave their line in courier.log (fault seam CONDUCTOR_COURIER_FAULT);
#      control: Stop-Process (TerminateProcess) leaves none - the limit the death record covers
#   B. `conductor courier install --task-name <scratch> --exe <wrapper.cmd> --no-start` (this tree)
#      registers a task whose XML, read back with schtasks /query /xml, carries the PT5M TimeTrigger
#   C. MEASURED: that task's courier exits 0 (fault seam) and the scheduler starts it again within five
#      minutes, twice; control: the same XML WITHOUT the TimeTrigger, registered as a second scratch
#      task, is not started again after its exit 0 (RestartOnFailure does not fire on exit 0)
#   D. a scratch `conductor run --once` (this tree) whose session boundary finds the scratch courier dead
#      restarts its task through the real scheduler; the run's OWNER-QUEUE.md carries
#      "courier restarted by this run, 1st time" and the courier comes back alive
#
# Scratch only. Every scheduled task here runs a wrapper .cmd under TEMP that sets its own state home,
# courier port and a fake token before starting the courier, and courier.json points the Bot API at a
# port nothing listens on - nothing in this rig can reach the real courier, its home, its task or its
# token, which are only ever READ (the real task's last run before and after). Both scratch tasks are
# deleted at the end. ASCII only (Windows PowerShell 5.1).

param(
    [string]$OutDir   = (Join-Path $env:TEMP "pk22-rig"),
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
    return @{ status = $f[3]; time = $f[5]; result = $f[6].Trim('"') }
}
# courier.log lines are "yyyy-MM-dd HH:mm:ssZ text"
function LogLines($path, $pattern) {
    if (-not (Test-Path $path)) { return @() }
    return @(Get-Content $path | Where-Object { $_ -like "*$pattern*" })
}
function StampOf($line) { [DateTime]::ParseExact($line.Substring(0, 20), "yyyy-MM-dd HH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal) }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null
"PK2.2 proof at $(Utc) on commit $(git rev-parse --short HEAD)"

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
$realBefore = LastRunOf "Conductor Courier"
"  real task (read only) before: status $($realBefore.status), last run $($realBefore.time), last result $($realBefore.result)"

function New-Home($name) {
    $h = Join-Path $OutDir $name
    New-Item -ItemType Directory -Path (Join-Path $h "courier") | Out-Null
    $settings = @{
        projects = @(@{ plan = "pk22-scratch"; repo = $OutDir })
        chats = @(@{ chatId = "770000001"; profile = "admin" })
        pollIntervalSeconds = 2
        apiBaseUrl = "http://127.0.0.1:$(FreePort)"
    } | ConvertTo-Json -Depth 4
    Set-Content -Path (Join-Path $h "courier\courier.json") -Value $settings -Encoding ASCII
    return $h
}
function Start-Courier($stateHome, $port, $fault, $argv) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $courier
    $psi.Arguments = $argv
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables["CONDUCTOR_STATE_HOME"] = $stateHome
    $psi.EnvironmentVariables["CONDUCTOR_COURIER_PORT"] = "$port"
    $psi.EnvironmentVariables["CONDUCTOR_TELEGRAM_TOKEN"] = "111111:pk22-scratch-token"
    $psi.EnvironmentVariables.Remove("CONDUCTOR_PLAN")
    if ($fault) { $psi.EnvironmentVariables["CONDUCTOR_COURIER_FAULT"] = $fault }
    return [System.Diagnostics.Process]::Start($psi)
}
function Write-Wrapper($path, $stateHome, $port, $taskName, $fault) {
    $lines = @(
        "@echo off",
        "set CONDUCTOR_STATE_HOME=$stateHome",
        "set CONDUCTOR_COURIER_PORT=$port",
        "set CONDUCTOR_TELEGRAM_TOKEN=111111:pk22-scratch-token",
        "set CONDUCTOR_PLAN=",
        "set CONDUCTOR_COURIER_FAULT=$fault",
        "`"$courier`" --task-name `"$taskName`"",
        "exit /b %ERRORLEVEL%"
    )
    Set-Content -Path $path -Value $lines -Encoding ASCII
}

# `courier status --json` for the keep-alive task's scratch home; the home reaches it only through the environment.
function StatusK() {
    $psi = New-Object System.Diagnostics.ProcessStartInfo $engine
    $psi.Arguments = "courier status --json --task-name `"$taskK`""
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables["CONDUCTOR_STATE_HOME"] = $homeK
    $psi.EnvironmentVariables.Remove("CONDUCTOR_PLAN")
    $sp = [System.Diagnostics.Process]::Start($psi); $so = $sp.StandardOutput.ReadToEnd(); $null = $sp.StandardError.ReadToEnd(); $sp.WaitForExit()
    return ($so | ConvertFrom-Json).vitals
}
# Seconds into the current five-minute slot of the local clock (the keep-alive ticks on :00, :05, ...).
function IntoSlot() { $n = Get-Date; return ($n.Minute % 5) * 60 + $n.Second }

$suffix = [Guid]::NewGuid().ToString("N").Substring(0, 6)
$taskK = "pk22-keepalive-$suffix"
$taskN = "pk22-nokeepalive-$suffix"
$procs = New-Object System.Collections.ArrayList
try {
    # ---- A. last words ---------------------------------------------------------------------------
    Section "A. exit journaling"
    $homeA = New-Home "home-a"
    $logA = Join-Path $homeA "courier\courier.log"
    $portA = FreePort

    $p = Start-Courier $homeA $portA "exit0:3" '--task-name "pk22-a"'
    [void]$procs.Add($p); $p.WaitForExit(60000) | Out-Null
    "  exit0:3 -> process exit code $($p.ExitCode)"
    Check "Environment.Exit(0) exits 0" ($p.ExitCode -eq 0) ("exit=" + $p.ExitCode)
    Check "the seam says it was armed" ((LogLines $logA "FAULT SEAM ARMED").Count -ge 1) ((LogLines $logA "FAULT SEAM ARMED") | Select-Object -Last 1)
    $l = LogLines $logA "courier process exit WITHOUT Main returning: exit code 0" | Select-Object -Last 1
    Check "ProcessExit journaled an exit Main never returned from" ([bool]$l) $l

    $p = Start-Courier $homeA $portA "throw:3" '--task-name "pk22-a"'
    [void]$procs.Add($p); $p.WaitForExit(60000) | Out-Null
    "  throw:3 -> process exit code $($p.ExitCode)"
    $l = LogLines $logA "courier run DIED (unhandled, terminating): InvalidOperationException: fault seam" | Select-Object -Last 1
    Check "an unhandled throw on a timer thread is journaled" ([bool]$l) $l
    Check "and the process did end non-zero" ($p.ExitCode -ne 0) ("exit=" + $p.ExitCode)

    $p = Start-Courier $homeA $portA $null '--task-name "pk22-a"'
    [void]$procs.Add($p)
    Start-Sleep -Seconds 4
    $before = @(Get-Content $logA).Count
    Stop-Process -Id $p.Id -Force; $p.WaitForExit(10000) | Out-Null
    Start-Sleep -Seconds 1
    $tail = @(Get-Content $logA | Select-Object -Skip $before)
    $exitLines = @($tail | Where-Object { $_ -like "*courier process exit*" -or $_ -like "*DIED*" })
    "  Stop-Process control: $($tail.Count) line(s) after the kill, $($exitLines.Count) exit line(s)"
    Check "control: TerminateProcess leaves no exit line (the death record's job)" ($exitLines.Count -eq 0) ""

    # ---- B. the task XML ---------------------------------------------------------------------------
    Section "B. this tree registers the keep-alive trigger; schtasks /query /xml reads it back"
    $homeK = New-Home "home-k"; $portK = FreePort
    $homeN = New-Home "home-n"; $portN = FreePort
    $wrapK = Join-Path $OutDir "courier-k.cmd"; Write-Wrapper $wrapK $homeK $portK $taskK "exit0:20"
    $wrapN = Join-Path $OutDir "courier-n.cmd"; Write-Wrapper $wrapN $homeN $portN $taskN "exit0:20"

    $inst = Native $engine @("courier", "install", "--task-name", $taskK, "--exe", $wrapK, "--no-start")
    $inst | ForEach-Object { "  $_" }
    Check "courier install registered the scratch task" ($nativeExit -eq 0) ("exit=" + $nativeExit)
    $xmlText = (Native "schtasks.exe" @("/Query", "/TN", $taskK, "/XML")) -join "`n"
    $xmlPath = Join-Path $OutDir "task-k.xml"
    Set-Content -Path $xmlPath -Value $xmlText -Encoding Unicode
    [xml]$xml = $xmlText
    $ns = New-Object Xml.XmlNamespaceManager $xml.NameTable
    $ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task")
    $interval = $xml.SelectSingleNode("//t:Triggers/t:TimeTrigger/t:Repetition/t:Interval", $ns).InnerText
    $boundary = $xml.SelectSingleNode("//t:Triggers/t:TimeTrigger/t:StartBoundary", $ns).InnerText
    $logon = $xml.SelectSingleNode("//t:Triggers/t:LogonTrigger", $ns)
    $policy = $xml.SelectSingleNode("//t:Settings/t:MultipleInstancesPolicy", $ns).InnerText
    $command = $xml.SelectSingleNode("//t:Actions/t:Exec/t:Command", $ns).InnerText
    "  read back: TimeTrigger interval $interval from $boundary; logon trigger $([bool]$logon); $policy; exec $command"
    Get-Content $xmlPath | Select-String -Pattern "Trigger|Interval|StartBoundary|StopAtDurationEnd|MultipleInstances|RestartOnFailure|Command" | ForEach-Object { "    " + $_.Line.Trim() }
    Check "read-back XML carries the PT5M keep-alive" ($interval -eq "PT5M") $interval
    Check "read-back XML keeps the logon trigger" ([bool]$logon) ""
    Check "read-back XML keeps IgnoreNew" ($policy -eq "IgnoreNew") $policy
    Check "the task runs the scratch wrapper" ($command -eq $wrapK) $command

    # The control: the very same definition without the TimeTrigger, as a second scratch task.
    $noKeep = $xml.Clone()
    $tt = $noKeep.SelectSingleNode("//t:Triggers/t:TimeTrigger", $ns)
    [void]$tt.ParentNode.RemoveChild($tt)
    $noKeep.SelectSingleNode("//t:Actions/t:Exec/t:Command", $ns).InnerText = $wrapN
    $xmlN = Join-Path $OutDir "task-n.xml"
    $noKeep.Save($xmlN)
    $null = Native "schtasks.exe" @("/Create", "/TN", $taskN, "/XML", $xmlN, "/F")
    Check "control task (no TimeTrigger) registered" ($nativeExit -eq 0) ("exit=" + $nativeExit)

    # ---- C. measured keep-alive -------------------------------------------------------------------
    Section "C. a courier that exits 0 is started again by the keep-alive within five minutes"
    $logK = Join-Path $homeK "courier\courier.log"
    $logN = Join-Path $homeN "courier\courier.log"
    # Start clear of a tick, so the hand-started courier's exit is not a tick that IgnoreNew swallowed.
    while ((IntoSlot) -lt 15 -or (IntoSlot) -gt 200) { Start-Sleep -Seconds 5 }
    $t0 = Utc
    $null = Native "schtasks.exe" @("/Run", "/TN", $taskK)
    $null = Native "schtasks.exe" @("/Run", "/TN", $taskN)
    "  both tasks started by hand at $t0; each courier exits 0 after 20s"
    $deadline = (Get-Date).AddMinutes(12.5)
    $sawZero = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 15
        $starts = LogLines $logK "courier run starting"
        $exits = LogLines $logK "courier process exit WITHOUT Main returning: exit code 0"
        $run = LastRunOf $taskK
        if ($exits.Count -ge 1 -and $run -and $run.result -eq "0") { $sawZero = $true }
        "  {0}  keep-alive task: {1} start(s), {2} exit(s), scheduler says {3} / {4}; control: {5} start(s)" -f (Utc), $starts.Count, $exits.Count, $run.status, $run.result, (LogLines $logN "courier run starting").Count
        if ($starts.Count -ge 3) { break }
    }
    Check "the scheduler recorded the courier's exit 0 as the task's last result" $sawZero ""
    $starts = @(LogLines $logK "courier run starting")
    $exits = @(LogLines $logK "courier process exit WITHOUT Main returning: exit code 0")
    "  keep-alive courier.log timeline:"
    Get-Content $logK | Where-Object { $_ -like "*courier run starting*" -or $_ -like "*WITHOUT Main*" -or $_ -like "*died silently*" } | ForEach-Object { "    $_" }
    Check "started three times (one by hand, two by the keep-alive)" ($starts.Count -ge 3) ("starts=" + $starts.Count)
    for ($i = 1; $i -lt [Math]::Min($starts.Count, 3); $i++) {
        $exitAt = StampOf $exits[$i - 1]; $restartAt = StampOf $starts[$i]
        $gap = ($restartAt - $exitAt).TotalSeconds
        Check ("restart {0}: exit 0 at {1:HH:mm:ss}Z, started again at {2:HH:mm:ss}Z" -f $i, $exitAt, $restartAt) (($gap -gt 0) -and ($gap -le 300)) ("{0:0}s after the exit (limit 300s)" -f $gap)
        Check ("restart {0} landed on a five-minute mark" -f $i) (($restartAt.ToLocalTime().Minute % 5 -eq 0) -and ($restartAt.Second -lt 30)) ("{0:HH:mm:ss}Z" -f $restartAt)
    }
    $nStarts = @(LogLines $logN "courier run starting").Count
    $nRun = LastRunOf $taskN
    "  control task: $nStarts start(s); scheduler last result $($nRun.result) at $($nRun.time)"
    Check "control: without the TimeTrigger, an exit 0 is never restarted (RestartOnFailure does not fire)" ($nStarts -eq 1 -and $nRun.result -eq "0") ("starts=$nStarts result=$($nRun.result)")
    $null = Native "schtasks.exe" @("/Delete", "/TN", $taskN, "/F")

    # ---- D. a run restarts the dead courier at its boundary ---------------------------------------
    Section "D. a scratch run restarts the dead scratch courier and says so on its owner queue"
    Write-Wrapper $wrapK $homeK $portK $taskK ""        # no fault from here on
    # Out of the keep-alive's way: act just after a five-minute mark, so the run's boundary is first.
    while ((IntoSlot) -lt 30 -or (IntoSlot) -gt 150) { Start-Sleep -Seconds 5 }
    $null = Native "schtasks.exe" @("/End", "/TN", $taskK)
    $presenceK = Join-Path $homeK "courier\courier.run.json"
    if (Test-Path $presenceK) {
        $pr = Get-Content $presenceK -Raw | ConvertFrom-Json
        $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($pr.pid)"
        if ($cim -and $cim.ExecutablePath -eq $courier) { Stop-Process -Id $pr.pid -Force; "  killed scratch courier pid $($pr.pid) ($($cim.ExecutablePath))" }
    } else {
        # The courier exited through the seam and left its record; make one if the seam's exit raced us.
        "  no presence record left; writing a dead one for pid 1"
        @{ protocol = 2; pid = 1; taskName = $taskK; startedUtc = (Get-Date).ToUniversalTime().AddHours(-1).ToString("o"); lastPollUtc = (Get-Date).ToUniversalTime().AddMinutes(-30).ToString("o") } | ConvertTo-Json | Set-Content -Path $presenceK -Encoding ASCII
    }

    $repo = Join-Path $OutDir "repo"
    New-Item -ItemType Directory -Path $repo | Out-Null
    Push-Location $repo
    try {
        $null = Native "git" @("init", "-b", "main")
        $null = Native "git" @("config", "user.email", "pk22@rig")
        $null = Native "git" @("config", "user.name", "PK2.2 rig")
        Set-Content -Path "TRACKER.md" -Encoding ASCII -Value @("# PK2.2 rig", "", "## Handoff", "last: none.", "", "## Checkpoints", "", "| # | Checkpoint | Status | Commit | Evidence |", "|---|---|---|---|---|", "| H0.1 | rig | TODO | | |")
        Set-Content -Path "fake-agent.cmd" -Encoding ASCII -Value @(
            "@echo off",
            'echo {"type":"step_start"}',
            'echo {"type":"text","part":{"text":"PK2.2 rig session."}}',
            'echo {"type":"step_finish","part":{"cost":0.0001,"tokens":{"input":10,"output":10,"reasoning":0,"cache":{"read":0}}}}',
            "exit /b 0")
        $plan = @{
            name = "PK22LiveRig"; repo = $repo; tracker = "TRACKER.md"
            stages = @(@{ id = "H0"; title = "Rig"; sessions = 1 })
            agent = @{ command = "cmd.exe"; args = @("/c", (Join-Path $repo "fake-agent.cmd"), "{prompt}"); provider = "opencode" }
            gatePolicy = "perSession"
            gates = @(@{ name = "smoke"; command = "echo ok"; tier = "fast"; timeoutMinutes = 1 })
            report = @{ commit = $false }
        } | ConvertTo-Json -Depth 6
        Set-Content -Path "conductor.plan.json" -Value $plan -Encoding ASCII
        $null = Native "git" @("add", "-A")
        $null = Native "git" @("commit", "-m", "chore: rig", "--no-gpg-sign")
    } finally { Pop-Location }

    $vit = StatusK
    "  $(Utc) before the run: life=$($vit.life)  $($vit.describe)"
    Check "the scratch courier reads dead before the run" ($vit.life -eq "dead") $vit.describe

    $runAt = Utc
    $psi = New-Object System.Diagnostics.ProcessStartInfo $engine
    $psi.Arguments = "run -p `"$(Join-Path $repo 'conductor.plan.json')`" --once --headless --no-control-plane --no-face"
    $psi.WorkingDirectory = $repo
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables["CONDUCTOR_STATE_HOME"] = $homeK
    $psi.EnvironmentVariables.Remove("CONDUCTOR_PLAN")
    $psi.EnvironmentVariables.Remove("CONDUCTOR_TELEGRAM_TOKEN")
    $rp = [System.Diagnostics.Process]::Start($psi)
    $rout = $rp.StandardOutput.ReadToEndAsync(); $rerr = $rp.StandardError.ReadToEndAsync()
    if (-not $rp.WaitForExit(240000)) { Stop-Process -Id $rp.Id -Force; "  run timed out" }
    "  run started $runAt, exit $($rp.ExitCode)"
    ($rout.Result -split "`n" | Where-Object { $_ -match "courier|session|error" } | Select-Object -First 15) | ForEach-Object { "    " + $_.TrimEnd() }
    if ($rerr.Result) { ($rerr.Result -split "`n" | Select-Object -First 10) | ForEach-Object { "    stderr: " + $_.TrimEnd() } }

    $runLog = Join-Path $repo ".conductor\conductor.log"
    $restartLine = if (Test-Path $runLog) { Select-String -Path $runLog -SimpleMatch "courier restarted by this run" | Select-Object -First 1 } else { $null }
    "  conductor.log: $($restartLine.Line)"
    Check "the run logged the restart" ([bool]$restartLine) ""
    $queuePath = Join-Path $repo ".conductor\OWNER-QUEUE.md"
    $queueHit = if (Test-Path $queuePath) { Select-String -Path $queuePath -SimpleMatch "courier restarted by this run, 1st time" | Select-Object -First 1 } else { $null }
    "  OWNER-QUEUE.md: $($queueHit.Line)"
    Check "the run's owner queue carries 'courier restarted by this run, 1st time'" ([bool]$queueHit) $queuePath

    $back = $null
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 1
        $back = LogLines $logK "courier run starting" | Where-Object { (StampOf $_) -ge [DateTime]::Parse($runAt).ToUniversalTime().AddSeconds(-1) } | Select-Object -First 1
        if ($back) { break }
    }
    "  courier.log after the run: $back"
    Check "the scheduler started the scratch courier after the run asked" ([bool]$back) ""
    for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Seconds 1; $vit = StatusK; if ($vit.life -eq "alive") { break } }
    "  $(Utc) after the run: life=$($vit.life)  $($vit.describe)"
    Check "the restarted scratch courier reads alive" ($vit.life -eq "alive") $vit.describe
}
finally {
    foreach ($p in $procs) { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force } }
    foreach ($t in @($taskK, $taskN)) {
        $null = Native "schtasks.exe" @("/End", "/TN", $t)
        $null = Native "schtasks.exe" @("/Delete", "/TN", $t, "/F")
    }
    foreach ($h in @("home-k", "home-n")) {
        $pp = Join-Path $OutDir "$h\courier\courier.run.json"
        if (Test-Path $pp) {
            $pr = Get-Content $pp -Raw | ConvertFrom-Json
            $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($pr.pid)"
            if ($cim -and $cim.ExecutablePath -eq $courier) { Stop-Process -Id $pr.pid -Force; "  stopped scratch courier pid $($pr.pid)" }
        }
    }
    "  scratch tasks ended and deleted"
}

Section "the real courier was only read"
$realAfter = LastRunOf "Conductor Courier"
"  real task after: status $($realAfter.status), last run $($realAfter.time), last result $($realAfter.result)"
Check "real task not restarted by the rig" ($realAfter.time -eq $realBefore.time) ("{0} / {1}" -f $realBefore.time, $realAfter.time)

Section "summary"
$results
$failed = @($results | Where-Object { $_ -like "FAIL*" }).Count
"{0} checks, {1} failed, finished {2}" -f $results.Count, $failed, (Utc)
if ($failed -gt 0) { exit 1 }
