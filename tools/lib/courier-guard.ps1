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

function Test-ConductorCourierTask {
    param([string]$TaskName = $script:CourierDefaultTaskName)
    & schtasks.exe /Query /TN "$TaskName" /FO CSV /NH > $null 2>&1
    return ($LASTEXITCODE -eq 0)
}

# Stops the courier and reports whether it WAS running, so the caller knows whether to start it
# again. A courier that was stopped by hand before the reinstall stays stopped afterwards.
function Stop-ConductorCourier {
    param(
        [string]$TaskName = $script:CourierDefaultTaskName,
        [string]$StateHome,
        [int]$TimeoutSeconds = 20
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

    if ($registered) { & schtasks.exe /End /TN "$TaskName" > $null 2>&1 }

    # Wait for the process to actually go: /End returns as soon as the scheduler has asked.
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-ConductorCourierPresence -StateHome $StateHome)) {
            $result.Stopped = $true
            return $result
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
    & schtasks.exe /Run /TN "$TaskName" > $null 2>&1
    return ($LASTEXITCODE -eq 0)
}
