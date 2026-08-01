# SF5.1 live drive: two `conductor watch` processes attached to one real engine run.
#
# Watch A: --timeout (a short heartbeat) -> must return the HEARTBEAT (exit 10) while the run is
#          mid-flight, with the count of events the engine appended in that window as the silence
#          measurement, and its hook must NOT have fired.
# Watch B: no timeout, --hook -> must stay blocked across every one of those same events and return
#          exactly once, on run-ended (exit 0), having handed the brief to the hook on stdin.
#
# Both are this working tree's build. The engine is too. Nothing here points at C:/code/conductor.
param([string]$Name = 'sf51a', [double]$AgentSleepSeconds = 12, [double]$HeartbeatMinutes = 0.35)
$ErrorActionPreference = 'Stop'

$exe = 'C:\code\conductor\src\Conductor\bin\Debug\net10.0\conductor.exe'
$out = 'C:\code\conductor\.conductor\evidence\SF5\live'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out -Force | Out-Null

& "$PSScriptRoot\SF5.1-watch-rig.ps1" -Name $Name -AgentSleepSeconds $AgentSleepSeconds | Out-Null
$root = Join-Path $env:TEMP "sarban-proofs\$Name"
$plan = ($root -replace '\\', '/') + '/rig.plan.json'
$events = Join-Path $root '.conductor\run.db'   # the event log is a TABLE, not a file (see WatchLoop)
$hookA = Join-Path $out 'hookA-should-not-exist.json'
$hookB = Join-Path $out 'hookB-stdin.json'

function EventCount { $l = Join-Path $root ".conductor\conductor.log"; if (Test-Path $l) { @(Get-Content $l -ErrorAction SilentlyContinue).Count } else { 0 } }
function Spawn($argLine, $tag) {
    $p = Start-Process -FilePath $exe -PassThru -NoNewWindow -ArgumentList $argLine `
        -RedirectStandardOutput (Join-Path $out "$tag.stdout.json") `
        -RedirectStandardError  (Join-Path $out "$tag.stderr.txt")
    $null = $p.Handle   # PS 5.1: touching Handle is what makes ExitCode readable later
    return $p
}

Write-Output "=== SF5.1 live drive ==="
Write-Output "build under test : $exe"
Write-Output "build stamp      : $((Get-Item $exe).LastWriteTime.ToString('u'))"
Write-Output "rig plan         : $plan"
Write-Output ""

# The engine goes first: `watch` attaches to a run that already exists, which is also the only way it
# is ever used. Arming happens once the run has a state dir and its first events on the wire, so the
# backlog fold is exercised too.
$engine = Start-Process -FilePath $exe -PassThru -NoNewWindow `
    -ArgumentList "run -p `"$plan`" --headless --no-face --no-control-plane" `
    -RedirectStandardOutput (Join-Path $out 'engine.stdout.txt') `
    -RedirectStandardError  (Join-Path $out 'engine.stderr.txt')
$null = $engine.Handle
$sw = [Diagnostics.Stopwatch]::StartNew()
Write-Output "engine pid $($engine.Id) started"
while (-not (Test-Path $events) -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 200 }
Start-Sleep -Milliseconds 500

$hookACmd = "Set-Content -Encoding ascii '$hookA' 'watch A fired a hook on a heartbeat - THIS IS THE BUG'"
$hookBCmd = "[Console]::In.ReadToEnd() | Set-Content -Encoding ascii '$hookB'; Write-Output 'supervisor woke'"

$a = Spawn "watch -p `"$plan`" --json --timeout $HeartbeatMinutes --poll 1 --hook `"$hookACmd`"" 'watchA'
$b = Spawn "watch -p `"$plan`" --json --poll 1 --hook `"$hookBCmd`"" 'watchB'
Start-Sleep -Seconds 2
$armed = EventCount
Write-Output "watch A pid $($a.Id): heartbeat $HeartbeatMinutes min, hook armed (must never fire)"
Write-Output "watch B pid $($b.Id): no timeout, hook armed (must fire once)"
Write-Output "engine log lines when both armed: $armed  (backlog folded, wakes discarded)"
Write-Output ""

$a.WaitForExit()
$aEvents = EventCount
Write-Output "watch A returned at t+$([int]$sw.Elapsed.TotalSeconds)s : exit $($a.ExitCode)   [0=wake 10=heartbeat 1=could-not-arm]"
Write-Output "  engine transitions while it waited : $($aEvents - $armed) log line(s)"
Write-Output "  watch B still blocked           : $(-not $b.HasExited)"
Write-Output "  watch A hook fired              : $(Test-Path $hookA)"
Write-Output ""

$b.WaitForExit()
$bEvents = EventCount
Write-Output "watch B returned at t+$([int]$sw.Elapsed.TotalSeconds)s : exit $($b.ExitCode)"
Write-Output "  engine transitions it slept through: $($bEvents - $armed) log line(s)"
Write-Output "  watch B hook received stdin     : $(Test-Path $hookB)"
$engine.WaitForExit()
Write-Output "engine exited: $($engine.ExitCode)"
Write-Output ""

Write-Output "--- rig engine log tail (what happened while the watches were silent) ---"
Get-Content (Join-Path $root ".conductor\conductor.log") -Tail 8
Write-Output ""

foreach ($tag in 'watchA', 'watchB') {
    Write-Output "--- $tag stderr (the human channel) ---"
    Get-Content (Join-Path $out "$tag.stderr.txt")
    $so = Join-Path $out "$tag.stdout.json"
    $lines = @(Get-Content $so)
    Write-Output "--- $tag stdout: $($lines.Count) lines, parses as JSON: $(try { $null = (Get-Content $so -Raw | ConvertFrom-Json); 'yes' } catch { 'NO' }) ---"
    $lines
    Write-Output ""
}

Write-Output "--- what the supervisor hook actually received on stdin ---"
if (Test-Path $hookB) { Get-Content $hookB } else { Write-Output "(hook never ran)" }


