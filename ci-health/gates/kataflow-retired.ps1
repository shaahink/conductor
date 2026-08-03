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

# NOTE ON QUOTING: no --jq flag anywhere here. Windows PowerShell 5.1 does not escape double
# quotes when handing arguments to a native exe, so a jq filter containing them arrives mangled
# and gh fails with a bare non-zero exit that reads like an auth problem. Parse in-process.

$ErrorActionPreference = 'Continue'
$slug = "$Owner/$Repo"
$problems = New-Object System.Collections.Generic.List[string]

function Invoke-GhJson {
    param([string[]]$GhArgs)
    $raw = & gh @GhArgs 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $text = ($raw -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    try { return $text | ConvertFrom-Json } catch { return $null }
}

# 1. archived
$info = Invoke-GhJson @('api', "repos/$slug")
if ($null -eq $info) {
    $problems.Add("$slug : cannot read the repository")
}
elseif (-not $info.archived) {
    $problems.Add("$slug : is NOT archived")
}
else {
    Write-Host "  OK   $slug is archived"
}

# 2. no active workflows
$wfDoc = Invoke-GhJson @('api', "repos/$slug/actions/workflows?per_page=100")
if ($null -ne $wfDoc) {
    # GitHub reports Dependabot's synthetic entry as active even on an archived repo; it cannot run.
    $names = @(@($wfDoc.workflows) |
        Where-Object { $_.state -eq 'active' -and $_.name -ne 'Dependabot Updates' } |
        ForEach-Object { $_.name })
    if ($names.Count -gt 0) {
        $problems.Add("$slug : $($names.Count) workflow(s) still active: $($names -join ', ')")
    }
    else {
        Write-Host "  OK   $slug has no active workflows"
    }
}

# 3. no open PRs
$prs = Invoke-GhJson @('pr', 'list', '--repo', $slug, '--state', 'open', '--limit', '100', '--json', 'number')
$openCount = @($prs).Count
if ($openCount -gt 0) {
    $problems.Add("$slug : $openCount pull request(s) still open")
}
else {
    Write-Host "  OK   $slug has no open pull requests"
}

# 4. README says so
$readmeDoc = Invoke-GhJson @('api', "repos/$slug/readme")
if ($null -ne $readmeDoc -and -not [string]::IsNullOrWhiteSpace($readmeDoc.content)) {
    $text = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String(($readmeDoc.content -replace '\s', '')))
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
