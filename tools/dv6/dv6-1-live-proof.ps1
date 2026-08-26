# DV6.1 live proof - the ledger issue class against the REAL GitHub API.
#
# What the fake cannot show and this can: that GitHub ACCEPTS the labels and the marker bodies
# conductor invents, that an issue created for a bug is a normal issue on a normal board, and
# above all that the lifetime rule holds on a real destination across three passes - the board
# closing while the ledger stays open, then the LEDGER closing one of its own.
#
# What it proves, in order:
#   1. a scratch run exists, with checkpoints, three filed bugs and a followups.md
#   2. --dry-run writes nothing and NAMES what it would create (bug: / followup: keys)
#   3. the real pass creates them: conductor:bug and conductor:followup labels, markers in the
#      bodies, all OPEN
#   4. `conductor bug fix` + a followups row flipped to CLOSED, and the NEXT pass closes exactly
#      those two issues - with a comment saying which side closed them
#   5. the run's own checkpoint cards are closed by the run; the still-open ledger issues are NOT
#   6. a third identical pass creates 0 and closes nothing - idempotent on a real replica
#
# Scratch only: its own state home, its own repo, its own PRIVATE GitHub repository (trap 5 - the
# real board mirror is the engine's job and no session touches shaahink/conductor by hand), and
# the fresh build from src/Conductor/bin (trap 2). It never runs tools/install.ps1 (trap 1) and
# never opens this repo's run.db.
# ASCII only (Windows PowerShell 5.1).

param(
    [string]$OutDir   = (Join-Path $env:TEMP "dv61-rig"),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$GhRepo   = "",              # owner/name; created PRIVATE if it does not exist
    [switch]$KeepRepo                    # leave the scratch GitHub repo behind for a human to read
)

$ErrorActionPreference = "Stop"
$env:CONDUCTOR_PLAN = $null              # trap 3: never inherit another rig's plan

$exe = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
if (-not (Test-Path $exe)) { throw "build first: dotnet build Conductor.slnx  (missing $exe)" }

$fails = 0
function Check($what, $ok, $detail) {
    if ($ok) { Write-Host ("  OK   " + $what) -ForegroundColor Green }
    else { $script:fails++; Write-Host ("  FAIL " + $what + "  <- " + $detail) -ForegroundColor Red }
}
# gh writes ordinary refusals to stderr, and under ErrorActionPreference=Stop PowerShell 5.1
# turns a native command's stderr into a TERMINATING NativeCommandError. So every gh call goes
# through here, which drops to Continue for the length of the call and hands back the exit code
# as data - "this repository does not exist" is an ANSWER, not a crash.
# NAMED Hub, not Gh: PowerShell resolves command names case-INSENSITIVELY, so a function called
# Gh swallows every `gh` call inside itself and the script dies with a call-depth overflow after
# a minute of looking like a network hang. Measured here, once.
function Hub {
    $old = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $out = (& gh.exe @args 2>&1 | Out-String)
        return New-Object psobject -Property @{ Out = $out; Code = $LASTEXITCODE }
    } finally { $ErrorActionPreference = $old }
}
# MEASURED here, and it is the same thing KS9.2 measured from the other side: the issues LIST is a
# read replica and does not show what was just created. The ENGINE is immune (it decides from the
# fold and its local map); a rig that reads the list back is not, so it waits for the replica to
# catch up rather than reporting the lag as a missing issue.
function WaitForIssues($repo, $expected, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ($true) {
        $all = GhJson ("repos/" + $repo + "/issues?state=all&per_page=100")
        if ($all.Count -ge $expected -or (Get-Date) -gt $deadline) { return $all }
        Start-Sleep -Seconds 2
    }
}
function GhJson($path) {
    # trap 12: never --jq from PowerShell when the filter has quotes. Raw json, ConvertFrom-Json.
    $r = Hub api $path
    if ($r.Code -ne 0) { throw ("gh api " + $path + " failed: " + $r.Out) }
    return ($r.Out | ConvertFrom-Json)
}

