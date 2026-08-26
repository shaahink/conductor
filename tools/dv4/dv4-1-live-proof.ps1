# DV4.1 live proof - the courier verb, as REAL PROCESSES, against a scratch Bot API.
#
# What the in-process test cannot show and this can: the durable offset surviving a
# process BOUNDARY. Every step below is a separate conductor.exe, started and exited,
# reading the same state home off the disk.
#
# What it proves, in order:
#   1. `courier status` on an unconfigured machine refuses BY NAME and prints the
#      24-hour retention limit (findings 6.3)
#   2. `courier allow` / `courier chat` write the EXPLICIT allowlist
#   3. `courier run --once` polls a scratch Bot API, routes a voice note to the
#      allowlisted project, and files it - with no run, no plan and no engine
#   4. a SECOND process is served nothing: the offset survived process death
#   5. with the offset put back to the value a KILL leaves behind - nothing
#      confirmed - a third process is re-served the same update and files it ZERO
#      times. One note on disk, one index line, start to finish.
#   6. `courier deny` takes the project off the list again
#
# Scratch only: its own state home, its own repo, its own bot token, a stub Bot API
# on loopback. It never touches this repo's .conductor, never starts a run, never
# dials api.telegram.org, and never aims a run-control verb at anything.
# ASCII only (Windows PowerShell 5.1).

