# PK3.1 live proof - protocol 3 on a scratch courier built from this tree.
#
# Part A (always): the fresh conductor-courier.exe on its own CONDUCTOR_STATE_HOME, its own
#   CONDUCTOR_COURIER_PORT and a scratch token, its Bot API a local stub. It must answer POST /send
#   (text, media group, document), /react, /delete and GET /chats; accept a protocol-2 /push; refuse a
#   newer protocol, a missing secret and a ceiling by name; and write every id to messages.jsonl.
# Part B (-RealSend only): ONE real message to the admin DM through a second scratch courier that
#   holds the environment's token. Its Bot API base is a local RELAY that forwards sendMessage and
#   deleteMessage to api.telegram.org and nothing else: getUpdates is answered locally, so the real
#   courier's one-consumer poll is never touched (trap 4). The message is deleted at once; its chat id
#   and message id are printed for the evidence.
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
$OutDir = Join-Path $env:TEMP ("pk31-proof-" + (Get-Date).ToUniversalTime().ToString("yyyyMMddTHHmmss"))
New-Item -ItemType Directory -Path $OutDir | Out-Null

$script:pass = 0
$script:fail = 0
function Utc() { (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") }
function Section($title) { ""; "==== $title ====" }
function Check($name, $ok, $detail) {
    if ($ok) { $script:pass++; "  PASS  $name" } else { $script:fail++; "  FAIL  $name" }
    if ($detail) { "        $detail" }
}
function FreePort() {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); return $p
}

# One HTTP call to a courier; answers @{ status; body; json } whatever the status code.
function Call($verb, $port, $path, $secret, $payload) {
    $req = [System.Net.HttpWebRequest]::Create("http://127.0.0.1:$port$path")
    $req.Method = $verb
    $req.Timeout = 90000
    if ($secret) { $req.Headers.Add("X-Conductor-Courier", $secret) }
    if ($null -ne $payload) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 6 -Compress))
        $req.ContentType = "application/json"
        $req.ContentLength = $bytes.Length
        $s = $req.GetRequestStream(); $s.Write($bytes, 0, $bytes.Length); $s.Close()
    }
    try { $resp = $req.GetResponse() } catch [System.Net.WebException] { $resp = $_.Exception.Response }
    if ($null -eq $resp) { return @{ status = 0; body = ""; json = $null } }
    $status = [int]$resp.StatusCode
    $reader = New-Object System.IO.StreamReader($resp.GetResponseStream(), [Text.Encoding]::UTF8)
    $body = $reader.ReadToEnd(); $resp.Close()
    $json = $null
    if ($body) { try { $json = $body | ConvertFrom-Json } catch { $json = $null } }
    return @{ status = $status; body = $body; json = $json }
}

# The stub Bot API (part A): ids from 5001, a media group answered with one message per item.
$stubScript = {
    param($port, $logPath)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://127.0.0.1:$port/")
    $l.Start()
    $next = 5001
    while ($true) {
        $ctx = $l.GetContext()
        $method = $ctx.Request.Url.AbsolutePath.Split('/')[-1]
        $reader = New-Object System.IO.StreamReader($ctx.Request.InputStream, [Text.Encoding]::GetEncoding(28591))
        $body = $reader.ReadToEnd()
        Add-Content -Path $logPath -Value $method
        if ($method -eq "getUpdates") { Start-Sleep -Milliseconds 1500; $json = '{"ok":true,"result":[]}' }
        elseif ($method -eq "sendMediaGroup") {
            $n = ([regex]::Matches($body, "attach://")).Count
            $items = @()
            for ($i = 0; $i -lt $n; $i++) { $items += ('{"message_id":' + $next + '}'); $next++ }
            $json = '{"ok":true,"result":[' + ($items -join ",") + ']}'
        }
        elseif ($method -eq "sendMessage" -or $method -eq "sendPhoto" -or $method -eq "sendDocument") {
            $json = '{"ok":true,"result":{"message_id":' + $next + '}}'; $next++
        }
        else { $json = '{"ok":true,"result":true}' }
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $ctx.Response.ContentType = "application/json"
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.Close()
    }
}