# ---------------------------------------------------------------- 1. the scratch run

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$stateHome = Join-Path $OutDir "state-home"
$repo      = Join-Path $OutDir "scratch-repo"
New-Item -ItemType Directory -Path $stateHome | Out-Null
New-Item -ItemType Directory -Path $repo | Out-Null
$env:CONDUCTOR_STATE_HOME = $stateHome
Set-Location $OutDir     # trap 3: never let a verb fall back to discovering THIS repo's plan

Push-Location $repo
& git init -q 2>&1 | Out-Null
& git config user.email "rig@example.com" | Out-Null
& git config user.name "dv61 rig" | Out-Null
Pop-Location

Set-Content -Path (Join-Path $repo "TRACKER.md") -Encoding ASCII -Value @(
    "# dv61 rig",
    "",
    "## Checkpoints",
    "",
    "| # | Checkpoint | Status | Commit | Evidence |",
    "|---|---|---|---|---|",
    "| DV6.1 | the ledger issue class | TODO | | |",
    "| DV6.2 | the columns | TODO | | |")

$plan = Join-Path $repo "conductor.plan.json"
@{
    name    = "dv61-rig"
    repo    = $repo
    tracker = "TRACKER.md"
    stages  = @(@{ id = "DV6"; title = "the record that gets out"; sessions = 1 })
    agent   = @{ command = "cmd"; args = @("/c", "echo {prompt}") }
} | ConvertTo-Json -Depth 6 | Set-Content -Path $plan -Encoding ASCII
if (-not (Test-Path $plan)) { throw "the scratch plan was not written" }

Push-Location $repo
& git add -A 2>&1 | Out-Null
& git commit -q -m "rig" 2>&1 | Out-Null
Pop-Location

Write-Host "`n== 1. a scratch run, three bugs, a followups file ==" -ForegroundColor Cyan
& $exe run -p $plan --once --headless --no-control-plane *> (Join-Path $OutDir "run.log")
$db = Join-Path $repo ".conductor\run.db"
if (-not (Test-Path $db)) { $db = (Get-ChildItem -Path $stateHome -Filter "run.db" -Recurse | Select-Object -First 1).FullName }
Check "the scratch run wrote a run.db" ($db -and (Test-Path $db)) "no run.db under $repo or $stateHome"

& $exe bug new "the courier does not upload files" -p $plan --severity high *> (Join-Path $OutDir "bug1.log")
& $exe bug new "a note stores only its first line" -p $plan --severity medium *> (Join-Path $OutDir "bug2.log")
& $exe bug new "this one gets fixed between passes" -p $plan --severity low *> (Join-Path $OutDir "bug3.log")
$bugList = (& $exe bug list -p $plan 2>&1 | Out-String)
Check "three bugs are on the scratch ledger" (([regex]::Matches($bugList, "\bopen\b")).Count -ge 3) $bugList

New-Item -ItemType Directory -Force -Path (Join-Path $repo ".conductor") | Out-Null
Set-Content -Path (Join-Path $repo ".conductor\followups.md") -Encoding ASCII -Value @(
    "# Tracked followups",
    "",
    "| id | item | detail | owning stage | status |",
    "|---|---|---|---|---|",
    "| FU-DV6-1 | the digest says nothing about the ledger | one line | DV6 | OPEN |",
    "| FU-DV6-2 | owner-gated, and stated as such | - | next | **OPEN, owner-gated** - the owner |",
    "| FU-DV6-3 | long since done | - | DV6 | CLOSED (abc1234) |")

# ---------------------------------------------------------------- the destination

if ([string]::IsNullOrWhiteSpace($GhRepo)) {
    $owner = (GhJson "user").login
    $GhRepo = "$owner/dv61-ledger-scratch"
}
$env:CONDUCTOR_GITHUB_TOKEN = (Hub auth token).Out.Trim()
if (-not $env:CONDUCTOR_GITHUB_TOKEN) { throw "no gh token - run gh auth login" }

