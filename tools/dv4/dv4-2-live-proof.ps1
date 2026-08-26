# DV4.2 live proof - the courier's lifecycle against the REAL Windows Task Scheduler.
#
# What the in-process test cannot show and this can: schtasks.exe actually ACCEPTING the
# task definition. A test can read the XML this build produces; only the scheduler can say
# whether it validates, whether a standard user may register it, and whether the settings
# survive the round trip. So this registers a SCRATCH-NAMED task, reads back what the
# scheduler stored, and removes it again.
#
# What it proves, in order:
#   1. `courier status` before anything: task not installed, nothing running, protocol stated
#   2. `courier install --task-name <scratch> --no-start` registers it - from an UNELEVATED
#      shell, which is the "no admin rights" claim of findings 6.4
#   3. the scheduler's OWN copy of the definition carries LogonTrigger, RestartOnFailure
#      (PT1M), LeastPrivilege, InteractiveToken, IgnoreNew and ExecutionTimeLimit PT0S, and
#      runs the fresh build's conductor.exe with `courier run`
#   4. `courier status` now sees the task by name
#   5. the version handshake: a courier whose presence record says an OLDER protocol is
#      refused BY NAME, with `conductor courier restart --task-name "<scratch>"` in the text
#   6. tools/lib/courier-guard.ps1 - what install.ps1 dot-sources - sees a live courier and
#      refuses to let the publish proceed while it holds the exe open; with nothing running
#      it is a no-op
#   7. `courier uninstall` removes the registration; the scheduler no longer knows the name
#   8. the OWNER's task ("Conductor Courier") is untouched from start to finish
#
# Scratch only: its own state home, its own scratch-named task, its own out dir. It never
# touches this repo's .conductor, never starts a run, never runs tools/install.ps1 (trap 1),
# and it starts the registered task ONLY when no bot token is present in this environment -
# a courier started with the owner's real token would steal getUpdates from the real bot
# (trap 4).
# ASCII only (Windows PowerShell 5.1).

param(
    [string]$OutDir   = (Join-Path $env:TEMP "dv42-rig"),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$TaskName = "Conductor Courier SCRATCH dv4-2"
)

$ErrorActionPreference = "Stop"
$env:CONDUCTOR_PLAN = $null            # trap 3: never inherit another rig's plan

$exe = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
if (-not (Test-Path $exe)) { throw "build first: dotnet build Conductor.slnx  (missing $exe)" }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$stateHome = Join-Path $OutDir "state-home"
New-Item -ItemType Directory -Path $stateHome | Out-Null
$env:CONDUCTOR_STATE_HOME = $stateHome

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

# schtasks writes its refusals to stderr, and with $ErrorActionPreference = "Stop" a native
# command's stderr is a TERMINATING error in Windows PowerShell - which would kill this proof
# on the very query whose failure it is asserting ("the scheduler no longer knows the name").
function Sch {
    $prior = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { return (& schtasks.exe @args 2>&1) } finally { $ErrorActionPreference = $prior }
}

Write-Host "DV4.2 live proof - courier lifecycle" -ForegroundColor Cyan
Write-Host ("  exe:        {0}" -f $exe)
Write-Host ("  state home: {0}" -f $stateHome)
Write-Host ("  task:       {0}" -f $TaskName)

# Elevation and token posture, recorded rather than assumed --------------------------------
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
$tokenHere = [bool]($env:CONDUCTOR_TELEGRAM_TOKEN)
$tokenUser = [bool]([Environment]::GetEnvironmentVariable("CONDUCTOR_TELEGRAM_TOKEN", "User"))
$tokenMach = [bool]([Environment]::GetEnvironmentVariable("CONDUCTOR_TELEGRAM_TOKEN", "Machine"))
$tokenAnywhere = $tokenHere -or $tokenUser -or $tokenMach

# A task started here runs with the USER's environment, not this shell's - so it resolves the
# machine's REAL state home. A courier there with a token AND chats AND projects would begin
# polling for real and steal getUpdates from the owner's bot (trap 4). One with nothing
# configured refuses by name and exits before it dials anything, which is safe to demonstrate.
$realHome = [Environment]::GetEnvironmentVariable("CONDUCTOR_STATE_HOME", "User")
if (-not $realHome) { $realHome = Join-Path $env:LOCALAPPDATA "conductor" }
$realSettings = Join-Path $realHome "courier\courier.json"
$realConfigured = $false
if (Test-Path $realSettings) {
    try {
        $rc = Get-Content -Raw $realSettings | ConvertFrom-Json
        $realConfigured = (($rc.chats | Measure-Object).Count -gt 0) -and (($rc.projects | Measure-Object).Count -gt 0)
    } catch { $realConfigured = $true }   # unreadable: assume the worst and do not start it
}
$safeToStart = -not ($tokenAnywhere -and $realConfigured)
Write-Host ("  elevated:   {0}" -f $elevated)
Write-Host ("  bot token present (process/user/machine): {0}/{1}/{2}" -f $tokenHere, $tokenUser, $tokenMach)
Write-Host ("  machine courier configured ({0}): {1}" -f $realSettings, $realConfigured)
Write-Host ("  safe to start the scratch task: {0}" -f $safeToStart)

