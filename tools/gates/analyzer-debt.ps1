<#
.SYNOPSIS
  Analyzer-debt ratchet (KS6.2). Counts every way this repo can silence an analyzer, and refuses a tree
  that silences more than the branch's recent history did.

.DESCRIPTION
  ratchet.ps1 already counts ONE spelling of analyzer debt - '#pragma warning disable' under src/ - and
  compares it to a number written in tools/gates/ratchet-baseline.json. Both halves of that are weak:

    THE COUNT IS ONE OF FIVE. A pragma is the honest way to silence a rule; it is also the only way this
    repo was measuring. [SuppressMessage], NoWarn, a severity downgrade in .editorconfig, and a pragma
    parked under tests/ or tools/ (outside the old PathSpec) all silence the same analyzer and none of
    them moved the number. The dangerous case is not a new suppression, it is a MIGRATION: delete a
    pragma, add 'dotnet_diagnostic.MA0045.severity = none', and the old gate reports an improvement while
    one site's suppression just became the whole repo's. Every kind is counted and named here, so a
    migration shows up as a rise in the kind it moved TO.

    THE BAR WAS A NUMBER IN A FILE THE SAME COMMIT EDITS. An agent that wants a suppression can add one
    and raise the ceiling in the same breath. ratchet.ps1 patches this by diffing the baseline file
    against the pushed branch; this script removes the file instead. THERE IS NO BASELINE FILE HERE.

    AND THE ANCHOR IS NOT ONE COMMIT, because one commit is reachable from inside the game. Measured
    while building this (KS6.2, and the seeded proof is in .conductor/evidence/KS6/): a session commits,
    pushes, and THEN conductor runs the battery - so at gate time origin/<branch> IS HEAD, and every check
    phrased as "not worse than what is already pushed", including the whole of ratchet.ps1 section 3, is
    comparing the tree against itself. It catches an uncommitted seed and nothing else. So the bar here is
    the MINIMUM over the last -Window commits that touched a measured file: the best this branch has
    recently been. One commit cannot move a minimum, and neither can twenty - the debt would have to stay
    elevated across the whole window while this gate went red on every commit in it. Raising the bar on
    purpose is what -Anchor and CONDUCTOR_ANALYZER_ANCHOR are for, and both are a human's call, in the open.

  Three rules:

    1. UNJUSTIFIED SUPPRESSIONS MUST BE ZERO. Absolute, no anchor needed. Every suppression states its
       reason ON ITS OWN LINE, where the next reader lands. This is the rule that turns hygiene into
       design: you may keep a suppression, but you have to be able to say why in one line.

    2. NO KIND MAY GROW AGAINST THE ANCHOR. Per kind, not in total, because a total lets one kind pay for
       another - which is exactly what laundering a pragma into a global downgrade looks like.

    3. A RULE THAT COULD FAIL THE BUILD MUST STILL BE ABLE TO. Counting cannot see a severity moved from
       error to suggestion: that is the same line saying less. See the per-rule section below.

  WHAT PROTECTS THIS SCRIPT, honestly stated: ratchet.ps1 section 3d fails if anything under tools/gates
  differs from the pushed branch, which catches an edit to this referee - or the deletion of the call to
  it - while that edit is uncommitted or unpushed. That check is real and the seeded proof shows it
  firing. It is NOT complete, for the reason above: once the edit is pushed, the diff is empty. The window
  minimum covers that gap for the MEASUREMENT; the referee's own SOURCE is covered by review and by
  tests/Conductor.Tests/KS6_2AnalyzerDebtRatchetTests.cs, which drives this script against a scratch repo
  and goes red if it stops catching what it exists to catch.

  Measurement is 'git grep' on both sides, so history is read straight out of the object database - no
  checkout, no stash, no working tree to get wrong, and provably the identical regex on both sides.

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

