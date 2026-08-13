# KS0.3 / bug #20 - reproduction: the CWD must beat an inherited CONDUCTOR_PLAN.
#
# Red  : the PUBLISHED engine on PATH resolves the env var and ignores the plan sitting in the
#        directory it was launched from - the shape that wrote the phantom F0-R0 stages into
#        plans/karvan/CORE-TRACKER.md from a throwaway rig.
# Green: the FRESH BUILD resolves the rig's own plan and says out loud that it overrode the variable.
#
# Nothing here touches C:/code/conductor. Two scratch rigs, two scratch state dirs, temp only.
# Windows PowerShell 5.1 compatible, ASCII only.
#
# KS1.3 clause 7 - STATE HOME ISOLATION. `doctor` resolves a plan, and resolving a plan upserts the
# machine catalogue (StateHome.Resolve -> StateCatalogue.Upsert). This rig ran `doctor` twice per
# invocation with no home of its own, so every run of it minted entries in the operator's REAL
# %LOCALAPPDATA%\conductor\catalogue.json - which is where the blank-id debris came from. Each child
# now gets CONDUCTOR_STATE_HOME under $Root, and the script checks the real catalogue either side of
# itself and fails if the count moved.

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP ("ks0-3-bug20-" + [guid]::NewGuid().ToString("N").Substring(0, 8))),
    # Empty means "derive it below". Windows PowerShell 5.1 does not bind $PSScriptRoot inside a
    # param() default, so the derivation cannot live here.
    [string]$FreshExe = ""
)

$ErrorActionPreference = "Stop"

# Derived from where this script lives, not from a hard-coded tree: run it from a lane worktree and it
# must exercise THAT worktree's build, never another checkout's.
if (-not $FreshExe) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $FreshExe = Join-Path $repoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
}

# Trap 4, in this shell too: nothing below may inherit the driving run's plan. The probes set the
# variable explicitly on the child, which is the whole subject of the test.
$inheritedPlan = $env:CONDUCTOR_PLAN
$inheritedHome = $env:CONDUCTOR_STATE_HOME
Remove-Item Env:\CONDUCTOR_PLAN -ErrorAction SilentlyContinue
Remove-Item Env:\CONDUCTOR_STATE_HOME -ErrorAction SilentlyContinue

function Get-RealCatalogueCount {
    $p = Join-Path $env:LOCALAPPDATA "conductor\catalogue.json"
    if (-not (Test-Path $p)) { return 0 }
    try { $j = Get-Content $p -Raw -ErrorAction Stop | ConvertFrom-Json } catch { return -1 }
    if ($null -eq $j.entries) { return 0 }
    return @($j.entries).Count
}

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

function Invoke-Probe([string]$exe, [string]$cwd, [string]$envPlan, [string]$stateHome) {
    $out = Join-Path $Root ("out-" + [IO.Path]::GetRandomFileName() + ".txt")
    $err = Join-Path $Root ("err-" + [IO.Path]::GetRandomFileName() + ".txt")
    New-Item -ItemType Directory -Force -Path $stateHome | Out-Null
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    # `doctor` names the plan it resolved on its first line, and writes nothing to the repo - but it
    # DOES catalogue what it resolved, which is why the home below is not optional.
    $psi.Arguments = "doctor"
    $psi.WorkingDirectory = $cwd
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    if ([string]::IsNullOrEmpty($envPlan)) {
        [void]$psi.EnvironmentVariables.Remove("CONDUCTOR_PLAN")
    }
    else {
        $psi.EnvironmentVariables["CONDUCTOR_PLAN"] = $envPlan
    }
    $psi.EnvironmentVariables["CONDUCTOR_STATE_HOME"] = $stateHome
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

$catalogueBefore = Get-RealCatalogueCount

$red = Invoke-Probe $published $rigA $planB (Join-Path $Root "home-red")
$green = Invoke-Probe $FreshExe $rigA $planB (Join-Path $Root "home-green")

$redPlan = if ($red.Stdout -match "rig-b-env") { "rig-b-env (the ENV var)" } elseif ($red.Stdout -match "rig-a-here") { "rig-a-here (the CWD)" } else { "?" }
$greenPlan = if ($green.Stdout -match "rig-a-here") { "rig-a-here (the CWD)" } elseif ($green.Stdout -match "rig-b-env") { "rig-b-env (the ENV var)" } else { "?" }

Write-Host "RED   published $published"
Write-Host "      resolved: $redPlan"
Write-Host "GREEN fresh     $FreshExe"
Write-Host "      resolved: $greenPlan"
Write-Host "      stderr  : $($green.Stderr.Trim())"
Write-Host ""

$catalogueAfter = Get-RealCatalogueCount
Write-Host "real catalogue entries: before=$catalogueBefore after=$catalogueAfter"
Write-Host ""

$ok = $true
if ($red.Stdout -notmatch "rig-b-env") { Write-Host "UNEXPECTED: the published engine did not show the bug"; $ok = $false }
if ($green.Stdout -notmatch "rig-a-here") { Write-Host "FAIL: the fresh build did not prefer the cwd plan"; $ok = $false }
if ($green.Stderr -notmatch "warning:") { Write-Host "FAIL: the fresh build overrode the variable silently"; $ok = $false }
if ($green.Stderr -notmatch [regex]::Escape($planB)) { Write-Host "FAIL: the warning does not name the variable's plan"; $ok = $false }
# KS1.3 clause 7. A rig that proves a bug and mints catalogue debris on the way has not passed.
if ($catalogueAfter -ne $catalogueBefore) {
    Write-Host "FAIL: this rig changed the REAL catalogue ($catalogueBefore -> $catalogueAfter). CONDUCTOR_STATE_HOME did not hold."
    $ok = $false
}

if ($inheritedPlan) { $env:CONDUCTOR_PLAN = $inheritedPlan }
if ($inheritedHome) { $env:CONDUCTOR_STATE_HOME = $inheritedHome }

if ($ok) { Write-Host "PASS - red on the published engine, green on this build, zero real catalogue entries added"; exit 0 }
Write-Host "--- red stdout ---";   Write-Host $red.Stdout
Write-Host "--- green stdout ---"; Write-Host $green.Stdout
exit 1
