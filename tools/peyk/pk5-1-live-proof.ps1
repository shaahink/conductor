# PK5.1 live proof - inbound with a name (D9). Windows PowerShell 5.1, ASCII only.
#
# A (stub Bot API, scratch everything): the fresh courier files a note from an update that carries a
#   from object; the note on disk has MessageId and the three sender fields; the courier answers it with
#   setMessageReaction and NO sendMessage; /note as a reply gets the one message (with the button);
#   the fresh engine's inbox list shows sender and msg id beside a note written before PK5.1; say
#   --reply-to resolves a note id to its message id and refuses the old note by name.
# B (-RealSend only): ONE real message to the admin DM, sent directly by say through a relay. The
#   fresh courier, holding the real token, is served a SYNTHESIZED update naming that real message id
#   (the relay answers getUpdates locally, so the real courier's getUpdates is never touched) and puts
#   its reaction on the real message through the relay. The reaction is then cleared and the message
#   deleted. The relay forwards sendMessage, setMessageReaction and deleteMessage only.
#
# Never touches the real courier, its home, its task or its port.
param(
    [switch]$RealSend,
    [string]$AdminChat = "99205495"
)
$ErrorActionPreference = "Stop"
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$env:CONDUCTOR_PLAN = $null
$OutDir = Join-Path $env:TEMP ("pk51-proof-" + (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmss"))
New-Item -ItemType Directory -Path $OutDir | Out-Null
$courier = Join-Path $RepoRoot "src\Conductor.Courier\bin\Debug\net10.0\conductor-courier.exe"
$engine = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
. (Join-Path $PSScriptRoot "pk3-rig-lib.ps1")

"PK5.1 proof at $(Utc) on commit $(git -C $RepoRoot rev-parse --short HEAD)"
"  engine  $engine  built $((Get-Item $engine).LastWriteTimeUtc.ToString('u'))"
"  courier $courier  built $((Get-Item $courier).LastWriteTimeUtc.ToString('u'))"

$PlanName = "PK51Rig"
$RigChat = "770000001"
$dot = [string][char]92 + "u00b7"

# One inbound message, replying to a push from $PlanName so the identity line routes it (no /project,
# so no routing answer is ever sent). $from is a JSON object or $null.
function Queue-Note($queueDir, $name, $chat, $messageId, $from, $text, $replyTo, $replyText) {
    $json = '{"message_id":' + $messageId + ',"date":1758100000,"chat":{"id":' + $chat + ',"type":"private"},'
    if ($from) { $json += '"from":' + $from + ',' }
    $json += '"text":"' + $text + '","reply_to_message":{"message_id":' + $replyTo + ',"date":1758100000,"chat":{"id":' + $chat + ',"type":"private"},"text":"' + $replyText + '"}}'
    Set-Content -Path (Join-Path $queueDir $name) -Value $json -Encoding ASCII
}
function Wait-Line($log, $text, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $log) -and (Select-String -Path $log -SimpleMatch $text -Quiet)) { return $true }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
# The methods the stub logged after "served update $name" and before the next served update.
function Calls-After($log, $name) {
    $lines = @(Get-Content $log)
    $start = [Array]::IndexOf($lines, "served update $name")
    if ($start -lt 0) { return @() }
    $out = @()
    for ($i = $start + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -like "served update *") { break }
        if ($lines[$i] -ne "getUpdates") { $out += $lines[$i] }
    }
    return $out
}
function Notes($repo) {
    $dir = Join-Path $repo ".conductor\inbox\notes"
    if (-not (Test-Path $dir)) { return @() }
    return @(Get-ChildItem $dir -Filter "*.json" | ForEach-Object { Get-Content $_.FullName -Raw | ConvertFrom-Json })
}

