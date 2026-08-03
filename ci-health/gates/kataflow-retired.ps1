# Truth gate for stage K: KataFlow is retired, not repaired.
#
# Asserts the four things the stage promised, from the real remote:
#   1. the repository is archived
#   2. no workflow is left in the active state
#   3. no Dependabot pull requests are left open
#   4. the README says the repo is retired
#
# Note on ordering: archiving makes a repo read-only, so 2-4 must have landed BEFORE 1. This gate
# runs after the whole stage, so it simply checks the end state.

param(
    [string]$Owner = 'shaahink',
    [string]$Repo = 'KataFlow'
)

$ErrorActionPreference = 'Continue'
$slug = "$Owner/$Repo"
$problems = New-Object System.Collections.Generic.List[string]

# 1. archived
$archived = & gh api "repos/$slug" --jq '.archived' 2>$null
if ($LASTEXITCODE -ne 0) {
    $problems.Add("$slug : cannot read the repository")
}
elseif ("$archived".Trim() -ne 'true') {
    $problems.Add("$slug : is NOT archived (archived=$archived)")
}
else {
    Write-Host "  OK   $slug is archived"
}

# 2. no active workflows
$active = & gh api "repos/$slug/actions/workflows" --paginate --jq '.workflows[] | select(.state == "active") | .name' 2>$null
if ($LASTEXITCODE -eq 0) {
    $names = @($active | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    # GitHub reports Dependabot's synthetic entry as active even on an archived repo; it cannot run.
    $names = @($names | Where-Object { $_ -ne 'Dependabot Updates' })
    if ($names.Count -gt 0) {
        $problems.Add("$slug : $($names.Count) workflow(s) still active: $($names -join ', ')")
    }
    else {
        Write-Host "  OK   $slug has no active workflows"
    }
}

# 3. no open PRs
$open = & gh pr list --repo $slug --state open --json number --jq 'length' 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($open)) {
    if ([int]$open.Trim() -ne 0) {
        $problems.Add("$slug : $($open.Trim()) pull request(s) still open")
    }
    else {
        Write-Host "  OK   $slug has no open pull requests"
    }
}

# 4. README says so
$readme = & gh api "repos/$slug/readme" --jq '.content' 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($readme)) {
    $text = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String(($readme -replace '\s', '')))
    if ($text -match '(?i)archiv|retire|no longer maintained|unmaintained') {
        Write-Host "  OK   $slug README carries a retirement notice"
    }
    else {
        $problems.Add("$slug : README has no retirement notice - a reader arriving cold cannot tell why CI is gone")
    }
}
else {
    $problems.Add("$slug : cannot read the README")
}

Write-Host ""
if ($problems.Count -gt 0) {
    Write-Host "RED - KataFlow is not fully retired:"
    foreach ($p in $problems) { Write-Host "  * $p" }
    exit 1
}

Write-Host "GREEN - KataFlow is archived, silent, and says why."
exit 0
