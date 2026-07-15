# Fake agent for smoke-testing conductor without burning tokens (v2).
# Emulates opencode-json nd-JSON wire format.
# Flips the first TODO checkpoint to DONE, commits (or not, per mode).
param(
    [Parameter(Mandatory)][string]$Repo,
    [string]$Prompt = "",
    [string]$SessionId = "fake",
    [string]$Mode = "success"   # success | no-commits | stall | limit | true-red
)
$ErrorActionPreference = "Stop"

# ---- emit opencode-json event ----
# The stable driver's AgentSession.ParseOpencode reads payloads nested under `part`
# (part.text / part.state.title / part.cost [a NUMBER] / part.tokens.{input,output,
# reasoning,cache.read}). Emitting them flat at root crashes the driver (it calls
# TryGetProperty on an absent `part`). Every emitter below wraps its payload in `part`.
function O($type, $part) {
    $o = @{ type = $type; session_id = $SessionId }
    if ($null -ne $part) { $o.part = $part }
    Write-Output ($o | ConvertTo-Json -Compress -Depth 6)
}
function Step($title, $inTok, $outTok, $cost) {
    O "step_finish" @{
        cost   = $cost
        tokens = @{ input = $inTok; output = $outTok; reasoning = 0; cache = @{ read = 0 } }
        state  = @{ title = $title }
    }
}

O "step_start" $null
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
    Step "working" 100 50 0.0001
    O "error" @{ text = "usage limit reached" }
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
        O "tool_use" @{ tool = "Edit"; state = @{ title = $trackerItem.Name } }
        Step "flipping tracker row" 150 80 0.0002
    } else {
        # no TODO row found - scenario may already be advanced
    }
} else {
    O "text" @{ text = "No *-START.md tracker found in $Repo" }
}

# ---- true-red mode: write a compile-breaking file to make dotnet build fail ----
if ($Mode -eq "true-red") {
    O "text" @{ text = "Deliberately introducing a compile error for true-red gate scenario." }
    $csFile = Join-Path $Repo "tools\fake-red.cs"
    $fakeCs = "// FAKE-RED: intentionally broken source to trip dotnet build{0}class FakeRed {{ syntax error here{0}" -f [Environment]::NewLine
    Set-Content $csFile $fakeCs -Encoding utf8
    git -C $Repo add -A 2>&1 | Out-Null
    git -C $Repo commit -m "feat(fake): deliberately broken source to test gate-red detection" --quiet 2>&1 | Out-Null
    O "tool_use" @{ tool = "Write"; state = @{ title = "tools\fake-red.cs" } }
    O "tool_use" @{ tool = "Bash"; state = @{ title = "git add -A; git commit" } }
    Step "committing fake-red" 100 50 0.0001
    O "text" @{ text = "SESSION-RESULT: introduced compile error - gates SHOULD be red." }
    exit 0
}

# ---- commit (or skip for no-commits) ----
# opencode-json has no `result` event (that is the Claude format); the driver builds
# ResultText from `text` events, so SESSION-RESULT is emitted as text.
if ($Mode -ne "no-commits") {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $null = git -C $Repo add -A 2>&1
    $null = git -C $Repo commit -m "feat(fake): checkpoint delivered by fake agent" --quiet 2>&1
    $ErrorActionPreference = $prev
    O "tool_use" @{ tool = "Bash"; state = @{ title = "git add -A; git commit" } }
    Step "committing" 80 60 0.0001
    O "text" @{ text = "SESSION-RESULT: delivered, gates green." }
} else {
    O "text" @{ text = "Skipping commit for no-commits scenario." }
    O "text" @{ text = "SESSION-RESULT: tracker updated but no commit." }
}

exit 0
