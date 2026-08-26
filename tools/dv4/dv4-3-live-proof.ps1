# DV4.3 live proof - the run<->courier loopback seam, against a REAL daemon on a REAL socket.
#
# What the in-process tests cannot show and this can: a separately STARTED conductor.exe
# opening the listener for itself, writing the port into its own presence record, and a
# second process finding it, proving itself with the file-permission-protected secret, and
# getting a message all the way out to a bot API. Everything below the first section is a
# claim about two processes, and no unit test has two processes.
#
# What it proves, in order:
#   1. `courier status --json` before anything: no port, no secret, nothing running
#   2. the daemon starts, binds its port, and RECORDS it - courier.run.json carries the port
#      and the protocol, and `courier status` prints the loopback url
#   3. the secret file, read back with Get-Acl by this script and not by the engine:
#      inheritance broken, exactly one identity, and that identity is this account
#   4. GET /hello with no header -> 401; with a wrong secret -> 401; with the real one -> 200
#      and the SAME record that is on disk (pid, protocol, port)
#   5. POST /push with the secret -> the message arrives at the bot API stub, stamped, with
#      its buttons - a run pushing THROUGH the daemon, end to end
#   6. POST /push claiming a NEWER protocol -> 409, naming `conductor courier restart`
#   7. an unknown path -> 404, and the listener is NOT reachable on this machine's own LAN
#      address, which is what loopback-only has to mean to be worth stating
#   8. a SECOND `courier run` while one is live is refused BY NAME (pid and all), exits
#      non-zero, and leaves the running courier's presence record untouched
#   9. the daemon is killed; a courier started with its named port HELD by something else
#      says so and claims no listener; `courier status --json` says unreachable; and
#      `conductor report` on a scratch plan writes "courier DEAD" into REPORT.md AND the
#      owner queue, with the restart verb
#
# Scratch only: its own state home, its own two ports (never CourierEndpoint.DefaultPort),
# its own repo, its own SCRATCH bot token, and a bot API stub on loopback. It never touches
# this repo's .conductor, never starts a run, never runs tools/install.ps1 (trap 1), and it
# OVERWRITES CONDUCTOR_TELEGRAM_TOKEN in this process so the rig can never reach the real
# bot (trap 4) - Telegram allows one getUpdates consumer per token.
# ASCII only (Windows PowerShell 5.1).

param(
    [string]$OutDir   = (Join-Path $env:TEMP "dv43-rig"),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"
$env:CONDUCTOR_PLAN = $null            # trap 3: never inherit another rig's plan

$exe = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
if (-not (Test-Path $exe)) { throw "build first: dotnet build Conductor.slnx  (missing $exe)" }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$stateHome = Join-Path $OutDir "state-home"
$repo      = Join-Path $OutDir "alpha-repo"
New-Item -ItemType Directory -Path $stateHome | Out-Null
New-Item -ItemType Directory -Path $repo | Out-Null
$env:CONDUCTOR_STATE_HOME = $stateHome
Set-Location $OutDir      # trap 3: never let a verb fall back to discovering THIS repo's plan

# Two free ports from the OS. NEVER the named one: a rig that bound it would starve a real
# courier on this machine of the socket its runs are dialling.
function FreePort {
    $l = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    $p = $l.LocalEndpoint.Port
    $l.Stop()
    return $p
}
$courierPort = FreePort
$botPort     = FreePort
$env:CONDUCTOR_COURIER_PORT   = $courierPort
$env:CONDUCTOR_TELEGRAM_TOKEN = "111111:dv43-scratch-token"

$fails = @()
function Check($label, $condition, $detail) {
    if ($condition) {
        Write-Host ("  OK   {0}" -f $label) -ForegroundColor Green
    } else {
        Write-Host ("  FAIL {0} :: {1}" -f $label, $detail) -ForegroundColor Red
        $script:fails += $label
    }
}
function Courier { & $exe courier @args 2>&1 }

# Invoke-WebRequest THROWS on 401/404/409 in Windows PowerShell, which is exactly the half of
# this proof that matters, so every call goes through here and the status code is read off the
# exception's response rather than lost.
function Http($method, $url, $secret, $body) {
    $headers = @{}
    if ($secret) { $headers["X-Conductor-Courier"] = $secret }
    try {
        if ($body) {
            $r = Invoke-WebRequest -Uri $url -Method $method -Headers $headers -Body $body `
                    -ContentType "application/json" -UseBasicParsing -TimeoutSec 15
        } else {
            $r = Invoke-WebRequest -Uri $url -Method $method -Headers $headers `
                    -UseBasicParsing -TimeoutSec 15
        }
        return [pscustomobject]@{ Status = [int]$r.StatusCode; Body = $r.Content }
    } catch {
        # Windows PowerShell has ALREADY drained the error response into ErrorDetails, so reading
        # the stream a second time returns nothing - which is how this rig asserted a 409's status
        # and silently lost its sentence on the first run.
        $resp   = $_.Exception.Response
        $status = if ($resp -ne $null) { [int]$resp.StatusCode } else { 0 }
        $text   = $null
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) {
            $text = $_.ErrorDetails.Message
        } elseif ($resp -ne $null) {
            try {
                $sr = New-Object System.IO.StreamReader($resp.GetResponseStream())
                $text = $sr.ReadToEnd()
            } catch { $text = "" }
        } else {
            $text = $_.Exception.Message
        }
        return [pscustomobject]@{ Status = $status; Body = $text }
    }
}

