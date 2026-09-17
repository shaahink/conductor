# PK3.3 live proof - a live run names its own project to the courier (D4).
#
# A scratch courier from this tree (own home, own port, scratch token, stub Bot API that can deliver an
# inbound note on demand) allows one repository under its OLD plan name. A reply to a push from a FRESH
# plan in that repository is parked (the F-COUR-4 gap, measured). Then a scratch `conductor run --once`
# of the fresh plan in that repository reaches its first session boundary, says hello, and the courier
# adds the entry "by: run <id>". The next reply is filed into the repository's inbox. `courier allow` is
# never run. Nothing here touches the real courier. Windows PowerShell 5.1, ASCII only.

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $RepoRoot
$env:CONDUCTOR_PLAN = $null
$OutDir = Join-Path $env:TEMP ("pk33-proof-" + (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmss"))
New-Item -ItemType Directory -Path $OutDir | Out-Null
$courier = Join-Path $RepoRoot "src\Conductor.Courier\bin\Debug\net10.0\conductor-courier.exe"
$engine = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
. (Join-Path $PSScriptRoot "pk3-rig-lib.ps1")

"PK3.3 proof at $(Utc) on commit $(git rev-parse --short HEAD)"
"  engine  $engine  built $((Get-Item $engine).LastWriteTimeUtc.ToString('u'))"
"  courier $courier  built $((Get-Item $courier).LastWriteTimeUtc.ToString('u'))"

$OldPlan = "PK33OldPlan"
$FreshPlan = "PK33FreshPlan"
$token = "111111:pk33-scratch-token"

# One inbound message replying to a push from $plan: the identity line "<plan> MIDDLE-DOT s1" routes it.
# The dot is written as a JSON escape built from char codes, so this file stays ASCII (a literal dot in
# a BOM-less script is read as two ANSI characters by Windows PowerShell 5.1 and the route never parses).
function Queue-Reply($queueDir, $name, $plan, $text) {
    $dot = [string][char]92 + "u00b7"
    $json = '{"message_id":' + (Get-Random -Minimum 1000 -Maximum 9999) + ',"date":1758100000,"chat":{"id":770000001,"type":"private"},' +
        '"from":{"id":770000001,"is_bot":false,"first_name":"rig"},"text":"' + $text + '",' +
        '"reply_to_message":{"message_id":800,"date":1758100000,"chat":{"id":770000001,"type":"private"},"text":"' + $plan + ' ' + $dot + ' s1\nsession 1 ended"}}'
    Set-Content -Path (Join-Path $queueDir $name) -Value $json -Encoding ASCII
}
function Wait-Served($stubLog, $name, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $stubLog) -and (Select-String -Path $stubLog -SimpleMatch "served update $name" -Quiet)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function Files-Containing($dir, $text) {
    if (-not (Test-Path $dir)) { return @() }
    return @(Get-ChildItem $dir -Recurse -File | Where-Object { (Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue) -like "*$text*" })
}

$jobs = @()
$procs = @()
try {
    Section "setup: an allowed repository, under its OLD plan name, and a scratch courier"
    $repo = Join-Path $OutDir "repo"
    New-Item -ItemType Directory -Path $repo | Out-Null
    Push-Location $repo
    try {
        $null = Native "git" @("init", "-b", "main")
        $null = Native "git" @("config", "user.email", "pk33@rig")
        $null = Native "git" @("config", "user.name", "PK3.3 rig")
        Set-Content -Path "TRACKER.md" -Encoding ASCII -Value @("# PK3.3 rig", "", "## Handoff", "last: none.", "", "## Checkpoints", "", "| # | Checkpoint | Status | Commit | Evidence |", "|---|---|---|---|---|", "| H0.1 | rig | TODO | | |")
        Set-Content -Path "fake-agent.cmd" -Encoding ASCII -Value @(
            "@echo off",
            'echo {"type":"step_start"}',
            'echo {"type":"text","part":{"text":"PK3.3 rig session."}}',
            'echo {"type":"step_finish","part":{"cost":0.0001,"tokens":{"input":10,"output":10,"reasoning":0,"cache":{"read":0}}}}',
            "exit /b 0")
        $plan = @{
            name = $FreshPlan; repo = $repo; tracker = "TRACKER.md"
            stages = @(@{ id = "H0"; title = "Rig"; sessions = 1 })
            agent = @{ command = "cmd.exe"; args = @("/c", (Join-Path $repo "fake-agent.cmd"), "{prompt}"); provider = "opencode" }
            gatePolicy = "perSession"
            gates = @(@{ name = "smoke"; command = "echo ok"; tier = "fast"; timeoutMinutes = 1 })
            report = @{ commit = $false }
            telegram = @{ apiBaseUrl = "http://127.0.0.1:1"; pollIntervalSeconds = 60; chats = @(@{ chatId = "770000001"; profile = "admin" }) }
        } | ConvertTo-Json -Depth 6
        Set-Content -Path "conductor.plan.json" -Value $plan -Encoding ASCII
        $null = Native "git" @("add", "-A")
        $null = Native "git" @("commit", "-m", "rig", "--no-gpg-sign")
    } finally { Pop-Location }

    $stubPort = FreePort
    $stubLog = Join-Path $OutDir "stub-bot-api.log"
    $queueDir = Join-Path $OutDir "queue"
    New-Item -ItemType Directory -Path $queueDir | Out-Null
    $jobs += Start-Job -ScriptBlock $stubScript -ArgumentList $stubPort, $stubLog, $queueDir
    Start-Sleep -Seconds 2
    $homeA = New-CourierHome "home-a" "http://127.0.0.1:$stubPort" @(@{ chatId = "770000001"; profile = "admin" }) @(@{ plan = $OldPlan; repo = $repo })
    $settingsPath = Join-Path $homeA "courier\courier.json"
    $portA = FreePort
    $procA = Start-ScratchCourier $homeA $portA $token "pk33 scratch"
    $procs += $procA
    "  scratch courier pid $($procA.Id) on port $portA, home $homeA; repo $repo allowed as $OldPlan"
    "  courier.json projects before: " + ((Get-Content $settingsPath -Raw | ConvertFrom-Json).projects | ConvertTo-Json -Compress)

    Section "1. before any run: a reply to a $FreshPlan push is parked"
    Queue-Reply $queueDir "01-before.json" $FreshPlan "PK3.3 note BEFORE the hello"
    Check "the courier took the inbound note" (Wait-Served $stubLog "01-before.json" 30) ""
    Start-Sleep -Seconds 4
    $parked = Files-Containing (Join-Path $homeA "dead-letter") "PK3.3 note BEFORE the hello"
    Check "it was parked in the dead-letter box - the fresh plan name is not on the allowlist" ($parked.Count -ge 1) (($parked | ForEach-Object { $_.FullName.Substring($homeA.Length) }) -join ", ")
    $why = @($parked | Where-Object { (Get-Content $_.FullName -Raw) -like "*which is not a project on this machine*" })
    Check "parked for the right reason: the reply routed by its identity line to a plan the allowlist lacks" ($why.Count -ge 1) ((@($parked | ForEach-Object { ((Get-Content $_.FullName -Raw) -split '"why":')[1] }) -join " ") -replace '\s+', ' ')
    Check "and not filed into the repository" ((Files-Containing (Join-Path $repo ".conductor\inbox") "PK3.3 note BEFORE the hello").Count -eq 0) ""

    Section "2. a scratch run of $FreshPlan in that repository - its first session boundary says hello"
    $runAt = Utc
    $run = Invoke-Engine $homeA $token "run -p `"$(Join-Path $repo 'conductor.plan.json')`" --once --headless --no-control-plane --no-face" $repo 300000
    "  run started $runAt, exit $($run.exit)"
    $runLogs = @(Get-ChildItem (Join-Path $repo ".conductor\logs") -Filter "conductor-*.log" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $hello = if ($runLogs.Count -gt 0) { @(Select-String -Path $runLogs -SimpleMatch "courier: the courier files notes for $FreshPlan") } else { @() }
    $hello | Select-Object -First 1 | ForEach-Object { "    run log: " + $_.Line.Trim() }
    Check "the run log says the courier now files notes for the fresh plan, added by this run" ($hello.Count -eq 1 -and $hello[0].Line -match "added by run \S+") "$($hello.Count) line(s)"
    $projects = @((Get-Content $settingsPath -Raw | ConvertFrom-Json).projects)
    "  courier.json projects after: " + ($projects | ConvertTo-Json -Compress)
    $fresh = @($projects | Where-Object { $_.plan -eq $FreshPlan })
    $old = @($projects | Where-Object { $_.plan -eq $OldPlan })
    Check "courier.json gained ($FreshPlan, repo) marked by: run <id>" ($fresh.Count -eq 1 -and $fresh[0].by -like "run *" -and $fresh[0].repo -eq $repo) ($fresh | ConvertTo-Json -Compress)
    Check "the owner's entry is untouched beside it" ($old.Count -eq 1 -and -not $old[0].by) ($old | ConvertTo-Json -Compress)
    $runId = if ($fresh.Count -eq 1) { $fresh[0].by.Substring(4) } else { "" }
    $courierLog = Join-Path $homeA "courier\courier.log"
    $added = @(Select-String -Path $courierLog -SimpleMatch "courier files notes for $FreshPlan")
    Check "courier.log records the addition" ($added.Count -ge 1) (($added | Select-Object -First 1).Line)

    Section "3. after the hello: the same kind of reply is FILED into the repository's inbox - no courier allow"
    Queue-Reply $queueDir "02-after.json" $FreshPlan "PK3.3 note AFTER the hello"
    Check "the courier took the inbound note" (Wait-Served $stubLog "02-after.json" 30) ""
    $filed = @()
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and $filed.Count -eq 0) {
        Start-Sleep -Seconds 1
        $filed = Files-Containing (Join-Path $repo ".conductor\inbox") "PK3.3 note AFTER the hello"
    }
    Check "it was filed into $repo\.conductor\inbox" ($filed.Count -ge 1) (($filed | ForEach-Object { $_.FullName.Substring($repo.Length) }) -join ", ")
    Check "and not parked" ((Files-Containing (Join-Path $homeA "dead-letter") "PK3.3 note AFTER the hello").Count -eq 0) ""
    Check "the courier process never restarted (routing picked the entry up live)" (-not $procA.HasExited) "pid $($procA.Id)"

    Section "D4 security note, restated"
    "  The allowlist exists so a daemon holding the bot token cannot be made to write into arbitrary"
    "  checkouts on this disk. A run already has write access to its own checkout, and its hello carries"
    "  the install's shared secret like every other verb, so the entry it adds grants no reach it did not"
    "  have. courier allow is unchanged and stays the way to allow a project with no run live."
    ""
    "  RUN: $runId named $FreshPlan at $repo; this rig never ran courier allow."
}
finally {
    foreach ($p in $procs) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { } }
    foreach ($j in $jobs) { try { Stop-Job $j; Remove-Job $j -Force } catch { } }
}

Section "result"
"  $script:pass/$($script:pass + $script:fail) checks passed; scratch dir $OutDir"
if ($script:fail -gt 0) { exit 1 }
exit 0
