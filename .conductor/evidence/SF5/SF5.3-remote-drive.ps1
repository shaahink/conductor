# SF5.3 live drive: the wake LEAVES THE MACHINE, against a real engine run.
#
# What only a live run can show (22 unit tests prove the dispatcher, not the verb):
#   A. `conductor watch` with a supervisor.remote block POSTs the brief to a listener process that
#      shares nothing with the run, and the body is byte-for-byte the brief on the watch's own stdout.
#   B. The header credential is expanded FROM THE ENVIRONMENT at send time -- the plan text names
#      ${SF53_WAKE_TOKEN} and never contains it.
#   C. The remote goes out even though the LOCAL supervisor is rate limited on the same wake. That is
#      the claim that matters at 3am: the hour a local babysitter has burnt its fuse is the hour a
#      human off the box most needs to hear.
#   D. Telegram: the same wake reaching api.telegram.org over the internet -- a listener that is
#      genuinely not on this machine -- sent by the WATCH process, not the engine.
#   E. --notify replaces the whole block: a second watch on the same wake hits the one-off URL only,
#      does not touch the plan's webhook, does not ring the phone, and does not spend the plan's fuse.
#
# Traps honoured: scratch repo under %TEMP% with its own plan and .conductor (never C:/code/conductor),
# driven by THIS working tree's build, -p on every call (bug #20), --no-control-plane so no port is
# taken, listener ports probed free (never 4317/4318), and the bot token is read from the User
# environment into this process only -- never echoed, never written to a file.
param([double]$AgentSleepSeconds = 5, [string]$TelegramChatId = '99205495')
$ErrorActionPreference = 'Stop'

$exe = 'C:\code\conductor\src\Conductor\bin\Debug\net10.0\conductor.exe'
$out = 'C:\code\conductor\.conductor\evidence\SF5\live53'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out -Force | Out-Null

function Get-FreePort {
    $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $probe.Start(); $p = $probe.LocalEndpoint.Port; $probe.Stop(); return $p
}

function Spawn($argLine, $tag) {
    $p = Start-Process -FilePath $exe -PassThru -NoNewWindow -ArgumentList $argLine `
        -RedirectStandardOutput (Join-Path $out "$tag.stdout.json") `
        -RedirectStandardError  (Join-Path $out "$tag.stderr.txt")
    $null = $p.Handle
    return $p
}

Write-Output "=== SF5.3 live drive: remote supervision ==="
Write-Output "build under test : $exe"
Write-Output "build stamp      : $((Get-Item $exe).LastWriteTime.ToString('u'))"
Write-Output ""

# The credential the plan NAMES but never contains.
$env:SF53_WAKE_TOKEN = 'live-drive-secret-' + (Get-Random)
# The bot token, from the user environment into this process only (never printed, never written).
$tok = [Environment]::GetEnvironmentVariable('CONDUCTOR_TELEGRAM_TOKEN', 'User')
$haveTelegram = -not [string]::IsNullOrWhiteSpace($tok)
if ($haveTelegram) { $env:CONDUCTOR_TELEGRAM_TOKEN = $tok }
Write-Output "telegram token available to the watch process: $haveTelegram   (value never printed)"

$portPlan = Get-FreePort
$portOneOff = Get-FreePort
Write-Output "listener ports (probed free, not 4317/4318): plan=$portPlan  one-off=$portOneOff"
Write-Output ""

# ---------------------------------------------------------------- the rig
& "$PSScriptRoot\SF5.1-watch-rig.ps1" -Name sf53a -AgentSleepSeconds $AgentSleepSeconds | Out-Null
$root = Join-Path $env:TEMP 'sarban-proofs\sf53a'
$planPath = Join-Path $root 'rig.plan.json'
$plan = ($root -replace '\\', '/') + '/rig.plan.json'
$sinkLocal = (Join-Path $out 'local-supervisor-should-not-run.json') -replace '\\', '/'

$cmd = "[Console]::In.ReadToEnd() | Set-Content -Encoding ascii '$sinkLocal'"
$block = @"
  "telegram": { "allowedChatIds": ["$TelegramChatId"] },
  "supervisor": {
    "command": "$cmd",
    "timeoutMinutes": 2,
    "maxPerHour": 1,
    "standingOrders": "SF5.3 proof rig: escalate everything.",
    "remote": {
      "webhookUrl": "http://127.0.0.1:$portPlan/wake",
      "headers": { "X-Wake-Auth": "Bearer `${SF53_WAKE_TOKEN}", "X-Wake-Missing": "`${SF53_NOT_SET_ANYWHERE}" },
      "telegram": $(if ($haveTelegram) { 'true' } else { 'false' })
    }
  },
"@
$text = Get-Content $planPath -Raw
# A name the owner can read on a phone and know instantly it is not their run.
$text = $text -replace '"name": "SF51WatchRig_?sf53a"', '"name": "SF53-REMOTE-PROOF-scratch-rig-not-your-run"'
$text = $text.Replace('  "report": { "commit": false }', $block + '  "report": { "commit": false }')
$text | Out-File -Encoding ascii $planPath

# The fire an EARLIER watch process left behind: the LOCAL supervisor is out of budget for this hour.
$state = Join-Path $root '.conductor'
New-Item -ItemType Directory -Path $state -Force | Out-Null
(Get-Date).ToUniversalTime().AddMinutes(-5).ToString('o') | Out-File -Encoding ascii (Join-Path $state 'supervisor-fires.log')

Write-Output "rig plan: $plan"
Write-Output "local supervisor: maxPerHour 1, one fire already on the ledger  -> it must NOT run"
Write-Output ""

