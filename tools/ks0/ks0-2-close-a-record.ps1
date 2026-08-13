<#
.SYNOPSIS
  KS0.2 - close a run record whose engine never closed it, rehearsed on a COPY first.

.DESCRIPTION
  This replaces the hand-SQL procedure that .conductor/WATCH-HANDOFF.md used to carry. The Karvan
  run's record was corrected by hand-editing two databases; that repair took no backup, checked no
  liveness, left no provenance, and could not be tested. `conductor run close` does all four, and
  this script is the operator procedure around it.

  Run WITHOUT -Apply first. That copies the store into a scratch home under the temp directory and
  drives the real verb against the copy, so the exact command you are about to run has been seen to
  work before it touches anything you care about.

  Two things this deliberately does NOT do:

    * It never copies catalogue.json into the rehearsal rig. A copied catalogue holds ABSOLUTE paths
      back into the original state home, so a rig built that way reaches around behind you and edits
      the very stores you were protecting - measured at KS0.1, one near-miss. Without it, the survey
      enumerates the rig's own directories and can only see the copy.

    * It never closes a record whose store a live engine is using. The verb refuses that itself; the
      script does not offer a way to argue with it. On a machine that runs more than one conductor
      at a time - this one does - that refusal is the only thing standing between a maintenance verb
      and somebody else's run.

.PARAMETER RunId
  Run id, or any unambiguous prefix of one.

.PARAMETER Reason
  Why the record is being closed. Goes into the run's event spine verbatim.

.PARAMETER Apply
  Close the record for real, in this machine's state home. Without it, only the copy is touched.

.EXAMPLE
  ./ks0-2-close-a-record.ps1 -RunId 0031daaa -Reason "engine exited without closing the record"
  ./ks0-2-close-a-record.ps1 -RunId 0031daaa -Reason "engine exited without closing the record" -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$RunId,
    [Parameter(Mandatory = $true)][string]$Reason,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'

# The engine under test is the working tree's, never the conductor on PATH: that one is the
# published build, and on a self-hosting repo it is very often the engine driving the session that
# is running this script.
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
function Conductor {
    param([string[]]$CliArgs)
    & dotnet run --project (Join-Path $repoRoot 'src/Conductor') -- @CliArgs
}

# Bug #20: CONDUCTOR_PLAN beats the working directory, and a scratch rig that inherits it edits the
# wrong plan. Nothing here needs a plan at all.
$env:CONDUCTOR_PLAN = $null

$stateHome = Join-Path $env:LOCALAPPDATA 'conductor\runs'
if (-not (Test-Path $stateHome)) { throw "no state home at $stateHome" }

# --------------------------------------------------------------------------- 1. find the store

Write-Host "== the record, as it stands ==" -ForegroundColor Cyan
Conductor @('run', 'close', $RunId, '--dry-run')
if ($LASTEXITCODE -ne 0) {
    Write-Host "the verb will not close this record. Nothing has been written." -ForegroundColor Yellow
    exit $LASTEXITCODE
}

# The dry run named the store; find it again here so the copy is of the right directory. A run id
# prefix is enough for the verb but not for a file copy, so match on the store that holds the run.
$store = $null
foreach ($dir in Get-ChildItem $stateHome -Directory) {
    $db = Join-Path $dir.FullName 'run.db'
    if (-not (Test-Path $db)) { continue }
    $probe = Conductor @('run', 'close', $RunId, '--home', $stateHome, '--dry-run', '--json')
    if ($probe -match [regex]::Escape($dir.Name)) { $store = $dir; break }
}
if ($null -eq $store) { throw "could not work out which store holds $RunId" }

# ------------------------------------------------------------------- 2. rehearse, on a real copy

$rig = Join-Path $env:TEMP ('ks02-rehearsal-' + [guid]::NewGuid().ToString('N').Substring(0, 10))
$rigStore = Join-Path (Join-Path $rig 'runs') $store.Name
New-Item -ItemType Directory -Force -Path $rigStore | Out-Null
Get-ChildItem $store.FullName -File | Where-Object { $_.Name -like 'run.db*' } |
    Copy-Item -Destination $rigStore

Write-Host ""
Write-Host "== rehearsal, against a copy in $rig ==" -ForegroundColor Cyan
Conductor @('run', 'close', $RunId, '--home', (Join-Path $rig 'runs'), '--reason', $Reason)
$rehearsed = $LASTEXITCODE
if ($rehearsed -ne 0) {
    Write-Host "the rehearsal failed; the real store has not been touched." -ForegroundColor Red
    exit $rehearsed
}

if (-not $Apply) {
    Write-Host ""
    Write-Host "rehearsal only. Re-run with -Apply to close the real record." -ForegroundColor Yellow
    Write-Host "the copy is at $rig - delete it when you are done reading it." -ForegroundColor DarkGray
    exit 0
}

# ------------------------------------------------------------------------------ 3. back up, then

$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$backup = Join-Path $env:LOCALAPPDATA ("conductor\backups\ks0-2-close-$stamp\" + $store.Name)
New-Item -ItemType Directory -Force -Path $backup | Out-Null
Get-ChildItem $store.FullName -File | Copy-Item -Destination $backup
Write-Host ""
Write-Host "== backed up to $backup ==" -ForegroundColor Green

Write-Host "== closing the real record ==" -ForegroundColor Cyan
Conductor @('run', 'close', $RunId, '--reason', $Reason)
exit $LASTEXITCODE
