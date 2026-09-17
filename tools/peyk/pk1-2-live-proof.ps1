# PK1.2 live proof - the engine installs around a live courier, and -CourierOnly replaces the courier
# without touching the engine.
#
# What it proves, in order, against a SCRATCH install path under TEMP:
#   1. tools/install.ps1 publishes both: conductor.exe, and conductor-courier.exe in <install>\courier
#   2. the INSTALLED engine's `courier install` registers a scratch task on the courier's own binary -
#      read back with schtasks /Query /XML, never started (trap 15)
#   3. with a scratch courier LIVE from <install>\courier, a second install - with the engine's files
#      aged so the publish really rewrites them - succeeds, rewrites conductor.exe, and leaves the
#      courier alone: same pid, still live, and ONE "courier run starting" line in its log
#   4. `release preflight` (the repo's fresh build) is green on the courier line for that courier and
#      says the engine installs without stopping it
#   5. install.ps1 -CourierOnly -NoCourierStart stops that courier, republishes its directory, and
#      re-registers the task on it, while conductor.exe keeps the same hash and timestamp; the
#      replaced courier then starts from the new binary
#
# Scratch only: its own install dir, state home, courier port, token, Bot API (nothing listens),
# repo, plan and task name. It never touches the real install path, the real courier, its home, its
# task or its token, never runs the PATH shim step, and never asks the scheduler to START a task
# (a logon task would run with the machine's real environment).
# ASCII only (Windows PowerShell 5.1).

param(
    [string]$OutDir   = (Join-Path $env:TEMP "pk12-rig"),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$TaskName = "Conductor Courier PK12 Scratch"
)

$ErrorActionPreference = "Stop"
$env:CONDUCTOR_PLAN = $null
Set-Location $RepoRoot

$results = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    $verdict = "FAIL"
    if ($ok) { $verdict = "PASS" }
    $line = "{0}  {1}  {2}" -f $verdict, $name, $detail
    [void]$results.Add($line)
    $line
}
function Section($title) { ""; "==== $title ====" }
function Native() {
    # Native stderr under "Stop" is a terminating error in Windows PowerShell 5.1.
    $ErrorActionPreference = "Continue"
    $exeArg = $args[0]
    $rest = @()
    if ($args.Count -gt 1) { $rest = $args[1..($args.Count - 1)] }
    $out = & $exeArg @rest 2>&1 | ForEach-Object { "$_" }
    $script:lastExit = $LASTEXITCODE
    return $out
}
# Get-FileHash is not loadable when this runs as a Windows PowerShell 5.1 child of pwsh 7 (the
# inherited PSModulePath points at 7's modules), so the hash is taken with the BCL directly.
function Sha256($path) { [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($path))) -replace "-", "" }
function StartLog() { @(Get-Content $logPath -ErrorAction SilentlyContinue | Where-Object { $_ -match "courier run starting" }).Count }
function TaskXml() { (Native schtasks.exe /Query /TN $TaskName /XML) -join "`n" }

$install = Join-Path $OutDir "install"
$realInstall = Join-Path $env:LOCALAPPDATA "Programs\conductor"
if ($install.StartsWith($realInstall, [StringComparison]::OrdinalIgnoreCase) -or -not $install.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase)) {
    throw "refusing: the install path must be a scratch directory under TEMP, not $install"
}
if ($TaskName -eq "Conductor Courier") { throw "refusing: the real task name" }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$stateHome = Join-Path $OutDir "state-home"
$repo = Join-Path $OutDir "repo"
New-Item -ItemType Directory -Path (Join-Path $stateHome "courier"), $repo | Out-Null
$logPath = Join-Path $stateHome "courier\courier.log"
$presencePath = Join-Path $stateHome "courier\courier.run.json"
$courierExe = Join-Path $install "courier\conductor-courier.exe"
$engineExe = Join-Path $install "conductor.exe"

$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start(); $deadApi = $probe.LocalEndpoint.Port; $probe.Stop()
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start(); $courierPort = $probe.LocalEndpoint.Port; $probe.Stop()

# Every child of this rig - install.ps1, the engines, the courier - inherits the scratch environment.
$env:CONDUCTOR_STATE_HOME = $stateHome
$env:CONDUCTOR_COURIER_PORT = "$courierPort"
$env:CONDUCTOR_TELEGRAM_TOKEN = "111111:pk12-scratch-token"

@{ projects = @(@{ plan = "pk12-rig"; repo = $repo }); chats = @(@{ chatId = "770000001"; profile = "admin" }); pollIntervalSeconds = 2; apiBaseUrl = "http://127.0.0.1:$deadApi" } |
    ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $stateHome "courier\courier.json") -Encoding ASCII
