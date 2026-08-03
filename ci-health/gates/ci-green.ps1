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
# Usage: ci-green.ps1 -Repos site,conductor[,...] [-Owner shaahink]

param(
    [Parameter(Mandatory = $true)][string]$Repos,
    [string]$Owner = 'shaahink'
)

$ErrorActionPreference = 'Continue'
$problems = New-Object System.Collections.Generic.List[string]
$skipped = 0
$checked = 0

foreach ($raw in $Repos.Split(',')) {
    $repo = $raw.Trim()
    if ([string]::IsNullOrWhiteSpace($repo)) { continue }
    $slug = "$Owner/$repo"

    $default = & gh api "repos/$slug" --jq '.default_branch' 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($default)) {
        $problems.Add("$slug : cannot read the repository (auth, network, or it does not exist)")
        continue
    }
    $default = $default.Trim()

    $wf = & gh api "repos/$slug/actions/workflows" --paginate --jq '.workflows[] | select(.state == "active") | "\(.id)|\(.name)"' 2>$null
    if ($LASTEXITCODE -ne 0) {
        $problems.Add("$slug : cannot list workflows")
        continue
    }
    if ($null -eq $wf) { $wf = @() }

    foreach ($line in @($wf)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parts = $line.Split('|', 2)
        $wfId = $parts[0]
        $wfName = $parts[1]

        # Dependabot's synthetic workflow has no real runs to judge.
        if ($wfName -eq 'Dependabot Updates') { continue }

        $run = & gh run list --repo $slug --workflow $wfId --branch $default --limit 1 `
            --json databaseId, conclusion, status, headSha `
            --jq '.[0] | "\(.databaseId)|\(.conclusion)|\(.status)|\(.headSha)"' 2>$null

        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($run) -or $run.StartsWith('|')) {
            Write-Host "  SKIP $slug / $wfName - no runs on $default (correct for a reusable workflow)"
            $skipped++
            continue
        }

        $rp = $run.Split('|')
        $runId = $rp[0]
        $concl = $rp[1]
        $status = $rp[2]
        $checked++

        if ($status -ne 'completed') {
            $problems.Add("$slug / $wfName : latest run on $default is still $status (run $runId) - it has not finished, so it is not evidence")
            continue
        }
        if ($concl -eq 'success') {
            Write-Host "  OK   $slug / $wfName - run $runId success on $default"
            continue
        }
        $problems.Add("$slug / $wfName : latest run on $default concluded '$concl' (run $runId) - https://github.com/$slug/actions/runs/$runId")
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
