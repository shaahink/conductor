# PK3.2 live proof - conductor say, and the direct fallback (D3), on the fresh build.
#
# A (always): a scratch courier from this tree on its own home and port, a stub Bot API. `say --dry-run`
#   prints the exact bytes, the resolved chat and the path; `say` goes through the courier; with the
#   courier stopped `say` sends directly and says so; a ceiling is refused by name and nothing is sent.
# B (always): a scratch `conductor run --once` (fake agent) in courier mode with its courier stopped:
#   its pushes go out directly and its conductor.log reads "courier unreachable - sent directly".
# C (-RealSend only): ONE real message to the admin DM with the scratch courier STOPPED: `say` sends it
#   directly with the environment's token, through a send-only relay (sendMessage and deleteMessage
#   forwarded, getUpdates never reaches Telegram), then `say --delete` takes it back.
#
# Never touches the real courier, its home, its task or its port. Windows PowerShell 5.1, ASCII only.
param(
    [switch]$RealSend,
    [string]$AdminChat = "99205495"
)

$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
Set-Location $RepoRoot
$env:CONDUCTOR_PLAN = $null
$OutDir = Join-Path $env:TEMP ("pk32-proof-" + (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmss"))
New-Item -ItemType Directory -Path $OutDir | Out-Null
$courier = Join-Path $RepoRoot "src\Conductor.Courier\bin\Debug\net10.0\conductor-courier.exe"
$engine = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
. (Join-Path $PSScriptRoot "pk3-rig-lib.ps1")

"PK3.2 proof at $(Utc) on commit $(git rev-parse --short HEAD)"
"  engine  $engine  built $((Get-Item $engine).LastWriteTimeUtc.ToString('u'))"
"  courier $courier  built $((Get-Item $courier).LastWriteTimeUtc.ToString('u'))"

function Between($text, $from, $to) {
    $a = $text.IndexOf($from); $b = $text.IndexOf($to)
    if ($a -lt 0 -or $b -lt 0) { return $null }
    return $text.Substring($a + $from.Length, $b - $a - $from.Length)
}

$jobs = @()
$procs = @()
try {
    # ------------------------------------------------------------------------------------------ A
    Section "A. say through a scratch courier, then directly with the courier stopped (stub Bot API)"
    $stubPort = FreePort
    $stubLog = Join-Path $OutDir "stub-bot-api.log"
    $jobs += Start-Job -ScriptBlock $stubScript -ArgumentList $stubPort, $stubLog
    Start-Sleep -Seconds 2
    $homeA = New-CourierHome "home-a" "http://127.0.0.1:$stubPort" @(@{ chatId = "770000001"; profile = "admin" }, @{ chatId = "770000002"; profile = "observer" })
    $portA = FreePort
    $procA = Start-ScratchCourier $homeA $portA "111111:pk32-scratch-token" "pk32 scratch A"
    $procs += $procA
    "  scratch courier pid $($procA.Id) on port $portA, home $homeA, stub Bot API on $stubPort"

    $body = Join-Path $OutDir "body.md"
    $bodyText = "<b>PK3.2</b> dry run & exact bytes`nsecond line`n"
    [IO.File]::WriteAllText($body, $bodyText, (New-Object Text.UTF8Encoding($false)))
    $dry = Invoke-Engine $homeA "111111:pk32-scratch-token" "say --to observer --file `"$body`" --reply-to 12 --dry-run"
    ($dry.out -split "`r?`n" | Where-Object { $_ }) | ForEach-Object { "    | $_" }
    Check "say --dry-run exits 0" ($dry.exit -eq 0) "exit $($dry.exit) $($dry.err)"
    Check "the dry run names the resolved chat" ($dry.out -match "chat:\s+observer -> 770000002") ""
    Check "the dry run names the path: through the courier on its port" ($dry.out -match "path:\s+through the courier on port $portA") ""
    $exact = Between $dry.out ("----- exact bytes -----" + [Environment]::NewLine) "----- end -----"
    Check "the dry run prints the body's exact bytes" ($exact -ceq $bodyText) ("[" + $exact + "]")
    Check "the dry run sent nothing" (-not (Test-Path $stubLog) -or @(Get-Content $stubLog | Where-Object { $_ -ne "getUpdates" }).Count -eq 0) ""

    $via = Invoke-Engine $homeA "111111:pk32-scratch-token" "say --to admin --text `"through the scratch courier`""
    "    | $($via.out.Trim())"
    Check "say goes through the live courier and prints the id" ($via.exit -eq 0 -and $via.out -match "sent through the courier: chat 770000001, message id\(s\) 5001") ""

    Stop-ScratchCourier $procA $homeA
    $procs = @()
    "  scratch courier stopped (pid $($procA.Id)); presence removed"

    $dry2 = Invoke-Engine $homeA "111111:pk32-scratch-token" "say --text `"x`" --dry-run"
    Check "with the courier stopped the dry run names the direct path and why" ($dry2.exit -eq 0 -and $dry2.out -match "path:\s+directly, with this environment's token - courier unreachable: no courier is running") (($dry2.out -split "`r?`n") | Where-Object { $_ -like "*path:*" })

    $direct = Invoke-Engine $homeA "111111:pk32-scratch-token" "say --to admin --text `"courier stopped`""
    "    | $($direct.out.Trim())"
    Check "with the courier stopped say sends directly and says so" ($direct.exit -eq 0 -and $direct.out -match "courier unreachable - sent directly: chat 770000001, message id\(s\) 5002") ""
    $ledgerA = Ledger $homeA
    Check "messages.jsonl carries both sends, the direct one marked" (@($ledgerA).Count -eq 2 -and @($ledgerA)[1].origin -eq "conductor say (sent directly)" -and @($ledgerA)[1].id -eq 5002) (($ledgerA | ForEach-Object { $_ | ConvertTo-Json -Compress }) -join " ")

    $callsBefore = @(Get-Content $stubLog | Where-Object { $_ -ne "getUpdates" }).Count
    $long = Invoke-Engine $homeA "111111:pk32-scratch-token" ("say --text " + ("x" * 4097))
    Check "a 4097-character text is refused by name, exit 2" ($long.exit -eq 2 -and $long.out -match "over Telegram's 4096-character message ceiling") $long.out.Trim()
    Check "and nothing reached the Bot API" (@(Get-Content $stubLog | Where-Object { $_ -ne "getUpdates" }).Count -eq $callsBefore) ""

    # ------------------------------------------------------------------------------------------ B
    Section "B. a scratch run in courier mode, its courier stopped, pushes directly and logs it"
    $repo = Join-Path $OutDir "repo"
    New-Item -ItemType Directory -Path $repo | Out-Null
    Push-Location $repo
    try {
        $null = Native "git" @("init", "-b", "main")
        $null = Native "git" @("config", "user.email", "pk32@rig")
        $null = Native "git" @("config", "user.name", "PK3.2 rig")
        Set-Content -Path "TRACKER.md" -Encoding ASCII -Value @("# PK3.2 rig", "", "## Handoff", "last: none.", "", "## Checkpoints", "", "| # | Checkpoint | Status | Commit | Evidence |", "|---|---|---|---|---|", "| H0.1 | rig | TODO | | |")
        Set-Content -Path "fake-agent.cmd" -Encoding ASCII -Value @(
            "@echo off",
            'echo {"type":"step_start"}',
            'echo {"type":"text","part":{"text":"PK3.2 rig session."}}',
            'echo {"type":"step_finish","part":{"cost":0.0001,"tokens":{"input":10,"output":10,"reasoning":0,"cache":{"read":0}}}}',
            "exit /b 0")
        $plan = @{
            name = "PK32LiveRig"; repo = $repo; tracker = "TRACKER.md"
            stages = @(@{ id = "H0"; title = "Rig"; sessions = 1 })
            agent = @{ command = "cmd.exe"; args = @("/c", (Join-Path $repo "fake-agent.cmd"), "{prompt}"); provider = "opencode" }
            gatePolicy = "perSession"
            gates = @(@{ name = "smoke"; command = "echo ok"; tier = "fast"; timeoutMinutes = 1 })
            report = @{ commit = $false }
            telegram = @{ apiBaseUrl = "http://127.0.0.1:$stubPort"; pollIntervalSeconds = 60; chats = @(@{ chatId = "770000001"; profile = "admin" }) }
        } | ConvertTo-Json -Depth 6
        Set-Content -Path "conductor.plan.json" -Value $plan -Encoding ASCII
        $null = Native "git" @("add", "-A")
        $null = Native "git" @("commit", "-m", "rig", "--no-gpg-sign")
    } finally { Pop-Location }

    $stubBefore = @(Get-Content $stubLog | Where-Object { $_ -ne "getUpdates" }).Count
    $runAt = Utc
    $run = Invoke-Engine $homeA "111111:pk32-scratch-token" "run -p `"$(Join-Path $repo 'conductor.plan.json')`" --once --headless --no-control-plane --no-face" $repo 300000
    "  run started $runAt, exit $($run.exit)"
    # The run log is .conductor\logs\conductor-<date>.log (conductor.log is the loop's own summary).
    $runLogs = @(Get-ChildItem (Join-Path $repo ".conductor\logs") -Filter "conductor-*.log" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    $direct = if ($runLogs.Count -gt 0) { @(Select-String -Path $runLogs -SimpleMatch "courier unreachable - sent directly") } else { @() }
    $direct | Select-Object -First 2 | ForEach-Object { "    run log: " + $_.Line.Trim() }
    Check "the run log reads 'courier unreachable - sent directly'" ($direct.Count -ge 1) "$($direct.Count) line(s)"
    $pushes = @(Get-Content $stubLog | Where-Object { $_ -ne "getUpdates" } | Select-Object -Skip $stubBefore)
    Check "the run's pushes reached the Bot API directly, one per logged line" ($pushes.Count -ge 1 -and $pushes.Count -eq $direct.Count) ($pushes -join ",")
    $polled = @(Get-Content $stubLog | Where-Object { $_ -eq "getUpdates" }).Count
    $refused = if ($runLogs.Count -gt 0) { @(Select-String -Path $runLogs -SimpleMatch "Telegram polling not started") } else { @() }
    Check "the run stayed in courier mode: polling not started" ($refused.Count -ge 1) ("getUpdates the stub saw in total, all the scratch courier's before it stopped: $polled")
    $report = Join-Path $repo ".conductor\REPORT.md"
    $health = if (Test-Path $report) { Select-String -Path $report -SimpleMatch "Channel DEAD" | Select-Object -First 1 } else { $null }
    "    REPORT.md: $($health.Line)"
    Check "REPORT.md's courier health line names the path the last push took" ($health -and $health.Line -like "*last push went directly at *") ""

    # ------------------------------------------------------------------------------------------ C
    if ($RealSend) {
        Section "C. ONE real send to the admin DM with the scratch courier stopped, then deleted"
        if (-not $env:CONDUCTOR_TELEGRAM_TOKEN) { throw "CONDUCTOR_TELEGRAM_TOKEN is not in this environment" }
        $relayPort = FreePort
        $relayLog = Join-Path $OutDir "relay.log"
        $jobs += Start-Job -ScriptBlock $relayScript -ArgumentList $relayPort, $relayLog
        Start-Sleep -Seconds 2
        $homeC = New-CourierHome "home-c" "http://127.0.0.1:$relayPort" @(@{ chatId = $AdminChat; profile = "admin" })
        $portC = FreePort
        $procC = Start-ScratchCourier $homeC $portC $null "pk32 scratch C, real token, relay"
        $procs += $procC
        "  scratch courier pid $($procC.Id) on port $portC with the environment token behind the relay on $relayPort"
        Stop-Process -Id $procC.Id -Force
        $procC.WaitForExit(10000) | Out-Null
        $procs = @()
        "  scratch courier stopped; its presence record is left naming a dead pid"

        $real = Invoke-Engine $homeC "INHERIT" "say --to admin --text `"PK3.2 live proof: conductor say with its courier stopped sends directly. Deleted at once.`""
        "    | $($real.out.Trim())"
        $m = [regex]::Match($real.out, "courier unreachable - sent directly: chat (\d+), message id\(s\) (\d+)")
        Check "say with the courier stopped sends the real message directly and says so" ($real.exit -eq 0 -and $m.Success -and $m.Groups[1].Value -eq $AdminChat) ""
        $realId = if ($m.Success) { $m.Groups[2].Value } else { "" }

        if ($realId) {
            $del = Invoke-Engine $homeC "INHERIT" "say --to admin --delete $realId"
            "    | $($del.out.Trim())"
            Check "say --delete takes it back, directly" ($del.exit -eq 0 -and $del.out -match "courier unreachable - deleted directly: chat $AdminChat, message id $realId") ""
        }
        $ledgerC = Ledger $homeC
        "  messages.jsonl:"
        $ledgerC | ForEach-Object { "    " + ($_ | ConvertTo-Json -Compress) }
        Check "messages.jsonl records the direct send and its delete" ((@($ledgerC | ForEach-Object { "$($_.id):$($_.verb)" }) -join ",") -eq "${realId}:send,${realId}:delete") ""
        Start-Sleep -Milliseconds 500
        $forwarded = @(Get-Content $relayLog | Where-Object { $_ -like "forwarded*" })
        $polls = @(Get-Content $relayLog | Where-Object { $_ -eq "answered getUpdates locally" }).Count
        "  relay: " + ($forwarded -join "; ") + "; getUpdates answered locally x$polls"
        Check "the relay forwarded exactly sendMessage then deleteMessage, both 200" (($forwarded -join ",") -eq "forwarded sendMessage -> 200,forwarded deleteMessage -> 200") ""
        Check "no getUpdates reached Telegram" (@(Get-Content $relayLog | Where-Object { $_ -like "*getUpdates*" -and $_ -notlike "answered*" }).Count -eq 0) ""
        ""
        "  REAL SEND: chat id $AdminChat (the admin DM), message id $realId - sent directly $(Utc), deleted"
    }
}
finally {
    foreach ($p in $procs) { try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { } }
    foreach ($j in $jobs) { try { Stop-Job $j; Remove-Job $j -Force } catch { } }
}

Section "result"
"  $script:pass/$($script:pass + $script:fail) checks passed; scratch dir $OutDir"
if ($script:fail -gt 0) { exit 1 }
exit 0
