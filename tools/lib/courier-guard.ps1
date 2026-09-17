<#
.SYNOPSIS
  DV4.2 / findings 6.4 - stop the courier before a reinstall overwrites the exe it is holding open,
  and start it again afterwards.

.DESCRIPTION
  The courier is the one process on this machine designed to outlive everything else. That is
  exactly what collides with the install discipline:

    * a running courier holds the published conductor.exe open, so `dotnet publish -o <installdir>`
      fails on a file lock, and
    * worse, a courier that is NOT restarted keeps running yesterday's engine indefinitely - it was
      built to survive, so nothing else will restart it for you.

  So tools/install.ps1 dot-sources this file and brackets the publish with Stop-ConductorCourier /
  Start-ConductorCourier. Both are no-ops when no courier is installed, which is every machine that
  has not run `conductor courier install`.

  It talks to schtasks.exe and to the courier's own presence record (courier.run.json in the state
  home) - never to conductor.exe, because the whole point is that the binary is about to be replaced.

  ASCII only (Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI).
#>

$script:CourierDefaultTaskName = "Conductor Courier"

function Get-ConductorStateHome {
    if ($env:CONDUCTOR_STATE_HOME) { return $env:CONDUCTOR_STATE_HOME }
    return (Join-Path $env:LOCALAPPDATA "conductor")
}

# What the running courier said about itself: pid, protocol, engine, exe, task. Null when there is
# no record, when it cannot be parsed, or when the process it names is gone (a courier killed with
# the machine leaves its file behind, and a stale claim must not stall a reinstall).
function Get-ConductorCourierPresence {
    param([string]$StateHome)
    if (-not $StateHome) { $StateHome = Get-ConductorStateHome }
    $file = Join-Path (Join-Path $StateHome "courier") "courier.run.json"
    if (-not (Test-Path $file)) { return $null }
    try {
        $presence = Get-Content -Raw -Path $file | ConvertFrom-Json
    } catch {
        return $null
    }
    if (-not $presence.pid) { return $null }
    $proc = Get-Process -Id $presence.pid -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }
    return $presence
}

# schtasks writes "ERROR: The system cannot find the file specified." to STDERR for a task name
# it does not know - and install.ps1 runs with $ErrorActionPreference = "Stop", under which a
# native command's stderr is a TERMINATING error in Windows PowerShell. Unguarded, that crashed
# the installer on every machine that has NOT installed a courier, which is every machine today.
# So each of these functions neutralises it in its OWN scope and reads $LASTEXITCODE instead.
function Invoke-CourierSchtasks {
    $ErrorActionPreference = "Continue"
    & schtasks.exe @args 2>&1 | Out-Null
    return $LASTEXITCODE
}

function Test-ConductorCourierTask {
    param([string]$TaskName = $script:CourierDefaultTaskName)
    return ((Invoke-CourierSchtasks /Query /TN "$TaskName" /FO CSV /NH) -eq 0)
}

# PK1.2 / D1: where the live courier runs from, relative to an install, decides what a reinstall
# must do to it. Measured on a scratch install: a courier running from the engine's directory
# locks the shared dlls and the engine publish fails; one running from <install>\courier does not.
#   none      - nothing is polling
#   own       - conductor-courier.exe in <install>\courier: the engine installs around it
#   engine    - it holds the engine's files: an engine binary running `courier run` (before D1),
#               a courier beside the engine, or a courier whose parent is this install's engine alias
#   elsewhere - a courier from another directory; this install's files are not its files
function Get-ConductorCourierShape {
    param([Parameter(Mandatory = $true)][string]$InstallDir, [string]$StateHome)
    $presence = Get-ConductorCourierPresence -StateHome $StateHome
    $shape = "none"
    $holder = $null
    $livePid = $null
    $liveExe = $null
    if ($presence) {
        $livePid = $presence.pid
        $liveExe = [string]$presence.exe
        $root = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
        $own = $root + 'courier\'
        if ($liveExe -and $liveExe.StartsWith($own, [StringComparison]::OrdinalIgnoreCase)) {
            $shape = "own"
            # The alias case: `<install>\conductor.exe courier run` starts the courier from its own
            # directory but stays alive itself, holding the engine open.
            $parent = Get-ConductorCourierParent -ProcessId $livePid
            if ($parent -and $parent.ExecutablePath -and
                $parent.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and
                -not $parent.ExecutablePath.StartsWith($own, [StringComparison]::OrdinalIgnoreCase)) {
                $shape = "engine"
                $holder = $parent.ProcessId
            }
        } elseif ($liveExe -and $liveExe.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
            $shape = "engine"
        } else {
            $shape = "elsewhere"
        }
    }
    return [pscustomobject]@{ Shape = $shape; Pid = $livePid; Exe = $liveExe; HolderPid = $holder }
}