# The owner's real task, before we touch anything -------------------------------------------
Sch /Query /TN "Conductor Courier" /FO CSV /NH | Out-Null
$ownerTaskBefore = ($LASTEXITCODE -eq 0)
Write-Host ("  owner task registered before: {0}" -f $ownerTaskBefore)

# 0. leave nothing of an earlier run behind -------------------------------------------------
Sch /Delete /TN "$TaskName" /F | Out-Null

# 1. status before -------------------------------------------------------------------------
Write-Host "`n[1] status before install" -ForegroundColor Cyan
$before = (Courier status --task-name $TaskName --json) -join "`n"
$beforeJson = $before | ConvertFrom-Json
Check "task reports not installed" (-not $beforeJson.task.registered) $before
Check "nothing is running" ($null -eq $beforeJson.running) $before
Check "this build states its protocol" ($beforeJson.protocol -ge 1) $before
Check "no stale-courier refusal when there is no courier" ($null -eq $beforeJson.stale) $before

# 2. install (registered, not started) ------------------------------------------------------
Write-Host "`n[2] courier install --no-start" -ForegroundColor Cyan
$install = (Courier install --task-name $TaskName --no-start) -join "`n"
Write-Host $install
Check "install reported success" ($LASTEXITCODE -eq 0 -and $install -match "installed") $install
Check "registered WITHOUT elevation" (-not $elevated) "this shell was elevated; the no-admin claim was not tested"

# 3. what the SCHEDULER stored --------------------------------------------------------------
Write-Host "`n[3] the scheduler's own copy of the definition" -ForegroundColor Cyan
$storedXml = (Sch /Query /TN "$TaskName" /XML) -join "`n"
Set-Content -Path (Join-Path $OutDir "registered-task.xml") -Value $storedXml -Encoding ASCII
Write-Host $storedXml
Check "logon trigger"            ($storedXml -match "<LogonTrigger>")                  $storedXml
Check "restart on failure PT1M"  ($storedXml -match "<RestartOnFailure>" -and $storedXml -match "<Interval>PT1M</Interval>") $storedXml
# The scheduler NORMALISES what it stores: it drops <RunLevel> when it is the default
# (LeastPrivilege) and rewrites <UserId> to the account's SID. So the no-admin claim is read
# from what is ABSENT - a task that wanted elevation would say HighestAvailable here - plus the
# fact that an unelevated shell registered it at all.
Check "no elevation requested" (-not ($storedXml -match "HighestAvailable")) $storedXml
Check "principal is this user"  ($storedXml -match [regex]::Escape($identity.User.Value)) $storedXml
Check "InteractiveToken logon"   ($storedXml -match "<LogonType>InteractiveToken</LogonType>") $storedXml
Check "IgnoreNew - one poller"   ($storedXml -match "<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>") $storedXml
Check "no execution time limit"  ($storedXml -match "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>") $storedXml
Check "runs THIS build"          ($storedXml -match [regex]::Escape($exe))             $storedXml
Check "action is the courier verb" ($storedXml -match "<Arguments>courier run</Arguments>") $storedXml

# 4. status after ---------------------------------------------------------------------------
Write-Host "`n[4] status after install" -ForegroundColor Cyan
$after = (Courier status --task-name $TaskName --json) -join "`n"
$afterJson = $after | ConvertFrom-Json
Check "task reports registered" ($afterJson.task.registered) $after
Check "task named in status"    ($afterJson.task.name -eq $TaskName) $after
$plain = (Courier status --task-name $TaskName) -join "`n"
Write-Host $plain

# 4b. starting it for real - only when no token can be stolen -------------------------------
if (-not $safeToStart) {
    Write-Host "`n[4b] SKIPPED starting the task: this machine has a bot token AND a configured" -ForegroundColor Yellow
    Write-Host "     courier, so starting one would steal getUpdates from the real bot (trap 4)." -ForegroundColor Yellow
} else {
    Write-Host "`n[4b] courier restart - the scheduler really runs it" -ForegroundColor Cyan
    $restart = (Courier restart --task-name $TaskName) -join "`n"
    Write-Host $restart
    Check "restart reported success" ($LASTEXITCODE -eq 0 -and $restart -match "restarted") $restart
    Start-Sleep -Seconds 5
    $row = (Sch /Query /TN "$TaskName" /FO CSV /NH) -join "`n"
    Write-Host ("  scheduler row: {0}" -f $row)
    # With no token the daemon refuses to start and exits 1 - it never dials Telegram. That is
    # the correct outcome here: what is being proven is that the TASK runs the engine at all.
}

