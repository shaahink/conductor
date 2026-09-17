<#
.SYNOPSIS
  Build the Conductor engine, its courier and the Go face, and install a global `conductor` command.

.DESCRIPTION
  One command turns "src/Conductor/bin/.../conductor.exe" into just `conductor`, callable from any
  terminal. It:
    1. publishes the C# engine (Release by default) to an install dir,
    2. publishes the courier (conductor-courier.exe) to its OWN directory, <install>\courier,
    3. builds the Go face (conductor-face.exe) RIGHT NEXT TO the engine, where FaceLauncher looks
       for it first, so `conductor run` auto-spawns the TUI with no extra flags,
    4. drops a `conductor` shim on your PATH (scoop's shim dir if present, else your user PATH).

  Re-run this after code changes to update the installed command. This is "cut a local release":
  the installed `conductor` is a snapshot, independent of the repo's Debug build.

  SC8.1: it now says which version it replaced and which version it installed (before -> after).
  Publishing in silence was the reason "rebuild before trusting it" had to be taken on faith: the
  operator had no way to confirm the rebuild took, and a stale engine looks exactly like a fresh one.

  PK1.2 / D1: the courier is its own executable in its own directory, and an engine install NO
  LONGER STOPS IT. Measured on a scratch install: a courier running beside the engine locked the
  shared dlls and the engine publish failed; one running from <install>\courier did not. So:
    * a courier in its own directory is left running on the binary it has; its directory is not
      republished (it is locked) and `-CourierOnly` is how it is replaced;
    * a courier still holding the engine's files - an engine running `courier run` from before D1,
      or one beside the engine - is stopped ONCE, moved to its own directory, and started there;
    * a registered courier task is re-registered on <install>\courier\conductor-courier.exe
      whenever that directory is published.

  -CourierOnly publishes conductor-courier.exe alone, re-registers the scheduled task on it and
  restarts it. It never touches conductor.exe, so the engine driving a live run stays installed and
  running. The task is registered by the engine built from THIS tree (src/Conductor/bin), because
  the installed engine may predate D1 and would register the wrong arguments.
  -NoCourierStart leaves the courier stopped afterwards - for a rig, whose scratch courier a
  logon task cannot start without the machine's real environment.

  -SkipShim leaves the PATH shim alone (publish only). Use it whenever you are installing to a
  scratch directory: without it, step 4 would repoint the global `conductor` command at the scratch
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
    [string]$CourierTaskName = "Conductor Courier",
    [switch]$CourierOnly,
    [switch]$NoCourierStart
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot   # tools/ -> repo root

# DV4.2 / findings 6.4, PK1.2 / D1: where the live courier runs from decides whether an install has
# to stop it at all.
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

function Publish-Courier {
    Write-Host ("  publishing the courier to {0}" -f $courierDir)
    & dotnet publish (Join-Path $repo "src\Conductor.Courier\Conductor.Courier.csproj") -c $Config -o $courierDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "courier publish failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path $courierExe)) { throw "expected $courierExe after publish, not found" }
}

# `courier install` through the engine given - the task XML has one author, CourierTask.cs.
function Register-CourierTask {
    param([string]$Registrar)
    $ErrorActionPreference = "Continue"
    & $Registrar courier install --exe $courierExe --task-name $CourierTaskName --no-start
    if ($LASTEXITCODE -ne 0) { throw "registering the courier task failed (exit $LASTEXITCODE)" }
}

function Start-CourierAgain {
    if ($NoCourierStart) {
        Write-Host "  courier not started (-NoCourierStart)" -ForegroundColor Yellow
        return
    }
    if (Start-ConductorCourier -TaskName $CourierTaskName) {
        Write-Host ("  courier started ({0}) from {1}" -f $CourierTaskName, $courierExe) -ForegroundColor Green
    } else {
        Write-Host ("  WARNING: the courier did not start. Start it with: conductor courier restart") -ForegroundColor Yellow
    }
}

$exe = Join-Path $InstallDir "conductor.exe"
$courierDir = Join-Path $InstallDir "courier"
$courierExe = Join-Path $courierDir "conductor-courier.exe"

# ---- -CourierOnly: the courier, and nothing else --------------------------------------------------
if ($CourierOnly) {
    Write-Host "conductor installer - courier only" -ForegroundColor Cyan
    Write-Host ("  install: {0}  (conductor.exe is not touched)" -f $InstallDir)
    Write-Host ("  task:    {0}" -f $CourierTaskName)
    Write-Host ""

    Write-Host "[1/3] building the engine that registers the task (this tree, not the install)..." -ForegroundColor Cyan
    & dotnet build (Join-Path $repo "src\Conductor\Conductor.csproj") -c $Config --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "engine build failed (exit $LASTEXITCODE)" }
    $registrar = Join-Path $repo ("src\Conductor\bin\{0}\net10.0\conductor.exe" -f $Config)

    Write-Host "[2/3] replacing the courier..." -ForegroundColor Cyan
    $shape = Get-ConductorCourierShape -InstallDir $InstallDir
    $stopped = Stop-ConductorCourier -TaskName $CourierTaskName -InstallDir $InstallDir
    if ($stopped.WasRunning) {
        if (-not $stopped.Stopped) {
            throw ("the courier (pid {0}, {1}) is still running; stop it with 'conductor courier stop' and re-run" -f $stopped.Pid, $stopped.Exe)
        }
        Write-Host ("  stopped the courier (pid {0}, {1})" -f $stopped.Pid, $stopped.Exe) -ForegroundColor Cyan
    }
    if ($shape.Shape -eq "elsewhere") {
        Write-Host ("  note: the courier that was running came from {0}, outside this install" -f $shape.Exe) -ForegroundColor Yellow
    }
    Publish-Courier

    Write-Host "[3/3] registering the task on the courier and starting it..." -ForegroundColor Cyan
    Register-CourierTask -Registrar $registrar
    Start-CourierAgain
    Write-Host ""
    Write-Host "Done." -ForegroundColor Green
    return
}