Write-Host "DV4.3 live proof - the run<->courier loopback seam" -ForegroundColor Cyan
Write-Host ("  exe:          {0}" -f $exe)
Write-Host ("  state home:   {0}" -f $stateHome)
Write-Host ("  courier port: {0}" -f $courierPort)
Write-Host ("  bot stub:     http://127.0.0.1:{0}" -f $botPort)
Write-Host ("  token:        SCRATCH (the real one, if any, is overwritten in this process)")

# ---- the bot API stub: a loopback listener that records what the daemon POSTs ----------------
$botLog = Join-Path $OutDir "bot-api.log"
New-Item -ItemType File -Path $botLog | Out-Null
$botJob = Start-Job -ScriptBlock {
    param($port, $logPath)
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://127.0.0.1:$port/")
    $l.Start()
    while ($l.IsListening) {
        try { $ctx = $l.GetContext() } catch { break }
        $path = $ctx.Request.Url.AbsolutePath
        $body = ""
        if ($ctx.Request.HasEntityBody) {
            $sr = New-Object System.IO.StreamReader($ctx.Request.InputStream)
            $body = $sr.ReadToEnd()
        }
        if ($path -like "*quit*") {
            $ctx.Response.StatusCode = 200
            $ctx.Response.Close()
            $l.Stop()
            break
        }
        if ($path -like "*sendMessage*") {
            Add-Content -Path $logPath -Value ("sendMessage " + $body)
            $json = '{"ok":true,"result":{"message_id":1}}'
        } elseif ($path -like "*getUpdates*") {
            $json = '{"ok":true,"result":[]}'
        } else {
            $json = '{"ok":true,"result":{"id":1,"username":"dv43_stub_bot"}}'
        }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
        $ctx.Response.ContentType = "application/json"
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.Close()
    }
} -ArgumentList $botPort, $botLog
Start-Sleep -Milliseconds 900

