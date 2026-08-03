# Fast gate: every repository this plan works in is committed and pushed.
#
# Runs after a session exits, so "uncommitted" is never legitimate work-in-flight — it is either
# something a session forgot or something it was about to sweep into the wrong commit. Both are
# worth catching in ninety seconds rather than at a stage boundary.
#
# The repo list is read from plan.json rather than hardcoded, so it cannot drift from the plan.
# The control room itself is exempt from the pushed check: conductor commits the tracker there
# every session and the plan may or may not push it.

$ErrorActionPreference = 'Continue'

$planPath = Join-Path $PSScriptRoot '..\plan.json'
if (-not (Test-Path $planPath)) {
    Write-Host "RED - cannot find plan.json at $planPath"
    exit 1
}

$plan = Get-Content $planPath -Raw | ConvertFrom-Json
$anchor = [System.IO.Path]::GetFullPath($plan.repo)

$targets = New-Object System.Collections.Generic.List[object]
$targets.Add([pscustomobject]@{ Path = $anchor; IsAnchor = $true })
foreach ($s in @($plan.satelliteRepos)) {
    if ([string]::IsNullOrWhiteSpace($s)) { continue }
    $targets.Add([pscustomobject]@{ Path = [System.IO.Path]::GetFullPath($s); IsAnchor = $false })
}

$problems = New-Object System.Collections.Generic.List[string]

foreach ($t in $targets) {
    $p = $t.Path
    $label = Split-Path $p -Leaf

    if (-not (Test-Path $p)) {
        $problems.Add("$label : declared in the plan but the path does not exist ($p)")
        continue
    }

    $dirty = & git -C $p status --porcelain 2>$null
    if ($LASTEXITCODE -ne 0) {
        $problems.Add("$label : not a git repository ($p)")
        continue
    }
    $dirtyLines = @($dirty | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($dirtyLines.Count -gt 0) {
        $shown = ($dirtyLines | Select-Object -First 5) -join ' ; '
        $problems.Add("$label : $($dirtyLines.Count) uncommitted change(s) - $shown")
    }

    if (-not $t.IsAnchor) {
        $branch = (& git -C $p branch --show-current 2>$null)
        if (-not [string]::IsNullOrWhiteSpace($branch)) {
            $branch = $branch.Trim()
            $upstream = & git -C $p rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($upstream)) {
                # No upstream yet is fine only if there is nothing to push.
                $head = & git -C $p rev-parse HEAD 2>$null
                $onRemote = & git -C $p branch -r --contains $head 2>$null
                if ([string]::IsNullOrWhiteSpace(($onRemote -join ''))) {
                    $problems.Add("$label : branch '$branch' has no upstream and its HEAD is on no remote branch - push it with the -u flag")
                }
            }
            else {
                $ahead = & git -C $p rev-list --count '@{u}..HEAD' 2>$null
                if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($ahead) -and [int]$ahead.Trim() -gt 0) {
                    $problems.Add("$label : branch '$branch' has $($ahead.Trim()) unpushed commit(s)")
                }
            }
        }
    }

    if ($dirtyLines.Count -eq 0) { Write-Host "  OK   $label" }
}

Write-Host ""
if ($problems.Count -gt 0) {
    Write-Host "RED - $($problems.Count) repository problem(s):"
    foreach ($p in $problems) { Write-Host "  * $p" }
    exit 1
}

Write-Host "GREEN - every declared repository is committed and pushed."
exit 0
