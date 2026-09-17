# PK5.2 live proof - the figure verbs answered by the courier from run.db, read-only (D10). Windows
# PowerShell 5.1, ASCII only.
#
# The fresh courier, on a scratch state home with a stub Bot API and a scratch token, answers /money,
# /tokens, /progress, /status and /evidence for a scratch checkout whose plan carries this era's plan
# name. The store it reads is a sqlite3 .backup COPY of the live store (trap 19: measurement verbs run
# against a copy), placed where the fresh engine's own resolver catalogued it. No run is live on the
# copy. The fresh engine's `money --run <id> --json` prices the same copy; the courier's /money must
# carry the same billed total, session count and per-checkpoint figure. Every file under the scratch
# home (bar the courier's own folder) and the checkout is hashed before and after the five verbs.
#
# Never touches the real courier, its home, its task, its port or the live store (read by .backup only).
param(
    [string]$LiveDb = "",
    [string]$Sqlite = "C:\adb\sqlite3.exe"
)
$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$env:CONDUCTOR_PLAN = $null
$OutDir = Join-Path $env:TEMP ("pk52-proof-" + (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmss"))
New-Item -ItemType Directory -Path $OutDir | Out-Null
$courier = Join-Path $RepoRoot "src\Conductor.Courier\bin\Debug\net10.0\conductor-courier.exe"
$engine = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
. (Join-Path $PSScriptRoot "pk3-rig-lib.ps1")

if (-not $LiveDb) { $LiveDb = (Get-Content (Join-Path $RepoRoot ".conductor\state-pointer.json") -Raw | ConvertFrom-Json).runDb }

"PK5.2 proof at $(Utc) on commit $(git -C $RepoRoot rev-parse --short HEAD)"
"  engine  $engine  built $((Get-Item $engine).LastWriteTimeUtc.ToString('u'))"
"  courier $courier  built $((Get-Item $courier).LastWriteTimeUtc.ToString('u'))"

$PlanName = "Peyk courier - the courier stands on its own"
$RigChat = "770000052"

# The PK5.2 stub: getUpdates from $queueDir, every sendMessage BODY kept as reply-NN.json (so the answer
# itself can be read back), everything else ok. Logs the method only.
$stub52 = {
    param($port, $logPath, $queueDir, $repliesDir)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://127.0.0.1:$port/")
    $l.Start()
    $next = 52001
    $updateId = 520001
    $n = 0
    while ($true) {
        $ctx = $l.GetContext()
        $method = $ctx.Request.Url.AbsolutePath.Split('/')[-1]
        $ms = New-Object System.IO.MemoryStream
        $ctx.Request.InputStream.CopyTo($ms)
        $body = $ms.ToArray()
        Add-Content -Path $logPath -Value $method
        if ($method -eq "getUpdates") {
            Start-Sleep -Milliseconds 1000
            $queued = Get-ChildItem $queueDir -Filter "*.json" | Sort-Object Name | Select-Object -First 1
            if ($queued) {
                $message = Get-Content $queued.FullName -Raw
                Remove-Item $queued.FullName
                $json = '{"ok":true,"result":[{"update_id":' + $updateId + ',"message":' + $message.Trim() + '}]}'
                $updateId++
                Add-Content -Path $logPath -Value ("served update " + $queued.Name)
            }
            else { $json = '{"ok":true,"result":[]}' }
        }
        elseif ($method -eq "sendMessage") {
            $n++
            [IO.File]::WriteAllBytes((Join-Path $repliesDir ("reply-" + $n.ToString("00") + ".json")), $body)
            $json = '{"ok":true,"result":{"message_id":' + $next + '}}'; $next++
        }
        elseif ($method -eq "getMe") { $json = '{"ok":true,"result":{"id":1,"username":"pk52_stub_bot"}}' }
        else { $json = '{"ok":true,"result":true}' }
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $ctx.Response.StatusCode = 200
        $ctx.Response.ContentType = "application/json"
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.Close()
    }
}

function Wait-Replies($dir, $count, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (@(Get-ChildItem $dir -Filter "reply-*.json").Count -ge $count) { return $true }
        Start-Sleep -Milliseconds 300
    }
    return $false
}

function Reply-Text($dir, $n) {
    $path = Join-Path $dir ("reply-" + ([int]$n).ToString("00") + ".json")
    if (-not (Test-Path $path)) { return "" }
    return ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($path)) | ConvertFrom-Json).text
}

