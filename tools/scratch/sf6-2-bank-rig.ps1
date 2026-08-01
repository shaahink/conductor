# SF6.2 proof rig. Spawns THIS working tree's engine (dotnet run, never the conductor on PATH)
# against throwaway repos under $env:TEMP and captures the composed prompt each session actually
# received (.conductor/logs/session-001.prompt.md). The rig's plan dir holds a COPY of the real
# bank - plans/personas, plans/packs, plans/sarban-templates - so what is measured is the shipped
# bank, not a fixture of it.
#
# Safety: -p is passed explicitly AND CONDUCTOR_PLAN is cleared for the child (bug #20 - run
# resolves CONDUCTOR_PLAN over the cwd, and this session's CONDUCTOR_PLAN points at the LIVE plan
# driving it). No control plane, no port, no face: nothing here can touch the run driving this
# session or the second conductor run on this machine.
[CmdletBinding()]
param(
    [string]$Root = '',
    [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
# $PSScriptRoot is empty in a param default when the script is launched through some hosts
# (conductor bg is one), so resolve it in the body from the invocation path instead.
$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $Root) { $Root = Join-Path $env:TEMP 'sarban-proofs\sf62' }
$repoRoot = (Resolve-Path (Join-Path $scriptDir '..\..')).Path
if (-not $OutDir) { $OutDir = Join-Path $repoRoot '.conductor\evidence\SF6' }
$bank = Join-Path $repoRoot 'plans'

function New-Rig {
    param([string]$Dir, [string]$Persona, [string[]]$Packs, [switch]$EraPacks)

    if (Test-Path $Dir) { Remove-Item $Dir -Recurse -Force }
    New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    Push-Location $Dir
    try {
        git init -b main -q
        git config user.email 'sf62@test'
        git config user.name 'SF6.2 rig'
        Set-Content -Path 'README.md' -Value '# sf6.2 rig' -Encoding utf8
        $tracker = @(
            '# SF6.2 rig', '', '## Handoff', 'none.', '',
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

    # The real bank, copied in. templatesDir gets sarban-templates WITHOUT a packs subdir, which is
    # the shipped shape: the shared plans/packs fallback is the only thing that can find a pack.
    Copy-Item (Join-Path $bank 'personas') (Join-Path $Dir 'personas') -Recurse -Force
    Copy-Item (Join-Path $bank 'sarban-templates') (Join-Path $Dir 'sarban-templates') -Recurse -Force
    if ($EraPacks) {
        New-Item -ItemType Directory -Path (Join-Path $Dir 'sarban-templates\packs') -Force | Out-Null
        Set-Content -Path (Join-Path $Dir 'sarban-templates\packs\agent-pitfalls.md') -Value 'ERA-COPY-WINS' -Encoding utf8
    } else {
        Copy-Item (Join-Path $bank 'packs') (Join-Path $Dir 'packs') -Recurse -Force
    }

    $stage = [ordered]@{ id = 'R1'; title = 'Rig stage'; sessions = 1 }
    if ($Persona) { $stage['persona'] = $Persona }
    $plan = [ordered]@{
        name    = 'SF6.2 bank rig'
        repo    = ($Dir -replace '\\', '/')
        tracker = 'TRACKER.md'
        planDoc = 'TRACKER.md'
        templatesDir = 'sarban-templates'
        stages  = @($stage)
        agent   = [ordered]@{ command = 'cmd.exe'; args = @('/c', (Join-Path $Dir 'agent.cmd'), '{prompt}'); provider = 'claude'; output = 'stream-json' }
        gates   = @(@{ name = 'smoke'; command = 'cmd /c exit 0'; tier = 'fast'; timeoutMinutes = 1 })
        limits  = @{ maxSessions = 1 }
        report  = @{ commit = $false }
    }
    if ($Packs) { $plan['packs'] = $Packs }

    $planPath = Join-Path $Dir 'rig.plan.json'
    ($plan | ConvertTo-Json -Depth 8) | Set-Content -Path $planPath -Encoding utf8
    return $planPath
}

function Invoke-Rig {
    param([string]$PlanPath, [string]$Label)

    Write-Host "== $Label ==" -ForegroundColor Cyan
    $saved = $env:CONDUCTOR_PLAN
    Push-Location $repoRoot
    try {
        # bug #20: run resolves CONDUCTOR_PLAN over the cwd. Clear it so a missed -p cannot aim
        # this rig at the live plan driving the session.
        $env:CONDUCTOR_PLAN = $null
        dotnet run --project src/Conductor --no-build -- run -p $PlanPath --no-control-plane --yes --max-sessions 1 2>&1 |
            Select-Object -Last 4 | Out-Host
    } finally {
        Pop-Location
        $env:CONDUCTOR_PLAN = $saved
    }

    $prompt = Join-Path (Split-Path $PlanPath) '.conductor\logs\session-001.prompt.md'
    if (-not (Test-Path $prompt)) { throw "no composed prompt at $prompt" }
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    Copy-Item $prompt (Join-Path $OutDir "SF6-2-$Label.prompt.md") -Force
    return (Get-Content $prompt -Raw)
}

$bare    = Invoke-Rig -PlanPath (New-Rig -Dir (Join-Path $Root 'bare')    -Persona $null -Packs $null)              -Label 'bare'
$persona = Invoke-Rig -PlanPath (New-Rig -Dir (Join-Path $Root 'persona') -Persona 'qa'  -Packs $null)              -Label 'persona'
$packed  = Invoke-Rig -PlanPath (New-Rig -Dir (Join-Path $Root 'packed')  -Persona 'qa'  -Packs @('agent-pitfalls','dotnet-engineer')) -Label 'persona-plus-packs'
$era     = Invoke-Rig -PlanPath (New-Rig -Dir (Join-Path $Root 'era')     -Persona 'qa'  -Packs @('agent-pitfalls') -EraPacks) -Label 'era-pack-wins'

$CEILING = 8191
$rows = @()
function Add-Check { param([string]$Name, [bool]$Ok, [string]$Detail = '')
    $script:rows += [pscustomobject]@{ check = $Name; result = $(if ($Ok) { 'PASS' } else { 'FAIL' }); detail = $Detail }
}

Add-Check 'persona resolves from the real plans/personas (qa text present)' ($persona -match 'You are a QA specialist')
Add-Check 'no persona declared means no persona text'                       ($bare -notmatch 'You are a QA specialist')
Add-Check 'shared plans/packs loads when the era set has no packs dir'      ($packed -match 'mistakes agents keep making here' -and $packed -match 'house style for this codebase')
Add-Check 'era packs dir wins over the shared bank'                         ($era -match 'ERA-COPY-WINS' -and $era -notmatch 'mistakes agents keep making here')
Add-Check 'proof-note pattern reached the agent'                            ($packed -match 'dated proof-note')
Add-Check 'owner-block alternate completion reached the agent'              ($persona -notmatch 'alternate' -and $packed -match 'anchor-repo commit every session')
Add-Check 'no unreplaced placeholder left in any prompt'                    (($bare + $persona + $packed + $era) -notmatch '\{[a-zA-Z]+\}')

$sizes = [ordered]@{
    'bare (template + tools only)'        = $bare.Length
    'plus persona qa'                     = $persona.Length
    'plus persona and both shipped packs' = $packed.Length
}
foreach ($k in $sizes.Keys) {
    $over = $sizes[$k] -gt $CEILING
    $rows += [pscustomobject]@{
        check  = "composed prompt chars - $k"
        result = $(if ($over) { "OVER by $($sizes[$k] - $CEILING)" } else { "under by $($CEILING - $sizes[$k])" })
        detail = "$($sizes[$k]) vs cmd.exe argv ceiling $CEILING (bug 15)"
    }
}

$rows | Format-Table -AutoSize -Wrap | Out-Host
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$rows | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $OutDir 'SF6-2-bank-checks.json') -Encoding utf8
Write-Host "prompts + checks written to $OutDir" -ForegroundColor Green
if ($rows | Where-Object { $_.result -eq 'FAIL' }) { Write-Host 'RIG FAILED' -ForegroundColor Red; exit 1 }
