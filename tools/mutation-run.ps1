# KS4.3 - the command a `class: mutation` gate runs.
#
# Its ONLY job is to produce a Stryker report. It deliberately does not decide anything: the
# threshold, the diff scoping and the comparison all live in the engine, because a gate that carries
# its own bar carries it inside the repository the coding agent edits, next to a runner with a dozen
# flags that each narrow what gets mutated. So this exits 0 whenever Stryker RAN, and the engine
# reads StrykerOutput/*/reports/mutation-report.json and scores it over the changed files.
#
# Windows PowerShell 5.1, ASCII only (repo rule).
[CmdletBinding()]
param(
    # Files to mutate, as Stryker glob patterns. Default: the whole of Conductor.Core, which is the
    # era-boundary shape. A per-session gate passes the files the session changed.
    [string[]] $Mutate = @(),
    [string]   $Project = 'Conductor.Core.csproj',
    [string]   $TestProject = 'tests/Conductor.Tests/Conductor.Tests.csproj',
    [int]      $Concurrency = 4,
    # Emit the Stryker patterns for every changed .cs file instead of taking -Mutate. This mirrors
    # the engine's own scope (Git.ChangedFiles) so a hand-run reproduces what the gate measured.
    [string]   $Since = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if ($Since) {
        $changed = @()
        $changed += (& git -C $root diff --name-only --diff-filter=d $Since)
        $changed += (& git -C $root ls-files --others --exclude-standard)
        $Mutate = @($changed | Where-Object { $_ -like '*.cs' } |
            Where-Object { $_ -like 'src/*' } | Sort-Object -Unique |
            ForEach-Object { '**/' + (Split-Path $_ -Leaf) })
        if ($Mutate.Count -eq 0) {
            Write-Host "mutation-run: nothing to mutate - no changed .cs under src/ against $Since"
            exit 0
        }
    }

    # NOT $args: that is an automatic variable in PowerShell and assigning to it is the kind of
    # shadowing that works until the day it does not.
    $strykerArgs = @('stryker', '--project', $Project, '--test-project', $TestProject,
                     '--reporter', 'json', '--reporter', 'progress',
                     '--concurrency', "$Concurrency")
    foreach ($m in $Mutate) { $strykerArgs += @('--mutate', $m) }

    Write-Host "mutation-run: dotnet $($strykerArgs -join ' ')"
    & dotnet @strykerArgs
    $code = $LASTEXITCODE

    # Stryker's own --break-at is deliberately NOT used, so a low score is not an error here. What IS
    # an error is Stryker failing to run at all - and the engine's fail-closed reading catches that
    # too, because a run that produced no report scores none of the changed files.
    if ($code -ne 0) {
        Write-Host "mutation-run: stryker exited $code"
        exit $code
    }
    $report = Get-ChildItem -Path (Join-Path $root 'StrykerOutput') -Recurse -Filter 'mutation-report.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($report) { Write-Host "mutation-run: report at $($report.FullName)" }
    exit 0
}
finally { Pop-Location }