function Ask($queueDir, $repliesDir, $n, $text) {
    $msg = '{"message_id":' + (5200 + $n) + ',"date":0,"chat":{"id":' + $RigChat + ',"type":"private"},"from":{"id":5550052,"is_bot":false,"first_name":"Rig"},"text":"' + $text + '"}'
    Set-Content -Path (Join-Path $queueDir (([int]$n).ToString("00") + ".json")) -Value $msg -Encoding ASCII
    return (Wait-Replies $repliesDir $n 60)
}

# Every file under the given roots: sha256, length, write time. $skip is a folder prefix left out, and
# so are the scratch courier's own stdout/stderr redirects (held open by the process).
function Tree($roots, $skip) {
    $map = @{}
    foreach ($root in $roots) {
        Get-ChildItem $root -Recurse -File -Force | Where-Object { -not $_.FullName.StartsWith($skip) -and $_.Name -ne "stdout.txt" -and $_.Name -ne "stderr.txt" } | ForEach-Object {
            $map[$_.FullName] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash + "/" + $_.Length + "/" + $_.LastWriteTimeUtc.Ticks
        }
    }
    return $map
}

$jobs = @()
$procs = @()
try {
    Section "setup: a scratch checkout carrying this era's plan name, a scratch courier home, a stub Bot API"
    $repo = Join-Path $OutDir "peyk-rig"
    New-Item -ItemType Directory -Path (Join-Path $repo ".conductor") | Out-Null
    Copy-Item (Join-Path $RepoRoot "plans\peyk\COURIER-TRACKER.md") (Join-Path $repo "TRACKER.md")
    $stages = @()
    foreach ($i in 1..6) { $stages += @{ id = "PK$i"; title = "Peyk stage $i"; sessions = 1 } }
    $plan = @{
        name = $PlanName; repo = $repo; tracker = "TRACKER.md"; stages = $stages
        agent = @{ command = "cmd.exe"; args = @("/c", "echo", "{prompt}"); provider = "opencode" }
        gates = @(@{ name = "smoke"; command = "echo ok"; tier = "fast"; timeoutMinutes = 1 })
    } | ConvertTo-Json -Depth 6
    Set-Content -Path (Join-Path $repo "conductor.plan.json") -Value $plan -Encoding ASCII

    $stubPort = FreePort
    $stubLog = Join-Path $OutDir "stub-bot-api.log"
    $queueDir = Join-Path $OutDir "queue"
    $repliesDir = Join-Path $OutDir "replies"
    New-Item -ItemType Directory -Path $queueDir, $repliesDir | Out-Null
    $jobs += Start-Job -ScriptBlock $stub52 -ArgumentList $stubPort, $stubLog, $queueDir, $repliesDir
    Start-Sleep -Seconds 2
    $home52 = New-CourierHome "home-52" "http://127.0.0.1:$stubPort" @(@{ chatId = $RigChat; profile = "admin" }) @(@{ plan = $PlanName; repo = $repo })

    Section "the store: the fresh engine catalogues the checkout, a sqlite3 .backup of the live store goes where it says"
    $st = Invoke-Engine $home52 $null "status -p `"$(Join-Path $repo 'conductor.plan.json')`"" $repo
    "  engine status (before the copy): exit $($st.exit): $(($st.out -split "`n")[0].Trim())"
    $entry = @((Get-Content (Join-Path $home52 "catalogue.json") -Raw | ConvertFrom-Json).entries | Where-Object { $_.plan -eq $PlanName })
    Check "the fresh engine catalogued the checkout under this plan name" ($entry.Count -eq 1) ""
    $copy = $entry[0].runDb
    New-Item -ItemType Directory -Force -Path (Split-Path $copy) | Out-Null
    # The run id is read from the LIVE store (read-only), never the copy: a CLI open of a WAL copy would
    # leave -wal and -shm beside it, which is a run that looks live.
    $runId = ((Native $Sqlite @("-readonly", $LiveDb, "SELECT run_id FROM runs WHERE plan_name='$PlanName' ORDER BY started_utc DESC LIMIT 1")) | Out-String).Trim()
    $null = Native $Sqlite @("-readonly", $LiveDb, (".backup '" + $copy.Replace('\', '/') + "'"))
    Check "the backup copy exists" (Test-Path $copy) "$copy"
    "  newest run of the plan (read from the live store, read-only): $runId"
    Check "the store holds a run of this plan" ($runId.Length -gt 0) ""
    Check "no run is live on the copy (no -wal, no -shm)" (-not (Test-Path "$copy-wal") -and -not (Test-Path "$copy-shm")) ""

    Section "the engine's money for that run, from the same copy"
    $moneyJson = Invoke-Engine $home52 $null "money --run $runId --home `"$home52`" --json" $repo
    Check "money --json exits 0" ($moneyJson.exit -eq 0) "exit $($moneyJson.exit) $($moneyJson.err)"
    Set-Content -Path (Join-Path $OutDir "engine-money.json") -Value $moneyJson.out -Encoding UTF8
    $moneyText = Invoke-Engine $home52 $null "money --run $runId --home `"$home52`"" $repo
    Set-Content -Path (Join-Path $OutDir "engine-money.txt") -Value $moneyText.out -Encoding UTF8
    $total = ($moneyJson.out | ConvertFrom-Json).total
    $usd = "$" + ([decimal]$total.costUsd).ToString("0.00", [Globalization.CultureInfo]::InvariantCulture)
    $perCp = if ($null -ne $total.costPerCheckpoint) { "$" + ([decimal]$total.costPerCheckpoint).ToString("0.00", [Globalization.CultureInfo]::InvariantCulture) } else { "" }
    "  engine: billed $usd over $($total.sessions) sessions, $($total.checkpoints) checkpoints, $perCp per checkpoint"

    Section "the courier: /project, then the five figure verbs"
    $portA = FreePort
    $procA = Start-ScratchCourier $home52 $portA "111111:pk52-scratch-token" "pk52 scratch"
    $procs += $procA
    "  scratch courier pid $($procA.Id) on port $portA, home $home52, stub Bot API on $stubPort"
    Check "/project peyk-rig is answered" (Ask $queueDir $repliesDir 1 "/project peyk-rig") (Reply-Text $repliesDir 1)
    "  " + ((Reply-Text $repliesDir 1) -split "`n")[0]

    $before = Tree @($home52, $repo) (Join-Path $home52 "courier")
    $verbs = @("/money", "/tokens", "/progress", "/status", "/evidence", "/evidence PK5.1")
    $n = 1
    foreach ($verb in $verbs) {
        $n++
        Check "$verb is answered" (Ask $queueDir $repliesDir $n $verb) ""
    }
    Start-Sleep -Seconds 2
    $after = Tree @($home52, $repo) (Join-Path $home52 "courier")

    Section "what the courier said"
    foreach ($i in 2..$n) {
        "---- reply $i ($($verbs[$i - 2])) ----"
        Reply-Text $repliesDir $i
    }

    Section "checks"
    $money = Reply-Text $repliesDir 2
    Check "/money carries the engine's billed total ($usd)" ($money.Contains("Billed $usd")) ""
    Check "/money carries the engine's session count ($($total.sessions))" ($money.Contains(" $($total.sessions) session")) ""
    if ($perCp) { Check "/money carries the engine's per-checkpoint figure ($perCp)" ($money.Contains("$perCp per delivered checkpoint ($($total.checkpoints) closed")) "" }
    $tokens = Reply-Text $repliesDir 3
    Check "/tokens counts the engine's sessions" ($tokens.Contains("tokens over $($total.sessions) session")) ""
    Check "/progress reads the checkout's tracker (PK5 row)" ((Reply-Text $repliesDir 4).Contains("PK5")) ""
    Check "/status reads the run (checkpoints line)" ((Reply-Text $repliesDir 5) -match "Checkpoints \d+/\d+") ""
    Check "/evidence lists the evidence registry" ((Reply-Text $repliesDir 6).Contains(".conductor/evidence/")) ""
    Check "bare /evidence leads with this plan's checkpoints (PK), not another era's sweep" ([regex]::Match((Reply-Text $repliesDir 6), "<code>([^<]*)</code>").Groups[1].Value -match "^(?i)PK\d") ""
    Check "/evidence PK5.1 answers for that checkpoint" ((Reply-Text $repliesDir 7).Contains("evidence for PK5.1")) ""
    $allRead = $true
    foreach ($i in 2..$n) { if (-not (Reply-Text $repliesDir $i).Contains("read-only")) { $allRead = $false } }
    Check "every answer says it came from run.db, read-only" $allRead ""

    $changed = @($before.Keys | Where-Object { -not $after.ContainsKey($_) -or $after[$_] -ne $before[$_] })
    $added = @($after.Keys | Where-Object { -not $before.ContainsKey($_) })
    "  files under the home (courier folder aside) and the checkout: $($before.Count) before, $($after.Count) after"
    foreach ($f in $changed + $added) { "  CHANGED: $f" }
    Check "no file changed, appeared or vanished while the five verbs were answered" (($changed.Count + $added.Count) -eq 0 -and $before.Count -eq $after.Count) ""
    Check "the copy still has no -wal or -shm" (-not (Test-Path "$copy-wal") -and -not (Test-Path "$copy-shm")) ""
    Check "no sendMessage went anywhere but the rig chat" (@(Get-ChildItem $repliesDir -Filter "reply-*.json" | Where-Object { ([Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($_.FullName)) | ConvertFrom-Json).chat_id -ne $RigChat }).Count -eq 0) ""

    Section "the engine's status for the same copy (LAST: conductor status opens its store writable)"
    $engineStatus = Invoke-Engine $home52 $null "status -p `"$(Join-Path $repo 'conductor.plan.json')`"" $repo
    Set-Content -Path (Join-Path $OutDir "engine-status.txt") -Value $engineStatus.out -Encoding UTF8
    ($engineStatus.out -split "`n" | Select-Object -First 2) | ForEach-Object { "  engine: " + $_.Trim() }
    $said = (Reply-Text $repliesDir 5) -split "`n"
    $verdict = $said[2].Trim()
    $e = [regex]::Match($engineStatus.out, "checkpoints (\d+/\d+) \u00B7 sessions (\d+) \u00B7 cost (\$[\d.]+)")
    $c = [regex]::Match((Reply-Text $repliesDir 5), "Checkpoints (\d+/\d+) \u00B7 sessions (\d+) \u00B7 billed (\$[\d.]+)")
    Check "/status gives the engine's verdict ($verdict)" ($engineStatus.exit -eq 0 -and $verdict.Length -gt 0 -and ($engineStatus.out -replace "\s+", " ").Contains(($verdict -replace "\s+", " "))) ""
    Check "/status gives the engine's checkpoints, sessions and cost" ($e.Success -and $c.Success -and $e.Groups[1].Value -eq $c.Groups[1].Value -and $e.Groups[2].Value -eq $c.Groups[2].Value -and $e.Groups[3].Value -eq $c.Groups[3].Value) "engine $($e.Value) / courier $($c.Value)"
}
finally {
    foreach ($p in $procs) { Stop-ScratchCourier $p $home52 }
    foreach ($j in $jobs) { Stop-Job $j -ErrorAction SilentlyContinue; Remove-Job $j -Force -ErrorAction SilentlyContinue }
    ""
    "out: $OutDir"
    "  $script:pass/$($script:pass + $script:fail) checks passed"
}
if ($script:fail -gt 0) { exit 1 }
exit 0
