<#
.SYNOPSIS
  Complexity-budget ratchet (KS6.3). Keeps CA1502 / CA1505 / CA1506 switched on, keeps their per-project
  budgets from being loosened, and refuses the one config typo that silently voids all three.

.DESCRIPTION
  KS6.1 curated a ruleset and KS6.2 counted what silences it. This gate guards the third way a quality bar
  dies: the bar stays written down, stays at 'error', and stops meaning anything because the NUMBER moved.

  THE NUMBERS. CA1502 (cyclomatic complexity per method), CA1505 (maintainability index) and CA1506 (class
  coupling) ship DISABLED in the analyzer package and read their thresholds from an AdditionalFile called
  CodeMetricsConfig.txt. This repo keeps one per PROJECT, because one repo-wide number would have to be the
  loosest of them - Conductor.Planning holds a cyclomatic budget of 10 while Conductor.Core carries 91, and
  a single number would have been 91 for both. Each budget was set to that project's MEASURED WORST, so
  every project sits exactly at its own bar and the next branch added to the worst method is a build error,
  not a warning somebody scrolls past.

  WHY A GATE AND NOT JUST THE BUILD. The build enforces today's number. Nothing in the build stops a
  session from editing '91' to '120' in the same commit as the method that needed it - the build goes green
  and the ceiling moved. So the bar here is not the file: it is the STRICTEST value this branch has shown
  over the last -Window commits that touched a budget. One commit cannot move a minimum, and neither can
  twenty, because the gate would have to be red on every commit in between. Deliberately raising a budget
  is what -Anchor and CONDUCTOR_COMPLEXITY_ANCHOR are for, and both are a human's call, in the open.

  Anchoring on the window rather than on origin/<branch> is not a preference. Measured in KS6.2 and written
  into .conductor/evidence/KS6/KS6.2-seeded-attacks.log: a session commits AND PUSHES before conductor runs
  the battery, so at gate time origin/<branch> IS HEAD and any check phrased against it compares the tree
  with itself. It catches an uncommitted seed and nothing else.

  THE SILENT VOID, and it is the reason this gate validates grammar at all. Measured 2026-08-19 on this
  repo: rebuild src/Conductor.Planning with 'CA1502: 8' and six diagnostics come back. Add ONE line the
  analyzer cannot parse - a symbol name in the parentheses, a typo, a stray word - and the same build
  reports ZERO. Not six, zero: CA1505 and CA1506 die with it. There is no AD0001 and no CA1509, which is
  the diagnostic that exists for precisely this and was enabled at error while the probe ran. A budget file
  can therefore be disarmed by a plausible-looking edit that no build output mentions. So every line of
  every budget file is checked here against the grammar the analyzer actually accepts, and
  KS6_3ComplexityBudgetTests compiles a canary to prove the rules are still live rather than merely
  configured - which is the KS6.1 lesson (roslynator_analyzers.enabled_by_default read correct and was read
  by nobody) one layer down.

  Five rules:

    1. EVERY PROJECT HAS A BUDGET FILE. A project without one falls back to the analyzer defaults
       (25 / 10 / 95), looser than every budget in this repo on at least one axis. Deleting a file is the
       cheapest loosening there is, so it is the first thing checked.
    2. EVERY BUDGET FILE PARSES, LINE BY LINE. See the silent void above.
    3. ALL THREE RULES ARE BUDGETED IN EVERY FILE. A missing rule is the analyzer default, quietly.
    4. NO BUDGET IS LOOSER THAN THE WINDOW'S STRICTEST. CA1502 and CA1506 are ceilings, so lower is
       stricter; CA1505 is a floor - the index runs 0-100 and the rule fires BELOW the number - so higher
       is stricter. The direction is per rule, not per file.
    5. THE RULES STAY ENFORCED AND STAY WIRED. All three at error or warning in .editorconfig (warnings are
       errors here), no section downgrading one, and Directory.Build.props still handing the budget files to
       the compiler as AdditionalFiles. A budget the compiler never receives is a text file.

  ASCII only, on purpose: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI, and a gate that
  fails to parse is worse than no gate at all.
#>
[CmdletBinding()]
param(
    [string]$Anchor = "",
    [int]$Window = 25,
    [switch]$Quiet
)

$ErrorActionPreference = "Continue"
$failures = New-Object System.Collections.Generic.List[string]

$BUDGET_FILE = "CodeMetricsConfig.txt"
$PROPS_FILE  = "Directory.Build.props"
$RULES       = @("CA1502", "CA1505", "CA1506")