# --- git plumbing -------------------------------------------------------------------------------------
# Windows PowerShell 5.1 turns a native command's stderr into ErrorRecords, and 'git grep' exits 1 on "no
# matches" - a perfectly normal answer. Both are filtered here so neither reads as a failure.
function Invoke-GitLines {
    param([string[]]$GitArgs)
    $out = & git @GitArgs 2>&1 | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
    if ($null -eq $out) { return @() }
    return @($out | ForEach-Object { [string]$_ } | Where-Object { $_ -ne "" })
}

# 'git grep -n' prints 'path:line:text', and with a treeish 'ref:path:line:text'. The fields are peeled
# off positionally and the rest is left intact - a naive split on ':' would cut C# and XML lines in half.
function Get-MatchRecords {
    param([string]$Pattern, [string[]]$PathSpec, [string]$Ref)
    $gitArgs = @("grep", "-I", "-n", "-E", $Pattern)
    if ($Ref) { $gitArgs += $Ref }
    $gitArgs += "--"
    $gitArgs += $PathSpec
    $records = New-Object System.Collections.Generic.List[object]
    foreach ($l in (Invoke-GitLines $gitArgs)) {
        $rest = $l
        if ($Ref -and $rest.IndexOf(($Ref + ":"), [System.StringComparison]::Ordinal) -eq 0) {
            $rest = $rest.Substring($Ref.Length + 1)
        }
        $c1 = $rest.IndexOf(":", [System.StringComparison]::Ordinal)
        if ($c1 -lt 0) { continue }
        $path = $rest.Substring(0, $c1)
        $rest = $rest.Substring($c1 + 1)
        $c2 = $rest.IndexOf(":", [System.StringComparison]::Ordinal)
        if ($c2 -lt 0) { continue }
        $records.Add([pscustomobject]@{ Path = $path; Text = $rest.Substring($c2 + 1) })
    }
    return $records
}

# A justification is worth having only if it is on the suppression's own line: a reader who lands on the
# pragma sees it without scrolling, and a reviewer diffing the line sees the reason change with it. The
# repo's own convention already agrees - 41 of the 45 pragmas that existed when this was written carried
# exactly this form. Twelve characters is the "not just '// ok'" bar.
function Test-HasReason {
    param([string]$Text, [string]$Marker)
    $i = $Text.IndexOf($Marker, [System.StringComparison]::Ordinal)
    if ($i -lt 0) { return $false }
    $reason = $Text.Substring($i + $Marker.Length).Trim()
    $reason = $reason.TrimEnd('-', '>').Trim()   # XML comments close with '-->'
    return ($reason.Length -ge 12)
}

# --- the kinds ------------------------------------------------------------------------------------------
# Each kind is (name, regex, pathspec, how a reason is spelled here). Adding a kind is a one-entry edit;
# that is the point of the shape.
# Anchored, and that is faithful rather than lenient: C# requires a #pragma directive to be the first
# token on its line, so a match anywhere else is not a suppression - it is the text of one, quoted inside
# a string literal. KS6_2AnalyzerDebtRatchetTests seeds exactly such literals, and an unanchored pattern
# counted four of them as repo debt. Match what the language allows, not what the characters look like.
$PRAGMA_RE    = '^[ \t]*#pragma[ \t]+warning[ \t]+disable'
$SUPPRESS_RE  = 'SuppressMessage[ \t]*\('
$NOWARN_RE    = '<(NoWarn|WarningsNotAsErrors)>'
$DOWNGRADE_RE = '^[ \t]*dotnet_(diagnostic\.[^.]+|analyzer_diagnostic(\.category-[^.]+)?)\.severity[ \t]*=[ \t]*(none|silent|suggestion)'
$BLANKET_RE   = '^[ \t]*dotnet_analyzer_diagnostic(\.category-[^.]+)?\.severity[ \t]*=[ \t]*(none|silent|suggestion)'
$SEVMAP_RE    = '^[ \t]*(\[[^]]*\]|dotnet_(diagnostic\.[^.]+|analyzer_diagnostic(\.category-[^.]+)?)\.severity[ \t]*=)'
$CONFIGS      = @(".editorconfig", "*.editorconfig", "*.globalconfig")

