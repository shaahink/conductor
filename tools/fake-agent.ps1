# Fake agent for smoke-testing conductor without burning tokens (v2).
# Emulates opencode-json nd-JSON wire format.
# Flips the first TODO checkpoint to DONE, commits (or not, per mode).
param(
    [Parameter(Mandatory)][string]$Repo,
    [string]$Prompt = "",
    [string]$SessionId = "fake",
    [string]$Mode = "success"   # success | gatesred | stall | limit
)
$ErrorActionPreference = "Stop"

# ---- emit opencode-json event ----
function O($type, $rest) {
    $o = @{ type = $type; session_id = $SessionId } + $rest
    Write-Output ($o | ConvertTo-Json -Compress -Depth 6)
}

O "step_start" @{ model = "fake/v1" }
O "text" @{ text = "Reading tracker and plan docs..." }
Start-Sleep -Milliseconds 300

if ($Mode -eq "stall") {
    O "text" @{ text = "Stalling for 1 hour..." }
    Start-Sleep -Seconds 3600
    exit 0
}

if ($Mode -eq "limit") {
    O "text" @{ text = "Attempting work..." }
    Start-Sleep -Milliseconds 200
    O "step_finish" @{
        state = @{ title = "working"; input = 100; output = 50; reasoning = 0; cache = @{ read = 0 } }
        cost = @{ total_cost = 0.0001 }
        tokens = @{ input = 100; output = 50; reasoning = 0; cache = @{ read = 0 } }
    }
    O "result" @{ subtype = "error_during_execution"; is_error = $true; result = "usage limit reached"; num_turns = 2; total_cost_usd = 0.0001 }
    exit 1
}

# ---- find tracker and flip first TODO to DONE ----
$rx = '^(?<head>\|\s*(?<id>[A-Za-z]+[\d.-]*[a-z]?)\s*\|[^|]*)\|\s*TODO\s*\|[^|]*\|[^|]*\|'
$trackerItem = Get-ChildItem $Repo -Filter "*-START.md" -ErrorAction SilentlyContinue | Select-Object -First 1

if ($trackerItem) {
    $lines = (Get-Content $trackerItem.FullName -Raw) -split "`n"
    $flipped = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match $rx) {
            $lines[$i] = $matches['head'] + "| DONE | abc1234 | eval-results/fake/$($matches['id'])-gate.txt |"
            $flipped = $true
            break
        }
    }
    if ($flipped) {
        Set-Content $trackerItem.FullName ($lines -join "`n") -Encoding utf8 -NoNewline
        O "tool_use" @{ tool = "Edit"; input = @{ file_path = $trackerItem.Name } }
        O "step_finish" @{
            state = @{ title = "working"; input = 150; output = 80; reasoning = 0; cache = @{ read = 0 } }
            cost = @{ total_cost = 0.0002 }
            tokens = @{ input = 150; output = 80; reasoning = 0; cache = @{ read = 0 } }
        }
    } else {
        # no TODO row found — scenario may already be advanced
    }
} else {
    O "text" @{ text = "No *-START.md tracker found in $Repo" }
}

# ---- commit (or skip for gatesred) ----
if ($Mode -ne "gatesred") {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $null = git -C $Repo add -A 2>&1
    $null = git -C $Repo commit -m "feat(fake): checkpoint delivered by fake agent" --quiet 2>&1
    $ErrorActionPreference = $prev
    O "tool_use" @{ tool = "Bash"; input = @{ command = "git add -A; git commit" } }
    O "step_finish" @{
        state = @{ title = "working"; input = 80; output = 60; reasoning = 0; cache = @{ read = 0 } }
        cost = @{ total_cost = 0.0001 }
        tokens = @{ input = 80; output = 60; reasoning = 0; cache = @{ read = 0 } }
    }
    O "result" @{ subtype = "success"; is_error = $false; result = "SESSION-RESULT: delivered, gates green."; num_turns = 4; total_cost_usd = 0.05 }
} else {
    O "text" @{ text = "Skipping commit for gates-red scenario." }
    O "result" @{ subtype = "success"; is_error = $false; result = "SESSION-RESULT: tracker updated but no commit."; num_turns = 4; total_cost_usd = 0.05 }
}

exit 0
