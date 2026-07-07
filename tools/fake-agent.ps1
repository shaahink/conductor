# Fake agent for smoke-testing conductor without burning tokens.
# Emits claude-style stream-json, flips the first TODO checkpoint to DONE, commits.
param(
    [Parameter(Mandatory)][string]$Repo,
    [string]$Prompt = "",
    [string]$SessionId = "fake",
    [string]$Mode = "success"   # success | stall | gatesred | limit
)
$ErrorActionPreference = "Stop"

function Emit($obj) { $obj | ConvertTo-Json -Compress -Depth 6 | Write-Output }

Emit @{ type = "system"; subtype = "init"; session_id = $SessionId }
Emit @{ type = "assistant"; message = @{ content = @(@{ type = "text"; text = "Reading tracker and plan docs..." }) } }
Start-Sleep -Milliseconds 300

if ($Mode -eq "stall") { Start-Sleep -Seconds 3600 }
if ($Mode -eq "limit") {
    Emit @{ type = "result"; subtype = "error_during_execution"; is_error = $true; result = "Claude usage limit reached. Try again later." }
    exit 1
}

$tracker = Get-ChildItem $Repo -Filter "*-START.md" | Select-Object -First 1
if ($tracker) {
    $lines = (Get-Content $tracker.FullName -Raw) -split "`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^(?<head>\|\s*[A-Za-z]+\d+(?:\.\d+)?\s*\|[^|]*)\|\s*TODO\s*\|[^|]*\|[^|]*\|') {
            $lines[$i] = $matches['head'] + '| DONE | abc1234 | eval-results/fake/evidence.txt |'
            break
        }
    }
    Set-Content $tracker.FullName ($lines -join "`n") -Encoding utf8 -NoNewline
    Emit @{ type = "assistant"; message = @{ content = @(@{ type = "tool_use"; name = "Edit"; input = @{ file = $tracker.Name } }) } }
}

if ($Mode -ne "gatesred") {
    git -C $Repo add -A | Out-Null
    git -C $Repo commit -m "feat(fake): checkpoint delivered by fake agent" --quiet
    Emit @{ type = "assistant"; message = @{ content = @(@{ type = "tool_use"; name = "Bash"; input = @{ cmd = "git commit" } }) } }
}

Emit @{ type = "result"; subtype = "success"; is_error = $false; num_turns = 4; total_cost_usd = 0.05; result = "SESSION-RESULT: fake agent flipped one checkpoint to DONE, committed, all gates expected green. Next session should continue." }
exit 0