# 5. the version handshake, live ------------------------------------------------------------
Write-Host "`n[5] a stale courier is refused BY NAME" -ForegroundColor Cyan
$courierDir = Join-Path $stateHome "courier"
New-Item -ItemType Directory -Path $courierDir -Force | Out-Null
$self = Get-Process -Id $PID
$presence = [ordered]@{
    protocol   = 0                       # older than anything this build speaks
    pid        = $PID                    # a process that IS alive, so Live() says yes
    engine     = "0.4.0"
    exe        = $exe
    taskName   = $TaskName
    startedUtc = $self.StartTime.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ")
}
Set-Content -Path (Join-Path $courierDir "courier.run.json") `
    -Value ($presence | ConvertTo-Json) -Encoding ASCII

$stalePlain = (Courier status --task-name $TaskName) -join "`n"
Write-Host $stalePlain
$staleJson = ((Courier status --task-name $TaskName --json) -join "`n" | ConvertFrom-Json)
Check "status sees the courier as running" ($null -ne $staleJson.running) $stalePlain
Check "refused as stale"        ($null -ne $staleJson.stale)                       $stalePlain
Check "refused BY NAME"         ($staleJson.stale -match [regex]::Escape($TaskName)) $staleJson.stale
Check "names the restart verb"  ($staleJson.stale -match "conductor courier restart") $staleJson.stale
Check "names THIS task's flag"  ($staleJson.stale -match "--task-name")               $staleJson.stale
Check "names the stale engine"  ($staleJson.stale -match "0\.4\.0")                   $staleJson.stale
Check "the refusal reaches the terminal, not only --json" ($stalePlain -match "stale courier") $stalePlain

# 6. the installer's guard -------------------------------------------------------------------
Write-Host "`n[6] tools/lib/courier-guard.ps1 - what install.ps1 dot-sources" -ForegroundColor Cyan
. (Join-Path $RepoRoot "tools\lib\courier-guard.ps1")
$seen = Stop-ConductorCourier -TaskName $TaskName -StateHome $stateHome -TimeoutSeconds 2
Write-Host ("  guard saw: registered={0} wasRunning={1} pid={2} stopped={3}" -f `
    $seen.Registered, $seen.WasRunning, $seen.Pid, $seen.Stopped)
Check "guard sees the registered task"   ($seen.Registered)            "$($seen | ConvertTo-Json -Compress)"
Check "guard sees a live courier"        ($seen.WasRunning)            "$($seen | ConvertTo-Json -Compress)"
Check "guard reports it did NOT stop"    (-not $seen.Stopped)          "a courier it cannot stop must not report success - install.ps1 throws on this"

Remove-Item (Join-Path $courierDir "courier.run.json") -Force
$quiet = Stop-ConductorCourier -TaskName $TaskName -StateHome $stateHome -TimeoutSeconds 2
Check "no-op when nothing is running"    (-not $quiet.WasRunning)      "$($quiet | ConvertTo-Json -Compress)"
Check "guard start is a no-op for an unknown task" (-not (Start-ConductorCourier -TaskName "Conductor Courier NO SUCH TASK dv4-2")) "started something that does not exist"

# The shape that actually broke: install.ps1 runs with ErrorActionPreference = "Stop", under
# which schtasks writing to STDERR for an unknown task name is a TERMINATING error. Every
# machine that has never installed a courier hits exactly that path.
$guardPath = Join-Path $RepoRoot "tools\lib\courier-guard.ps1"
$strict = & powershell -NoProfile -ExecutionPolicy Bypass -Command `
    "`$ErrorActionPreference='Stop'; . '$guardPath'; `$r = Stop-ConductorCourier -TaskName 'Conductor Courier NO SUCH TASK dv4-2' -StateHome '$stateHome' -TimeoutSeconds 1; if (`$r.Registered -or `$r.WasRunning) { exit 3 }; exit 0" 2>&1
$strictExit = $LASTEXITCODE
Check "guard survives ErrorActionPreference=Stop on a machine with no courier" ($strictExit -eq 0) ("exit {0}: {1}" -f $strictExit, ($strict -join " "))

# 7. uninstall ---------------------------------------------------------------------------------
Write-Host "`n[7] courier uninstall" -ForegroundColor Cyan
$uninstall = (Courier uninstall --task-name $TaskName) -join "`n"
Write-Host $uninstall
Sch /Query /TN "$TaskName" /FO CSV /NH | Out-Null
Check "the scheduler no longer knows the name" ($LASTEXITCODE -ne 0) "task still registered"
$gone = (Courier status --task-name $TaskName --json) -join "`n" | ConvertFrom-Json
Check "status agrees it is gone" (-not $gone.task.registered) $gone
$again = (Courier uninstall --task-name $TaskName) -join "`n"
Check "uninstalling twice is not an error" ($LASTEXITCODE -eq 0) $again

# 8. the owner's courier was never touched -------------------------------------------------
Sch /Query /TN "Conductor Courier" /FO CSV /NH | Out-Null
$ownerTaskAfter = ($LASTEXITCODE -eq 0)
Check "owner's task unchanged" ($ownerTaskAfter -eq $ownerTaskBefore) `
    ("before={0} after={1}" -f $ownerTaskBefore, $ownerTaskAfter)

Write-Host ""
if ($fails.Count -eq 0) {
    Write-Host "DV4.2 LIVE PROOF: PASS" -ForegroundColor Green
    exit 0
} else {
    Write-Host ("DV4.2 LIVE PROOF: FAIL ({0}) - {1}" -f $fails.Count, ($fails -join "; ")) -ForegroundColor Red
    exit 1
}