# The PK5.1 relay: sendMessage, setMessageReaction and deleteMessage to api.telegram.org; getUpdates from
# $queueDir, answered here. Logs the METHOD, the status and a refusal's description - never the path.
$relay51 = {
    param($port, $logPath, $queueDir)
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://127.0.0.1:$port/")
    $l.Start()
    $updateId = 900001
    while ($true) {
        $ctx = $l.GetContext()
        $path = $ctx.Request.Url.AbsolutePath
        $method = $path.Split('/')[-1]
        $ms = New-Object System.IO.MemoryStream
        $ctx.Request.InputStream.CopyTo($ms)
        $body = $ms.ToArray()
        $status = 200
        if ($method -eq "sendMessage" -or $method -eq "setMessageReaction" -or $method -eq "deleteMessage") {
            $req = [System.Net.HttpWebRequest]::Create("https://api.telegram.org" + $path)
            $req.Method = "POST"
            $req.ContentType = $ctx.Request.ContentType
            $req.ContentLength = $body.Length
            $s = $req.GetRequestStream(); $s.Write($body, 0, $body.Length); $s.Close()
            try { $resp = $req.GetResponse() } catch [System.Net.WebException] { $resp = $_.Exception.Response }
            if ($null -eq $resp) { $status = 502; $json = '{"ok":false,"error_code":502,"description":"relay could not reach the Bot API"}' }
            else {
                $status = [int]$resp.StatusCode
                $rs = New-Object System.IO.StreamReader($resp.GetResponseStream())
                $json = $rs.ReadToEnd(); $resp.Close()
            }
            $line = "forwarded " + $method + " -> " + $status
            if ($status -ne 200) { $line += " " + $json }
            Add-Content -Path $logPath -Value $line
        }
        elseif ($method -eq "getUpdates") {
            Start-Sleep -Milliseconds 1500
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
        else {
            $status = 403
            $json = '{"ok":false,"error_code":403,"description":"the pk51 relay forwards sendMessage, setMessageReaction and deleteMessage only"}'
            Add-Content -Path $logPath -Value ("refused " + $method)
        }
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $ctx.Response.StatusCode = $status
        $ctx.Response.ContentType = "application/json"
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.Close()
    }
}

$jobs = @()
$procs = @()
try {
    Section "setup: a scratch repo with a plan, a note written before PK5.1, a stub Bot API and a scratch courier"
    $repo = Join-Path $OutDir "repo"
    New-Item -ItemType Directory -Path (Join-Path $repo ".conductor\inbox\notes") | Out-Null
    $null = Native "git" @("-C", $repo, "init", "-b", "main")
    Set-Content -Path (Join-Path $repo "TRACKER.md") -Encoding ASCII -Value @("# PK5.1 rig", "", "## Checkpoints", "", "| # | Checkpoint | Status | Commit | Evidence |", "|---|---|---|---|---|", "| H0.1 | rig | TODO | | |")
    $plan = @{
        name = $PlanName; repo = $repo; tracker = "TRACKER.md"
        stages = @(@{ id = "H0"; title = "Rig"; sessions = 1 })
        agent = @{ command = "cmd.exe"; args = @("/c", "echo", "{prompt}"); provider = "opencode" }
        gates = @(@{ name = "smoke"; command = "echo ok"; tier = "fast"; timeoutMinutes = 1 })
        report = @{ commit = $false }
    } | ConvertTo-Json -Depth 6
    Set-Content -Path (Join-Path $repo "conductor.plan.json") -Value $plan -Encoding ASCII
    # Written the way the DV4 courier wrote a note: no MessageId, no sender.
    Set-Content -Path (Join-Path $repo ".conductor\inbox\notes\83806400.json") -Encoding ASCII -Value @(
        '{', '  "Id": 83806400,', '  "ReceivedUtc": "2026-09-05T10:00:00Z",', ('  "ChatId": "' + $RigChat + '",'),
        '  "Kind": "text",', '  "Text": "PK5.1 rig: a note written before PK5.1",', '  "ReplyToMessageId": 651', '}')

    $stubPort = FreePort
    $stubLog = Join-Path $OutDir "stub-bot-api.log"
    $queueDir = Join-Path $OutDir "queue"
    New-Item -ItemType Directory -Path $queueDir | Out-Null
    $jobs += Start-Job -ScriptBlock $stubScript -ArgumentList $stubPort, $stubLog, $queueDir
    Start-Sleep -Seconds 2
    $homeA = New-CourierHome "home-a" "http://127.0.0.1:$stubPort" @(@{ chatId = $RigChat; profile = "admin" }) @(@{ plan = $PlanName; repo = $repo })
    $portA = FreePort
    $procA = Start-ScratchCourier $homeA $portA "111111:pk51-scratch-token" "pk51 scratch A"
    $procs += $procA
    "  scratch courier pid $($procA.Id) on port $portA, home $homeA, stub Bot API on $stubPort"

    Section "A1. a note from a rig update carries its sender and message id"
    $ada = '{"id":5550001,"is_bot":false,"first_name":"Ada","last_name":"Lovelace","username":"ada_l"}'
    Queue-Note $queueDir "01-ada.json" $RigChat 41 $ada "PK5.1 rig note from Ada" 800 "$PlanName $dot s1\nsession 1 ended"
    Check "the courier took the update" (Wait-Line $stubLog "served update 01-ada.json" 30) ""
    $deadline = (Get-Date).AddSeconds(20)
    $note = $null
    while ((Get-Date) -lt $deadline -and -not $note) {
        $note = Notes $repo | Where-Object { $_.Text -eq "PK5.1 rig note from Ada" } | Select-Object -First 1
        if (-not $note) { Start-Sleep -Milliseconds 500 }
    }
    Check "it was filed into the scratch repo's inbox" ($null -ne $note) ""
    if ($note) { "    | " + ($note | ConvertTo-Json -Compress) }
    Check "MessageId is 41" ($note.MessageId -eq 41) ""
    Check "SenderId is 5550001" ($note.SenderId -eq 5550001) ""
    Check "SenderName is Ada Lovelace" ($note.SenderName -eq "Ada Lovelace") ""
    Check "SenderUsername is ada_l" ($note.SenderUsername -eq "ada_l") ""

    Section "A2. the acknowledgement is a reaction, never a message"
    $null = Wait-Line $stubLog "setMessageReaction" 20
    Start-Sleep -Seconds 4
    $after1 = Calls-After $stubLog "01-ada.json"
    "    | calls after the note: " + ($after1 -join ", ")
    Check "setMessageReaction was sent once" (@($after1 | Where-Object { $_ -eq "setMessageReaction" }).Count -eq 1) ""
    Check "and no sendMessage (nor any other send)" (@($after1 | Where-Object { $_ -like "send*" }).Count -eq 0) ""

    Section "A3. /note, as a reply to the note, is the one message - the answer with the button"
    Queue-Note $queueDir "02-ask.json" $RigChat 42 $ada "/note" 41 "PK5.1 rig note from Ada"
    Check "the courier took the /note" (Wait-Line $stubLog "served update 02-ask.json" 30) ""
    Start-Sleep -Seconds 5
    $after2 = Calls-After $stubLog "02-ask.json"
    "    | calls after /note: " + ($after2 -join ", ")
    Check "exactly one sendMessage answers it, and no reaction" (@($after2 | Where-Object { $_ -eq "sendMessage" }).Count -eq 1 -and @($after2 | Where-Object { $_ -eq "setMessageReaction" }).Count -eq 0) ""
    Check "a /note files nothing" (@(Notes $repo).Count -eq 2) "$(@(Notes $repo).Count) notes (the old one and Ada's)"
    Stop-ScratchCourier $procA $homeA
    "  scratch courier A stopped"

    Section "A4. the fresh engine's inbox list shows sender and msg id, and still lists the old note"
    $list = Invoke-Engine $homeA "111111:pk51-scratch-token" "inbox list -p `"$(Join-Path $repo 'conductor.plan.json')`"" $repo
    $list.out.TrimEnd().Split("`n") | ForEach-Object { "    | " + $_.TrimEnd() }
    Check "inbox list exits 0" ($list.exit -eq 0) "exit $($list.exit) $($list.err)"
    Check "it has from and msg columns" ($list.out -match "from" -and $list.out -match "msg") ""
    # The table wraps at the redirected console's 80 columns, so the handle can fall to the next line.
    $adaRow = @($list.out.Split("`n") | Where-Object { $_ -match "Ada Lovelace" })
    Check "Ada's row names her and message 41" ($adaRow.Count -eq 1 -and $adaRow[0] -match "\b41\b" -and $list.out -match "\(@ada_l\)") ""
    $oldRow = @($list.out.Split("`n") | Where-Object { $_ -match "83806400" })
    Check "the note written before PK5.1 still lists, with - for sender and msg" ($oldRow.Count -eq 1 -and $oldRow[0] -notmatch "@") ""

    Section "A5. say --reply-to takes a note id; the old note is refused by name"
    $noteId = $note.Id
    $say = Invoke-Engine $homeA $null "say --reply-to $noteId --text `"answered`" --dry-run" $repo
    $say.out.TrimEnd().Split("`n") | ForEach-Object { "    | " + $_.TrimEnd() }
    Check "a note id answers that note's message in its chat" ($say.exit -eq 0 -and $say.out -match "reply to:   note $noteId of ${PlanName}: message 41 in chat $RigChat, from Ada Lovelace \(@ada_l\)") ""
    $old = Invoke-Engine $homeA $null "say --reply-to 83806400 --text `"answered`" --dry-run" $repo
    "    | " + $old.out.Trim()
    Check "the old note is refused by name (exit 2)" ($old.exit -eq 2 -and $old.out -match "filed before notes carried a message id") ""
    $raw = Invoke-Engine $homeA $null "say --to $RigChat --reply-to 12 --text `"answered`" --dry-run" $repo
    Check "a number that is no note is a message id, as before" ($raw.exit -eq 0 -and $raw.out -match "reply to:   12\r?\n") ""

    if ($RealSend) {
        Section "B. ONE real message to the admin DM; the fresh courier reacts on it through the relay; cleared; deleted"
        if (-not $env:CONDUCTOR_TELEGRAM_TOKEN) { throw "CONDUCTOR_TELEGRAM_TOKEN is not in this environment" }
        $relayPort = FreePort
        $relayLog = Join-Path $OutDir "relay.log"
        $queueB = Join-Path $OutDir "queue-b"
        New-Item -ItemType Directory -Path $queueB | Out-Null
        $jobs += Start-Job -ScriptBlock $relay51 -ArgumentList $relayPort, $relayLog, $queueB
        Start-Sleep -Seconds 2
        $homeB = New-CourierHome "home-b" "http://127.0.0.1:$relayPort" @(@{ chatId = $AdminChat; profile = "admin" }) @(@{ plan = $PlanName; repo = $repo })

        $real = Invoke-Engine $homeB "INHERIT" "say --to admin --text `"PK5.1 live proof: the courier reacts to a filed note instead of posting. Cleared and deleted at once.`""
        "    | $($real.out.Trim())"
        $m = [regex]::Match($real.out, "sent directly: chat (\d+), message id\(s\) (\d+)")
        Check "say sends ONE real message directly to the admin DM" ($real.exit -eq 0 -and $m.Success -and $m.Groups[1].Value -eq $AdminChat) ""
        $realId = if ($m.Success) { $m.Groups[2].Value } else { "" }

        if ($realId) {
            $portB = FreePort
            $procB = Start-ScratchCourier $homeB $portB $null "pk51 scratch B, real token, relay"
            $procs += $procB
            "  scratch courier B pid $($procB.Id) on port $portB, home $homeB, relay on $relayPort"
            $from = '{"id":1,"is_bot":true,"first_name":"PK5.1 relay (synthesized update)"}'
            Queue-Note $queueB "01-real.json" $AdminChat $realId $from "PK5.1 relay: the real message as a note" 800 "$PlanName $dot s1\nsession 1 ended"
            Check "the courier was served the synthesized update" (Wait-Line $relayLog "served update 01-real.json" 30) ""
            Check "the fresh courier's reaction reached Telegram: setMessageReaction -> 200" (Wait-Line $relayLog "forwarded setMessageReaction -> 200" 30) ""
            Start-Sleep -Seconds 4
            Stop-ScratchCourier $procB $homeB
            $realNote = Notes $repo | Where-Object { $_.Text -eq "PK5.1 relay: the real message as a note" } | Select-Object -First 1
            Check "the note was filed with the real message id" ($realNote -and "$($realNote.MessageId)" -eq $realId) ""

            # Cleared: the empty reaction list, sent through the relay the same way.
            $clearBody = '{"chat_id":"' + $AdminChat + '","message_id":' + $realId + ',"reaction":[]}'
            $clear = Invoke-WebRequest -UseBasicParsing -Method Post -ContentType "application/json" -Body $clearBody `
                -Uri ("http://127.0.0.1:$relayPort/bot" + $env:CONDUCTOR_TELEGRAM_TOKEN + "/setMessageReaction")
            Check "the reaction is cleared (setMessageReaction with no reactions -> 200)" ($clear.StatusCode -eq 200) ""

            $del = Invoke-Engine $homeB "INHERIT" "say --to admin --delete $realId"
            "    | $($del.out.Trim())"
            Check "say --delete takes the message back" ($del.exit -eq 0 -and $del.out -match "deleted directly: chat $AdminChat, message id $realId") ""

            $forwarded = @(Get-Content $relayLog | Where-Object { $_ -like "forwarded *" })
            "    | relay: " + ($forwarded -join " | ")
            Check "the relay forwarded exactly sendMessage (say), setMessageReaction (courier), setMessageReaction (clear), deleteMessage" (($forwarded -join ",") -eq "forwarded sendMessage -> 200,forwarded setMessageReaction -> 200,forwarded setMessageReaction -> 200,forwarded deleteMessage -> 200") ""
            "    | relay refused (not forwarded to Telegram): " + ((@(Get-Content $relayLog | Where-Object { $_ -like "refused *" }) | Sort-Object -Unique) -join ", ")
            ""
            "  REAL SEND: chat id $AdminChat (the admin DM), message id $realId - reacted by the fresh courier, reaction cleared, message deleted $(Utc)"
        }
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