$kinds = @(
    @{ Name = "pragma-src";          Pattern = $PRAGMA_RE;    Paths = @("src/*.cs");
       Marker = "//";   Note = "#pragma warning disable under src/" },
    @{ Name = "pragma-tests-tools";  Pattern = $PRAGMA_RE;    Paths = @("tests/*.cs", "tools/*.cs");
       Marker = "//";   Note = "the same pragma parked where the old gate never looked" },
    @{ Name = "suppressmessage";     Pattern = $SUPPRESS_RE;  Paths = @("*.cs");
       Marker = "//";   Note = "[SuppressMessage] attributes; a reason here means Justification=" },
    @{ Name = "nowarn";              Pattern = $NOWARN_RE;    Paths = @("*.props", "*.csproj", "*.targets");
       Marker = "<!--"; Note = "NoWarn / WarningsNotAsErrors in build files" },
    # SkipGrowth, and the reason matters: a rule set to none because this repo DECLINED to adopt it is
    # curation, not debt - KS6.1's whole deliverable was a curated set and it added two such lines, each
    # with a measurement behind it. A raw count cannot tell "never adopted" from "coverage we had and
    # lost", so the growth check for severities happens per RULE below, where the difference is visible.
    # The zero-unjustified rule still applies to every one of these lines.
    @{ Name = "severity-downgrade";  Pattern = $DOWNGRADE_RE; Paths = $CONFIGS;
       Marker = "#";    SkipGrowth = $true;
       Note = "severity = none/silent/suggestion" },
    # The blanket is NOT curation, and it does grow-check. An explicit dotnet_diagnostic.X.severity beats
    # a blanket whatever the order, so a blanket never touches a rule this repo listed on purpose - it
    # reaches exactly the default-enabled rules nobody wrote a line for, which is most of them, silently.
    @{ Name = "severity-blanket";    Pattern = $BLANKET_RE;   Paths = $CONFIGS;
       Marker = "#";    Note = "dotnet_analyzer_diagnostic[.category-X].severity - a whole category at once" }
)

# [SuppressMessage] spells its reason as a named argument, not a comment. Same rule, different syntax.
function Test-KindReason {
    param([hashtable]$Kind, [string]$Text)
    if ($Kind.Name -eq "suppressmessage") {
        return ($Text -match 'Justification[ \t]*=[ \t]*"[^"]{12,}"')
    }
    return (Test-HasReason -Text $Text -Marker $Kind.Marker)
}

function Measure-Kind {
    param([hashtable]$Kind, [string]$Ref)
    $total = 0; $unjustified = 0
    $examples = New-Object System.Collections.Generic.List[string]
    foreach ($r in (Get-MatchRecords -Pattern $Kind.Pattern -PathSpec $Kind.Paths -Ref $Ref)) {
        $total++
        if (-not (Test-KindReason -Kind $Kind -Text $r.Text)) {
            $unjustified++
            if ($examples.Count -lt 5) { $examples.Add(($r.Path + ": " + $r.Text.Trim())) }
        }
    }
    return [pscustomobject]@{ Total = $total; Unjustified = $unjustified; Examples = $examples }
}

# --- the anchor commits ---------------------------------------------------------------------------------
# One explicit ref if a human named one; otherwise the window of recent commits that touched a measured
# file, and the bar is the MINIMUM across them. 'git log -- <paths>' rather than plain 'git log', so a run
# of docs-only commits cannot slide the window past the evidence.
function Get-AnchorCommits {
    if ($Anchor) { return @($Anchor) }
    if ($env:CONDUCTOR_ANALYZER_ANCHOR) { return @($env:CONDUCTOR_ANALYZER_ANCHOR) }
    if ($env:CONDUCTOR_BASE_REF) { return @($env:CONDUCTOR_BASE_REF) }
    $paths = @()
    foreach ($k in $kinds) { $paths += $k.Paths }
    return Invoke-GitLines (@("log", "--format=%H", "-n", "$Window", "--") + @($paths | Select-Object -Unique))
}

