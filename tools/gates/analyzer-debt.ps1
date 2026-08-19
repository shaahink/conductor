<#
.SYNOPSIS
  Analyzer-debt ratchet (KS6.2). Counts every way this repo can silence an analyzer, and refuses a tree
  that silences more than the anchor commit did.

.DESCRIPTION
  ratchet.ps1 already counts ONE spelling of analyzer debt - '#pragma warning disable' under src/ - and
  compares it to a number written in tools/gates/ratchet-baseline.json. Both halves of that are weak:

    THE COUNT IS ONE OF FIVE. A pragma is the honest way to silence a rule; it is also the only way this
    repo was measuring. [SuppressMessage], NoWarn, a severity downgrade in .editorconfig, and a pragma
    parked under tests/ or tools/ (outside the old PathSpec) all silence the same analyzer and none of
    them moved the number. The dangerous case is not a new suppression, it is a MIGRATION: delete a
    pragma, add 'dotnet_diagnostic.MA0045.severity = none', and the old gate reports an improvement while
    a single site's suppression just became the whole repo's. This script counts all five KINDS and names
    each one, so a migration shows up as a rise in the kind it moved TO.

    THE BAR WAS A NUMBER IN A FILE THE SAME COMMIT EDITS. An agent that wants a suppression can add one
    and raise the ceiling in the same breath. ratchet.ps1 patches this by diffing the baseline file
    against the pushed branch; this script removes the file instead. THERE IS NO BASELINE FILE HERE. The
    bar is MEASURED, by this same code, against an anchor commit - origin/<branch> by default, which is
    what is already pushed. A commit cannot change what a previous commit contained, so the referee is
    not reachable from inside the game.

  Two rules:

    1. UNJUSTIFIED SUPPRESSIONS MUST BE ZERO. Absolute, no anchor needed. Every suppression states its
       reason ON ITS OWN LINE, where the next reader hits it. This is the rule that turns hygiene into
       design: you may keep a suppression, but you must be able to say why in one line.

    2. NO KIND MAY GROW AGAINST THE ANCHOR. Per kind, not in total, because a total lets one kind pay
       for another - which is exactly what laundering a pragma into a global downgrade looks like.

  WHAT PROTECTS THIS SCRIPT: ratchet.ps1 section 3d fails if anything under tools/gates differs from the
  pushed branch, so editing this referee is caught by the other one, and deleting the call to it from
  ratchet.ps1 is the same diff. Neither is escapable without the other noticing. Proof of both, seeded on
  purpose, is in .conductor/evidence/KS6/.

  Measurement is 'git grep' for both sides, so the anchor is read straight out of the object database -
  no checkout, no stash, no working tree to get wrong, and the identical regex on both sides.

  ASCII only, on purpose: Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI, and a gate that
  fails to parse is worse than no gate at all.
