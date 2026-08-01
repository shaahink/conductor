# Session 36 (SF6 fix) - full engine suite. Scoped filters proved the three reported reds green, but
# this session compressed prose in ToolContract.cs, which every composed prompt carries and several
# suites assert sentences from, so the whole suite is the honest check.
# A script rather than an inline command because `conductor bg` hands the command to cmd.exe, which
# eats the `|` an xunit OR-filter needs.
$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $root) { $root = (Get-Location).Path }
Set-Location $root
dotnet test Conductor.slnx
exit $LASTEXITCODE
