# SF5.1 rig: a scratch run to drive `conductor watch` against a REAL engine.
#
# Why a rig at all: 32 unit tests prove the classifier's policy event-by-event, but they cannot prove
# the verb. The three things only a live run shows are (a) that the tail actually reads what a running
# engine appends, (b) that the wait is silent while real churn happens, and (c) that stdout is a clean
# JSON document while the human line goes to stderr.
#
# Traps honoured: scratch repo under %TEMP% with its OWN plan and .conductor (never C:/code/conductor),
# driven by THIS working tree's build (src/Conductor/bin/Debug/net10.0/conductor.exe), plan passed with
# -p on every call so bug #20's CONDUCTOR_PLAN inheritance cannot redirect it, and --no-control-plane
# so the rig cannot touch the port the other run on this machine holds.
#
#   .\SF5.1-watch-rig.ps1 -Name sf51a                      # completes: 2 stages, 2 checkpoints
#   .\SF5.1-watch-rig.ps1 -Name sf51b -MaxRunCostUsd 0.0001   # budget park at the boundary
#   .\SF5.1-watch-rig.ps1 -Name sf51c -MaxSessions 1          # session-cap park at the boundary
param(
    [string]$Name = 'sf51a',
    [double]$MaxRunCostUsd = 0,
    [int]$MaxSessions = 0,
    [double]$AgentSleepSeconds = 6
)
$ErrorActionPreference = 'Stop'

$root = Join-Path $env:TEMP "sarban-proofs\$Name"
if (Test-Path $root) { Remove-Item -Recurse -Force $root }
New-Item -ItemType Directory -Path $root -Force | Out-Null
$rootFwd = $root -replace '\\', '/'
$exe = 'C:/code/conductor/src/Conductor/bin/Debug/net10.0/conductor.exe'

Push-Location $root
git init -q
git config user.email 'sf51@proof.local'
git config user.name 'SF51 Proof'

@'
# SF5.1 Watch Rig Tracker

## Handoff
last: rig created.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| T0.1 | first checkpoint | TODO | | |
| T1.1 | second checkpoint | TODO | | |
'@ | Out-File -Encoding ascii (Join-Path $root 'TRACKER.md')

# The fake agent. Emits the opencode-json wire the engine parses, sleeps long enough that the watch is
# demonstrably blocked across real work, commits, and claims the stage's checkpoint through the SAME
# build the watch is running from.
$agent = @'
param([string]$Prompt = "")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe  = "__EXE__"
$plan = Join-Path $root "rig.plan.json"
$sleep = __SLEEP__

function O($o) { Write-Output ($o | ConvertTo-Json -Compress -Depth 6) }
O @{ type = "step_start"; session_id = "rig" }
O @{ type = "text"; session_id = "rig"; part = @{ text = "rig agent: working the checkpoint" } }
Start-Sleep -Seconds $sleep

# Whichever checkpoint is still TODO in run.db is this session's work.
$list = & $exe task --list -p $plan 2>&1 | Out-String
$id = if ($list -match "T0\.1[^\r\n]*TODO") { "T0.1" } elseif ($list -match "T1\.1[^\r\n]*TODO") { "T1.1" } else { "" }

$stamp = Get-Random
"rig work $id $stamp" | Out-File -Encoding ascii (Join-Path $root "work-$id.txt")
Push-Location $root
git add -A | Out-Null
git commit -q -m "feat: rig session work $id $stamp"
Pop-Location

if ($id -ne "") { & $exe task --done $id -p $plan -e "work-$id.txt" | Out-Null }

O @{ type = "step_finish"; session_id = "rig"; part = @{ cost = 0.005; tokens = @{ input = 1200; output = 400; reasoning = 0; cache = @{ read = 0 } } } }
O @{ type = "text"; session_id = "rig"; part = @{ text = "SESSION-RESULT: rig session claimed $id and committed." } }
exit 0
'@
$agent = $agent.Replace('__EXE__', $exe).Replace('__SLEEP__', [string]$AgentSleepSeconds)
$agent | Out-File -Encoding ascii (Join-Path $root 'agent.ps1')

$limits = @()
if ($MaxRunCostUsd -gt 0) { $limits += '"maxRunCostUsd": ' + $MaxRunCostUsd }
if ($MaxSessions -gt 0) { $limits += '"maxSessions": ' + $MaxSessions }
$limitsJson = if ($limits.Count -gt 0) { '"limits": { ' + ($limits -join ', ') + ' },' } else { '' }

$plan = @'
{
  "name": "SF51WatchRig___NAME__",
  "repo": "__ROOT__",
  "tracker": "TRACKER.md",
  "verifyEachDelivery": false,
  "pipeline": { "qa": { "mode": "off" } },
  __LIMITS__
  "agent": {
    "command": "powershell.exe",
    "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "__ROOT__/agent.ps1", "{prompt}"],
    "provider": "opencode"
  },
  "gates": [
    { "name": "quick", "command": "exit 0", "tier": "fast" }
  ],
  "stages": [
    { "id": "T0", "title": "First rig stage", "sessions": 3 },
    { "id": "T1", "title": "Second rig stage", "sessions": 3 }
  ],
  "report": { "commit": false }
}
'@
$plan = $plan.Replace('__NAME__', $Name).Replace('__ROOT__', $rootFwd).Replace('__LIMITS__', $limitsJson)
$plan | Out-File -Encoding ascii (Join-Path $root 'rig.plan.json')

$ErrorActionPreference = 'Continue'   # git writes CRLF warnings to stderr; they are not failures
git add -A 2>&1 | Out-Null
git commit -q -m "chore: sf51 watch rig" 2>&1 | Out-Null
$ErrorActionPreference = 'Stop'
Pop-Location

Write-Output "rig ready: $root"
Write-Output "  plan: $rootFwd/rig.plan.json"
