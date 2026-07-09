param(
    [Parameter(ValueFromPipeline=$true)]
    [string]$InputText,
    [string]$InputFile,
    [string]$OutputDir = ".conductor\plans\",
    [string]$TrackerDir = ".conductor\",
    [string]$WorkflowDir = "docs\workflows\",
    [string]$RepoPath = (Get-Location).Path,
    [string]$DriverPath = "C:\Code\conductor\bin\conductor.exe"
)

# paste-plan.ps1 — Paste a structured plan markdown, get runnable conductor files
#
# Usage:
#   .\tools\paste-plan.ps1 -InputFile myplan.md
#   Get-Clipboard | .\tools\paste-plan.ps1
#   gc plan.md | .\tools\paste-plan.ps1 -OutputDir my-plans
#
# Input format (paste this template):
#   # Plan: <name>
#   Branch: <branch>
#   Repo: <repo-path>
#   Gates: build command | test command | ...
#   
#   ## Stage 1
#   ID: S1
#   Title: Do the thing
#   Effort: ~30m
#   Notes: What to do, what files, what gate proves it.
#   Read: path/to/doc.md
#   
#   ## Stage 2
#   ...

# Read input
if ($InputFile) {
    $InputText = Get-Content -Path $InputFile -Raw
}
if (-not $InputText) {
    Write-Error "No input. Pipe a plan or use -InputFile."
    exit 1
}

# Parse header
$headerMatch = [regex]::Match($InputText, '# Plan: (.+)')
$planName = if ($headerMatch.Success) { $headerMatch.Groups[1].Value.Trim() } else { "CustomPlan" }

$branchMatch = [regex]::Match($InputText, 'Branch: (.+)')
$branch = if ($branchMatch.Success) { $branchMatch.Groups[1].Value.Trim() } else { "^main$" }

$repoMatch = [regex]::Match($InputText, 'Repo: (.+)')
$repo = if ($repoMatch.Success) { $repoMatch.Groups[1].Value.Trim() } else { $RepoPath }

$gatesMatch = [regex]::Match($InputText, 'Gates: (.+)')
$gateLines = if ($gatesMatch.Success) { $gatesMatch.Groups[1].Value.Split('|').Trim() } else { @("dotnet build", "dotnet test") }

$readOrderLine = [regex]::Match($InputText, 'Read order: (.+)')
$readOrder = if ($readOrderLine.Success) { $readOrderLine.Groups[1].Value.Split(',').Trim() } else { @() }

# Parse stages
$stages = @()
$stageBlocks = [regex]::Split($InputText, '(?=## Stage \d+)') | Where-Object { $_ -match 'ID: (\S+)' }
foreach ($block in $stageBlocks) {
    $idMatch = [regex]::Match($block, 'ID: (\S+)')
    $titleMatch = [regex]::Match($block, 'Title: (.+)')
    $effortMatch = [regex]::Match($block, 'Effort: (.+)')
    $notesMatch = [regex]::Match($block, 'Notes: (.+)')
    $readMatch = [regex]::Match($block, 'Read: (.+)')
    $personaMatch = [regex]::Match($block, 'Persona: (.+)')

    if ($idMatch.Success) {
        $notes = if ($notesMatch.Success) { $notesMatch.Groups[1].Value.Trim() } else { "See plan doc." }
        $read = if ($readMatch.Success) { $readMatch.Groups[1].Value.Trim() } else { "" }
        if ($read -ne "" -and $notes -notmatch [regex]::Escape($read)) {
            $notes = "Read $read. $($notesMatch.Groups[1].Value.Trim())"
        }

        $stage = @{
            id = $idMatch.Groups[1].Value.Trim()
            title = if ($titleMatch.Success) { $titleMatch.Groups[1].Value.Trim() } else { "Unknown" }
            sessions = 1
            notes = $notes
        }
        if ($personaMatch.Success) { $stage.persona = $personaMatch.Groups[1].Value.Trim().ToLower() }

        $stages += $stage
    }
}

if ($stages.Count -eq 0) {
    Write-Error "No stages found. Use format: ## Stage 1`nID: S1`nTitle: ..."
    exit 1
}

# Create output dirs
$planDir = Join-Path $OutputDir
$null = New-Item -ItemType Directory -Path $planDir -Force
$null = New-Item -ItemType Directory -Path $WorkflowDir -Force

# Sanitize name for filenames
$safeName = $planName -replace '[^a-zA-Z0-9-]', '-'

# Build plan JSON
$gatesJson = @()
$i = 0
foreach ($g in $gateLines) {
    $g = $g.Trim()
    if ($g -eq "") { continue }
    $i++
    $gateName = if ($g -match '^\S+') { $matches[0] } else { "gate$i" }
    $gatesJson += @"
    { "name": "$gateName", "command": "$g", "tier": "fast", "timeoutMinutes": 10 }
"@
}

$gatesStr = $gatesJson -join ",`n"

