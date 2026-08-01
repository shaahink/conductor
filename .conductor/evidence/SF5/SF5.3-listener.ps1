# SF5.3 stand-in for the far end of a wake: a listener process that is NOT the conductor, NOT the
# watch, and shares nothing with either except an HTTP port. It records exactly what arrived.
#
# This is the "cloud Claude Code session" seat in the live drive: whatever a relay would hand a remote
# session is whatever lands in webhook-body.json here.
param(
    [Parameter(Mandatory = $true)][int]$Port,
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$Tag = 'webhook',
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Out -Force | Out-Null

$l = New-Object System.Net.HttpListener
$l.Prefixes.Add("http://127.0.0.1:$Port/")
$l.Start()
"listening on http://127.0.0.1:$Port/" | Out-File -Encoding ascii (Join-Path $Out "$Tag.listener.log")

try {
    $task = $l.GetContextAsync()
    if (-not $task.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        "TIMEOUT: nothing arrived in $TimeoutSeconds s" | Add-Content -Encoding ascii (Join-Path $Out "$Tag.listener.log")
        return
    }
    $ctx = $task.Result
    $req = $ctx.Request

    $reader = New-Object System.IO.StreamReader($req.InputStream, [Text.Encoding]::UTF8)
    $body = $reader.ReadToEnd()
    $reader.Close()

    $body | Out-File -Encoding ascii (Join-Path $Out "$Tag-body.json")
    $lines = @("method: $($req.HttpMethod)", "path: $($req.Url.AbsolutePath)", "content-type: $($req.ContentType)", "bytes: $($body.Length)")
    foreach ($k in $req.Headers.AllKeys) { $lines += "header $k : $($req.Headers[$k])" }
    $lines | Out-File -Encoding ascii (Join-Path $Out "$Tag-headers.txt")

    $payload = [Text.Encoding]::UTF8.GetBytes('{"ok":true,"received":"conductor wake"}')
    $ctx.Response.StatusCode = 200
    $ctx.Response.ContentType = 'application/json'
    $ctx.Response.ContentLength64 = $payload.Length
    $ctx.Response.OutputStream.Write($payload, 0, $payload.Length)
    $ctx.Response.Close()
    "received 1 request, answered 200" | Add-Content -Encoding ascii (Join-Path $Out "$Tag.listener.log")
}
finally { $l.Stop(); $l.Close() }