param(
    [string]$OutDir   = (Join-Path $env:TEMP "dv41-rig"),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"
$env:CONDUCTOR_PLAN = $null            # trap 3: never inherit another rig's plan

$exe = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
if (-not (Test-Path $exe)) { throw "build first: dotnet build Conductor.slnx  (missing $exe)" }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$stateHome = Join-Path $OutDir "state-home"
$repo      = Join-Path $OutDir "dv41-project"
New-Item -ItemType Directory -Path $stateHome | Out-Null
New-Item -ItemType Directory -Path (Join-Path $repo ".conductor") | Out-Null
Set-Content -Path (Join-Path $repo "TRACKER.md") -Value "# dv41 rig" -Encoding ASCII

# ---- a scratch Bot API on loopback -------------------------------------------------------
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$probe.Start(); $port = $probe.LocalEndpoint.Port; $probe.Stop()
$root  = "http://127.0.0.1:$port"
$token = "111111:dv41-scratch-token"
$sent  = Join-Path $OutDir "sent.log"
Set-Content -Path $sent -Value "" -Encoding ASCII

$server = {
    param($prefix, $sentLog)
    $listener = [System.Net.HttpListener]::new()
    $listener.Prefixes.Add("$prefix/")
    $listener.Start()

    # Two updates, served exactly the way api.telegram.org serves them: anything whose
    # update_id is at or above the offset the caller asked with, every time it asks.
    $updates = @(
        @{ id = 1; json = '{"message_id":1,"chat":{"id":770000001},"text":"/project dv41-project"}' },
        @{ id = 2; json = '{"message_id":2,"chat":{"id":770000001},"caption":"the courier heard this with no run live","voice":{"file_id":"voice-1","file_unique_id":"u1","duration":7,"mime_type":"audio/ogg","file_size":64}}' }
    )

    while ($listener.IsListening) {
        try { $ctx = $listener.GetContext() } catch { break }
        $path  = $ctx.Request.Url.AbsolutePath
        $query = $ctx.Request.Url.Query
        $body  = '{"ok":true,"result":{"message_id":4242}}'

        if ($path -like "*/file/bot*") {
            # the audio bytes themselves
            $bytes = [byte[]](1..64 | ForEach-Object { 0x4F })
            $ctx.Response.ContentType = "application/octet-stream"
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
            $ctx.Response.Close()
            continue
        }

        $method = ($path -split '/')[-1]
        if ($method -eq "getUpdates") {
            $offset = 0
            if ($query -match "offset=(\d+)") { $offset = [int]$matches[1] }
            $serve = @($updates | Where-Object { $_.id -ge $offset })
            $parts = @($serve | ForEach-Object { '{"update_id":' + $_.id + ',"message":' + $_.json + '}' })
            $body = '{"ok":true,"result":[' + ($parts -join ',') + ']}'
        }
        elseif ($method -eq "getFile") {
            $body = '{"ok":true,"result":{"file_id":"voice-1","file_path":"voice/file_1.oga","file_size":64}}'
        }
        elseif ($method -eq "sendMessage") {
            $reader = [System.IO.StreamReader]::new($ctx.Request.InputStream)
            Add-Content -Path $sentLog -Value $reader.ReadToEnd()
            $reader.Dispose()
        }

        $out = [System.Text.Encoding]::UTF8.GetBytes($body)
        $ctx.Response.ContentType = "application/json"
        $ctx.Response.OutputStream.Write($out, 0, $out.Length)
        $ctx.Response.Close()
    }
}

$job = Start-Job -ScriptBlock $server -ArgumentList $root, $sent
Start-Sleep -Milliseconds 700

$env:CONDUCTOR_STATE_HOME    = $stateHome
$env:CONDUCTOR_TELEGRAM_TOKEN = $token

function Step($title, [scriptblock]$body) {
    ""
    "=== $title ==="
    & $body 2>&1 | ForEach-Object { $_.ToString() }
    "(exit $LASTEXITCODE)"
}

try {
    Step "1. courier status - nothing configured yet" { & $exe courier status }

    Step "2. the EXPLICIT allowlist" {
        & $exe courier allow --repo $repo --plan dv41-project
        & $exe courier chat --id 770000001 --profile admin
    }

    # The stub's api root goes into the courier's own settings file - the machine-level
    # twin of telegram.apiBaseUrl, so no rig ever dials the real Bot API.
    $settingsPath = Join-Path $stateHome "courier\courier.json"
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName apiBaseUrl -NotePropertyValue $root -Force
    $settings | ConvertTo-Json -Depth 6 | Set-Content -Path $settingsPath -Encoding ASCII

    Step "3. courier status - ready" { & $exe courier status }

    Step "4. PROCESS A: courier run --once" { & $exe courier run --once }

    $inbox = Join-Path $repo ".conductor\inbox"
    Step "   what landed" {
        Get-ChildItem -Path $inbox -Recurse -File | ForEach-Object {
            $_.FullName.Substring($inbox.Length + 1) + "  " + $_.Length + " bytes"
        }
    }
    Step "   the offset, on disk" { Get-Content (Join-Path $stateHome "courier\offset.json") -Raw }

    Step "5. PROCESS B: courier run --once - the offset survived the process" { & $exe courier run --once }

    # The state a KILL between receive and acknowledge leaves behind: the note is filed,
    # nothing was confirmed. Put the offset back there and let a third process meet it.
    $offsetPath = Join-Path $stateHome "courier\offset.json"
    Set-Content -Path $offsetPath -Value '{"offset": 0, "updatedUtc": "2026-01-01T00:00:00Z"}' -Encoding ASCII

    Step "6. PROCESS C: the offset a kill leaves - re-served, filed zero times" { & $exe courier run --once }

    Step "   ONE note, ONE index line - after three processes" {
        "notes:       " + @(Get-ChildItem (Join-Path $inbox "notes") -Filter *.json).Count
        "index lines: " + @(Get-Content (Join-Path $inbox "index.jsonl") | Where-Object { $_.Trim() }).Count
        "media:       " + @(Get-ChildItem (Join-Path $inbox "media") -File).Count
        Get-Content (Join-Path $inbox "notes\2.json") -Raw
    }

    Step "   what the owner was told (once, not twice)" {
        (Get-Content $sent -Raw) -split '(?<=\})\s*(?=\{)' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    }

    Step "7. courier deny - off the list again" {
        & $exe courier deny --repo $repo
        & $exe courier status
    }
}
finally {
    Stop-Job $job -ErrorAction SilentlyContinue | Out-Null
    Remove-Job $job -Force -ErrorAction SilentlyContinue | Out-Null
    Remove-Item Env:\CONDUCTOR_STATE_HOME -ErrorAction SilentlyContinue
    Remove-Item Env:\CONDUCTOR_TELEGRAM_TOKEN -ErrorAction SilentlyContinue
}

""
"[rig] done. scratch tree: $OutDir"