# Higher is stricter for CA1505 (it is a floor: the rule fires BELOW the number). Lower is stricter for the
# other two (they are ceilings). Getting this backwards would turn the gate into its own opposite, so the
# direction is data, stated once.
$STRICTER_IS_HIGHER = @{ "CA1502" = $false; "CA1505" = $true; "CA1506" = $false }

# The grammar the analyzer actually accepts. Anything else voids the file in silence - see the header.
# Trailing CR is stripped before matching rather than tolerated in the pattern: these lines are read out of
# git on a CRLF checkout, and a '$'-anchored match against an untrimmed line silently misses every one of
# them - a trap this repo has already paid for once, in KS6.2.
$SYMBOL_KINDS = "Assembly|Namespace|Type|NamedType|Method|Field|Event|Property"
$ENTRY_RE     = "^[ \t]*(CA150[0-9])([ \t]*\((" + $SYMBOL_KINDS + ")\))?[ \t]*:[ \t]*([0-9]+)[ \t]*$"
$SKIP_RE      = "^[ \t]*(#|$)"

function Invoke-GitLines {
    param([string[]]$GitArgs)
    $out = & git @GitArgs 2>&1 | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
    if ($null -eq $out) { return @() }
    return @($out | ForEach-Object { [string]$_ })
}

# One reader for both sides. An empty ref means the working tree, which is what the build compiles; a ref
# means 'git show', so history is read straight out of the object database with no checkout to get wrong.
# CR is stripped here rather than at every use site.
function Get-FileLines {
    param([string]$Path, [string]$Ref)
    if ($Ref) {
        $lines = Invoke-GitLines @("show", ($Ref + ":" + $Path))
        if ($LASTEXITCODE -ne 0) { return $null }
        if ($null -eq $lines -or $lines.Count -eq 0) { return $null }
    }
    else {
        if (-not (Test-Path -LiteralPath $Path)) { return $null }
        $lines = @(Get-Content -LiteralPath $Path)
    }
    return @($lines | ForEach-Object { ([string]$_).TrimEnd([char]13) })
}

# Returns @{ Budgets = @{key=int}; BadLines = @(text) }, or $null when the file is not there at all - the
# caller has to tell a deleted budget from a voided one, because they fail for different reasons.
function Read-Budget {
    param([string]$Path, [string]$Ref)
    $lines = Get-FileLines -Path $Path -Ref $Ref
    if ($null -eq $lines) { return $null }
    $budgets = @{}
    $bad = New-Object System.Collections.Generic.List[string]
    foreach ($line in $lines) {
        if ($line -match $SKIP_RE) { continue }
        if ($line -match $ENTRY_RE) {
            $rule = $Matches[1]
            $kind = $Matches[3]
            $val  = [int]$Matches[4]
            # A per-SymbolKind entry is legal grammar but it overrides the global one for that kind, so it
            # is a second budget hiding behind the first. Recorded under its own key and ratcheted the same.
            if ($kind) { $budgets[($rule + "(" + $kind + ")")] = $val }
            else       { $budgets[$rule] = $val }
        }
        else {
            $bad.Add($line.Trim())
        }
    }
    return [pscustomobject]@{ Budgets = $budgets; BadLines = $bad }
}

