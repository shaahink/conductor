# Reclaims the per-test scratch directories the .NET test fixtures leak into %TEMP%.
# Every ControlPlaneServer/Telegram/SC1/harness test class creates one in its constructor
# (conductor-<suffix>-<guid>) and best-effort deletes it in Dispose; a killed or crashed test host
# never runs Dispose, so they accumulate. Session 21 found 24,324 of them and drive C: at 0 bytes
# free, which fails 18 tests with "There is not enough space on the disk" and reads like a
# regression.
#
# Only directories older than the cutoff are touched, so a test host running RIGHT NOW - in this
# repo or in the other conductor run sharing this machine - keeps its live scratch dir. Nothing
# outside %TEMP% and nothing not matching conductor-* is looked at; %TEMP%\sarban-proofs (the
# live-run proof rigs) does not match the filter.
param([int]$OlderThanHours = 2)

$cut = (Get-Date).AddHours(-$OlderThanHours)
$deleted = 0
$failed = 0
Get-ChildItem $env:TEMP -Directory -Filter 'conductor-*' -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -lt $cut } |
    ForEach-Object {
        try { Remove-Item $_.FullName -Recurse -Force -ErrorAction Stop; $deleted++ }
        catch { $failed++ }
    }
$free = (Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'").FreeSpace
Write-Output "deleted=$deleted skipped-or-locked=$failed freeGB=$([math]::Round($free/1GB,2))"
