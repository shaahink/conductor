# SF5.2 live drive: the supervisor named in the PLAN, against a real engine run.
#
# What only a live run can show (the unit tests prove the policy, not the verb):
#   A. `conductor watch` with NO --hook on the command line runs the plan's supervisor command and
#      hands it the brief on stdin -- including the standingOrders the plan carries.
#   B. --hook beats the plan block, and does NOT spend a fire from the plan's hourly fuse.
#   C. The fuse is cross-process: a fires ledger written by an EARLIER process makes this watch
#      decline to run the supervisor, out loud on stderr, while still waking normally.
#
# Traps honoured: scratch repo under %TEMP% with its own plan and .conductor (never C:/code/conductor),
# driven by THIS working tree's build, -p on every call (bug #20), --no-control-plane so no port is
# taken from the other run on this machine.
param([double]$AgentSleepSeconds = 5)
$ErrorActionPreference = 'Stop'

$exe = 'C:\code\conductor\src\Conductor\bin\Debug\net10.0\conductor.exe'
$out = 'C:\code\conductor\.conductor\evidence\SF5\live52'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out -Force | Out-Null

$orders = 'You MAY approve an owner gate whose checkpoint has an evidence path. You MUST escalate anything that spends money or merges to master.'

function Sink($name) { (Join-Path $out $name) -replace '\\', '/' }

# Insert a supervisor block into a rig plan produced by the SF5.1 rig script.
function Add-Supervisor($planPath, $sinkFile, $maxPerHour) {
    $cmd = "[Console]::In.ReadToEnd() | Set-Content -Encoding ascii '$sinkFile'"
    $block = @"
  "supervisor": {
    "command": "$cmd",
    "timeoutMinutes": 2,
    "maxPerHour": $maxPerHour,
    "standingOrders": "$orders"
  },
"@
    $text = Get-Content $planPath -Raw
    $text = $text.Replace('  "report": { "commit": false }', $block + '  "report": { "commit": false }')
    $text | Out-File -Encoding ascii $planPath
}

function Spawn($argLine, $tag) {
    $p = Start-Process -FilePath $exe -PassThru -NoNewWindow -ArgumentList $argLine `
        -RedirectStandardOutput (Join-Path $out "$tag.stdout.json") `
        -RedirectStandardError  (Join-Path $out "$tag.stderr.txt")
    $null = $p.Handle   # PS 5.1: touching Handle is what makes ExitCode readable later
    return $p
}

