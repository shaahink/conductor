<#
.SYNOPSIS
PK4.1 live proof - the fresh build's `room` verb against a scratch state home, importing the owner's
old private telegram home by POINTING at it, and the KS11.1 golden replay with those rooms present.

.DESCRIPTION
Never touches the real courier home: CONDUCTOR_STATE_HOME is a temp directory for every engine call,
and the script checks the real rooms directory did not appear. Reads the old home's config.json only
to learn which ids must NOT appear in any output; prints no id, no footer string and no voice line.
The voice files are hashed before and after (the import must not touch them) and scanned for, in the
room files, line by line - the evidence carries the verdict, never the words.

Windows PowerShell 5.1, ASCII only (trap 15).
#>
param(
    [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$OldHome = (Join-Path $HOME '.claude\telegram'),
    [string]$Evidence = ''
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
if (-not $Evidence) { $Evidence = Join-Path $Repo '.conductor\evidence\PK4\pk4.1.md' }

$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("conductor-pk41-proof-" + [guid]::NewGuid().ToString('N'))
$stateHome = Join-Path $scratch 'state-home'
New-Item -ItemType Directory -Force -Path $stateHome | Out-Null
Remove-Item Env:CONDUCTOR_PLAN -ErrorAction SilentlyContinue
Remove-Item Env:CONDUCTOR_GOLDEN_REBASELINE -ErrorAction SilentlyContinue
$env:CONDUCTOR_STATE_HOME = $stateHome

$realRooms = Join-Path $env:LOCALAPPDATA 'conductor\courier\rooms'
$realRoomsBefore = Test-Path -LiteralPath $realRooms

$checks = New-Object System.Collections.Generic.List[string]
$failed = 0
function Check([string]$name, [bool]$ok) {
    $script:checks.Add(("{0}  {1}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name))
    if (-not $ok) { $script:failed++ }
}

function Engine([string[]]$VerbArgs) {
    # A native stderr line is an ErrorRecord in 5.1; under Stop it would throw instead of being captured.
    $ErrorActionPreference = 'Continue'
    Push-Location $scratch
    try {
        $all = @('run', '--project', (Join-Path $Repo 'src\Conductor'), '--no-build', '--') + $VerbArgs
        $out = & dotnet @all 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ Code = $LASTEXITCODE; Text = ($out -join "`n") }
    } finally { Pop-Location }
}

# What must never be printed: every chat id the old home holds. Learned here, never written out.
$ids = New-Object System.Collections.Generic.List[string]
$voices = @{}
Get-ChildItem -LiteralPath $OldHome -Directory | ForEach-Object {
    $cfg = Join-Path $_.FullName 'config.json'
    if (Test-Path -LiteralPath $cfg) {
        $json = Get-Content -Raw -Encoding UTF8 -LiteralPath $cfg | ConvertFrom-Json
        foreach ($p in $json.chats.PSObject.Properties) { $ids.Add(([string]$p.Value).TrimStart('-')) }
        $voice = Join-Path $_.FullName 'voice.md'
        if (Test-Path -LiteralPath $voice) { $voices[$_.Name] = $voice }
    }
}
function OldHomeHash() {
    (Get-ChildItem -LiteralPath $OldHome -Recurse -File | Sort-Object FullName |
        ForEach-Object {
            # .NET rather than Get-FileHash: 5.1 started from pwsh inherits a module path that hides it.
            $sha = [System.Security.Cryptography.SHA256]::Create()
            try { [BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($_.FullName))) } finally { $sha.Dispose() }
        }) -join ','
}
$oldHashBefore = OldHomeHash

$listBefore = Engine @('room', 'list')
$import = Engine @('room', 'import')
$listAfter = Engine @('room', 'list')
$importAgain = Engine @('room', 'import')
$show = Engine @('room', 'show', '--project', 'BookToCourse')
# show's footer lines are the project's own words: kept out of the evidence.
$showKept = ($show.Text -split "`n" | Where-Object { $_ -notmatch '^\s+(counters|live|pending)\s' }) -join "`n"

$names = @($listAfter.Text -split "`n" | Where-Object { $_ -match '^  \S' } | ForEach-Object { $_.Trim() })
Check "room list before the import offers BookToCourse, cv" ($listBefore.Code -eq 0 -and $listBefore.Text -match 'not imported yet from .*: BookToCourse, cv')
Check "room import exits 0 and imports 2" ($import.Code -eq 0 -and $import.Text -match '2 imported')
Check "room list after shows exactly BookToCourse, cv by name" ($listAfter.Code -eq 0 -and ($names -join ',') -eq 'BookToCourse,cv' -and $listAfter.Text -match 'rooms \(2\)')
Check "room list after offers nothing" ($listAfter.Text -notmatch 'not imported yet')
Check "a second import overwrites nothing (0 imported, 2 kept)" ($importAgain.Text -match '0 imported' -and ([regex]::Matches($importAgain.Text, 'kept ')).Count -eq 2)
Check "room show names the voice path and says observer set" ($show.Code -eq 0 -and $show.Text -match 'observer  set' -and $show.Text -match 'voice\.md')

$everything = $listBefore.Text + $import.Text + $listAfter.Text + $importAgain.Text + $show.Text
$leaks = @($ids | Where-Object { $_.Length -gt 0 -and $everything.Contains($_) }).Count
Check ("no chat id from the old home appears in any output ({0} ids checked)" -f $ids.Count) ($ids.Count -gt 0 -and $leaks -eq 0)

$roomsDir = Join-Path $stateHome 'courier\rooms'
foreach ($name in $voices.Keys) {
    $roomFile = Join-Path $roomsDir (($name.ToLowerInvariant()) + '.json')
    $room = Get-Content -Raw -Encoding UTF8 -LiteralPath $roomFile | ConvertFrom-Json
    Check ("{0}: the room points at its voice file" -f $name) ($room.voice -eq (Resolve-Path -LiteralPath $voices[$name]).Path)
    $raw = Get-Content -Raw -Encoding UTF8 -LiteralPath $roomFile
    $lines = @(Get-Content -Encoding UTF8 -LiteralPath $voices[$name] | Where-Object { $_.Trim().Length -ge 20 })
    $copied = @($lines | Where-Object { $raw.Contains($_.Trim()) }).Count
    Check ("{0}: no line of the voice is in the room file ({1} lines checked)" -f $name, $lines.Count) ($lines.Count -gt 0 -and $copied -eq 0)
}
Check "the old private home is byte-identical after two imports" ((OldHomeHash) -eq $oldHashBefore)
Check "the real courier home grew no rooms directory" ((Test-Path -LiteralPath $realRooms) -eq $realRoomsBefore)
$inRepo = @(Get-ChildItem -LiteralPath $Repo -Recurse -File -Filter 'booktocourse.json' -ErrorAction SilentlyContinue).Count
Check "no room file was written into the repository" ($inRepo -eq 0)

# KS11.1 golden replay with those rooms on the machine and no room for the replay's repo.
Push-Location $Repo
$ErrorActionPreference = 'Continue'
try {
    $golden = & dotnet test (Join-Path $Repo 'tests\Conductor.Tests') --no-build --filter 'FullyQualifiedName~KS11_1' 2>&1 | ForEach-Object { "$_" }
    $goldenCode = $LASTEXITCODE
    $goldenDiff = (& git status --porcelain -- 'tests/Conductor.Tests/testdata/ks11') -join "`n"
} finally { Pop-Location }
$goldenLine = ($golden | Where-Object { $_ -match '(Passed|Failed)!' }) -join ' '
Check "KS11.1 golden replay green with rooms present" ($goldenCode -eq 0 -and $goldenLine -match 'Failed:\s+0')
Check "KS11.1 goldens byte-identical (git status of testdata/ks11 empty, no rebaseline)" ($goldenDiff.Length -eq 0)

$head = (& git -C $Repo rev-parse --short HEAD)
$fence = '```'
$doc = @(
    '# PK4.1 - rooms in the courier home, the one-time import, the golden replay',
    '',
    ("Generated by ``tools/peyk/pk4-1-rooms-proof.ps1`` at {0:yyyy-MM-ddTHH:mm:ssZ} on commit {1}." -f (Get-Date).ToUniversalTime(), $head),
    'Engine: the fresh build, `dotnet run --project src/Conductor --no-build -- room ...`, with',
    'CONDUCTOR_STATE_HOME set to a scratch directory under the temp dir. The old private home read is the',
    "owner's ~/.claude/telegram; its chat ids, footer strings and voice files are POINTED AT and never",
    'printed here: the checks below learn them in memory and report only the verdict.',
    '',
    ("## Verdict: {0} of {1} checks pass" -f ($checks.Count - $failed), $checks.Count),
    '',
    $fence, ($checks -join "`n"), $fence,
    '',
    '## `room list` before the import (the offer)', '', $fence, $listBefore.Text, $fence, '',
    '## `room import`', '', $fence, $import.Text, $fence, '',
    '## `room list` after', '', $fence, $listAfter.Text, $fence, '',
    '## `room import` a second time', '', $fence, $importAgain.Text, $fence, '',
    '## `room show --project BookToCourse` (footer lines withheld: the project''s own words)', '', $fence, $showKept, $fence, '',
    '## KS11.1 golden replay (dotnet test --filter KS11_1, rooms present)', '', $fence, $goldenLine,
    ("git status --porcelain tests/Conductor.Tests/testdata/ks11: '{0}'" -f $goldenDiff), $fence
) -join "`n"

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Evidence) | Out-Null
[System.IO.File]::WriteAllText($Evidence, $doc + "`n", (New-Object System.Text.UTF8Encoding($false)))
Write-Output ($checks -join "`n")
Write-Output ("evidence: {0}  ({1} failed)" -f $Evidence, $failed)

Remove-Item Env:CONDUCTOR_STATE_HOME
try { Remove-Item -Recurse -Force -LiteralPath $scratch } catch { }
exit $failed