# The send-only relay (part B). Logs the METHOD and the status, never the path: the path holds the token.
$relayScript = {
    param($port, $logPath)
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://127.0.0.1:$port/")
    $l.Start()
    while ($true) {
        $ctx = $l.GetContext()
        $path = $ctx.Request.Url.AbsolutePath
        $method = $path.Split('/')[-1]
        $ms = New-Object System.IO.MemoryStream
        $ctx.Request.InputStream.CopyTo($ms)
        $body = $ms.ToArray()
        $status = 200
        if ($method -eq "sendMessage" -or $method -eq "deleteMessage") {
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
            Add-Content -Path $logPath -Value ("forwarded " + $method + " -> " + $status)
        }
        elseif ($method -eq "getUpdates") {
            Start-Sleep -Milliseconds 1500
            $json = '{"ok":true,"result":[]}'
            Add-Content -Path $logPath -Value "answered getUpdates locally"
        }
        else {
            $status = 403
            $json = '{"ok":false,"error_code":403,"description":"the pk31 relay forwards sendMessage and deleteMessage only"}'
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

$courier = Join-Path $RepoRoot "src\Conductor.Courier\bin\Debug\net10.0\conductor-courier.exe"
"PK3.1 proof at $(Utc) on commit $(git rev-parse --short HEAD)"
"  courier $courier  built $((Get-Item $courier).LastWriteTimeUtc.ToString('u'))"

function New-CourierHome($name, $apiBase, $chats) {
    $h = Join-Path $OutDir $name
    New-Item -ItemType Directory -Path (Join-Path $h "courier") | Out-Null
    $settings = @{ projects = @(@{ plan = "pk31-scratch"; repo = $OutDir }); chats = $chats; pollIntervalSeconds = 2; apiBaseUrl = $apiBase } | ConvertTo-Json -Depth 4
    Set-Content -Path (Join-Path $h "courier\courier.json") -Value $settings -Encoding ASCII
    return $h
}

# Starts the fresh courier on a scratch home and port. The environment is set for the child and put
# back: Start-Process in 5.1 has no -Environment, and the child inherits this process's.
function Start-ScratchCourier($stateHome, $port, $token, $label) {
    $saved = @{ home = $env:CONDUCTOR_STATE_HOME; port = $env:CONDUCTOR_COURIER_PORT; token = $env:CONDUCTOR_TELEGRAM_TOKEN }
    try {
        $env:CONDUCTOR_STATE_HOME = $stateHome
        $env:CONDUCTOR_COURIER_PORT = "$port"
        if ($token) { $env:CONDUCTOR_TELEGRAM_TOKEN = $token }
        $p = Start-Process -FilePath $courier -ArgumentList @("--task-name", "`"Conductor Courier ($label)`"") -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $stateHome "stdout.txt") -RedirectStandardError (Join-Path $stateHome "stderr.txt")
    }
    finally {
        $env:CONDUCTOR_STATE_HOME = $saved.home
        $env:CONDUCTOR_COURIER_PORT = $saved.port
        $env:CONDUCTOR_TELEGRAM_TOKEN = $saved.token
    }
    $presence = Join-Path $stateHome "courier\courier.run.json"
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $presence) {
            $rec = Get-Content $presence -Raw | ConvertFrom-Json
            if ($rec.port -eq $port) { return $p }
        }
        Start-Sleep -Milliseconds 500
    }
    throw "the scratch courier ($label) never wrote a presence record naming port $port"
}

function Ledger($stateHome) {
    $path = Join-Path $stateHome "courier\messages.jsonl"
    if (-not (Test-Path $path)) { return @() }
    return @(Get-Content $path | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
}

$jobs = @()
$procs = @()
try {
    # ------------------------------------------------------------------------------------------ A
    Section "A. a scratch courier answers protocol 3 (stub Bot API)"
    $stubPort = FreePort
    $stubLog = Join-Path $OutDir "stub-bot-api.log"
    $jobs += Start-Job -ScriptBlock $stubScript -ArgumentList $stubPort, $stubLog
    $homeA = New-CourierHome "home-a" "http://127.0.0.1:$stubPort" @(@{ chatId = "770000001"; profile = "admin" }, @{ chatId = "770000002"; profile = "observer" })
    $portA = FreePort
    $procA = Start-ScratchCourier $homeA $portA "111111:pk31-scratch-token" "pk31 scratch A"
    $procs += $procA
    $secretA = (Get-Content (Join-Path $homeA "courier\courier.secret") -Raw).Trim()
    "  scratch courier pid $($procA.Id) on port $portA, home $homeA, stub Bot API on $stubPort"

    $hello = Call "GET" $portA "/hello" $secretA $null
    Check "GET /hello answers protocol 3" ($hello.status -eq 200 -and $hello.json.protocol -eq 3) $hello.body

    $text = Call "POST" $portA "/send" $secretA @{ chat = "admin"; text = "<b>PK3.1</b> rig"; replyTo = 12; origin = "pk3-1-live-proof"; stamp = "conductor@feat/peyk-courier - PK3 - PK3.1" }
    Check "POST /send text to a PROFILE answers 200 with its message id and the chat it resolved to" ($text.status -eq 200 -and $text.json.accepted -and @($text.json.messageIds).Count -eq 1 -and $text.json.chatId -eq "770000001") $text.body

    $png1 = Join-Path $OutDir "before.png"; [IO.File]::WriteAllBytes($png1, [byte[]](137, 80, 78, 71, 1, 2, 3))
    $png2 = Join-Path $OutDir "after.png";  [IO.File]::WriteAllBytes($png2, [byte[]](137, 80, 78, 71, 4, 5, 6))
    $group = Call "POST" $portA "/send" $secretA @{ chat = "770000002"; text = "before and after"; photos = @($png1, $png2); origin = "pk3-1-live-proof" }
    Check "POST /send a media group of two photos answers with two ids" ($group.status -eq 200 -and @($group.json.messageIds).Count -eq 2) $group.body

    $md = Join-Path $OutDir "body.md"; Set-Content -Path $md -Value "# a document" -Encoding ASCII
    $doc = Call "POST" $portA "/send" $secretA @{ chat = "admin"; text = "a document"; documents = @($md); origin = "pk3-1-live-proof" }
    Check "POST /send one document answers with its id" ($doc.status -eq 200 -and @($doc.json.messageIds).Count -eq 1) $doc.body

    $firstId = @($text.json.messageIds)[0]
    $react = Call "POST" $portA "/react" $secretA @{ chat = "admin"; messageId = $firstId; emoji = "+1"; origin = "pk3-1-live-proof" }
    Check "POST /react on that id is accepted" ($react.status -eq 200 -and $react.json.accepted) $react.body

    $del = Call "POST" $portA "/delete" $secretA @{ chat = "admin"; messageId = $firstId; origin = "pk3-1-live-proof" }
    Check "POST /delete on that id is accepted" ($del.status -eq 200 -and $del.json.accepted) $del.body

    $chats = Call "GET" $portA "/chats" $secretA $null
    $profiles = @($chats.json | ForEach-Object { $_.profile }) -join ","
    Check "GET /chats lists both chats with their profiles" ($chats.status -eq 200 -and @($chats.json).Count -eq 2 -and $profiles -eq "admin,observer") $chats.body

    $push = Call "POST" $portA "/push" $secretA @{ chatId = "770000001"; text = "a protocol-2 push"; protocol = 2; stamp = "installed engine"; origin = "run pk31-rig" }
    Check "a protocol-2 POST /push is accepted, and now answers with its id" ($push.status -eq 200 -and $push.json.accepted -and @($push.json.messageIds).Count -eq 1) $push.body

    $newer = Call "POST" $portA "/send" $secretA @{ chat = "admin"; text = "from the future"; protocol = 4 }
    Check "a protocol-4 send is refused by name (409)" ($newer.status -eq 409 -and $newer.json.detail -like "*speaks protocol 3*") $newer.body

    $nosecret = Call "POST" $portA "/send" $null @{ chat = "admin"; text = "no secret" }
    Check "a send without the secret is refused (401)" ($nosecret.status -eq 401) ""

    $long = Call "POST" $portA "/send" $secretA @{ chat = "admin"; text = ("x" * 4097) }
    Check "a 4097-character text is refused by name" ($long.status -ne 200 -and $long.json.detail -like "*4096-character message ceiling*") $long.json.detail

    $unknown = Call "POST" $portA "/send" $secretA @{ chat = "stakeholders"; text = "who" }
    Check "a chat that is neither an id nor a profile is refused by name" ($unknown.status -ne 200 -and $unknown.json.detail -like "*neither a chat id nor a profile*") $unknown.json.detail

    Start-Sleep -Milliseconds 500
    $ledgerA = Ledger $homeA
    "  messages.jsonl:"
    $ledgerA | ForEach-Object { "    " + ($_ | ConvertTo-Json -Compress) }
    $verbs = @($ledgerA | ForEach-Object { $_.verb }) -join ","
    Check "messages.jsonl holds send x1, send x2, send x1, delete, push - in order" ($verbs -eq "send,send,send,send,delete,push") $verbs
    $complete = @($ledgerA | Where-Object { $_.id -gt 0 -and $_.chat -and $_.origin -and $_.when }).Count -eq $ledgerA.Count
    Check "every ledger line carries id, chat, origin and when" $complete ""
    Check "the text send's line carries its stamp" (@($ledgerA)[0].stamp -eq "conductor@feat/peyk-courier - PK3 - PK3.1") @($ledgerA)[0].stamp
    $stubCalls = @(Get-Content $stubLog | Where-Object { $_ -ne "getUpdates" }) -join ","
    Check "the stub saw exactly the Bot API calls those verbs imply" ($stubCalls -eq "sendMessage,sendMediaGroup,sendDocument,setMessageReaction,deleteMessage,sendMessage") $stubCalls
    $sentLines = @(Select-String -Path (Join-Path $homeA "courier\courier.log") -Pattern "Courier sent to chat").Count
    Check "courier.log names each send with its ids" ($sentLines -ge 4) "$sentLines lines"

    Stop-Process -Id $procA.Id -Force
    $procs = @()

    # ------------------------------------------------------------------------------------------ B
    if ($RealSend) {
        Section "B. ONE real send to the admin DM through a scratch courier behind a send-only relay"
        if (-not $env:CONDUCTOR_TELEGRAM_TOKEN) { throw "CONDUCTOR_TELEGRAM_TOKEN is not in this environment" }
        $relayPort = FreePort
        $relayLog = Join-Path $OutDir "relay.log"
        $jobs += Start-Job -ScriptBlock $relayScript -ArgumentList $relayPort, $relayLog
        $homeB = New-CourierHome "home-b" "http://127.0.0.1:$relayPort" @(@{ chatId = $AdminChat; profile = "admin" })
        $portB = FreePort
        $procB = Start-ScratchCourier $homeB $portB $null "pk31 scratch B, real token, relay"
        $procs += $procB
        $secretB = (Get-Content (Join-Path $homeB "courier\courier.secret") -Raw).Trim()
        "  scratch courier pid $($procB.Id) on port $portB, home $homeB, relay on $relayPort (forwards sendMessage + deleteMessage only)"
        Start-Sleep -Seconds 4

        $real = Call "POST" $portB "/send" $secretB @{ chat = "admin"; text = "PK3.1 live proof: one protocol-3 send through a scratch courier. It is deleted at once."; origin = "pk3-1-live-proof"; stamp = "conductor@feat/peyk-courier - PK3 - PK3.1" }
        Check "the real /send is accepted and answers with a Telegram message id" ($real.status -eq 200 -and $real.json.accepted -and @($real.json.messageIds).Count -eq 1) $real.body
        $realId = @($real.json.messageIds)[0]
        Start-Sleep -Milliseconds 500
        $ledgerB = Ledger $homeB
        $line = @($ledgerB | Where-Object { $_.id -eq $realId -and $_.verb -eq "send" })
        Check "messages.jsonl carries the id the real send returned" ($line.Count -eq 1 -and $line[0].chat -eq $AdminChat) ($line | ConvertTo-Json -Compress)

        $realDel = Call "POST" $portB "/delete" $secretB @{ chat = "admin"; messageId = $realId; origin = "pk3-1-live-proof" }
        Check "the real message is deleted through /delete" ($realDel.status -eq 200 -and $realDel.json.accepted) $realDel.body
        Start-Sleep -Milliseconds 500
        $ledgerB = Ledger $homeB
        "  messages.jsonl:"
        $ledgerB | ForEach-Object { "    " + ($_ | ConvertTo-Json -Compress) }
        Check "messages.jsonl records the delete of the same id" (@($ledgerB | Where-Object { $_.id -eq $realId -and $_.verb -eq "delete" }).Count -eq 1) ""

        Stop-Process -Id $procB.Id -Force
        $procs = @()
        Start-Sleep -Milliseconds 500
        $forwarded = @(Get-Content $relayLog | Where-Object { $_ -like "forwarded*" })
        $polls = @(Get-Content $relayLog | Where-Object { $_ -eq "answered getUpdates locally" }).Count
        "  relay: " + ($forwarded -join "; ") + "; getUpdates answered locally x$polls; refused: " + (@(Get-Content $relayLog | Where-Object { $_ -like "refused*" }) -join ",")
        Check "the relay forwarded exactly sendMessage then deleteMessage to Telegram, both 200" (($forwarded -join ",") -eq "forwarded sendMessage -> 200,forwarded deleteMessage -> 200") ""
        Check "no getUpdates reached Telegram (the real courier's poll was never contended)" (@(Get-Content $relayLog | Where-Object { $_ -like "*getUpdates*" -and $_ -notlike "answered*" }).Count -eq 0 -and $polls -ge 1) "$polls polls answered by the relay"
        ""
        "  REAL SEND: chat id $AdminChat (the admin DM), message id $realId - sent $(Utc), deleted"
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