if ((Hub repo view $GhRepo).Code -eq 0) {
    Write-Host ("  a scratch repo of that name exists - deleting it: " + $GhRepo) -ForegroundColor DarkGray
    $null = Hub repo delete $GhRepo --yes
}
$made = Hub repo create $GhRepo --private --description "DV6.1 scratch - delete me"
Set-Content -Path (Join-Path $OutDir "ghcreate.log") -Value $made.Out -Encoding ASCII
if ($made.Code -ne 0) { throw ("could not create the scratch repository " + $GhRepo + ": " + $made.Out) }
Write-Host ("  destination: " + $GhRepo + " (private)") -ForegroundColor DarkGray

# ---------------------------------------------------------------- 2. the dry run

Write-Host "`n== 2. --dry-run names the ledger and writes nothing ==" -ForegroundColor Cyan
$dry = (& $exe github sync --backfill $db --repo $GhRepo --dry-run -p $plan 2>&1 | Out-String)
Set-Content -Path (Join-Path $OutDir "dry.log") -Value $dry -Encoding ASCII
Check "the dry run says it would create the bug and followup keys" `
    (($dry -match "created") -and ($dry -match "0 errors")) $dry
$afterDry = GhJson ("repos/" + $GhRepo + "/issues?state=all&per_page=100")
Check "and wrote NOTHING: the repository is still empty" ($afterDry.Count -eq 0) ("issues: " + $afterDry.Count)

# ---------------------------------------------------------------- 3. the real pass

Write-Host "`n== 3. the first real pass ==" -ForegroundColor Cyan
$first = (& $exe github sync --backfill $db --repo $GhRepo -p $plan 2>&1 | Out-String)
Set-Content -Path (Join-Path $OutDir "pass1.log") -Value $first -Encoding ASCII
# 2 cards + 3 bugs + 2 followups + 1 run diary
$issues = WaitForIssues $GhRepo 8 60

function Labels($i) { return @($i.labels | ForEach-Object { $_.name }) }
function ByMarker($all, $marker) { return @($all | Where-Object { $_.body -and $_.body.Contains($marker) })[0] }

$bugIssues = @($issues | Where-Object { (Labels $_) -contains "conductor:bug" })
$fuIssues  = @($issues | Where-Object { (Labels $_) -contains "conductor:followup" })
$cards     = @($issues | Where-Object { $_.body -and $_.body.Contains("<!-- conductor:task ") })

Check "three bug issues, labelled conductor:bug"        ($bugIssues.Count -eq 3) ("got " + $bugIssues.Count)
Check "two followup issues - the CLOSED row is NOT one" ($fuIssues.Count -eq 2)  ("got " + $fuIssues.Count)
Check "the checkpoint cards are there too, in their own class" ($cards.Count -ge 2) ("got " + $cards.Count)
Check "every ledger issue is OPEN" `
    ((@($bugIssues + $fuIssues) | Where-Object { $_.state -ne "open" }).Count -eq 0) "one was created closed"
$high = @($bugIssues | Where-Object { (Labels $_) -contains "conductor:severity:high" })
Check "severity rides along as a label"  ($high.Count -eq 1) ("got " + $high.Count)
Check "a bug issue carries no task marker" `
    ((@($bugIssues | Where-Object { $_.body.Contains("<!-- conductor:task ") })).Count -eq 0) "a ledger issue looks like a card"
Check "the lifetime is stated on the issue itself" `
    ($bugIssues[0].body -match "kept by the LEDGER") $bugIssues[0].body

# ---------------------------------------------------------------- 4/5. the ledger closes its own

# SETTLE before the second pass, and this is not politeness. The read-only backfill carries no
# persistent map (bug #79, measured by this very rig): if the listing it reads is still missing an
# issue this pass created, it creates a second one. The claim under test here is a LIFETIME, not
# GitHub's replica lag, so the rig waits for the destination to agree with itself first.
Start-Sleep -Seconds 15
$settled = WaitForIssues $GhRepo 8 90
Check "the destination has settled before the second pass" ($settled.Count -ge 8) ("issues: " + $settled.Count)

Write-Host "`n== 4. the ledger closes one, the run closes the board ==" -ForegroundColor Cyan
$doomed = @($bugIssues | Where-Object { $_.title -match "fixed between passes" })[0]
if (-not $doomed) { throw "the rig lost track of the bug it meant to fix" }
$doomedId = [int]($doomed.title -replace "^bug #(\d+).*$", '$1')
& $exe bug fix $doomedId -p $plan *> (Join-Path $OutDir "bugfix.log")

