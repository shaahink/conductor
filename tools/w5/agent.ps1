# W5.1 rehearsal agent -- a token-free stand-in for a real coding agent.
#
# Unlike tools/fake-agent.ps1 (which hand-EDITS the tracker markdown, i.e. the M4.1 rigged-agent
# case the engine is supposed to discard), this one behaves like a WELL-BEHAVED agent on the W1/W2
# claim path: it reads the cards out of its own prompt, does a token of work, commits, and reports
# through `conductor task --done`. It deliberately calls the verb with NO -p, so the run only
# advances if W2.1's CONDUCTOR_PLAN really reaches the child environment.
#
# ASCII ONLY. Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI, and a single non-ASCII
# byte tears the next string literal (this silently broke the M9.1 harness once already).
param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$Exe,
    [string]$Prompt = "",
    [string]$SessionId = "w5"
)
$ErrorActionPreference = "Stop"

$log = Join-Path $Repo ".w5-agent.log"
function Note($text) { Add-Content -Path $log -Value ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss"), $text) -Encoding ascii }

# ---- opencode-json wire format (part-nested; flat payloads crash AgentSession.ParseOpencode) ----
function O($type, $part) {
    $o = @{ type = $type; session_id = $SessionId }
    if ($null -ne $part) { $o.part = $part }
    Write-Output ($o | ConvertTo-Json -Compress -Depth 6)
}
function Step($title, $cost) {
    O "step_finish" @{
        cost   = $cost
        tokens = @{ input = 120; output = 60; reasoning = 0; cache = @{ read = 0 } }
        state  = @{ title = $title }
    }
}

O "step_start" $null

# ---- the verifier role: a Verify prompt demands one {"score":...} object and nothing else -------
if ($Prompt -match "VERIFICATION session") {
    Note "VERIFY session -- answering PASS"
    O "text" @{ text = "Re-checking the claims against the diff (rehearsal verifier)." }
    Step "verifying" 0.0001
    O "text" @{ text = '{"score":95,"findings":[],"verdict":"PASS"}' }
    exit 0
}

# ---- which card is this session for? ------------------------------------------------------------
# Exactly what the prompt instructs: "DELIVER the next incomplete checkpoint(s) of stage <S> only",
# having first read the tracker. So: take the stage from the prompt, then take the first not-done
# row of that stage out of the GENERATED tracker. This is the agent-side half of the contract -- the
# engine's assignment policy computed the same answer independently, and the run only advances if
# the two agree.
#
# Note it does NOT read "## Work items in scope": that section carries a card's owner CONTEXT (and
# any subtasks), and a checkpoint with no context is deliberately absent from it. Treating it as the
# work list would make this agent idle on exactly the cards nobody annotated.
$stageId = ""
if ($Prompt -match 'checkpoint\(s\) of stage\s+(?<s>[A-Za-z]{1,4}\d+)') { $stageId = $matches['s'] }
elseif ($Prompt -match '^\s*##\s+Stage\s+(?<s>[A-Za-z]{1,4}\d+)') { $stageId = $matches['s'] }

$trackerFile = Get-ChildItem $Repo -Filter "*.md" -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content $_.FullName -Raw) -match '\|\s*(TODO|IN PROGRESS)\s*\|' } |
    Select-Object -First 1

$ids = @()
if ($trackerFile) {
    foreach ($line in ((Get-Content $trackerFile.FullName -Raw) -split "`n")) {
        if ($line -match '^\s*\|\s*(?<id>[A-Za-z]{1,4}\d+\.[A-Za-z0-9]+)\s*\|[^|]*\|\s*(?<st>[^|]*?)\s*\|') {
            $id = $matches['id']; $st = $matches['st']
            if ($st -notmatch '^(TODO|IN PROGRESS)$') { continue }
            if ($stageId -and ($id -notlike ($stageId + ".*"))) { continue }
            $ids += $id
            break   # one checkpoint per session -- the engine's default policy
        }
    }
}
Note ("prompt " + $Prompt.Length + " chars; stage=" + $stageId + "; tracker=" +
      $(if ($trackerFile) { $trackerFile.Name } else { "NONE" }) + "; claiming: [" + ($ids -join ", ") + "]")
if ($ids.Count -eq 0) {
    Note "NOTHING TO DELIVER -- no open row for this stage in the tracker"
    O "text" @{ text = "The tracker shows no incomplete checkpoint for this stage -- nothing to deliver." }
    Step "idle" 0.0001
    O "text" @{ text = "SESSION-RESULT: nothing left open for this stage." }
    exit 0
}

# ---- deliver: a real commit, so the verdict has a diff to judge ---------------------------------
$stamp = (Get-Date -Format "o")
foreach ($id in $ids) {
    Add-Content -Path (Join-Path $Repo "work.txt") -Value ("{0} delivered {1}" -f $stamp, $id) -Encoding ascii
}
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$null = git -C $Repo add -A 2>&1
$null = git -C $Repo commit -m ("feat(rehearsal): deliver " + ($ids -join ", ")) --no-gpg-sign --quiet 2>&1
$sha = (git -C $Repo rev-parse --short HEAD 2>&1)
$ErrorActionPreference = $prev
O "tool_use" @{ tool = "Bash"; state = @{ title = "git add -A; git commit" } }
Step "committing" 0.0002

# ---- report: the one claim path. No -p on purpose -- CONDUCTOR_PLAN has to carry the plan. ------
Note ("CONDUCTOR_PLAN=" + $env:CONDUCTOR_PLAN)
foreach ($id in $ids) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $out = & $Exe task --done $id -c $sha -e "delivered by the W5.1 rehearsal agent" 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    Note ("task --done " + $id + " -> exit " + $code + ": " + (($out | Out-String).Trim()))
    if ($code -ne 0) {
        O "text" @{ text = ("could not claim " + $id + ": " + (($out | Out-String).Trim())) }
    }
    O "tool_use" @{ tool = "Bash"; state = @{ title = ("conductor task --done " + $id) } }
}
Step "claiming" 0.0001

O "text" @{ text = ("SESSION-RESULT: delivered " + ($ids -join ", ") + " at " + $sha + "; gates should be green.") }
exit 0
