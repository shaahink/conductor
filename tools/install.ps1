<#
.SYNOPSIS
  Build the Conductor engine + the Go face and install a global `conductor` command.

.DESCRIPTION
  One command turns "src/Conductor/bin/.../conductor.exe" into just `conductor`, callable from any
  terminal. It:
    1. publishes the C# engine (Release by default) to an install dir,
    2. builds the Go face (conductor-face.exe) RIGHT NEXT TO the engine, where FaceLauncher looks
       for it first, so `conductor run` auto-spawns the TUI with no extra flags,
    3. drops a `conductor` shim on your PATH (scoop's shim dir if present, else your user PATH).

  Re-run this after code changes to update the installed command. This is "cut a local release":
  the installed `conductor` is a snapshot, independent of the repo's Debug build.

  SC8.1: it now says which version it replaced and which version it installed (before -> after).
  Publishing in silence was the reason "rebuild before trusting it" had to be taken on faith: the
  operator had no way to confirm the rebuild took, and a stale engine looks exactly like a fresh one.

  -SkipShim leaves the PATH shim alone (publish only). Use it whenever you are installing to a
  scratch directory: without it, step 3 would repoint the global `conductor` command at the scratch
  build, which is how you accidentally swap the engine that is driving a live run.

  NOTE: the self-referential Maestro plan (plans/conductor-maestro.plan.json) is meant to be driven
  by the binary built FROM THE BRANCH under test, so for THAT one run use the repo's fresh build.
  For everything else -- doctor, status, init, driving other plans -- the installed command is what
  you want.

  ASCII only (Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\conductor"),
    [ValidateSet("Release", "Debug")][string]$Config = "Release",
    [switch]$SkipShim,
    [string]$CourierTaskName = "Conductor Courier"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot   # tools/ -> repo root

# DV4.2 / findings 6.4: a running courier holds the published exe open, so the publish below fails
# on a file lock - and a courier that is not restarted keeps running yesterday's engine for as long
# as the machine stays up, precisely because it is built to outlive everything else.
. (Join-Path $PSScriptRoot "lib\courier-guard.ps1")

# Ask a conductor binary what it is. Three layers, because the answer has to survive the case that
# matters most: upgrading a binary that predates the `version` verb entirely.
#   1. `conductor version --short` - authoritative, the engine's own stamp.
#   2. the exe's ProductVersion resource - what an SC8-less build can still tell us, without running it.
#   3. give up honestly rather than print something invented.
function Get-ConductorVersion {
    param([string]$ExePath)
    if (-not (Test-Path $ExePath)) { return "(none installed)" }
    try {
        $out = & $ExePath version --short 2>$null
        if (($LASTEXITCODE -eq 0) -and $out) {
            return ([string](@($out)[0])).Trim()
        }
    } catch { }
    try {
        $pv = (Get-Item $ExePath).VersionInfo.ProductVersion
        if ($pv) { return ("{0} (no version verb - predates SC8)" -f $pv.Trim()) }
    } catch { }
    return "(unknown)"
}

$exe = Join-Path $InstallDir "conductor.exe"
$before = Get-ConductorVersion $exe

Write-Host "conductor installer" -ForegroundColor Cyan
Write-Host ("  repo:    {0}" -f $repo)
Write-Host ("  install: {0}" -f $InstallDir)
Write-Host ("  config:  {0}" -f $Config)
Write-Host ("  current: {0}" -f $before)
Write-Host ""

# 0. the courier ------------------------------------------------------------------------------------
$courier = Stop-ConductorCourier -TaskName $CourierTaskName
if ($courier.WasRunning) {
    if (-not $courier.Stopped) {
        throw ("the courier (pid {0}) is still running and holds {1} open; stop it with 'conductor courier stop' and re-run" -f $courier.Pid, $courier.Exe)
    }
    Write-Host ("[0/3] stopped the courier ({0}, pid {1}) - it holds the engine open" -f $courier.TaskName, $courier.Pid) -ForegroundColor Cyan
}

# 1. engine ---------------------------------------------------------------------------------------
Write-Host "[1/3] publishing engine..." -ForegroundColor Cyan
& dotnet publish (Join-Path $repo "src\Conductor\Conductor.csproj") -c $Config -o $InstallDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "engine publish failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $exe)) { throw "expected $exe after publish, not found" }
$after = Get-ConductorVersion $exe

# The courier goes back up on the NEW engine. A courier left down is a bot that stops answering,
# and it is the one thing here nothing else will restart.
if ($courier.WasRunning) {
    if (Start-ConductorCourier -TaskName $CourierTaskName) {
        Write-Host ("  courier restarted ({0}) - now running {1}" -f $courier.TaskName, $after) -ForegroundColor Green
    } else {
        Write-Host ("  WARNING: the courier did not restart. Start it with: conductor courier restart") -ForegroundColor Yellow
    }
}

# 2. face (next to the engine, so ResolveEntrypoint's first candidate hits) -----------------------
Write-Host "[2/3] building Go face..." -ForegroundColor Cyan
Push-Location (Join-Path $repo "face-go")
try {
    & go build -o (Join-Path $InstallDir "conductor-face.exe") ./cmd/conductor-face/
    if ($LASTEXITCODE -ne 0) { throw "face build failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }

# 3. shim on PATH ---------------------------------------------------------------------------------
$scoopShims = Join-Path $env:USERPROFILE "scoop\shims"
if ($SkipShim) {
    Write-Host "[3/3] skipping PATH shim (-SkipShim)..." -ForegroundColor Cyan
    Write-Host ("  the global 'conductor' command was NOT changed; this build lives only in {0}" -f $InstallDir)
    $ready = $false
} elseif (Test-Path $scoopShims) {
    Write-Host "[3/3] installing 'conductor' on PATH..." -ForegroundColor Cyan
    # A .cmd shim in scoop's shim dir (already on PATH) works in PowerShell and cmd, no restart.
    $shim = Join-Path $scoopShims "conductor.cmd"
    Set-Content -Path $shim -Value ('@"{0}" %*' -f $exe) -Encoding ascii
    Write-Host ("  shim: {0} -> {1}" -f $shim, $exe) -ForegroundColor Green
    $ready = $true
} else {
    Write-Host "[3/3] installing 'conductor' on PATH..." -ForegroundColor Cyan
    $userPath = [Environment]::GetEnvironmentVariable("Path", "User")
    if ($userPath -notlike "*$InstallDir*") {
        [Environment]::SetEnvironmentVariable("Path", ($userPath.TrimEnd(';') + ";" + $InstallDir), "User")
        Write-Host ("  added to your user PATH: {0}" -f $InstallDir) -ForegroundColor Green
        Write-Host "  (open a NEW terminal for it to take effect)" -ForegroundColor Yellow
    } else {
        Write-Host ("  already on PATH: {0}" -f $InstallDir) -ForegroundColor Green
    }
    $ready = $false
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
# The whole point of SC8.1's installer change: the operator can see the swap took, or see that it
# did not, without running anything else.
Write-Host ("  version: {0}  ->  {1}" -f $before, $after) -ForegroundColor $(if ($before -eq $after) { "Yellow" } else { "Green" })
if ($before -eq $after) {
    Write-Host "  (unchanged - same commit, clean tree, and nothing to rebuild)" -ForegroundColor Yellow
}
if ($ready) {
    Write-Host "Try it now:  " -NoNewline; Write-Host "conductor doctor -p plans\conductor-maestro.plan.json" -ForegroundColor Cyan
} else {
    Write-Host "In a new terminal:  " -NoNewline; Write-Host "conductor doctor" -ForegroundColor Cyan
}