$stagesJson = @()
foreach ($s in $stages) {
    $p = if ($s.persona) { "`n      `"persona`": `"$($s.persona)`"," } else { "" }
    $stagesJson += @"
    {
      "id": "$($s.id)",
      "title": "$($s.title)",
      "sessions": $($s.sessions),$p
      "notes": "$($s.notes)"
    }
"@
}

$stagesStr = $stagesJson -join ",`n"

$readOrderStr = if ($readOrder.Count -gt 0) {
    $readOrder | ForEach-Object { "`"$_`"" } -join ", "
} else {
    '"TRACKER.md"'
}

$planJson = @"
{
  "name": "$safeName",
  "repo": "$repo",
  "tracker": "TRACKER.md",
  "planDoc": "docs/workflows/$safeName-workflow.md",
  "branchPattern": "$branch",
  "pauseOnBlocked": false,
  "batteryCollapse": true,

  "agent": {
    "command": "opencode",
    "args": [
      "run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--share", "--format", "json", "{prompt}"
    ],
    "resumeArgs": [
      "run", "-m", "deepseek/deepseek-v4-pro", "--auto", "--thinking", "--share", "--format", "json", "--continue", "{prompt}"
    ],
    "provider": "opencode",
    "output": "opencode-json"
  },

  "setup": {
    "command": "dotnet build-server shutdown; exit 0",
    "timeoutMinutes": 2
  },
  "teardown": {
    "command": "dotnet build-server shutdown; exit 0",
    "timeoutMinutes": 2
  },

  "advisor": {
    "enabled": true,
    "command": "opencode",
    "args": ["run", "{prompt}", "-m", "deepseek/deepseek-v4-pro"],
    "output": "text",
    "timeoutMinutes": 6
  },

  "stages": [
$stagesStr
  ],

  "gatePolicy": "perPhase",

  "gates": [
$gatesStr
  ],

  "limits": {
    "stallMinutes": 12,
    "sessionTimeoutMinutes": 90,
    "maxResumesPerSession": 2,
    "stageSlackFactor": 2,
    "backoffMinutes": 30,
    "maxBackoffs": 5,
    "maxSessionTokens": 2000000
  },

  "report": { "commit": true, "push": true, "heartbeatMinutes": 0 },
  "notify": { "command": "", "args": [] },
  "statusAgent": { "enabled": false },
  "readOrder": [$readOrderStr],
  "batteries": {
    "lessons": true,
    "recentFailure": true,
    "lessonsMaxEntries": 3,
    "maxBytes": 2048
  },

  "promptExtra": "Generated by paste-plan.ps1. See docs/workflows/$safeName-workflow.md for session details."
}
"@

# Build TRACKER.md
$trackerLines = @"
# $planName — Phase Tracker

**Generated by paste-plan.ps1.**

## Handoff (overwrite this block)
last: Plan created. No sessions run yet.
stage: Waiting for first session.
next: Read this file, then start Stage $($stages[0].id).
trap: None.

## Checkpoints

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
"@

foreach ($s in $stages) {
    $trackerLines += "`n| $($s.id) | $($s.title) | TODO | — | — |"
}

$trackerLines += @"

## Quick Commands

```powershell
dotnet build
dotnet test
$DriverPath run --plan $planDir\$safeName.plan.json
$DriverPath run --dry-run --plan $planDir\$safeName.plan.json
```
"@

# Build workflow doc
$workflowLines = @"
# $planName — Workflow

**Generated by paste-plan.ps1 from structured plan input.**

## Universal Pre-Session Ritual (≤5 min)

1. Read this workflow doc (first session only).
2. Read TRACKER.md handoff block.
3. Read your stage's notes and referenced docs.
4. Run **selective** gate — never build on red.

## Universal Post-Session Ritual (≤10 min)

1. Re-run gate — confirm nothing regressed.
2. Produce evidence artifact.
3. Overwrite TRACKER.md handoff block (≤12 lines).
4. Update checkpoint status.
5. Commit, push.

## Stages

"@

foreach ($s in $stages) {
    $workflowLines += @"

### $($s.id) — $($s.title)

**Effort:** $(if ($s.notes -match '~(\d+m)') { $matches[1] } else { "unknown" })

$($s.notes)

**Evidence:** TRACKER.md row flips to DONE with commit hash.
"@
}

# Write files
$planPath = Join-Path $planDir "$safeName.plan.json"
$trackerPath = Join-Path $TrackerDir "TRACKER.md"
$workflowPath = Join-Path $WorkflowDir "$safeName-workflow.md"

# For plan files in .conductor/plans/, keep plan JSON but write tracker+workflow to repo
$trackerAbsPath = Join-Path (Get-Location) "TRACKER.md"
$workflowAbsPath = Join-Path (Get-Location) $workflowPath

$planJson | Out-File -FilePath $planPath -Encoding utf8
$trackerLines | Out-File -FilePath $trackerAbsPath -Encoding utf8
$workflowLines | Out-File -FilePath $workflowAbsPath -Encoding utf8

Write-Host "✅ Generated $($stages.Count) stages across 3 files:"
Write-Host "   Plan:     $planPath"
Write-Host "   Tracker:  $trackerAbsPath"
Write-Host "   Workflow: $workflowAbsPath"
Write-Host ""
Write-Host "To run: $DriverPath run --plan $planPath"
Write-Host "To dry-run: $DriverPath run --dry-run --plan $planPath"