# Projects come from git, not from the filesystem: an untracked csproj is not part of the build this gate
# is judging, and a tracked one that somebody deleted from disk still has to answer for its budget.
function Get-ProjectDirs {
    param([string]$Ref)
    if ($Ref) { $gitArgs = @("ls-tree", "-r", "--name-only", $Ref) }
    else      { $gitArgs = @("ls-files") }
    $files = Invoke-GitLines ($gitArgs + @("--", "*.csproj"))
    $dirs = New-Object System.Collections.Generic.List[string]
    foreach ($f in $files) {
        $norm = ([string]$f).Replace("\", "/").Trim()
        if ($norm -eq "") { continue }
        $slash = $norm.LastIndexOf("/")
        if ($slash -gt 0) { $dirs.Add($norm.Substring(0, $slash)) }
    }
    return @($dirs | Select-Object -Unique | Sort-Object)
}

function Get-AnchorCommits {
    if ($Anchor) { return @($Anchor) }
    if ($env:CONDUCTOR_COMPLEXITY_ANCHOR) { return @($env:CONDUCTOR_COMPLEXITY_ANCHOR) }
    if ($env:CONDUCTOR_BASE_REF) { return @($env:CONDUCTOR_BASE_REF) }
    return Invoke-GitLines @("log", "--format=%H", "-n", "$Window", "--", ("*" + $BUDGET_FILE), ".editorconfig", $PROPS_FILE)
}

$anchorCommits = @(Get-AnchorCommits | Where-Object { $_ -match "^[0-9a-f]{7,}$" })
if (-not $Quiet) {
    if ($anchorCommits.Count -eq 1) {
        Write-Host ("complexity-budget: anchor is {0} (named explicitly)" -f $anchorCommits[0])
    }
    elseif ($anchorCommits.Count -gt 1) {
        Write-Host ("complexity-budget: bar is the STRICTEST value over the last {0} commits that touched a budget ({1} found) - no single commit moves it" -f $Window, $anchorCommits.Count)
    }
    else {
        Write-Host "complexity-budget: no history to anchor against - loosening checks skipped, every absolute rule still applies."
    }
}

# --- rules 1-3: the files exist, parse, and cover all three rules --------------------------------------
$projectDirs = @(Get-ProjectDirs -Ref "")
if ($projectDirs.Count -eq 0) {
    $failures.Add("NO PROJECTS FOUND - 'git ls-files -- *.csproj' came back empty, so this gate measured nothing. That is a broken gate, not a clean tree.")
}

$now = @{}
foreach ($dir in $projectDirs) {
    $path = $dir + "/" + $BUDGET_FILE
    $read = Read-Budget -Path $path -Ref ""
    if ($null -eq $read) {
        $failures.Add(("NO COMPLEXITY BUDGET - '{0}' is missing. A project without one gets the analyzer defaults (CA1502=25, CA1505=10, CA1506=95), which is looser than every budget in this repo. Deleting the file is a loosening, so it is refused like one." -f $path))
        continue
    }
    if ($read.BadLines.Count -gt 0) {
        $failures.Add(("UNPARSEABLE BUDGET LINE - '{0}' contains {1} line(s) the analyzer cannot read: {2}. This is not cosmetic: one such line disables CA1502, CA1505 AND CA1506 for the whole project, silently, with no diagnostic of any kind. Only 'RuleId: N' and 'RuleId(SymbolKind): N' parse." -f $path, $read.BadLines.Count, (($read.BadLines | Select-Object -First 3) -join " | ")))
    }
    foreach ($rule in $RULES) {
        if (-not $read.Budgets.ContainsKey($rule)) {
            $failures.Add(("BUDGET MISSING A RULE - '{0}' has no '{1}:' line, so that rule falls back to the analyzer default. All three are budgeted in every project or the set means nothing." -f $path, $rule))
        }
    }
    $now[$path] = $read.Budgets
}

# --- rule 4: no budget looser than the window's strictest ----------------------------------------------
if ($anchorCommits.Count -gt 0) {
    $bar = @{}      # "path|key" -> @{ Value; At }
    foreach ($c in $anchorCommits) {
        foreach ($dir in (Get-ProjectDirs -Ref $c)) {
            $path = $dir + "/" + $BUDGET_FILE
            $read = Read-Budget -Path $path -Ref $c
            if ($null -eq $read) { continue }
            foreach ($key in $read.Budgets.Keys) {
                $rule = $key.Substring(0, 6)
                if (-not $STRICTER_IS_HIGHER.ContainsKey($rule)) { continue }
                $v = $read.Budgets[$key]
                $id = $path + "|" + $key
                if (-not $bar.ContainsKey($id)) { $bar[$id] = @{ Value = $v; At = $c }; continue }
                if ($STRICTER_IS_HIGHER[$rule]) { $isStricter = ($v -gt $bar[$id].Value) }
                else                            { $isStricter = ($v -lt $bar[$id].Value) }
                if ($isStricter) { $bar[$id] = @{ Value = $v; At = $c } }
            }
        }
    }

    # A budget with no bar is a budget this branch has never carried before - a brand new project, or the
    # first commit that introduces one. Printed rather than skipped: a gate that says OK while showing
    # nothing is indistinguishable from a gate that measured nothing, which is how KS6.2's vacuous section
    # survived as long as it did.
    foreach ($path in ($now.Keys | Sort-Object)) {
        foreach ($key in ($now[$path].Keys | Sort-Object)) {
            if ($bar.ContainsKey($path + "|" + $key)) { continue }
            if (-not $Quiet) {
                Write-Host ("complexity-budget: {0,-46} {1,-14} bar=new   now={2}" -f $path, $key, $now[$path][$key])
            }
        }
    }

    foreach ($id in ($bar.Keys | Sort-Object)) {
        $sep = $id.LastIndexOf("|")
        $path = $id.Substring(0, $sep)
        $key = $id.Substring($sep + 1)
        $rule = $key.Substring(0, 6)
        $barVal = $bar[$id].Value
        $at = [string]$bar[$id].At
        $atShort = $at.Substring(0, [Math]::Min(9, $at.Length))
        if (-not $now.ContainsKey($path)) { continue }   # already reported as a missing file
        if (-not $now[$path].ContainsKey($key)) {
            $failures.Add(("BUDGET DROPPED - '{0}' no longer sets '{1}'; it was {2} at {3}. Removing the line is the same loosening as raising the number, and it is quieter." -f $path, $key, $barVal, $atShort))
            continue
        }
        $nowVal = $now[$path][$key]
        if ($STRICTER_IS_HIGHER[$rule]) { $looser = ($nowVal -lt $barVal) }
        else                            { $looser = ($nowVal -gt $barVal) }
        if (-not $Quiet) {
            Write-Host ("complexity-budget: {0,-46} {1,-14} bar={2,-5} now={3}" -f $path, $key, $barVal, $nowVal)
        }
        if ($looser) {
            if ($STRICTER_IS_HIGHER[$rule]) { $dir = "lowering" } else { $dir = "raising" }
            $failures.Add(("COMPLEXITY BUDGET LOOSENED - {0} '{1}' went {2} -> {3} ({4} it is a loosening for this rule). The bar of {2} was set by commit {5}, and committing this does not move it. If the code genuinely needs the room, that is a refactor or a human's decision recorded with -Anchor, not an edit to the number." -f $path, $key, $barVal, $nowVal, $dir, $atShort))
        }
    }
}
elseif (-not $Quiet) {
    foreach ($path in ($now.Keys | Sort-Object)) {
        foreach ($key in ($now[$path].Keys | Sort-Object)) {
            Write-Host ("complexity-budget: {0,-46} {1,-14} now={2}" -f $path, $key, $now[$path][$key])
        }
    }
}

# --- rule 5: still enforced, still wired ---------------------------------------------------------------
# A budget the compiler never receives is a text file. Two ways that happens: the severity line stops
# saying error/warning (in ANY section - a path-scoped 'none' exempts a whole tree at once), or the
# AdditionalFiles item that hands the file to the compiler goes away.
$editorLines = Get-FileLines -Path ".editorconfig" -Ref ""
if ($null -eq $editorLines) {
    $failures.Add("NO .editorconfig - the three code-metrics rules are OFF by default in the analyzer package, so without it there is no budget at all, whatever the CodeMetricsConfig.txt files say.")
}
else {
    foreach ($rule in $RULES) {
        $seen = $false
        foreach ($line in $editorLines) {
            if ($line -notmatch ("^[ \t]*dotnet_diagnostic\." + $rule + "\.severity[ \t]*=[ \t]*([a-z]+)")) { continue }
            $sev = $Matches[1]
            if ($sev -eq "error" -or $sev -eq "warning") { $seen = $true; continue }
            $failures.Add(("COMPLEXITY RULE UN-ENFORCED - .editorconfig sets {0} to '{1}'. Warnings are errors in this repo, so error and warning both fail the build and anything else does not. A budget that cannot fail a build is documentation." -f $rule, $sev))
        }
        if (-not $seen) {
            $failures.Add(("COMPLEXITY RULE NOT ENABLED - .editorconfig has no enforcing 'dotnet_diagnostic.{0}.severity' line. All three code-metrics rules ship DISABLED in the analyzer package; that line is the whole of what turns this one on." -f $rule))
        }
    }
}

$propsLines = Get-FileLines -Path $PROPS_FILE -Ref ""
$wired = $false
if ($null -ne $propsLines) {
    foreach ($line in $propsLines) {
        if ($line -match "AdditionalFiles" -and $line -match [regex]::Escape($BUDGET_FILE)) { $wired = $true }
    }
}
if (-not $wired) {
    $failures.Add(("BUDGETS NOT WIRED - {0} no longer hands {1} to the compiler as an AdditionalFiles item. Without it every project falls back to the analyzer defaults and each budget file becomes a text file nobody reads." -f $PROPS_FILE, $BUDGET_FILE))
}

# --- verdict -------------------------------------------------------------------------------------------
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "COMPLEXITY-BUDGET GATE FAILED:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  * " + $f) -ForegroundColor Red }
    Write-Host ""
    Write-Host "There is no baseline file to edit: the bar is the strictest this branch's own history has been." -ForegroundColor Red
    exit 1
}

Write-Host "complexity-budget: OK - all three rules enforced, every project budgeted, nothing loosened." -ForegroundColor Green
exit 0