$anchorCommits = @(Get-AnchorCommits)
if (-not $Quiet) {
    if ($anchorCommits.Count -eq 1) {
        Write-Host ("analyzer-debt: anchor is {0} (named explicitly)" -f $anchorCommits[0])
    }
    elseif ($anchorCommits.Count -gt 1) {
        Write-Host ("analyzer-debt: bar is the MINIMUM over the last {0} commits that touched a measured file ({1} found) - no single commit moves it" -f $Window, $anchorCommits.Count)
    }
    else {
        Write-Host "analyzer-debt: no history to anchor against - growth checks skipped, the zero-unjustified rule still applies."
    }
}

# --- measure now, and at the anchor ----------------------------------------------------------------------
$nowTotal = 0; $anchorTotal = 0; $unjustifiedTotal = 0
foreach ($k in $kinds) {
    $now = Measure-Kind -Kind $k -Ref ""
    $nowTotal += $now.Total
    $unjustifiedTotal += $now.Unjustified

    if ($anchorCommits.Count -gt 0) {
        $bar = [int]::MaxValue; $barAt = ""
        foreach ($c in $anchorCommits) {
            $m = Measure-Kind -Kind $k -Ref $c
            if ($m.Total -lt $bar) { $bar = $m.Total; $barAt = $c }
        }
        $anchorTotal += $bar
        if (-not $Quiet) {
            Write-Host ("analyzer-debt: {0,-20} bar={1,-4} now={2,-4} unjustified={3}{4}" -f $k.Name, $bar, $now.Total, $now.Unjustified, $(if ($k.SkipGrowth) { "   (count not ratcheted - see the per-rule check)" } else { "" }))
        }
        if ($now.Total -gt $bar -and -not $k.SkipGrowth) {
            $failures.Add(("SUPPRESSIONS ROSE - kind '{0}' went {1} -> {2} ({3}). The bar of {1} was set by commit {4}, and committing this does not move it. Silencing the analyzer is not the fix: if a rule is genuinely wrong here, say so via 'conductor note' and take it out of the ruleset in the open." -f $k.Name, $bar, $now.Total, $k.Note, $barAt.Substring(0, [Math]::Min(9, $barAt.Length))))
        }
    }
    elseif (-not $Quiet) {
        Write-Host ("analyzer-debt: {0,-20} now={1,-4} unjustified={2}" -f $k.Name, $now.Total, $now.Unjustified)
    }

    if ($now.Unjustified -gt 0) {
        $failures.Add(("UNJUSTIFIED SUPPRESSIONS - kind '{0}' has {1} with no reason on the line. Every suppression carries its reason where the next reader lands. Offenders: {2}" -f $k.Name, $now.Unjustified, ($now.Examples -join " | ")))
    }
}

if (-not $Quiet) {
    if ($anchorCommits.Count -gt 0) { Write-Host ("analyzer-debt: {0,-20} bar={1,-4} now={2,-4} unjustified={3}" -f "TOTAL", $anchorTotal, $nowTotal, $unjustifiedTotal) }
    else                            { Write-Host ("analyzer-debt: {0,-20} now={1,-4} unjustified={2}" -f "TOTAL", $nowTotal, $unjustifiedTotal) }
}

