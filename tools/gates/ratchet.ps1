<#
.SYNOPSIS
  Anti-cheat gate. Proves the agent made the bar instead of lowering it.

.DESCRIPTION
  Every "green" signal in this system can be faked by weakening the thing that measures it: delete the
  failing test, suppress the analyzer, soften the gate, raise the ceiling. This gate fails if the bar moved
  DOWN. It is deliberately dumb and mechanical - there is no model in the loop to be talked around.

  Two independent checks:

    ABSOLUTE FLOORS (tools/gates/ratchet-baseline.json) - the suite may never have fewer tests, nor more
    analyzer suppressions, than the recorded floor. The floor ratchets UP as the work lands.

    RELATIVE TO WHAT IS ALREADY PUSHED (origin/<this-branch>) - you may not push something worse than what
    is already on the branch. This catches lowering the floor file itself, deleting tests, and softening
    gate commands, all in one move.

  It interlocks with tests/Conductor.Tests/ArchitectureTests.cs: those enforce the design (file-size and
  type ceilings, layering) against architecture-baseline.json, and THIS gate makes deleting them impossible,
  because the test count may never fall. Neither is escapable without the other noticing.

    THE REAL CATALOGUE MAY NOT GROW ACROSS A GATE RUN (KS1.3) - every verb that loads a plan upserts the
    machine catalogue as a side effect, so a test, a gate or a scratch rig that forgets
    CONDUCTOR_STATE_HOME writes into the operator's real history. Capture the count before the battery
    with -CatalogueBaseline; a later plain run fails if it went up.

  ASCII only, on purpose: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI, and a gate that
  fails to parse is worse than no gate at all.
#>
[CmdletBinding()]
param(
    [string]$BaseRef = "",
    [switch]$CatalogueBaseline
)

# NOT "Stop": Windows PowerShell 5.1 wraps a native command's stderr in an ErrorRecord, so a perfectly
# normal `git cat-file -e` miss would abort the whole gate. Exit codes are checked explicitly instead.
$ErrorActionPreference = "Continue"
$failures = New-Object System.Collections.Generic.List[string]
$floorPath = "tools/gates/ratchet-baseline.json"
$archPath  = "tests/Conductor.Tests/architecture-baseline.json"

# --- the machine catalogue, which is not a repo file --------------------------------------------------
# KS1.3. StateHome.Resolve upserts %LOCALAPPDATA%\conductor\catalogue.json every time anything loads a
# plan, so a gate battery or a scratch rig that forgot CONDUCTOR_STATE_HOME quietly mints entries in the
# operator's real history - that is where six blank-id rows came from, and a downstream consumer refused
# the whole payload over them. The count is machine state, not repo state, so its baseline is captured
# beside the run rather than committed.
#
# Every degradation is deliberately toward silence: no catalogue file counts as zero, an unreadable one
# counts as zero, no captured baseline skips the comparison, and a count that FELL is somebody repairing
# on purpose. Only growth is evidence, and only growth fails.

function Get-CatalogueCount {
    $stateHome = $env:CONDUCTOR_STATE_HOME
    if (-not $stateHome -and $env:LOCALAPPDATA) { $stateHome = Join-Path $env:LOCALAPPDATA "conductor" }
    if (-not $stateHome) { return 0 }
    $p = Join-Path $stateHome "catalogue.json"
    if (-not (Test-Path $p)) { return 0 }
    try { $j = Get-Content $p -Raw -ErrorAction Stop | ConvertFrom-Json } catch { return 0 }
    if ($null -eq $j.entries) { return 0 }
    return @($j.entries).Count
}

function Get-CatalogueBaselinePath {
    if ($env:CONDUCTOR_CATALOGUE_RATCHET) { return $env:CONDUCTOR_CATALOGUE_RATCHET }
    $dir = $env:TEMP
    if (-not $dir) { $dir = "." }
    return (Join-Path $dir "conductor-catalogue-ratchet.json")
}

if ($CatalogueBaseline) {
    $count = Get-CatalogueCount
    $path = Get-CatalogueBaselinePath
    @{ entries = $count; capturedUtc = (Get-Date).ToUniversalTime().ToString("o") } |
        ConvertTo-Json | Set-Content -Path $path -Encoding ASCII
    Write-Host ("ratchet: catalogue baseline captured - entries={0} -> {1}" -f $count, $path)
    exit 0
}