# ---------------------------------------------------------------- listeners + engine + two watches
$lp = Start-Process -FilePath 'powershell.exe' -PassThru -NoNewWindow -ArgumentList `
    "-NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\SF5.3-listener.ps1`" -Port $portPlan -Out `"$out`" -Tag webhook"
$lo = Start-Process -FilePath 'powershell.exe' -PassThru -NoNewWindow -ArgumentList `
    "-NoProfile -ExecutionPolicy Bypass -File `"$PSScriptRoot\SF5.3-listener.ps1`" -Port $portOneOff -Out `"$out`" -Tag oneoff"
Start-Sleep -Seconds 2

$engine = Start-Process -FilePath $exe -PassThru -NoNewWindow `
    -ArgumentList "run -p `"$plan`" --headless --no-face --no-control-plane" `
    -RedirectStandardOutput (Join-Path $out 'engine.stdout.txt') `
    -RedirectStandardError  (Join-Path $out 'engine.stderr.txt')
$null = $engine.Handle

$db = Join-Path $root '.conductor\run.db'
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path $db) -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 200 }
Start-Sleep -Milliseconds 500

$wp = Spawn "watch -p `"$plan`" --json --poll 1" 'watchPlan'
$wn = Spawn "watch -p `"$plan`" --json --poll 1 --notify http://127.0.0.1:$portOneOff/one-off" 'watchNotify'
Write-Output "watch (plan remote) pid $($wp.Id)   watch (--notify) pid $($wn.Id)"

$wp.WaitForExit(); $wn.WaitForExit(); $engine.WaitForExit()
$lp.WaitForExit(20000) | Out-Null
$lo.WaitForExit(20000) | Out-Null
Write-Output "watch(plan) exit $($wp.ExitCode)   watch(--notify) exit $($wn.ExitCode)   engine exit $($engine.ExitCode)   [watch: 0=wake 10=heartbeat]"
Write-Output ""

# ---------------------------------------------------------------- what actually arrived
$bodyFile = Join-Path $out 'webhook-body.json'
$briefFile = Join-Path $out 'watchPlan.stdout.json'
Write-Output "--- A: the listener process received the wake ---"
Write-Output "webhook body written by the listener : $(Test-Path $bodyFile)"
if (Test-Path $bodyFile) {
    $body = (Get-Content $bodyFile -Raw).Trim()
    $brief = (Get-Content $briefFile -Raw).Trim()
    Write-Output "body is byte-for-byte the brief on the watch's stdout: $($body -eq $brief)  ($($body.Length) chars)"
    $j = $body | ConvertFrom-Json
    Write-Output "  reason         : $($j.reason)"
    Write-Output "  plan           : $($j.plan)"
    Write-Output "  standingOrders : $($j.standingOrders)"
    Write-Output "  suggest        : $($j.suggest -join ' | ')"
    Write-Output ""
    Write-Output "--- B: the credential was expanded from the environment, not stored in the plan ---"
    $hdr = Get-Content (Join-Path $out 'webhook-headers.txt')
    $auth = @($hdr | Where-Object { $_ -like 'header X-Wake-Auth *' })[0]
    $expected = 'header X-Wake-Auth : Bearer ' + $env:SF53_WAKE_TOKEN
    Write-Output ($hdr | Where-Object { $_ -like 'method*' -or $_ -like 'content-type*' })
    Write-Output "  arrived expanded  : $($auth -eq $expected)   (header value never printed)"
    Write-Output "  literal in plan   : $((Get-Content $planPath -Raw) -match '\$\{SF53_WAKE_TOKEN\}')   (the plan names it, never contains it)"
    Write-Output "  token in the plan : $((Get-Content $planPath -Raw).Contains($env:SF53_WAKE_TOKEN))   (must be False)"
    Write-Output "  unset-var header dropped: $(-not ($hdr | Where-Object { $_ -like 'header X-Wake-Missing*' }))   (must be True)"
}
Write-Output ""

Write-Output "--- C: the local supervisor was rate limited on this same wake ---"
Write-Output "local supervisor ran     : $(Test-Path ($sinkLocal -replace '/', '\'))   (must be False)"
Write-Output "remote fires ledger      : $(if (Test-Path (Join-Path $state 'supervisor-remote-fires.log')) { @(Get-Content (Join-Path $state 'supervisor-remote-fires.log')).Count } else { 0 } ) line(s)"
Write-Output "supervisor fires ledger  : $(@(Get-Content (Join-Path $state 'supervisor-fires.log')).Count) line(s)   (the pre-seeded one only: two fuses, two ledgers)"
Write-Output ""
Write-Output "--- watch(plan) stderr: the human channel ---"
Get-Content (Join-Path $out 'watchPlan.stderr.txt') -Encoding UTF8
Write-Output ""
Write-Output "--- E: --notify replaced the whole block ---"
Write-Output "one-off listener got the brief : $(Test-Path (Join-Path $out 'oneoff-body.json'))"
Write-Output "--- watch(--notify) stderr ---"
Get-Content (Join-Path $out 'watchNotify.stderr.txt') -Encoding UTF8
Write-Output ""
Write-Output "--- D: the phone line that went to api.telegram.org (rendered from the same brief) ---"
if (Test-Path $bodyFile) {
    $j = (Get-Content $bodyFile -Raw) | ConvertFrom-Json
    Write-Output "reason=$($j.reason) plan=$($j.plan) stage=$($j.stage) spend=$($j.spendUsd)"
}
Write-Output "(delivery status is the 'remote telegram' line in watch(plan) stderr above)"