(Get-Content (Join-Path $repo ".conductor\followups.md")) `
    -replace "\| FU-DV6-1 \| the digest says nothing about the ledger \| one line \| DV6 \| OPEN \|",
             "| FU-DV6-1 | the digest says nothing about the ledger | one line | DV6 | CLOSED (deadbee) |" `
    | Set-Content -Path (Join-Path $repo ".conductor\followups.md") -Encoding ASCII

$second = (& $exe github sync --backfill $db --repo $GhRepo -p $plan 2>&1 | Out-String)
Set-Content -Path (Join-Path $OutDir "pass2.log") -Value $second -Encoding ASCII
Check "the second pass creates NOTHING - every entry already has its issue" ($second -match "0 created") $second
$after = WaitForIssues $GhRepo 8 60

$closedBug = ByMarker $after ("<!-- conductor:bug " + $doomedId + " -->")
$closedFu  = ByMarker $after "<!-- conductor:followup FU-DV6-1 -->"
$openBug   = @($after | Where-Object { (Labels $_) -contains "conductor:bug" -and $_.title -match "courier" })[0]
$openFu    = ByMarker $after "<!-- conductor:followup FU-DV6-2 -->"

Check "the fixed bug's issue is CLOSED"            ($closedBug.state -eq "closed") ("state=" + $closedBug.state)
Check "and relabelled conductor:status:fixed"      ((Labels $closedBug) -contains "conductor:status:fixed") ((Labels $closedBug) -join ",")
Check "the CLOSED followup row's issue is CLOSED"  ($closedFu.state -eq "closed") ("state=" + $closedFu.state)
Check "the bug nobody fixed is STILL OPEN"         ($openBug.state -eq "open") ("state=" + $openBug.state)
Check "the prose-OPEN followup is STILL OPEN"      ($openFu.state -eq "open") ("state=" + $openFu.state)

$comments = GhJson ("repos/" + $GhRepo + "/issues/" + $closedBug.number + "/comments")
Check "the close says which side closed it" `
    ((@($comments | Where-Object { $_.body -match "no longer lists this bug as open" })).Count -eq 1) `
    (($comments | ForEach-Object { $_.body }) -join " | ")

# ---------------------------------------------------------------- 6. idempotence

Write-Host "`n== 5. a third identical pass ==" -ForegroundColor Cyan
$third = (& $exe github sync --backfill $db --repo $GhRepo -p $plan 2>&1 | Out-String)
Set-Content -Path (Join-Path $OutDir "pass3.log") -Value $third -Encoding ASCII
Check "creates nothing"  ($third -match "0 created") $third
Check "and errors on nothing" ($third -match "0 errors") $third
$final = WaitForIssues $GhRepo $after.Count 30
Check "the issue count did not move" ($final.Count -eq $after.Count) ("was " + $after.Count + ", now " + $final.Count)

# ---------------------------------------------------------------- the verdict

Write-Host ""
if (-not $KeepRepo) {
    $null = Hub repo delete $GhRepo --yes
    Write-Host ("scratch repository " + $GhRepo + " deleted") -ForegroundColor DarkGray
} else {
    Write-Host ("scratch repository kept: https://github.com/" + $GhRepo) -ForegroundColor DarkGray
}
if ($fails -eq 0) { Write-Host "DV6.1 LIVE PROOF: all checks passed" -ForegroundColor Green; exit 0 }
Write-Host ("DV6.1 LIVE PROOF: " + $fails + " check(s) failed") -ForegroundColor Red
exit 1