function Invoke-Git {
    param([string[]]$GitArgs)
    $out = & git @GitArgs 2>&1 | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
    return [pscustomobject]@{ Ok = ($LASTEXITCODE -eq 0); Text = ($out | Out-String) }
}

function Test-RefExists  { param([string]$Ref)                (Invoke-Git @("rev-parse", "--verify", "--quiet", $Ref)).Ok }
function Test-PathInRef  { param([string]$Ref, [string]$Path) (Invoke-Git @("cat-file", "-e", "${Ref}:${Path}")).Ok }
function Get-FileFromRef { param([string]$Ref, [string]$Path) (Invoke-Git @("show", "${Ref}:${Path}")).Text }

function Resolve-BaseRef {
    if ($BaseRef) { return $BaseRef }
    if ($env:CONDUCTOR_BASE_REF) { return $env:CONDUCTOR_BASE_REF }
    # The bar is what is already pushed on THIS branch - not master, which on a long-lived era branch is
    # hundreds of commits and several hundred tests behind, and would make every check a false positive.
    $branch = (Invoke-Git @("rev-parse", "--abbrev-ref", "HEAD")).Text.Trim()
    if ($branch -and (Test-RefExists "origin/$branch")) { return "origin/$branch" }
    return ""   # no remote yet: absolute floors still apply, relative checks are skipped
}

function Count-InTree {
    param([string]$Pattern, [string]$PathSpec)
    $total = 0
    Get-ChildItem -Path $PathSpec -Recurse -File -Filter *.cs -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.FullName -match '\\(obj|bin)\\') { return }
        $content = Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue
        if ($content) { $total += ([regex]::Matches($content, $Pattern)).Count }
    }
    return $total
}

$testPattern   = '\[(Fact|Theory)\]'
$pragmaPattern = '#pragma\s+warning\s+disable'

$nowTests   = Count-InTree -Pattern $testPattern   -PathSpec "tests"
$nowPragmas = Count-InTree -Pattern $pragmaPattern -PathSpec "src"

# --- 1. absolute floors -----------------------------------------------------------------------------
if (-not (Test-Path $floorPath)) { throw "ratchet: missing $floorPath - the floor file is part of the gate." }
$floor = Get-Content $floorPath -Raw | ConvertFrom-Json

Write-Host ("ratchet: tests    floor={0}  now={1}" -f $floor.minTests, $nowTests)
if ($nowTests -lt $floor.minTests) {
    $failures.Add("TEST COUNT BELOW FLOOR ($nowTests < $($floor.minTests)). Tests are a ratchet. If a test is genuinely wrong, fix its assertion and say why via 'conductor note' - do not delete it.")
}

Write-Host ("ratchet: pragmas  ceil={0}   now={1}" -f $floor.maxPragmas, $nowPragmas)
if ($nowPragmas -gt $floor.maxPragmas) {
    $failures.Add("ANALYZER SUPPRESSIONS ABOVE CEILING ($nowPragmas > $($floor.maxPragmas)). Fix what the analyzer is complaining about. A new suppression needs an inline justification AND a ledger note explaining why the rule is wrong here.")
}

# --- 2. house strictness ----------------------------------------------------------------------------
$props = Get-Content "Directory.Build.props" -Raw -ErrorAction SilentlyContinue
if (-not $props -or $props -notmatch '<TreatWarningsAsErrors>\s*true\s*</TreatWarningsAsErrors>') {
    $failures.Add("TreatWarningsAsErrors is no longer true in Directory.Build.props.")
}
if ($props -match '<AnalysisLevel>\s*none\s*</AnalysisLevel>') {
    $failures.Add("AnalysisLevel was set to none.")
}

