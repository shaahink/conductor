# W3.3 window-close proof -- a token-free agent that is deliberately SLOW.
#
# The close rail can only be proven while a session is genuinely in flight: if the agent has already
# finished, "state was saved" says nothing, because there was nothing to lose. So this agent starts,
# announces itself on disk (the driver waits for that marker before it closes the window), and then
# sits in the middle of its work until a release file appears.
#
# The SAME script is the resume agent: on the second run the release file is already there, so it
# takes the fast path, commits, and claims through `conductor task --done` -- which is what proves
# the run the window-close interrupted was genuinely resumable, not merely recorded.
#
# ASCII ONLY. Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI, and a single non-ASCII
# byte tears the next string literal.
param(
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$Exe,
    [string]$Prompt = "",
    [string]$SessionId = "w3",
    [int]$MaxWaitSeconds = 150
)
$ErrorActionPreference = "Stop"

$marker = Join-Path $Repo ".w3-agent-started"
$release = Join-Path $Repo ".w3-release"
$log = Join-Path $Repo ".w3-agent.log"
function Note($text) { Add-Content -Path $log -Value ("[{0}] {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $text) -Encoding ascii }

function O($type, $part) {
    $o = @{ type = $type; session_id = $SessionId }
    if ($null -ne $part) { $o.part = $part }
    Write-Output ($o | ConvertTo-Json -Compress -Depth 6)
}
function Step($title, $cost) {
    O "step_finish" @{
        cost   = $cost
        tokens = @{ input = 100; output = 40; reasoning = 0; cache = @{ read = 0 } }
        state  = @{ title = $title }
    }
}

O "step_start" $null

# A verification session must answer with one score object and nothing else.
if ($Prompt -match "VERIFICATION session") {
    Note "VERIFY session -- answering PASS"
    O "text" @{ text = "Re-checking the claim against the diff (w3 window-close proof)." }
    Step "verifying" 0.0001
    O "text" @{ text = '{"score":95,"findings":[],"verdict":"PASS"}' }
    exit 0
}

# The marker is the driver's signal that a session is really in flight: the engine has spawned a
# child, the prompt is written, and there is live work to lose.
Set-Content -Path $marker -Value ("pid=" + $PID) -Encoding ascii
Note ("started; pid=" + $PID + "; prompt " + $Prompt.Length + " chars")
O "text" @{ text = "Reading the tracker and starting on the first open checkpoint." }
Step "reading" 0.0001

# ---- the middle of the work: sit here until released -------------------------------------------
if (-not (Test-Path $release)) {
    Note "no release file -- holding mid-session (this is the state the window close must not lose)"
    $deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
    while (-not (Test-Path $release) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path $release)) {
        Note "never released -- giving up so the harness cannot hang forever"
        O "text" @{ text = "SESSION-RESULT: nothing delivered (harness never released this agent)." }
        exit 0
    }
}
Note "released -- delivering"

# ---- deliver: first open row of the generated tracker, one real commit, one real claim ----------
$stageId = ""
if ($Prompt -match 'checkpoint\(s\) of stage\s+(?<s>[A-Za-z]{1,4}\d+)') { $stageId = $matches['s'] }

$ids = @()
$trackerFile = Get-ChildItem $Repo -Filter "*.md" -ErrorAction SilentlyContinue |
    Where-Object { (Get-Content $_.FullName -Raw) -match '\|\s*(TODO|IN PROGRESS)\s*\|' } |
    Select-Object -First 1
if ($trackerFile) {
    foreach ($line in ((Get-Content $trackerFile.FullName -Raw) -split "`n")) {
        if ($line -match '^\s*\|\s*(?<id>[A-Za-z]{1,4}\d+\.[A-Za-z0-9]+)\s*\|[^|]*\|\s*(?<st>[^|]*?)\s*\|') {
            $id = $matches['id']; $st = $matches['st']
            if ($st -notmatch '^(TODO|IN PROGRESS)$') { continue }
            if ($stageId -and ($id -notlike ($stageId + ".*"))) { continue }
            $ids += $id
            break
        }
    }
}
Note ("stage=" + $stageId + "; claiming: [" + ($ids -join ", ") + "]")
if ($ids.Count -eq 0) {
    O "text" @{ text = "The tracker shows no incomplete checkpoint for this stage -- nothing to deliver." }
    Step "idle" 0.0001
    O "text" @{ text = "SESSION-RESULT: nothing left open for this stage." }
    exit 0
}

$stamp = (Get-Date -Format "o")
foreach ($id in $ids) {
    Add-Content -Path (Join-Path $Repo "work.txt") -Value ("{0} delivered {1}" -f $stamp, $id) -Encoding ascii
}
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$null = git -C $Repo add -A 2>&1
$null = git -C $Repo commit -m ("feat(w3): deliver " + ($ids -join ", ")) --no-gpg-sign --quiet 2>&1
$sha = (git -C $Repo rev-parse --short HEAD 2>&1)
$ErrorActionPreference = $prev
Step "committing" 0.0002

foreach ($id in $ids) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $out = & $Exe task --done $id -c $sha -e "delivered after the window-close resume" 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = $prev
    Note ("task --done " + $id + " -> exit " + $code + ": " + (($out | Out-String).Trim()))
}
Step "claiming" 0.0001
O "text" @{ text = ("SESSION-RESULT: delivered " + ($ids -join ", ") + " at " + $sha + ".") }
exit 0