function Get-ConductorCourierParent {
    param([int]$ProcessId)
    $proc = Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f $ProcessId) -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }
    return (Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f $proc.ParentProcessId) -ErrorAction SilentlyContinue)
}

# Ends a courier the scheduler did not start (a hand-started one, which /End cannot reach) - but
# only when its binary lives under $InstallDir, and its engine alias parent with it. Never anything
# else. Safe to end: the daemon writes its offset only after a delivery is handled, so a killed
# courier re-receives what was in flight and files it exactly once.
function Stop-ConductorCourierProcess {
    param([int]$ProcessId, [string]$InstallDir)
    $root = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
    $proc = Get-CimInstance Win32_Process -Filter ("ProcessId = {0}" -f $ProcessId) -ErrorAction SilentlyContinue
    if (-not ($proc -and $proc.ExecutablePath -and $proc.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase))) {
        return $false
    }
    $parent = Get-ConductorCourierParent -ProcessId $ProcessId
    Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
    if ($parent -and $parent.ExecutablePath -and $parent.ExecutablePath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $parent.ProcessId -Force -ErrorAction SilentlyContinue
    }
    return $true
}

# Stops the courier and reports whether it WAS running, so the caller knows whether to start it
# again. A courier that was stopped by hand before the reinstall stays stopped afterwards.
# PK1.2: with -InstallDir, a courier from that install which /End did not reach is ended by pid.
function Stop-ConductorCourier {
    param(
        [string]$TaskName = $script:CourierDefaultTaskName,
        [string]$StateHome,
        [int]$TimeoutSeconds = 20,
        [string]$InstallDir
    )

    $registered = Test-ConductorCourierTask -TaskName $TaskName
    $presence = Get-ConductorCourierPresence -StateHome $StateHome
    $livePid = $null
    $liveExe = $null
    if ($presence) { $livePid = $presence.pid; $liveExe = $presence.exe }
    $result = [pscustomobject]@{
        TaskName   = $TaskName
        Registered = $registered
        WasRunning = [bool]$presence
        Pid        = $livePid
        Exe        = $liveExe
        Stopped    = $false
    }

    if (-not $registered -and -not $presence) { return $result }

    if ($registered) { Invoke-CourierSchtasks /End /TN "$TaskName" | Out-Null }

    # Wait for the process to actually go: /End returns as soon as the scheduler has asked.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $forceAt = (Get-Date).AddSeconds([Math]::Min(5, $TimeoutSeconds / 2))
    $forced = $false
    while ((Get-Date) -lt $deadline) {
        $still = Get-ConductorCourierPresence -StateHome $StateHome
        if (-not $still) {
            $result.Stopped = $true
            return $result
        }
        if ($InstallDir -and -not $forced -and ((Get-Date) -gt $forceAt)) {
            [void](Stop-ConductorCourierProcess -ProcessId $still.pid -InstallDir $InstallDir)
            $forced = $true
        }
        Start-Sleep -Milliseconds 500
    }

    $result.Stopped = $false
    return $result
}

# Starts the task now rather than waiting for the next logon. Returns $true when the scheduler took
# the request; a courier that refuses to start says so through `conductor courier status`.
function Start-ConductorCourier {
    param([string]$TaskName = $script:CourierDefaultTaskName)
    if (-not (Test-ConductorCourierTask -TaskName $TaskName)) { return $false }
    return ((Invoke-CourierSchtasks /Run /TN "$TaskName") -eq 0)
}