function Start-Engine($plan, $tag) {
    $p = Start-Process -FilePath $exe -PassThru -NoNewWindow `
        -ArgumentList "run -p `"$plan`" --headless --no-face --no-control-plane" `
        -RedirectStandardOutput (Join-Path $out "$tag.engine.stdout.txt") `
        -RedirectStandardError  (Join-Path $out "$tag.engine.stderr.txt")
    $null = $p.Handle
    return $p
}

Write-Output "=== SF5.2 live drive: the supervisor plan block ==="
Write-Output "build under test : $exe"
Write-Output "build stamp      : $((Get-Item $exe).LastWriteTime.ToString('u'))"
Write-Output ""

# ---------------------------------------------------------------- rig A: block fires, --hook wins
& "$PSScriptRoot\SF5.1-watch-rig.ps1" -Name sf52a -AgentSleepSeconds $AgentSleepSeconds | Out-Null
$rootA = Join-Path $env:TEMP 'sarban-proofs\sf52a'
$planA = ($rootA -replace '\\', '/') + '/rig.plan.json'
$sinkP = Sink 'supervisor-stdin.json'      # written by the PLAN's supervisor
$sinkH = Sink 'hook-stdin.json'            # written by the --hook override
$firesA = Join-Path $rootA '.conductor\supervisor-fires.log'
Add-Supervisor (Join-Path $rootA 'rig.plan.json') $sinkP 6

Write-Output "--- A: plan supervisor vs --hook, one run, two watches ---"
Write-Output "rig plan: $planA"
$engineA = Start-Engine $planA 'A'
$dbA = Join-Path $rootA '.conductor\run.db'
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path $dbA) -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 200 }
Start-Sleep -Milliseconds 500

$hookCmd = "[Console]::In.ReadToEnd() | Set-Content -Encoding ascii '$sinkH'"
$wp = Spawn "watch -p `"$planA`" --json --poll 1" 'watchP'                          # no --hook: the plan decides
$wh = Spawn "watch -p `"$planA`" --json --poll 1 --hook `"$hookCmd`"" 'watchH'      # --hook: overrides it
Write-Output "watch P pid $($wp.Id): no --hook, plan supervisor must run"
Write-Output "watch H pid $($wh.Id): --hook given, must override the block"

$wp.WaitForExit(); $wh.WaitForExit(); $engineA.WaitForExit()
Write-Output "watch P exit $($wp.ExitCode)   watch H exit $($wh.ExitCode)   engine exit $($engineA.ExitCode)   [watch: 0=wake 10=heartbeat]"
Write-Output "plan supervisor received stdin : $(Test-Path $sinkP)"
Write-Output "--hook override received stdin: $(Test-Path $sinkH)"
$fireLines = if (Test-Path $firesA) { @(Get-Content $firesA).Count } else { 0 }
Write-Output "fires recorded in supervisor-fires.log: $fireLines   (2 supervisors ran; only the PLAN one spends the fuse)"
Write-Output ""
Write-Output "--- watch P stderr (the human channel) ---"
Get-Content (Join-Path $out 'watchP.stderr.txt')
Write-Output "--- watch H stderr ---"
Get-Content (Join-Path $out 'watchH.stderr.txt')
Write-Output ""
Write-Output "--- what the PLAN's supervisor actually received on stdin ---"
if (Test-Path $sinkP) { Get-Content $sinkP } else { Write-Output '(supervisor never ran)' }
Write-Output ""
$standing = if (Test-Path $sinkP) { (Get-Content $sinkP -Raw | ConvertFrom-Json).standingOrders } else { '' }
Write-Output "standingOrders reached the supervisor on stdin: $($standing -eq $orders)"
Write-Output ""

# ---------------------------------------------------------------- rig B: the cross-process fuse
& "$PSScriptRoot\SF5.1-watch-rig.ps1" -Name sf52b -AgentSleepSeconds $AgentSleepSeconds | Out-Null
$rootB = Join-Path $env:TEMP 'sarban-proofs\sf52b'
$planB = ($rootB -replace '\\', '/') + '/rig.plan.json'
$sinkB = Sink 'ratelimited-should-not-exist.json'
Add-Supervisor (Join-Path $rootB 'rig.plan.json') $sinkB 1

# The fire an EARLIER watch process left behind. This is the whole point: the counter cannot live in
# memory, because every wake starts a fresh `conductor watch`.
$stateB = Join-Path $rootB '.conductor'
New-Item -ItemType Directory -Path $stateB -Force | Out-Null
(Get-Date).ToUniversalTime().AddMinutes(-5).ToString('o') | Out-File -Encoding ascii (Join-Path $stateB 'supervisor-fires.log')

Write-Output "--- B: maxPerHour 1, one fire already on the ledger from an earlier process ---"
$engineB = Start-Engine $planB 'B'
$dbB = Join-Path $rootB '.conductor\run.db'
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path $dbB) -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 200 }
Start-Sleep -Milliseconds 500
$wr = Spawn "watch -p `"$planB`" --json --poll 1" 'watchR'
$wr.WaitForExit(); $engineB.WaitForExit()

Write-Output "watch R exit $($wr.ExitCode)   (it must still WAKE -- the fuse silences the supervisor, not the watch)"
Write-Output "supervisor ran despite the cap : $(Test-Path $sinkB)   (must be False)"
Write-Output "brief still printed on stdout  : $(@(Get-Content (Join-Path $out 'watchR.stdout.json')).Count) lines"
Write-Output "--- watch R stderr ---"
Get-Content (Join-Path $out 'watchR.stderr.txt')