$plan = Join-Path $repo "conductor.plan.json"
@{
    name    = "pk12-rig"
    repo    = $repo
    tracker = "TRACKER.md"
    stages  = @(@{ id = "PK1"; title = "its own process"; sessions = 1 })
    # Never invoked - no run is ever started here - but plan validation wants a shape.
    agent   = @{ command = "echo"; args = @("{prompt}") }
} | ConvertTo-Json -Depth 6 | Set-Content -Path $plan -Encoding ASCII
Set-Content -Path (Join-Path $repo "TRACKER.md") -Value "# pk12 rig" -Encoding ASCII

"PK1.2 proof at $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')) on commit $(git rev-parse --short HEAD)"
"  scratch install $install"
"  scratch state home $stateHome, courier port $courierPort, Bot API http://127.0.0.1:$deadApi (nothing listens), task '$TaskName'"

$courier = $null
try {
    [void](Native schtasks.exe /Query /TN $TaskName)
    if ($lastExit -eq 0) { throw "a task named '$TaskName' already exists - remove it before running the rig" }

    Section "0. the repo's fresh build (for release preflight)"
    $o = Native dotnet build Conductor.slnx "-clp:ErrorsOnly"   # quoted: through $args, 5.1 splits -clp:ErrorsOnly in two
    if ($lastExit -ne 0) { $o | Select-Object -Last 15 | ForEach-Object { "  | $_" } }
    Check "fresh build" ($lastExit -eq 0) ("exit=" + $lastExit)
    $fresh = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"

    # ---- 1 -------------------------------------------------------------------------------------
    Section "1. first install: both binaries"
    $o = Native powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\install.ps1") -InstallDir $install -SkipShim -Config Release -CourierTaskName $TaskName
    $o | ForEach-Object { "  | $_" }
    Check "install exit 0" ($lastExit -eq 0) ("exit=" + $lastExit)
    Check "engine installed" (Test-Path $engineExe) $engineExe
    Check "courier installed in its own directory" (Test-Path $courierExe) $courierExe

    # ---- 2 -------------------------------------------------------------------------------------
    Section "2. the installed engine registers the task on the courier's own binary (never started)"
    $o = Native $engineExe courier install --task-name $TaskName --no-start
    $o | ForEach-Object { "  | $_" }
    Check "courier install exit 0" ($lastExit -eq 0) ("exit=" + $lastExit)
    $xml = TaskXml
    $cmd = [regex]::Match($xml, "<Command>([^<]*)</Command>").Groups[1].Value
    $arg = [regex]::Match($xml, "<Arguments>([^<]*)</Arguments>").Groups[1].Value
    "  schtasks /Query /XML: Command=$cmd  Arguments=$arg"
    Check "task runs the courier's own binary" ($cmd -eq $courierExe) $cmd
    Check "task passes the courier its task name" ($arg -eq ('--task-name &quot;' + $TaskName + '&quot;') -or $arg -eq ('--task-name "' + $TaskName + '"')) $arg

    # ---- 3 -------------------------------------------------------------------------------------
    Section "3. a live courier; the engine is installed around it"
    $psi = New-Object System.Diagnostics.ProcessStartInfo $courierExe
    $psi.Arguments = '--task-name "' + $TaskName + '"'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $courier = [System.Diagnostics.Process]::Start($psi)
    for ($i = 0; ($i -lt 60) -and -not (Test-Path $presencePath); $i++) { Start-Sleep -Milliseconds 500 }
    $presence = Get-Content $presencePath -Raw | ConvertFrom-Json
    "  courier pid $($courier.Id); presence: " + ($presence | ConvertTo-Json -Compress)
    Check "courier live from its own directory" (($presence.pid -eq $courier.Id) -and ($presence.exe -eq $courierExe)) $presence.exe
    Check "one start in its log" ((StartLog) -eq 1) ("starts=" + (StartLog))

    Get-ChildItem $install -File | ForEach-Object { $_.LastWriteTime = [datetime]"2000-01-01" }
    $engineBefore = (Get-Item $engineExe).LastWriteTimeUtc
    "  engine files aged to $($engineBefore.ToString('u')) so the publish rewrites every one of them"

    $o = Native powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\install.ps1") -InstallDir $install -SkipShim -Config Release -CourierTaskName $TaskName
    $o | ForEach-Object { "  | $_" }
    $engineAfter = (Get-Item $engineExe).LastWriteTimeUtc
    Check "install with the courier live: exit 0" ($lastExit -eq 0) ("exit=" + $lastExit)
    Check "the installer says it left the courier running" (($o -join "`n") -match "runs from its own directory - left running") "[0/4] line"
    Check "conductor.exe was rewritten" ($engineAfter -gt $engineBefore) ("{0} -> {1}" -f $engineBefore.ToString('u'), $engineAfter.ToString('u'))
    Check "the courier was not stopped" (-not $courier.HasExited) ("pid $($courier.Id) exited=" + $courier.HasExited)
    $presence2 = Get-Content $presencePath -Raw | ConvertFrom-Json
    Check "same courier in the presence record" ($presence2.pid -eq $courier.Id) ("presence pid " + $presence2.pid)
    Check "no restart in its log" ((StartLog) -eq 1) ("starts=" + (StartLog))
    "  courier.log:"
    Get-Content $logPath | ForEach-Object { "    " + $_ }

    # ---- 4 -------------------------------------------------------------------------------------
    Section "4. release preflight's courier line (the repo's fresh build, scratch task)"
    $o = Native $fresh release preflight -p $plan --courier-task $TaskName
    $o | ForEach-Object { "  | $_" }
    "  (preflight exit $lastExit - the other six lines judge a scratch repo with no git history and are not this checkpoint's)"
    # Joined and whitespace-collapsed: a redirected console is 80 columns wide and long lines wrap.
    $text = (($o -join " ") -replace "\s+", " ")
    Check "courier line green" (($text -match "the courier is installed, running and reachable") -and -not ($text -match "the courier would not survive")) "courier headline"
    Check "it reads the scratch courier" ($text -match ("running pid " + $courier.Id)) ("pid " + $courier.Id)
    Check "it says the engine installs without stopping it" ($text -match "publishes the engine without stopping it") "reinstall line"
    "  note: the 'persisted' half of the token line reads this machine's User/Machine environment (read-only); the scratch token itself is process-only."

    # ---- 5 -------------------------------------------------------------------------------------
    Section "5. -CourierOnly replaces the courier and never touches conductor.exe"
    $hashBefore = Sha256 $engineExe
    $timeBefore = (Get-Item $engineExe).LastWriteTimeUtc
    Get-ChildItem (Join-Path $install "courier") -File | ForEach-Object { try { $_.LastWriteTime = [datetime]"2000-01-01" } catch { } }
    $courierBefore = (Get-Item (Join-Path $install "courier\Conductor.Core.dll")).LastWriteTimeUtc
    $o = Native powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $RepoRoot "tools\install.ps1") -InstallDir $install -CourierOnly -NoCourierStart -Config Release -CourierTaskName $TaskName
    $o | ForEach-Object { "  | $_" }
    Check "-CourierOnly exit 0" ($lastExit -eq 0) ("exit=" + $lastExit)
    $courier.WaitForExit(10000) | Out-Null
    Check "the live courier was stopped" $courier.HasExited ("pid $($courier.Id) exited=" + $courier.HasExited)
    $courierAfter = (Get-Item (Join-Path $install "courier\Conductor.Core.dll")).LastWriteTimeUtc
    Check "the courier's directory was republished" ($courierAfter -gt $courierBefore) ("{0} -> {1}" -f $courierBefore.ToString('u'), $courierAfter.ToString('u'))
    $hashAfter = Sha256 $engineExe
    $timeAfter = (Get-Item $engineExe).LastWriteTimeUtc
    Check "conductor.exe untouched (hash)" ($hashAfter -eq $hashBefore) $hashAfter
    Check "conductor.exe untouched (timestamp)" ($timeAfter -eq $timeBefore) $timeAfter.ToString('u')
    $xml = TaskXml
    $cmd = [regex]::Match($xml, "<Command>([^<]*)</Command>").Groups[1].Value
    Check "task re-registered on the courier's own binary" ($cmd -eq $courierExe) $cmd

    $courier = [System.Diagnostics.Process]::Start($psi)
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $presencePath) {
            $p3 = Get-Content $presencePath -Raw | ConvertFrom-Json
            if ($p3.pid -eq $courier.Id) { break }
        }
    }
    Check "the replaced courier starts from the new binary" (($p3.pid -eq $courier.Id) -and ($p3.exe -eq $courierExe)) ("pid " + $p3.pid)
    Check "its log shows the second start, and only that" ((StartLog) -eq 2) ("starts=" + (StartLog))
}
finally {
    Section "cleanup"
    if ($courier -and -not $courier.HasExited) { $courier.Kill(); $courier.WaitForExit(10000) | Out-Null; "  stopped the rig's courier pid $($courier.Id)" }
    Get-CimInstance Win32_Process -Filter "Name = 'conductor-courier.exe'" |
        Where-Object { $_.ExecutablePath -like "$OutDir\*" } |
        ForEach-Object { "  stopping a scratch courier left behind: pid $($_.ProcessId)"; Stop-Process -Id $_.ProcessId -Force }
    if (Test-Path $engineExe) {
        $o = Native $engineExe courier uninstall --task-name $TaskName
        $o | ForEach-Object { "  | $_" }
    }
    [void](Native schtasks.exe /Query /TN $TaskName)
    Check "scratch task removed" ($lastExit -ne 0) ("query exit=" + $lastExit)
    Remove-Item Env:\CONDUCTOR_STATE_HOME -ErrorAction SilentlyContinue
    Remove-Item Env:\CONDUCTOR_COURIER_PORT -ErrorAction SilentlyContinue
    Remove-Item Env:\CONDUCTOR_TELEGRAM_TOKEN -ErrorAction SilentlyContinue
}

Section "summary"
$results
$failed = @($results | Where-Object { $_ -like "FAIL*" }).Count
"checks: $($results.Count), failed: $failed"
exit $failed