# --- rule-level severity: what could fail the build then, must still be able to now -----------------------
# The counting kinds cannot see this one. A rule moved from error to suggestion is not a new line, it is
# the SAME line saying less - and a repo that curates its ruleset adds and removes such lines legitimately
# every time it adopts or declines a rule. So this comparison is per RULE and it runs one way: a rule that
# was ENFORCED at any commit in the window must still be enforced. Adopting a new rule is free. Declining
# a rule this repo never enforced is free - that is what KS6.1's curation was. Quietly taking the teeth
# out of one it did enforce is not.
#
# 'Enforced' means error or warning, deliberately the same bucket: TreatWarningsAsErrors is true here and
# ratchet.ps1 section 2 keeps it that way, so a warning fails the build exactly as an error does.
# Everything else - suggestion, silent, none, no line at all - is the other bucket, "does not fail the
# build". Two buckets rather than five ranks is what keeps this free of false positives on tidy-up commits.
function Test-Enforced { param([string]$Sev) ; return ($Sev -eq "error" -or $Sev -eq "warning") }

# .editorconfig is section-scoped: the same rule id appears at the root and again under [tests/**/*.cs]
# with a different severity, and those are not the same setting. So the key is file + section + rule,
# which means reading the file in order. git grep returns matches in that order, so one grep matching BOTH
# section headers and severity lines reconstructs it in a single pass.
function Get-SeverityMap {
    param([string]$Ref)
    $map = @{}
    $curPath = ""; $section = "(root)"
    foreach ($r in (Get-MatchRecords -Pattern $SEVMAP_RE -PathSpec $CONFIGS -Ref $Ref)) {
        $text = $r.Text.Trim()
        if ($r.Path -ne $curPath) { $curPath = $r.Path; $section = "(root)" }
        if ($text.StartsWith("[", [System.StringComparison]::Ordinal)) { $section = $text; continue }
        $eq = $text.IndexOf("=", [System.StringComparison]::Ordinal)
        if ($eq -lt 0) { continue }
        $rule = $text.Substring(0, $eq).Trim()
        $val  = $text.Substring($eq + 1).Trim()
        $hash = $val.IndexOf("#", [System.StringComparison]::Ordinal)
        if ($hash -ge 0) { $val = $val.Substring(0, $hash).Trim() }
        $map[("{0} {1} {2}" -f $r.Path, $section, $rule)] = $val.ToLowerInvariant()
    }
    return $map
}

if ($anchorCommits.Count -gt 0) {
    $nowMap = Get-SeverityMap -Ref ""
    $nowEnforced = 0
    foreach ($v in $nowMap.Values) { if (Test-Enforced $v) { $nowEnforced++ } }

    $everEnforced = @{}
    foreach ($c in $anchorCommits) {
        $m = Get-SeverityMap -Ref $c
        foreach ($key in $m.Keys) { if (Test-Enforced $m[$key]) { $everEnforced[$key] = $c } }
    }

    $lost = New-Object System.Collections.Generic.List[string]
    foreach ($key in $everEnforced.Keys) {
        if (Test-Enforced $nowMap[$key]) { continue }
        $what = "the line is gone"
        if ($nowMap[$key]) { $what = "now '" + $nowMap[$key] + "'" }
        $at = [string]$everEnforced[$key]
        $lost.Add(("{0} ({1}, enforced at {2})" -f $key, $what, $at.Substring(0, [Math]::Min(9, $at.Length))))
    }

    if (-not $Quiet) {
        Write-Host ("analyzer-debt: {0,-20} bar={1,-4} now={2,-4} un-enforced={3}" -f "rules-enforced", $everEnforced.Count, $nowEnforced, $lost.Count)
    }
    if ($lost.Count -gt 0) {
        $failures.Add(("RULES QUIETLY UN-ENFORCED - {0} rule(s) that could fail this build no longer can: {1}. Declining a rule this repo never enforced is free; taking the teeth out of one it did is not." -f $lost.Count, ($lost -join "; ")))
    }
}

# --- verdict ----------------------------------------------------------------------------------------------
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "ANALYZER-DEBT GATE FAILED:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  * " + $f) -ForegroundColor Red }
    Write-Host ""
    Write-Host "There is no baseline file to edit: the bar is what this branch's own history measured." -ForegroundColor Red
    exit 1
}

Write-Host "analyzer-debt: OK - nothing silenced that was not silenced before, and every suppression says why." -ForegroundColor Green
exit 0
