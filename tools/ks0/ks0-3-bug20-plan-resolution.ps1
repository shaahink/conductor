# KS0.3 / bug #20 - reproduction: the CWD must beat an inherited CONDUCTOR_PLAN.
#
# Red  : the PUBLISHED engine on PATH resolves the env var and ignores the plan sitting in the
#        directory it was launched from - the shape that wrote the phantom F0-R0 stages into
#        plans/karvan/CORE-TRACKER.md from a throwaway rig.
# Green: the FRESH BUILD resolves the rig's own plan and says out loud that it overrode the variable.
#
# Nothing here touches C:/code/conductor. Two scratch rigs, two scratch state dirs, temp only.
# Windows PowerShell 5.1 compatible, ASCII only.

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP ("ks0-3-bug20-" + [guid]::NewGuid().ToString("N").Substring(0, 8))),
    [string]$FreshExe = "C:\code\conductor\src\Conductor\bin\Debug\net10.0\conductor.exe"
)

$ErrorActionPreference = "Stop"

function New-Rig([string]$dir, [string]$planName) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $stateDir = Join-Path $dir ".conductor"
    New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
    $plan = @{
        name     = $planName
        repo     = $dir
        tracker  = "TRACKER.md"
        stateDir = $stateDir
        agent    = @{ command = "cmd"; args = @("/c", "echo", "{prompt}") }
        stages   = @(@{ id = "S1"; title = "the only stage"; sessions = 1 })
    } | ConvertTo-Json -Depth 6
    $planPath = Join-Path $dir "$planName.plan.json"
    [IO.File]::WriteAllText($planPath, $plan)
    [IO.File]::WriteAllText((Join-Path $dir "TRACKER.md"), "# tracker`n`n| ID | Title | Status |`n|---|---|---|`n| S1.1 | a row | TODO |`n")
    return $planPath
}

function Invoke-Probe([string]$exe, [string]$cwd, [string]$envPlan) {
    $out = Join-Path $Root ("out-" + [IO.Path]::GetRandomFileName() + ".txt")
    $err = Join-Path $Root ("err-" + [IO.Path]::GetRandomFileName() + ".txt")
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    # `doctor` names the plan it resolved on its first line, and writes nothing.
    $psi.Arguments = "doctor"
    $psi.WorkingDirectory = $cwd
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.EnvironmentVariables["CONDUCTOR_PLAN"] = $envPlan
    $p = [System.Diagnostics.Process]::Start($psi)
    $stdout = $p.StandardOutput.ReadToEnd()
    $stderr = $p.StandardError.ReadToEnd()
    $p.WaitForExit()
    [IO.File]::WriteAllText($out, $stdout)
    [IO.File]::WriteAllText($err, $stderr)
    return [pscustomobject]@{ Stdout = $stdout; Stderr = $stderr; Exit = $p.ExitCode }
}

Write-Host "rig root: $Root"
$rigA = Join-Path $Root "rig-a"
$rigB = Join-Path $Root "rig-b"
$planA = New-Rig $rigA "rig-a-here"
$planB = New-Rig $rigB "rig-b-env"

Write-Host ""
Write-Host "cwd            = $rigA          (holds exactly one plan: rig-a-here)"
Write-Host "CONDUCTOR_PLAN = $planB   (a DIFFERENT run's plan, as a session hands it down)"
Write-Host ""

$published = (Get-Command conductor -ErrorAction SilentlyContinue).Source
if (-not $published) { throw "no published conductor on PATH to compare against" }

$red = Invoke-Probe $published $rigA $planB
$green = Invoke-Probe $FreshExe $rigA $planB

$redPlan = if ($red.Stdout -match "rig-b-env") { "rig-b-env (the ENV var)" } elseif ($red.Stdout -match "rig-a-here") { "rig-a-here (the CWD)" } else { "?" }
$greenPlan = if ($green.Stdout -match "rig-a-here") { "rig-a-here (the CWD)" } elseif ($green.Stdout -match "rig-b-env") { "rig-b-env (the ENV var)" } else { "?" }

Write-Host "RED   published $published"
Write-Host "      resolved: $redPlan"
Write-Host "GREEN fresh     $FreshExe"
Write-Host "      resolved: $greenPlan"
Write-Host "      stderr  : $($green.Stderr.Trim())"
Write-Host ""

$ok = $true
if ($red.Stdout -notmatch "rig-b-env") { Write-Host "UNEXPECTED: the published engine did not show the bug"; $ok = $false }
if ($green.Stdout -notmatch "rig-a-here") { Write-Host "FAIL: the fresh build did not prefer the cwd plan"; $ok = $false }
if ($green.Stderr -notmatch "warning:") { Write-Host "FAIL: the fresh build overrode the variable silently"; $ok = $false }
if ($green.Stderr -notmatch [regex]::Escape($planB)) { Write-Host "FAIL: the warning does not name the variable's plan"; $ok = $false }

if ($ok) { Write-Host "PASS - red on the published engine, green on this build"; exit 0 }
Write-Host "--- red stdout ---";   Write-Host $red.Stdout
Write-Host "--- green stdout ---"; Write-Host $green.Stdout
exit 1
