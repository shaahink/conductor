# SF6.1 proof rig. Spawns THIS working tree's engine (dotnet run, never the conductor on PATH)
# against two throwaway repos under $env:TEMP, one single-repo plan and one with a declared
# satellite, and captures the composed prompt each session actually received
# (.conductor/logs/session-001.prompt.md). No control plane, no port, no face: nothing here can
# touch the run driving this session or the second conductor run on this machine.
[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP 'sarban-proofs\sf6'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\.conductor\evidence\SF6')
)
$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')

function New-Rig {
    param([string]$Dir, [string[]]$Satellites)

    if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force }
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    Push-Location $Dir
    try {
        git init -b main -q
        git config user.email 'sf6@test'
        git config user.name 'SF6 rig'
        Set-Content -Path 'README.md' -Value '# sf6 rig' -Encoding utf8
        $tracker = @(
            '# SF6 rig',
            '',
            '## Handoff',
            'none.',
            '',
            '| # | Checkpoint | Status | Commit | Evidence |',
            '|---|---|---|---|---|',
            '| R1.1 | the rig checkpoint | TODO | | |'
        )
        Set-Content -Path 'TRACKER.md' -Value $tracker -Encoding utf8
        # The fake agent: reads nothing, does nothing, exits clean. The engine writes the composed
        # prompt to .conductor/logs regardless of what the child does with its argv.
        Set-Content -Path 'agent.cmd' -Value @('@echo off', 'echo rig agent ran', 'exit /b 0') -Encoding ascii
        git add -A
        git commit -q -m 'init' --no-gpg-sign
    } finally { Pop-Location }

    $plan = [ordered]@{
        name    = 'SF6 prompt rig'
        repo    = ($Dir -replace '\\', '/')
        tracker = 'TRACKER.md'
        planDoc = 'TRACKER.md'
        stages  = @(@{ id = 'R1'; title = 'Rig stage'; sessions = 1 })
        agent   = [ordered]@{ command = 'cmd.exe'; args = @('/c', (Join-Path $Dir 'agent.cmd'), '{prompt}'); provider = 'claude'; output = 'stream-json' }
        gates   = @(@{ name = 'smoke'; command = 'cmd /c exit 0'; tier = 'fast'; timeoutMinutes = 1 })
        limits  = @{ maxSessions = 1 }
        report  = @{ commit = $false }
    }
    if ($Satellites) { $plan['satelliteRepos'] = $Satellites }

    $planPath = Join-Path $Dir 'rig.plan.json'
    ($plan | ConvertTo-Json -Depth 8) | Set-Content -Path $planPath -Encoding utf8
    return $planPath
}

function Invoke-Rig {
    param([string]$PlanPath, [string]$Label)

    Write-Host "== $Label ==" -ForegroundColor Cyan
    Push-Location $repoRoot
    try {
        dotnet run --project src/Conductor -- run -p $PlanPath --no-control-plane --yes --max-sessions 1 2>&1 |
            Select-Object -Last 6 | Out-Host
    } finally { Pop-Location }

    $prompt = Join-Path (Split-Path $PlanPath) '.conductor\logs\session-001.prompt.md'
    if (-not (Test-Path $prompt)) { throw "no composed prompt at $prompt" }
    $text = Get-Content $prompt -Raw
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    Copy-Item $prompt (Join-Path $OutDir "$Label.prompt.md") -Force
    return $text
}

$solo = New-Rig -Dir (Join-Path $Root 'solo') -Satellites $null
$sat = Join-Path $Root 'sibling'
New-Rig -Dir $sat -Satellites $null | Out-Null
$multi = New-Rig -Dir (Join-Path $Root 'multi') -Satellites @(($sat -replace '\\', '/'))

$soloText = Invoke-Rig -PlanPath $solo -Label 'single-repo'
$multiText = Invoke-Rig -PlanPath $multi -Label 'multi-repo'

$checks = [ordered]@{
    'in-progress verb present'          = { param($t) $t -match 'conductor task --in-progress <id>' }
    'marked before the first edit'      = { param($t) $t -match 'BEFORE your first edit' }
    'claim verb present'                = { param($t) $t -match 'conductor task --done <id> --evidence <path>' }
    'claim precedes the handoff'        = { param($t) $t -match 'BEFORE you write the handoff' }
    'deferred MCP + CLI on one line'    = { param($t) ($t -split "`n" | Where-Object { $_ -match 'DEFERRED' -and $_ -match 'ToolSearch' -and $_ -match 'conductor task --done' }).Count -ge 1 }
    'gate battery step names bg'        = { param($t) ($t -split "`n" | Where-Object { $_ -match 'gate battery' -and $_ -match 'conductor bg' }).Count -ge 1 }
    'brace discipline stated'           = { param($t) $t -match 'curly braces' }
    'no literal brace left in prompt'   = { param($t) $t -notmatch '\{' }
}

$rows = @()
foreach ($name in $checks.Keys) {
    $rows += [pscustomobject]@{
        check       = $name
        'single'    = if ((& $checks[$name] $soloText)) { 'PASS' } else { 'FAIL' }
        'multi'     = if ((& $checks[$name] $multiText)) { 'PASS' } else { 'FAIL' }
    }
}
$anchor = if ($multiText -match 'land at least one commit HERE every session') { 'PASS' } else { 'FAIL' }
$anchorAbsent = if ($soloText -notmatch 'land at least one commit HERE') { 'PASS' } else { 'FAIL' }
$rows += [pscustomobject]@{ check = 'anchor-repo rule (present on multi / absent on single)'; 'single' = $anchorAbsent; 'multi' = $anchor }
$rows += [pscustomobject]@{ check = "composed prompt chars (cmd.exe argv ceiling 8191, bug 15)"; 'single' = $soloText.Length; 'multi' = $multiText.Length }

$rows | Format-Table -AutoSize | Out-Host
$rows | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'checks.json') -Encoding utf8
Write-Host "prompts + checks written to $OutDir" -ForegroundColor Green
if ($rows | Where-Object { $_.single -eq 'FAIL' -or $_.multi -eq 'FAIL' }) { exit 1 }
