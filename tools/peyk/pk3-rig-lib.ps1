# PK3 rig helpers, dot-sourced by the PK3.2 and PK3.3 proofs. The caller sets $OutDir (a scratch
# directory under %TEMP%), $RepoRoot, $courier (the fresh conductor-courier.exe) and $engine (the fresh
# conductor.exe) before calling anything here. Windows PowerShell 5.1, ASCII only.
#
# Nothing here touches the real courier: every courier gets its own CONDUCTOR_STATE_HOME and
# CONDUCTOR_COURIER_PORT, and a courier holding the real token only ever sees the send-only relay.

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
function Native($exe, [string[]]$argv) {
    $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    try { $out = & $exe @argv 2>&1 } finally { $ErrorActionPreference = $prev }
    return $out
}

# The stub Bot API: ids from 5001, a media group answered with one message per item, every method logged.
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
        elseif ($method -eq "getMe") { $json = '{"ok":true,"result":{"id":1,"username":"pk3_stub_bot"}}' }
        else { $json = '{"ok":true,"result":true}' }
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $ctx.Response.ContentType = "application/json"
        $ctx.Response.ContentLength64 = $bytes.Length
        $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        $ctx.Response.Close()
    }
}

# The send-only relay for a real send: sendMessage and deleteMessage go to api.telegram.org, getUpdates
# is answered here, anything else is refused. Logs the METHOD and status, never the path (the token).
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
            $json = '{"ok":false,"error_code":403,"description":"the pk3 relay forwards sendMessage and deleteMessage only"}'
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

function New-CourierHome($name, $apiBase, $chats, $projects) {
    $h = Join-Path $OutDir $name
    New-Item -ItemType Directory -Path (Join-Path $h "courier") | Out-Null
    if (-not $projects) { $projects = @(@{ plan = "pk3-scratch"; repo = $OutDir }) }
    $settings = @{ projects = $projects; chats = $chats; pollIntervalSeconds = 2; apiBaseUrl = $apiBase } | ConvertTo-Json -Depth 4
    Set-Content -Path (Join-Path $h "courier\courier.json") -Value $settings -Encoding ASCII
    return $h
}

# Starts the fresh courier on a scratch home and port and waits for its presence record. $token null
# keeps this process's CONDUCTOR_TELEGRAM_TOKEN (the real one, for a relay courier only).
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

# Stops a scratch courier this rig started (by its own process handle) and removes the presence record
# it could not clear, so a run's keep-alive finds no task to restart - absent, not dead.
function Stop-ScratchCourier($proc, $stateHome) {
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    try { $proc.WaitForExit(10000) | Out-Null } catch { }
    Remove-Item (Join-Path $stateHome "courier\courier.run.json") -ErrorAction SilentlyContinue
}

function Ledger($stateHome) {
    $path = Join-Path $stateHome "courier\messages.jsonl"
    if (-not (Test-Path $path)) { return @() }
    return @(Get-Content $path | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
}

# Runs the fresh engine with a scratch state home; answers @{ exit; out }. $token: a scratch token, or
# "INHERIT" to keep this process's, or $null to remove it.
function Invoke-Engine($stateHome, $token, $arguments, $workDir, $timeoutMs) {
    if (-not $timeoutMs) { $timeoutMs = 120000 }
    $psi = New-Object System.Diagnostics.ProcessStartInfo $engine
    $psi.Arguments = $arguments
    if ($workDir) { $psi.WorkingDirectory = $workDir }
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.CreateNoWindow = $true
    $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
    $psi.EnvironmentVariables["CONDUCTOR_STATE_HOME"] = $stateHome
    $psi.EnvironmentVariables.Remove("CONDUCTOR_PLAN")
    $psi.EnvironmentVariables.Remove("CONDUCTOR_COURIER_PORT")
    if ($null -eq $token) { $psi.EnvironmentVariables.Remove("CONDUCTOR_TELEGRAM_TOKEN") }
    elseif ($token -ne "INHERIT") { $psi.EnvironmentVariables["CONDUCTOR_TELEGRAM_TOKEN"] = $token }
    $p = [System.Diagnostics.Process]::Start($psi)
    $err = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEndAsync()
    if (-not $p.WaitForExit($timeoutMs)) { Stop-Process -Id $p.Id -Force; return @{ exit = -1; out = "timed out"; err = "" } }
    return @{ exit = $p.ExitCode; out = $out.Result; err = $err.Result }
}
