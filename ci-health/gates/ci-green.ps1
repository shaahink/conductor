# Truth gate: for each named repo, every ACTIVE workflow's latest run on the DEFAULT BRANCH
# must have concluded successfully.
#
# Why the latest-run-per-workflow shape rather than check-runs on the head commit: check-runs
# only show workflows that the head commit itself triggered. A schedule-only or dispatch-only
# workflow (site's link check, Shamshir's release before it gained a dispatch trigger) never
# appears there, so a head-commit check would report a repo green while its actually-broken
# workflow sat red and untouched.
#
# A workflow with ZERO runs is reported and skipped, not failed: that is the correct state for
# a reusable workflow_call file, which only ever executes inside the repos that call it.
#
# NOTE ON QUOTING, learned the hard way at plan time: this script does NOT use gh's --jq flag.
# Windows PowerShell 5.1 does not escape double quotes when it hands arguments to a native exe,
# so a jq filter containing them - select(.state == "active") - arrives at gh mangled and the
# call fails with a bare non-zero exit. That read as "cannot list workflows" for every repo,
# which looks exactly like an auth problem and is not one. Fetch raw JSON, parse in-process.
#
# Usage: ci-green.ps1 -Repos site,conductor[,...] [-Owner shaahink]

param(
    [Parameter(Mandatory = $true)][string]$Repos,
    [string]$Owner = 'shaahink'
)

$ErrorActionPreference = 'Continue'
$problems = New-Object System.Collections.Generic.List[string]
$skipped = 0
$checked = 0

function Invoke-GhJson {
    param([string[]]$GhArgs)
    $raw = & gh @GhArgs 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $text = ($raw -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    try { return $text | ConvertFrom-Json } catch { return $null }
}

foreach ($raw in $Repos.Split(',')) {
    $repo = $raw.Trim()
    if ([string]::IsNullOrWhiteSpace($repo)) { continue }
    $slug = "$Owner/$repo"

    $info = Invoke-GhJson @('api', "repos/$slug")
    if ($null -eq $info -or [string]::IsNullOrWhiteSpace($info.default_branch)) {
        $problems.Add("$slug : cannot read the repository (auth, network, or it does not exist)")
        continue
    }
    $default = $info.default_branch

    $wfDoc = Invoke-GhJson @('api', "repos/$slug/actions/workflows?per_page=100")
    if ($null -eq $wfDoc) {
        $problems.Add("$slug : cannot list workflows")
        continue
    }

    foreach ($w in @($wfDoc.workflows)) {
        if ($w.state -ne 'active') { continue }
        # Dependabot's synthetic entry has no real runs to judge.
        if ($w.name -eq 'Dependabot Updates') { continue }

        $runs = Invoke-GhJson @('run', 'list', '--repo', $slug, '--workflow', "$($w.id)",
            '--branch', $default, '--limit', '1', '--json', 'databaseId,conclusion,status')

        $run = @($runs)[0]
        if ($null -eq $run) {
            Write-Host "  SKIP $slug / $($w.name) - no runs on $default (normal for a tag-triggered, pull-request-only or reusable workflow)"
            $skipped++
            continue
        }

        $checked++
        $runId = $run.databaseId

        if ($run.status -ne 'completed') {
            $problems.Add("$slug / $($w.name) : latest run on $default is still '$($run.status)' (run $runId) - it has not finished, so it is not evidence")
            continue
        }
        if ($run.conclusion -eq 'success') {
            Write-Host "  OK   $slug / $($w.name) - run $runId success on $default"
            continue
        }
        $problems.Add("$slug / $($w.name) : latest run on $default concluded '$($run.conclusion)' (run $runId) - https://github.com/$slug/actions/runs/$runId")
    }
}

Write-Host ""
Write-Host "checked $checked workflow(s), skipped $skipped with no runs on their default branch"

if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Host "RED - $($problems.Count) workflow(s) are not green:"
    foreach ($p in $problems) { Write-Host "  * $p" }
    Write-Host ""
    Write-Host "Reminder: a schedule-only or dispatch-only workflow does NOT get a fresh run from a"
    Write-Host "merge. If the fix landed but the default branch still shows the old red run, dispatch"
    Write-Host "the workflow on the default branch."
    exit 1
}

if ($checked -eq 0) {
    Write-Host "RED - nothing was actually checked; that is not a pass."
    exit 1
}

Write-Host "GREEN - every active workflow's latest run on its default branch succeeded."
exit 0