$daemon = $null
try {

# ---- 1. before anything -----------------------------------------------------------------------
Write-Host "`n[1] before anything" -ForegroundColor Cyan
Courier allow --repo $repo | Out-Null
Courier chat --id 424242 | Out-Null

# The allowlist is written by the VERBS; only the api root is patched, because there is no verb
# for it and a rig must never dial the real bot API.
$courierJson = Join-Path $stateHome "courier\courier.json"
$cfg = Get-Content $courierJson -Raw | ConvertFrom-Json
$cfg | Add-Member -NotePropertyName apiBaseUrl -NotePropertyValue ("http://127.0.0.1:" + $botPort) -Force
$cfg | ConvertTo-Json -Depth 8 | Set-Content -Path $courierJson -Encoding ASCII

$before = Courier status --json | Out-String | ConvertFrom-Json
Check "no courier is running yet" ($before.running -eq $null) ("running=" + ($before.running | Out-String))
Check "no port yet"               ($before.port -eq $null)    ("port=" + $before.port)
Check "the state home is the SCRATCH one" ($before.dir -like "*$([IO.Path]::GetFileName($OutDir))*") $before.dir

# ---- 2. the daemon starts and records the port it bound ---------------------------------------
Write-Host "`n[2] the daemon starts" -ForegroundColor Cyan
$daemonOut = Join-Path $OutDir "daemon.out.log"
$daemonErr = Join-Path $OutDir "daemon.err.log"
$daemon = Start-Process -FilePath $exe -ArgumentList "courier","run" -PassThru -NoNewWindow `
            -RedirectStandardOutput $daemonOut -RedirectStandardError $daemonErr

$presencePath = Join-Path $stateHome "courier\courier.run.json"
$deadline = (Get-Date).AddSeconds(30)
while ((Get-Date) -lt $deadline) {
    if (Test-Path $presencePath) {
        $p = Get-Content $presencePath -Raw | ConvertFrom-Json
        if ($p.port) { break }
    }
    Start-Sleep -Milliseconds 300
}
$presence = Get-Content $presencePath -Raw | ConvertFrom-Json
Check "the presence record carries the bound port" ($presence.port -eq $courierPort) ("port=" + $presence.port)
Check "the presence record names the daemon's pid" ($presence.pid -eq $daemon.Id) ("pid=" + $presence.pid + " started=" + $daemon.Id)
Check "it states a protocol"                       ($presence.protocol -ge 2) ("protocol=" + $presence.protocol)

$status = Courier status --json | Out-String | ConvertFrom-Json
Check "status agrees about the port"     ($status.port -eq $courierPort) ("port=" + $status.port)
Check "status says the seam is reachable" ($status.unreachable -eq $null) ("unreachable=" + $status.unreachable)
Check "status says the secret is present" ($status.secret.present -eq $true) ("present=" + $status.secret.present)
Check "status says the secret is NOT exposed" ($status.secret.exposed -eq $null) ("exposed=" + $status.secret.exposed)

# ---- 3. the secret file, inspected by THIS script and not by the engine ------------------------
Write-Host "`n[3] the secret file" -ForegroundColor Cyan
$secretPath = Join-Path $stateHome "courier\courier.secret"
$secret = (Get-Content $secretPath -Raw).Trim()
Check "the secret is 32 bytes of hex" ($secret.Length -eq 64) ("len=" + $secret.Length)

$acl = Get-Acl -Path $secretPath
$me  = ([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value
$ids = @($acl.Access | ForEach-Object { $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]).Value } | Sort-Object -Unique)
Check "the secret does NOT inherit the state home's permissions" ($acl.AreAccessRulesProtected) "inherited"
Check "exactly one identity is granted"  ($ids.Count -eq 1) ("identities=" + ($ids -join ","))
Check "and that identity is this account" ($ids[0] -eq $me) ("granted=" + $ids[0] + " me=" + $me)

# ---- 4. the hello is not exempt from the secret ------------------------------------------------
Write-Host "`n[4] the hello" -ForegroundColor Cyan
$base = "http://127.0.0.1:$courierPort"
$anon  = Http GET "$base/hello" $null $null
$wrong = Http GET "$base/hello" ("0" * 64) $null
$right = Http GET "$base/hello" $secret $null
Check "no header  -> 401" ($anon.Status -eq 401)  ("status=" + $anon.Status)
Check "wrong secret -> 401" ($wrong.Status -eq 401) ("status=" + $wrong.Status)
Check "the secret -> 200"   ($right.Status -eq 200) ("status=" + $right.Status + " body=" + $right.Body)

$hello = $right.Body | ConvertFrom-Json
Check "the hello IS the presence record (pid)"      ($hello.pid -eq $presence.pid) ("hello=" + $hello.pid)
Check "the hello IS the presence record (port)"     ($hello.port -eq $courierPort) ("hello=" + $hello.port)
Check "the hello IS the presence record (protocol)" ($hello.protocol -eq $presence.protocol) ("hello=" + $hello.protocol)

# ---- 5. a run pushes THROUGH the daemon, end to end -------------------------------------------
Write-Host "`n[5] a push, all the way out" -ForegroundColor Cyan
$push = @{
    chatId   = "424242"
    text     = "the run says hello"
    stamp    = "<i>alpha-repo@main - DV4</i>"
    severity = "Alert"
    protocol = $presence.protocol
    origin   = "dv43-rig"
    buttons  = @(@{ text = "promote"; callbackData = "promote:1" })
} | ConvertTo-Json -Depth 6
$pushed = Http POST "$base/push" $secret $push
Check "the daemon accepted the push" ($pushed.Status -eq 200) ("status=" + $pushed.Status + " body=" + $pushed.Body)

$deadline = (Get-Date).AddSeconds(15)
$sent = ""
while ((Get-Date) -lt $deadline) {
    $sent = (Get-Content $botLog -Raw -ErrorAction SilentlyContinue)
    if ($sent -and $sent -match "sendMessage") { break }
    Start-Sleep -Milliseconds 300
}
Check "it reached the bot API"                ($sent -match "sendMessage") "nothing arrived at the stub"
Check "carrying the RUN's stamp"              ($sent -match "alpha-repo@main") "no stamp on the wire"
Check "carrying the body"                     ($sent -match "the run says hello") "no body on the wire"
Check "carrying the buttons as an inline keyboard" ($sent -match "inline_keyboard" -and $sent -match "promote:1") "no keyboard"
Check "Alert buzzed rather than arriving silently" ($sent -match '"disable_notification":\s*false') "silent"

# ---- 6. the handshake, the other way round ----------------------------------------------------
Write-Host "`n[6] a push from a NEWER run" -ForegroundColor Cyan
$future = @{ chatId = "424242"; text = "from the future"; protocol = ($presence.protocol + 1) } | ConvertTo-Json
$refused = Http POST "$base/push" $secret $future
Check "a newer protocol is refused"          ($refused.Status -eq 409) ("status=" + $refused.Status)
Check "and the refusal names the restart verb" ($refused.Body -match "conductor courier restart") $refused.Body

# ---- 7. loopback only, and nothing else answered ----------------------------------------------
Write-Host "`n[7] the surface is exactly two verbs" -ForegroundColor Cyan
$nope = Http GET "$base/state" $secret $null
Check "an unknown path -> 404" ($nope.Status -eq 404) ("status=" + $nope.Status)

$lan = @([System.Net.Dns]::GetHostAddresses([System.Net.Dns]::GetHostName()) |
         Where-Object { $_.AddressFamily -eq "InterNetwork" -and -not [System.Net.IPAddress]::IsLoopback($_) })
if ($lan.Count -eq 0) {
    Write-Host "  --   no non-loopback IPv4 on this machine; the negative is not provable here" -ForegroundColor Yellow
} else {
    $sock = New-Object System.Net.Sockets.Socket("InterNetwork","Stream","Tcp")
    $reached = $false
    try {
        $ar = $sock.BeginConnect($lan[0], $courierPort, $null, $null)
        $reached = $ar.AsyncWaitHandle.WaitOne(3000) -and $sock.Connected
    } catch { $reached = $false } finally { $sock.Close() }
    Check ("not reachable on this machine's own address " + $lan[0]) (-not $reached) "REACHED from a non-loopback address"
}

# ---- 8. a second courier is refused BY NAME and clobbers nothing ------------------------------
Write-Host "`n[8] a second courier" -ForegroundColor Cyan
$secondOut = Join-Path $OutDir "second.log"
& $exe courier run *> $secondOut
$secondExit = $LASTEXITCODE
$secondText = (Get-Content $secondOut -Raw)
Check "a second courier run exits non-zero" ($secondExit -ne 0) ("exit=" + $secondExit)
Check "and says WHICH courier is already running" ($secondText -match ("pid " + $daemon.Id)) $secondText
Check "naming the verb that stops it" ($secondText -match "courier stop") $secondText

$stillThere = Get-Content $presencePath -Raw | ConvertFrom-Json
Check "the running courier's presence record is untouched" `
      ($stillThere.pid -eq $daemon.Id -and $stillThere.port -eq $courierPort) `
      ("pid=" + $stillThere.pid + " port=" + $stillThere.port)

# ---- 9. kill it, and watch the surfaces go loud ------------------------------------------------
Write-Host "`n[9] the daemon is killed" -ForegroundColor Cyan
$daemonPid = $daemon.Id
Stop-Process -Id $daemonPid -Force
Start-Sleep -Seconds 2
$daemon = $null

$after = Courier status --json | Out-String | ConvertFrom-Json
Check "status no longer sees a running courier" ($after.running -eq $null) ("running=" + ($after.running | Out-String))
Check "and says why a run cannot reach it"      ($after.unreachable -match "no courier is running") ("unreachable=" + $after.unreachable)

# A courier whose NAMED port is held by something else still polls - inbound notes are the
# half of this daemon that needs no socket - but it says so, and it does not scan past it.
$squatter = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $courierPort)
$squatter.Start()
$blockedOut = Join-Path $OutDir "blocked.out.log"
$blocked = Start-Process -FilePath $exe -ArgumentList "courier","run" -PassThru -NoNewWindow `
             -RedirectStandardOutput $blockedOut -RedirectStandardError (Join-Path $OutDir "blocked.err.log")
Start-Sleep -Seconds 4
$blockedLog = (Get-Content $blockedOut -Raw -ErrorAction SilentlyContinue) +
              (Get-Content (Join-Path $OutDir "blocked.err.log") -Raw -ErrorAction SilentlyContinue)
$blockedPresence = Get-Content $presencePath -Raw | ConvertFrom-Json
try { Stop-Process -Id $blocked.Id -Force -ErrorAction SilentlyContinue } catch { }
$squatter.Stop()
Start-Sleep -Seconds 1
Check "a taken port is refused BY NAME"                ($blockedLog -match [regex]::Escape("$courierPort is already in use")) $blockedLog
Check "pointing at the override rather than scanning"  ($blockedLog -match "CONDUCTOR_COURIER_PORT") $blockedLog
Check "and the daemon says it has NO listener rather than claiming one" ($blockedPresence.port -eq $null) ("port=" + $blockedPresence.port)

$plan = Join-Path $repo "conductor.plan.json"
@{
    name    = "dv43-rig"
    repo    = $repo
    tracker = "TRACKER.md"
    stages  = @(@{ id = "DV4"; title = "the courier"; sessions = 1 })
    # Never invoked - no run is ever started here - but plan validation wants a shape.
    agent   = @{ command = "echo"; args = @("{prompt}") }
} | ConvertTo-Json -Depth 6 | Set-Content -Path $plan -Encoding ASCII
Set-Content -Path (Join-Path $repo "TRACKER.md") -Value "# dv43 rig" -Encoding ASCII
if (-not (Test-Path $plan)) { throw "the scratch plan was not written - refusing to run a verb that could fall back to another repo's plan" }

& $exe report -p $plan *> (Join-Path $OutDir "report.log")
$report = Get-Content (Join-Path $repo ".conductor\REPORT.md") -Raw
$queue  = Get-Content (Join-Path $repo ".conductor\owner-queue.md") -Raw -ErrorAction SilentlyContinue
Check "REPORT.md says courier DEAD"    ($report -match "courier DEAD") "no courier line in REPORT.md"
Check "the owner queue says so too"    ($queue  -match "courier is DEAD") "no courier item in the owner queue"
Check "and names the command that fixes it" ($queue -match "conductor courier restart") "no restart verb in the queue"

}
finally {
    if ($daemon -ne $null) { try { Stop-Process -Id $daemon.Id -Force -ErrorAction SilentlyContinue } catch { } }
    # The stub parks in a blocking accept, so it is UNBLOCKED with a request before it is
    # stopped: Stop-Job on a job inside a blocking native call never returns, and that is what
    # left this rig hung for a reader who could only see the last line it had printed.
    if ($botJob -ne $null) {
        try { Invoke-WebRequest -Uri ("http://127.0.0.1:" + $botPort + "/quit") -TimeoutSec 3 -UseBasicParsing | Out-Null } catch { }
        try { Remove-Job $botJob -Force -ErrorAction SilentlyContinue } catch { }
    }
}

Write-Host ""
if ($fails.Count -eq 0) {
    Write-Host "DV4.3 LIVE PROOF: PASS" -ForegroundColor Green
    exit 0
} else {
    Write-Host ("DV4.3 LIVE PROOF: FAIL (" + $fails.Count + ") -> " + ($fails -join "; ")) -ForegroundColor Red
    exit 1
}
