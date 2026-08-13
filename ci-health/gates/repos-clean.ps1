# Fast gate: every repository this plan works in is committed and pushed.
#
# Runs after a session exits, so "uncommitted" is never legitimate work-in-flight — it is either
# something a session forgot or something it was about to sweep into the wrong commit. Both are
# worth catching in ninety seconds rather than at a stage boundary.
#
# The repo list is read from plan.json rather than hardcoded, so it cannot drift from the plan.
# The control room itself is exempt from the pushed check: conductor commits the tracker there
# every session and the plan may or may not push it.
#
# BASELINE: the owner had uncommitted work in some of these repos before this run started
# (Shamshir's research drivers and iteration notes, at plan time). Sessions are told not to touch
# those files, so failing the gate on them would be an unreachable bar - and an unreachable bar is
# an instruction to weaken something, which here would mean committing or reverting the owner's
# work. dirty-baseline.txt records that pre-existing set; only NEW dirt fails.

$ErrorActionPreference = 'Continue'

$baseline = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$baselinePath = Join-Path $PSScriptRoot 'dirty-baseline.txt'
if (Test-Path $baselinePath) {
    foreach ($b in Get-Content $baselinePath) {
        $b = $b.Trim()
        if ([string]::IsNullOrWhiteSpace($b) -or $b.StartsWith('#')) { continue }
        [void]$baseline.Add($b)
    }
}

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

    $newDirt = New-Object System.Collections.Generic.List[string]
    $baselined = 0
    foreach ($d in $dirtyLines) {
        # porcelain: two status chars, a space, then the path (rename shows "old -> new")
        $file = $d.Substring([Math]::Min(3, $d.Length)).Trim().Trim('"')
        if ($file -match '->') { $file = ($file -split '->')[-1].Trim().Trim('"') }
        if ($baseline.Contains("$label|$file")) { $baselined++; continue }
        $newDirt.Add($file)
    }
    if ($baselined -gt 0) {
        Write-Host "  note $label - $baselined pre-existing uncommitted file(s) ignored per dirty-baseline.txt"
    }
    if ($newDirt.Count -gt 0) {
        $shown = ($newDirt | Select-Object -First 5) -join ' ; '
        $problems.Add("$label : $($newDirt.Count) uncommitted change(s) this run did not start with - $shown")
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

    if ($newDirt.Count -eq 0) { Write-Host "  OK   $label" }
}

Write-Host ""
if ($problems.Count -gt 0) {
    Write-Host "RED - $($problems.Count) repository problem(s):"
    foreach ($p in $problems) { Write-Host "  * $p" }
    exit 1
}

Write-Host "GREEN - every declared repository is committed and pushed."
exit 0