# --- 3. nothing gets worse than what is already pushed -----------------------------------------------
$base = Resolve-BaseRef
if (-not $base) {
    Write-Host "ratchet: no remote branch yet - relative checks skipped (absolute floors still enforced)."
}
else {
    Write-Host "ratchet: comparing against $base"

    # 3a. the floor file itself may not be lowered
    if (Test-PathInRef $base $floorPath) {
        $oldFloor = Get-FileFromRef $base $floorPath | ConvertFrom-Json
        if ($floor.minTests -lt $oldFloor.minTests) {
            $failures.Add("THE TEST FLOOR WAS LOWERED ($($oldFloor.minTests) -> $($floor.minTests)) in $floorPath. The floor only goes up.")
        }
        if ($floor.maxPragmas -gt $oldFloor.maxPragmas) {
            $failures.Add("THE SUPPRESSION CEILING WAS RAISED ($($oldFloor.maxPragmas) -> $($floor.maxPragmas)) in $floorPath. Raising it is a human decision: put HUMAN: in the handoff and stop.")
        }
    }

    # 3b. the architecture debt ledger may only shrink
    if ((Test-Path $archPath) -and (Test-PathInRef $base $archPath)) {
        $oldArch = Get-FileFromRef $base $archPath | ConvertFrom-Json
        $newArch = Get-Content $archPath -Raw | ConvertFrom-Json

        $oldSum = 0; $oldArch.filesOverLineCeiling.PSObject.Properties | ForEach-Object { $oldSum += $_.Value }
        $newSum = 0; $newArch.filesOverLineCeiling.PSObject.Properties | ForEach-Object { $newSum += $_.Value }
        Write-Host ("ratchet: archdebt base={0}  now={1}" -f $oldSum, $newSum)

        if ($newSum -gt $oldSum) {
            $failures.Add("ARCHITECTURE DEBT ROSE ($oldSum -> $newSum lines over ceiling). architecture-baseline.json records debt that EXISTS; it is not a permission slip.")
        }
        if ($newArch.lineCeiling -gt $oldArch.lineCeiling -or $newArch.maxTypesPerFile -gt $oldArch.maxTypesPerFile) {
            $failures.Add("ARCHITECTURE CEILINGS WERE RAISED. That is a human decision: put HUMAN: in the handoff and stop.")
        }
        $oldKeys = @($oldArch.filesOverLineCeiling.PSObject.Properties.Name)
        foreach ($k in @($newArch.filesOverLineCeiling.PSObject.Properties.Name)) {
            if ($oldKeys -notcontains $k) {
                $failures.Add("NEW GOD CLASS ADDED TO THE BASELINE: $k. New files obey the ceiling; the baseline is for pre-existing debt only.")
            }
        }
    }

    # 3c. tests may not be deleted
    $deleted = (Invoke-Git @("diff", "--diff-filter=D", "--name-only", $base, "--", "tests")).Text.Trim()
    if ($deleted) { $failures.Add("TEST FILES DELETED: " + ($deleted -replace "`r?`n", ", ")) }

    # 3d. gate commands are the contract
    $planDiff = (Invoke-Git @("diff", "--unified=0", $base, "--", "plans")).Text
    if ($planDiff -match '(?m)^-\s*.*"command"') {
        $failures.Add('A GATE COMMAND WAS CHANGED in a plan file. Gates are the contract; changing one is a human decision. Put HUMAN: in the handoff instead.')
    }
    $gateToolDiff = (Invoke-Git @("diff", "--name-only", $base, "--", "tools/gates")).Text.Trim()
    if ($gateToolDiff) {
        $failures.Add("THE GATE SCRIPTS THEMSELVES WERE MODIFIED: " + ($gateToolDiff -replace "`r?`n", ", ") + ". You do not get to edit the referee.")
    }
}

# --- 4. the operator's real catalogue may not grow across a gate run ---------------------------------
$cataloguePath = Get-CatalogueBaselinePath
$catalogueNow = Get-CatalogueCount
if (-not (Test-Path $cataloguePath)) {
    Write-Host ("ratchet: catalogue now={0} - no baseline captured, comparison skipped (run with -CatalogueBaseline first)." -f $catalogueNow)
}
else {
    $captured = $null
    try { $captured = (Get-Content $cataloguePath -Raw -ErrorAction Stop | ConvertFrom-Json).entries } catch { $captured = $null }
    if ($null -eq $captured) {
        Write-Host "ratchet: catalogue baseline file is unreadable - comparison skipped."
    }
    else {
        Write-Host ("ratchet: catalogue base={0}   now={1}" -f $captured, $catalogueNow)
        if ($catalogueNow -gt $captured) {
            $failures.Add("THE MACHINE CATALOGUE GREW ($captured -> $catalogueNow entries) while this ran. Something loaded a plan without CONDUCTOR_STATE_HOME set and wrote into the operator's real history. Give every rig, fixture and out-of-process proof its own state home.")
        }
    }
}

# --- verdict ----------------------------------------------------------------------------------------
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "RATCHET GATE FAILED - the bar was lowered:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  * " + $f) -ForegroundColor Red }
    Write-Host ""
    Write-Host "Retrying will not help. Fix the work, not the measurement." -ForegroundColor Red
    exit 1
}

Write-Host "ratchet: OK - nothing was weakened." -ForegroundColor Green
exit 0