# ---- the full install --------------------------------------------------------------------------
$before = Get-ConductorVersion $exe

Write-Host "conductor installer" -ForegroundColor Cyan
Write-Host ("  repo:    {0}" -f $repo)
Write-Host ("  install: {0}" -f $InstallDir)
Write-Host ("  config:  {0}" -f $Config)
Write-Host ("  current: {0}" -f $before)
Write-Host ""

# 0. the courier ------------------------------------------------------------------------------------
$shape = Get-ConductorCourierShape -InstallDir $InstallDir
$publishCourier = $true
$startCourier = $false
switch ($shape.Shape) {
    "own" {
        # D1: the whole point. It holds only its own directory, so the engine installs around it.
        $publishCourier = $false
        Write-Host ("[0/4] the courier (pid {0}) runs from its own directory - left running" -f $shape.Pid) -ForegroundColor Cyan
    }
    "engine" {
        # Before D1, or beside the engine: it holds the files this install replaces. Moved once.
        $stopped = Stop-ConductorCourier -TaskName $CourierTaskName -InstallDir $InstallDir
        if (-not $stopped.Stopped) {
            throw ("the courier (pid {0}) is still running and holds {1} open; stop it with 'conductor courier stop' and re-run" -f $shape.Pid, $shape.Exe)
        }
        $startCourier = $true
        Write-Host ("[0/4] stopped the courier (pid {0}, {1}) - it held the engine's files; it moves to {2} and starts there" -f $shape.Pid, $shape.Exe, $courierDir) -ForegroundColor Cyan
    }
    "elsewhere" {
        Write-Host ("[0/4] a courier runs from {0}, outside this install - left alone" -f $shape.Exe) -ForegroundColor Cyan
    }
}

# 1. engine ---------------------------------------------------------------------------------------
Write-Host "[1/4] publishing engine..." -ForegroundColor Cyan
& dotnet publish (Join-Path $repo "src\Conductor\Conductor.csproj") -c $Config -o $InstallDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "engine publish failed (exit $LASTEXITCODE)" }
if (-not (Test-Path $exe)) { throw "expected $exe after publish, not found" }
$after = Get-ConductorVersion $exe

# 2. courier (its own directory) ------------------------------------------------------------------
Write-Host "[2/4] courier..." -ForegroundColor Cyan
if ($publishCourier) {
    Publish-Courier
    if ($startCourier -or (Test-ConductorCourierTask -TaskName $CourierTaskName)) {
        Register-CourierTask -Registrar $exe
    }
    if ($startCourier) { Start-CourierAgain }
} else {
    Write-Host ("  not republished: the live courier holds {0}. It keeps running its binary; the run's protocol check refuses it if it is too old, and 'tools/install.ps1 -CourierOnly' replaces it (one restart)." -f $courierDir) -ForegroundColor Yellow
}

# 3. face (next to the engine, so ResolveEntrypoint's first candidate hits) -----------------------
Write-Host "[3/4] building Go face..." -ForegroundColor Cyan
Push-Location (Join-Path $repo "face-go")
try {
    & go build -o (Join-Path $InstallDir "conductor-face.exe") ./cmd/conductor-face/
    if ($LASTEXITCODE -ne 0) { throw "face build failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }

# 4. shim on PATH ---------------------------------------------------------------------------------
$scoopShims = Join-Path $env:USERPROFILE "scoop\shims"
if ($SkipShim) {
    Write-Host "[4/4] skipping PATH shim (-SkipShim)..." -ForegroundColor Cyan
    Write-Host ("  the global 'conductor' command was NOT changed; this build lives only in {0}" -f $InstallDir)
    $ready = $false
} elseif (Test-Path $scoopShims) {
    Write-Host "[4/4] installing 'conductor' on PATH..." -ForegroundColor Cyan
    # A .cmd shim in scoop's shim dir (already on PATH) works in PowerShell and cmd, no restart.
    $shim = Join-Path $scoopShims "conductor.cmd"
    Set-Content -Path $shim -Value ('@"{0}" %*' -f $exe) -Encoding ascii
    Write-Host ("  shim: {0} -> {1}" -f $shim, $exe) -ForegroundColor Green
    $ready = $true
} else {
    Write-Host "[4/4] installing 'conductor' on PATH..." -ForegroundColor Cyan
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