#>
[CmdletBinding()]
param(
    [string]$Anchor = "",
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

function Test-RefExists { param([string]$Ref) ; $null = & git rev-parse --verify --quiet $Ref 2>&1 ; return ($LASTEXITCODE -eq 0) }

function Resolve-Anchor {
    if ($Anchor) { return $Anchor }
    if ($env:CONDUCTOR_ANALYZER_ANCHOR) { return $env:CONDUCTOR_ANALYZER_ANCHOR }
    if ($env:CONDUCTOR_BASE_REF) { return $env:CONDUCTOR_BASE_REF }
    $branch = (& git rev-parse --abbrev-ref HEAD 2>&1 | Out-String).Trim()
    if ($branch -and (Test-RefExists "origin/$branch")) { return "origin/$branch" }
    if (Test-RefExists "origin/master") { return "origin/master" }
    return ""
}

# 'git grep -n' prints 'path:line:text', and with a treeish 'ref:path:line:text'. Only the TEXT is needed
# beyond the count, so the leading fields are peeled off positionally and the rest is left intact - a
# naive split on ':' would cut C# and XML lines in half.
function Get-MatchTexts {
    param([string]$Pattern, [string[]]$PathSpec, [string]$Ref)
    $gitArgs = @("grep", "-I", "-n", "-E", $Pattern)
    if ($Ref) { $gitArgs += $Ref }
    $gitArgs += "--"
    $gitArgs += $PathSpec
    $lines = Invoke-GitLines $gitArgs
    $texts = New-Object System.Collections.Generic.List[string]
    foreach ($l in $lines) {
        $rest = $l
        if ($Ref) {
            $i = $rest.IndexOf(($Ref + ":"), [System.StringComparison]::Ordinal)
            if ($i -eq 0) { $rest = $rest.Substring($Ref.Length + 1) }
        }
        # peel 'path:' then 'line:'
        $c1 = $rest.IndexOf(":", [System.StringComparison]::Ordinal)
        if ($c1 -lt 0) { continue }
        $rest = $rest.Substring($c1 + 1)
        $c2 = $rest.IndexOf(":", [System.StringComparison]::Ordinal)
        if ($c2 -lt 0) { continue }
        $texts.Add($rest.Substring($c2 + 1))
    }
    return $texts
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

# --- the five kinds -----------------------------------------------------------------------------------
# Each kind is (name, regex, pathspec, how a reason is spelled here, how many suppressions a line is).
# Adding a sixth kind is a one-entry edit; that is the point of the shape.
$PRAGMA_RE   = '#pragma[ \t]+warning[ \t]+disable'
$SUPPRESS_RE = 'SuppressMessage[ \t]*\('
$NOWARN_RE   = '<(NoWarn|WarningsNotAsErrors)>'
$DOWNGRADE_RE = '^[ \t]*dotnet_(diagnostic\.[^.]+|analyzer_diagnostic(\.category-[^.]+)?)\.severity[ \t]*=[ \t]*(none|silent|suggestion)'

$kinds = @(
    @{ Name = "pragma-src";          Pattern = $PRAGMA_RE;    Paths = @("src/*.cs");
       Marker = "//";   Note = "#pragma warning disable under src/" },
    @{ Name = "pragma-tests-tools";  Pattern = $PRAGMA_RE;    Paths = @("tests/*.cs", "tools/*.cs");
       Marker = "//";   Note = "the same pragma parked where the old gate never looked" },
    @{ Name = "suppressmessage";     Pattern = $SUPPRESS_RE;  Paths = @("*.cs");
       Marker = "//";   Note = "[SuppressMessage] attributes; a reason here means Justification=" },
    @{ Name = "nowarn";              Pattern = $NOWARN_RE;    Paths = @("*.props", "*.csproj", "*.targets");
       Marker = "<!--"; Note = "NoWarn / WarningsNotAsErrors in build files" },
    @{ Name = "severity-downgrade";  Pattern = $DOWNGRADE_RE; Paths = @(".editorconfig", "*.editorconfig", "*.globalconfig");
       Marker = "#";    Note = "severity = none/silent/suggestion. 'warning' is NOT counted: TreatWarningsAsErrors is true here, so a warning still fails the build" }
)

# [SuppressMessage] spells its reason as a named argument, not a comment. Same rule, different syntax.
function Test-KindReason {
    param([hashtable]$Kind, [string]$Text)
    if ($Kind.Name -eq "suppressmessage") {
        if ($Text -notmatch 'Justification[ \t]*=') { return $false }
        return ($Text -match 'Justification[ \t]*=[ \t]*"[^"]{12,}"')
    }
    return (Test-HasReason -Text $Text -Marker $Kind.Marker)
}

function Measure-Kind {
    param([hashtable]$Kind, [string]$Ref)
    $texts = Get-MatchTexts -Pattern $Kind.Pattern -PathSpec $Kind.Paths -Ref $Ref
    $total = 0; $unjustified = 0
    $examples = New-Object System.Collections.Generic.List[string]
    foreach ($t in $texts) {
        $total++
        if (-not (Test-KindReason -Kind $Kind -Text $t)) {
            $unjustified++
            if ($examples.Count -lt 5) { $examples.Add($t.Trim()) }
        }
    }
    return [pscustomobject]@{ Total = $total; Unjustified = $unjustified; Examples = $examples }
}

# --- measure now, and at the anchor --------------------------------------------------------------------
$anchorRef = Resolve-Anchor
if (-not $Quiet) {
    if ($anchorRef) { Write-Host ("analyzer-debt: anchor is {0} (measured from git, not from a file in this commit)" -f $anchorRef) }
    else { Write-Host "analyzer-debt: no anchor ref available - growth checks skipped, the zero-unjustified rule still applies." }
}

$nowTotal = 0; $anchorTotal = 0; $unjustifiedTotal = 0
foreach ($k in $kinds) {
    $now = Measure-Kind -Kind $k -Ref ""
    $nowTotal += $now.Total
    $unjustifiedTotal += $now.Unjustified

    if ($anchorRef) {
        $was = Measure-Kind -Kind $k -Ref $anchorRef
        $anchorTotal += $was.Total
        if (-not $Quiet) {
            Write-Host ("analyzer-debt: {0,-20} anchor={1,-4} now={2,-4} unjustified={3}" -f $k.Name, $was.Total, $now.Total, $now.Unjustified)
        }
        if ($now.Total -gt $was.Total) {
            $failures.Add(("SUPPRESSIONS ROSE - kind '{0}' went {1} -> {2} ({3}). Silencing the analyzer is not the fix; if a rule is genuinely wrong here, say so via 'conductor note' and take it out of the ruleset in the open." -f $k.Name, $was.Total, $now.Total, $k.Note))
        }
    }
    elseif (-not $Quiet) {
        Write-Host ("analyzer-debt: {0,-20} now={1,-4} unjustified={2}" -f $k.Name, $now.Total, $now.Unjustified)
    }

    if ($now.Unjustified -gt 0) {
        $sample = ($now.Examples -join " | ")
        $failures.Add(("UNJUSTIFIED SUPPRESSIONS - kind '{0}' has {1} with no reason on the line. Every suppression carries its reason where the next reader lands. Offenders: {2}" -f $k.Name, $now.Unjustified, $sample))
    }
}

if (-not $Quiet) {
    if ($anchorRef) { Write-Host ("analyzer-debt: TOTAL              anchor={0,-4} now={1,-4} unjustified={2}" -f $anchorTotal, $nowTotal, $unjustifiedTotal) }
    else            { Write-Host ("analyzer-debt: TOTAL              now={0,-4} unjustified={1}" -f $nowTotal, $unjustifiedTotal) }
}

# --- verdict --------------------------------------------------------------------------------------------
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "ANALYZER-DEBT GATE FAILED:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host ("  * " + $f) -ForegroundColor Red }
    Write-Host ""
    Write-Host "There is no baseline file to edit: the bar is what the anchor commit measured." -ForegroundColor Red
    exit 1
}

Write-Host "analyzer-debt: OK - nothing was silenced that was not silenced before, and every suppression says why." -ForegroundColor Green
exit 0
