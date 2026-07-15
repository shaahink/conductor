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

  NOTE: the self-referential Maestro plan (plans/conductor-maestro.plan.json) is meant to be driven
  by the binary built FROM THE BRANCH under test, so for THAT one run use the repo's fresh build.
  For everything else -- doctor, status, init, driving other plans -- the installed command is what
  you want.

  ASCII only (Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\conductor"),
    [ValidateSet("Release", "Debug")][string]$Config = "Release"
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot   # tools/ -> repo root

Write-Host "conductor installer" -ForegroundColor Cyan
Write-Host ("  repo:    {0}" -f $repo)
Write-Host ("  install: {0}" -f $InstallDir)
Write-Host ("  config:  {0}" -f $Config)
Write-Host ""

# 1. engine ---------------------------------------------------------------------------------------
Write-Host "[1/3] publishing engine..." -ForegroundColor Cyan
& dotnet publish (Join-Path $repo "src\Conductor\Conductor.csproj") -c $Config -o $InstallDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "engine publish failed (exit $LASTEXITCODE)" }
$exe = Join-Path $InstallDir "conductor.exe"
if (-not (Test-Path $exe)) { throw "expected $exe after publish, not found" }

# 2. face (next to the engine, so ResolveEntrypoint's first candidate hits) -----------------------
Write-Host "[2/3] building Go face..." -ForegroundColor Cyan
Push-Location (Join-Path $repo "face-go")
try {
    & go build -o (Join-Path $InstallDir "conductor-face.exe") ./cmd/conductor-face/
    if ($LASTEXITCODE -ne 0) { throw "face build failed (exit $LASTEXITCODE)" }
} finally { Pop-Location }

# 3. shim on PATH ---------------------------------------------------------------------------------
Write-Host "[3/3] installing 'conductor' on PATH..." -ForegroundColor Cyan
$scoopShims = Join-Path $env:USERPROFILE "scoop\shims"
if (Test-Path $scoopShims) {
    # A .cmd shim in scoop's shim dir (already on PATH) works in PowerShell and cmd, no restart.
    $shim = Join-Path $scoopShims "conductor.cmd"
    Set-Content -Path $shim -Value ('@"{0}" %*' -f $exe) -Encoding ascii
    Write-Host ("  shim: {0} -> {1}" -f $shim, $exe) -ForegroundColor Green
    $ready = $true
} else {
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
if ($ready) {
    Write-Host "Try it now:  " -NoNewline; Write-Host "conductor doctor -p plans\conductor-maestro.plan.json" -ForegroundColor Cyan
} else {
    Write-Host "In a new terminal:  " -NoNewline; Write-Host "conductor doctor" -ForegroundColor Cyan
}
