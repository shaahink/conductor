<#
.SYNOPSIS
  W3.3 -- the window-close rail, proven by really closing a window.

.DESCRIPTION
  W3.3 shipped a CTRL_CLOSE/LOGOFF/SHUTDOWN rail and could only gate it one level down from the
  brief: `GenerateConsoleCtrlEvent` emits CTRL_C and CTRL_BREAK and nothing else, so no in-process
  test can make Windows deliver a close. The handler body was gated directly and the "clean park +
  resumable run.db" half was gated on the same cancellation path, but the join between them -- the
  OS actually delivering the event to a live run -- was left as "worth one manual X".

  This driver is that X, automated, from outside the process. It clicks nothing and simulates
  nothing at the .NET level: it posts WM_CLOSE to the real console window of a real `conductor run`,
  which is byte for byte what the window manager does when the owner clicks the X, and lets Windows
  decide whether to deliver CTRL_CLOSE_EVENT.

  One wrinkle makes it possible at all. On Windows 11 the default terminal is ConPTY-hosted, and
  `GetConsoleWindow()` there returns a hidden `PseudoConsoleWindow` stub -- posting to it does
  nothing, which is why this looked unautomatable. Launched through `conhost.exe`, the run gets a
  genuine `ConsoleWindowClass` window, and WM_CLOSE on that follows the classic path. The rail is
  the same either way: both hosts end in CTRL_CLOSE_EVENT to the attached process.

  Phases:
    1  scaffold a hermetic scratch repo, `init --from-idea`, stand in a deliberately SLOW agent
    2  start `conductor run` under conhost.exe (its own real console window)
    3  wait until a session is genuinely IN FLIGHT (the agent writes a marker from inside the worker)
    4  post WM_CLOSE -- the X -- and time how long the process takes to die
    5  assert from disk: the rail fired, the session was recorded Interrupted, the lock was
       released, the agent child was not orphaned, run.db is intact
    6  resume the run for real and let it finish the work the X interrupted
    7  negative control: hard-kill an identical run and show the same evidence is ABSENT

  Windows only, and interactive: a console window appears on the desktop for a few seconds and is
  closed programmatically. Not a CI test -- CI runners have no window station to close.

.PARAMETER Exe
  Path to conductor.exe. Default: the repo's Debug build. Built if missing unless -NoBuild.

.PARAMETER Keep
  Keep the scratch repos and print their paths.

.PARAMETER EvidenceOut
  Optional path for a machine-readable JSON summary of the checks.

.PARAMETER SkipControl
  Skip phase 7 (the hard-kill negative control).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/w3/window-close.ps1 -Keep
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [switch]$Keep,
    [switch]$NoBuild,
    [switch]$SkipControl,
    [string]$EvidenceOut
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

if (-not $IsWindows -and $PSVersionTable.PSVersion.Major -ge 6) { throw "window-close.ps1 is Windows-only." }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$agentScript = Join-Path $repoRoot "tools\w3\slow-agent.ps1"
if (-not (Test-Path $agentScript)) { throw "helper not found: $agentScript" }

$script:ok = $true
$script:checks = @()
function Section($t) { Write-Host ""; Write-Host ("=== " + $t + " ===") -ForegroundColor Cyan }
function Check($label, $cond, $detail = "") {
    $pass = [bool]$cond
    $script:checks += [ordered]@{ label = $label; pass = $pass; detail = "$detail" }
    if ($pass) { Write-Host ("  PASS  " + $label) -ForegroundColor Green }
    else {
        Write-Host ("  FAIL  " + $label) -ForegroundColor Red
        if ($detail) { Write-Host ("        " + $detail) -ForegroundColor DarkGray }
        $script:ok = $false
    }
}
function Slash($p) { $p -replace '\\', '/' }

# --- the X, as Win32 sees it ----------------------------------------------------------------------
# AttachConsole borrows the target's console just long enough to ask for its window handle, then
# gives it back. PostMessage(WM_CLOSE) on that handle is exactly the message the window manager
# sends when the X is clicked; the console host turns it into CTRL_CLOSE_EVENT for every attached
# process. Nothing here touches the conductor process itself.
Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class W3Console {
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool AttachConsole(uint pid);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool FreeConsole();
  [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
  [DllImport("user32.dll", SetLastError=true)] static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  const uint ATTACH_PARENT = 0xFFFFFFFF;
  const uint WM_CLOSE = 0x0010;

  static IntPtr Borrow(uint pid) {
    FreeConsole();
    IntPtr h = IntPtr.Zero;
    if (AttachConsole(pid)) h = GetConsoleWindow();
    FreeConsole();
    AttachConsole(ATTACH_PARENT);   // best effort: give our own console back
    return h;
  }
  /// <summary>Window class of the target's console: ConsoleWindowClass is a real window whose X
  /// can be clicked; PseudoConsoleWindow is the ConPTY stub, which no message can close.</summary>
  public static string ClassOf(uint pid) {
    IntPtr h = Borrow(pid);
    if (h == IntPtr.Zero) return "";
    var cls = new StringBuilder(256); GetClassName(h, cls, 256);
    return cls.ToString();
  }
  /// <summary>Click the X.</summary>
  public static bool Close(uint pid) {
    IntPtr h = Borrow(pid);
    if (h == IntPtr.Zero) return false;
    return PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
  }
}
"@

# --- engine ---------------------------------------------------------------------------------------
if (-not $Exe) { $Exe = Join-Path $repoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe" }
if (-not (Test-Path $Exe)) {
    if ($NoBuild) { throw "engine exe not found at $Exe and -NoBuild was passed" }
    Section "build engine"
    & dotnet build (Join-Path $repoRoot "src\Conductor\Conductor.csproj") -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "engine build failed (exit $LASTEXITCODE)" }
}
$Exe = (Resolve-Path $Exe).Path
Write-Host ("engine: " + $Exe)

# --- scaffolding ----------------------------------------------------------------------------------
function New-Scratch($tag) {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ("conductor-w33-" + $tag + "-" + [Guid]::NewGuid().ToString("N").Substring(0, 6))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    git -C $dir init -q -b main
    git -C $dir config user.email "w33@conductor.local"
    git -C $dir config user.name  "w33 window close"
    Set-Content -Path (Join-Path $dir "README.md") -Value "# window-close scratch repo" -Encoding ascii
    Set-Content -Path (Join-Path $dir ".gitignore") `
        -Value @(".conductor/", ".w3-agent.log", ".w3-agent-started", ".w3-release", "resume.out.log", "resume.err.log") -Encoding ascii
    git -C $dir add -A
    git -C $dir commit -q -m "chore: scratch scaffold" --no-gpg-sign

    # Two stages, because `MarkdownPlanParser.LooksStructured` deliberately wants >= 2 stage headers
    # before it will treat a document as a plan rather than prose for the advisor to interpret.
    $doc = @"
# Window close toy plan

Small enough to finish fast, long enough that a session can be interrupted with work still open.

## X1 - The slice - something for a session to be in the middle of
- **X1.1** the first slice exists
- **X1.2** the second slice exists

## X2 - The finish - work left over for the resumed run
- **X2.1** the last slice exists
"@
    $docPath = Join-Path $dir "TOY-PLAN.md"
    Set-Content -Path $docPath -Value $doc -Encoding ascii

    & $Exe init -o $dir --name ("w33-" + $tag) --repo $dir --from-idea $docPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "conductor init failed (exit $LASTEXITCODE) in $dir" }
    $planPath = Join-Path $dir "conductor.plan.json"

    $plan = Get-Content $planPath -Raw | ConvertFrom-Json
    $plan.agent = [ordered]@{
        command  = "powershell"
        args     = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Slash $agentScript),
                     "-Repo", (Slash $dir), "-Exe", (Slash $Exe), "-Prompt", "{prompt}")
        provider = "opencode"
    }
    $plan | Add-Member -NotePropertyName gates -NotePropertyValue @(
        [ordered]@{ name = "smoke"; command = "git --version"; tier = "fast"; timeoutMinutes = 2 }
    ) -Force
    $plan | Add-Member -NotePropertyName gatePolicy -NotePropertyValue "perSession" -Force
    $plan | Add-Member -NotePropertyName report -NotePropertyValue ([ordered]@{ commit = $false; push = $false }) -Force
    $plan | Add-Member -NotePropertyName limits -NotePropertyValue ([ordered]@{
        maxRunCostUsd = 1.0
        sessionTimeoutMinutes = 10
        stallMinutes = 10
    }) -Force
    ($plan | ConvertTo-Json -Depth 20) | Set-Content -Path $planPath -Encoding ascii
    return [pscustomobject]@{ Dir = $dir; Plan = $planPath }
}

# Start a run in its OWN real console window and hand back the conductor process.
function Start-RunInItsOwnWindow($scratch, $planPath) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "conhost.exe"
    $psi.Arguments = ('"{0}" run -p "{1}" --headless --no-face' -f $Exe, $planPath)
    $psi.WorkingDirectory = $scratch
    $psi.UseShellExecute = $true       # the whole point: a new console, not this one
    $conhost = [System.Diagnostics.Process]::Start($psi)

    $deadline = (Get-Date).AddSeconds(30)
    $child = $null
    while ((Get-Date) -lt $deadline) {
        $child = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($conhost.Id)" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq "conductor.exe" } | Select-Object -First 1
        if ($child) { break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $child) { throw "conductor.exe never appeared under conhost pid $($conhost.Id)" }
    return [pscustomobject]@{ Conhost = $conhost; Pid = [int]$child.ProcessId; Proc = (Get-Process -Id $child.ProcessId) }
}

function Wait-ForFile($path, $seconds, $what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while (-not (Test-Path $path) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    return (Test-Path $path)
}

$scratches = @()
try {
    # ============================================================ 1-2. a live run in a real window
    Section "scaffold + start a run in its own console window"
    $main = New-Scratch "close"
    $scratches += $main.Dir
    Write-Host $main.Dir
    $run = Start-RunInItsOwnWindow $main.Dir $main.Plan
    Write-Host ("conductor pid " + $run.Pid + " under conhost pid " + $run.Conhost.Id)

    # ============================================================ 3. wait until work is in flight
    Section "wait for a session to be genuinely in flight"
    $marker = Join-Path $main.Dir ".w3-agent-started"
    $inFlight = Wait-ForFile $marker 120 "agent marker"
    Check "a session is in flight (the agent announced itself from inside the worker)" $inFlight $marker
    if (-not $inFlight) { throw "no session started -- nothing to lose, so nothing to prove" }
    $lockPath = Join-Path $main.Dir ".conductor\conductor.lock"
    Check "the run holds its lock while working" (Test-Path $lockPath) $lockPath
    # Give the session a moment past its first write so the interrupt lands mid-work, not mid-startup.
    Start-Sleep -Seconds 2

    # ============================================================ 4. click the X
    Section "close the window"
    $cls = [W3Console]::ClassOf([uint32]$run.Pid)
    Write-Host ("console window class: '" + $cls + "'")
    Check "the run owns a REAL console window (one with an X to click)" `
        ($cls -eq "ConsoleWindowClass") ("class was '" + $cls + "' -- PseudoConsoleWindow means ConPTY, which no message can close")

    $sw = [Diagnostics.Stopwatch]::StartNew()
    $posted = [W3Console]::Close([uint32]$run.Pid)
    Check "WM_CLOSE was posted to that window (the X, byte for byte)" $posted ""
    $exited = $run.Proc.WaitForExit(20000)
    $sw.Stop()
    Check "the run process ended after the close" $exited ("waited " + $sw.Elapsed.TotalSeconds.ToString("0.0") + "s")
    Write-Host ("process ended " + $sw.Elapsed.TotalSeconds.ToString("0.00") + "s after WM_CLOSE")
    # Windows kills the process once the handler returns; the rail's whole job is to finish saving
    # INSIDE that window. Longer than the grace means the save was cut off, faster than ~0.2s means
    # nothing waited at all.
    Check "the close was graceful, not instant -- something ran before the process died" `
        ($sw.Elapsed.TotalSeconds -gt 0.2) ($sw.Elapsed.TotalSeconds.ToString("0.00") + "s")

    Start-Sleep -Seconds 1
    $orphan = Get-Process -Id $run.Pid -ErrorAction SilentlyContinue
    Check "the conductor process is gone" ($null -eq $orphan) ""
    $agentPid = 0
    if (Test-Path $marker) { $agentPid = [int](((Get-Content $marker -Raw) -replace '[^0-9]', '')) }
    $agentAlive = $false
    if ($agentPid -gt 0) { $agentAlive = $null -ne (Get-Process -Id $agentPid -ErrorAction SilentlyContinue) }
    Check "the agent child was not left orphaned behind it" (-not $agentAlive) ("agent pid " + $agentPid)

    # ============================================================ 5. the evidence, from disk
    Section "what the close left behind"
    # The rail's own messages go to AnsiConsole -- i.e. to the console that is in the act of closing,
    # which is unreadable by construction. So the evidence is what the graceful path WROTE, and the
    # negative control below is what makes that evidence falsifiable.
    Check 'the lock was released -- the next `conductor run` is not locked out' `
        (-not (Test-Path $lockPath)) $lockPath

    # SF1.2: `report --query` is gone. Ad-hoc SQL against run.db survives as the MCP `run_query` tool,
    # driven out-of-process against the shipped binary exactly as the old read was.
    . (Join-Path $PSScriptRoot "..\lib\run-query.ps1")
    $q = { param($sql, $dir) (Invoke-ConductorQuery -Exe $Exe -StateDir (Join-Path $dir ".conductor") -Sql $sql | Out-String) }
    $sessions = & $q "SELECT number, kind, outcome, ended_utc FROM sessions ORDER BY number" $main.Dir
    Write-Host $sessions
    Check "the interrupted session was RECORDED, with an outcome and an end time" `
        (($sessions -match "Interrupted") -and ($sessions -notmatch "no rows")) $sessions

    $events = & $q "SELECT type FROM events ORDER BY seq DESC LIMIT 6" $main.Dir
    Write-Host $events
    Check "run.db survived the close and still reads back" ($events -notmatch "no rows") $events
    Check "the session was closed off in the event log, not left hanging" `
        ($events -match "SessionFinished") $events

    # ============================================================ 6. resume for real
    # "Resumable" is a claim about the future, so make the future happen: release the agent and start
    # the SAME plan again. If the close really parked cleanly, this run finishes the interrupted work.
    Section "resume the run the X interrupted"
    Set-Content -Path (Join-Path $main.Dir ".w3-release") -Value "go" -Encoding ascii
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = ('run -p "{0}" --headless --no-face' -f $main.Plan)
    $psi.WorkingDirectory = $main.Dir
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $resume = [System.Diagnostics.Process]::Start($psi)
    $outFile = Join-Path $main.Dir "resume.out.log"
    $writer = [IO.StreamWriter]::new($outFile, $false, [Text.Encoding]::UTF8); $writer.AutoFlush = $true
    $pump = { param($reader, $writer) while ($null -ne ($line = $reader.ReadLine())) { $writer.WriteLine($line) } }
    $j1 = [PowerShell]::Create().AddScript($pump).AddArgument($resume.StandardOutput).AddArgument($writer)
    $j2 = [PowerShell]::Create().AddScript($pump).AddArgument($resume.StandardError).AddArgument($writer)
    $h1 = $j1.BeginInvoke(); $h2 = $j2.BeginInvoke()
    $finished = $resume.WaitForExit(10 * 60 * 1000)
    if (-not $finished) { try { $resume.Kill($true) } catch { } }
    foreach ($j in @($j1, $j2)) { try { $j.Dispose() } catch { } }
    try { $writer.Flush(); $writer.Dispose() } catch { }
    $resumeText = if (Test-Path $outFile) { Get-Content $outFile -Raw } else { "" }
    if ($resumeText) { ($resumeText -split "`n" | Select-Object -Last 25) | ForEach-Object { Write-Host $_ } }

    Check "the interrupted run resumed rather than starting over" `
        ($resumeText -match "resuming run") $resumeText
    Check "the resumed run exited cleanly" ($finished -and $resume.ExitCode -eq 0) `
        ("exit " + $(if ($finished) { $resume.ExitCode } else { "TIMEOUT" }))
    $after = & $q "SELECT COUNT(*) AS done FROM events WHERE type = 'CheckpointConfirmed'" $main.Plan
    Check "the work the X interrupted was delivered and confirmed after the resume" `
        (($after -notmatch "no rows") -and ($after -notmatch '\|\s*0\s*\|')) $after

    # ============================================================ 7. negative control
    # Without this the evidence above is unfalsifiable: maybe every dying run leaves that trail. It
    # does not. Same scaffold, same moment, killed instead of closed.
    if (-not $SkipControl) {
        Section "negative control -- the same run, hard-killed instead of closed"
        $ctl = New-Scratch "kill"
        $scratches += $ctl.Dir
        $krun = Start-RunInItsOwnWindow $ctl.Dir $ctl.Plan
        $kMarker = Join-Path $ctl.Dir ".w3-agent-started"
        $kInFlight = Wait-ForFile $kMarker 120 "agent marker"
        Check "control: a session is in flight before the kill" $kInFlight $kMarker
        Start-Sleep -Seconds 2
        Stop-Process -Id $krun.Pid -Force
        [void]$krun.Proc.WaitForExit(20000)
        Start-Sleep -Seconds 1
        try { Stop-Process -Id $krun.Conhost.Id -Force -ErrorAction SilentlyContinue } catch { }

        $kLock = Join-Path $ctl.Dir ".conductor\conductor.lock"
        Check "control: a hard kill leaves the lock behind (releasing it is the rail's work)" `
            (Test-Path $kLock) $kLock
        $kSessions = & $q "SELECT number, kind, outcome, ended_utc FROM sessions ORDER BY number" $ctl.Plan
        Write-Host $kSessions
        Check "control: a hard kill records NO Interrupted session (that record is the rail's work)" `
            ($kSessions -notmatch "Interrupted") $kSessions
        $kAgentPid = 0
        if (Test-Path $kMarker) { $kAgentPid = [int](((Get-Content $kMarker -Raw) -replace '[^0-9]', '')) }
        if ($kAgentPid -gt 0) {
            $stray = Get-Process -Id $kAgentPid -ErrorAction SilentlyContinue
            if ($stray) { Write-Host ("control: killing the agent the hard kill orphaned (pid " + $kAgentPid + ")") -ForegroundColor DarkYellow
                          try { Stop-Process -Id $kAgentPid -Force } catch { } }
        }
    }
}
catch {
    Check "the window-close proof ran to the end without throwing" $false ($_ | Out-String)
}
finally {
    Section "result"
    $pass = ($script:checks | Where-Object { $_.pass }).Count
    $total = $script:checks.Count
    Write-Host ("{0}/{1} checks passed" -f $pass, $total) -ForegroundColor $(if ($script:ok) { "Green" } else { "Red" })
    if ($EvidenceOut) {
        ([ordered]@{ ok = $script:ok; passed = $pass; total = $total; checks = $script:checks } |
            ConvertTo-Json -Depth 6) | Set-Content -Path $EvidenceOut -Encoding ascii
        Write-Host ("evidence: " + $EvidenceOut)
    }
    foreach ($d in $scratches) {
        if ($Keep) { Write-Host ("kept: " + $d) }
        else { try { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue } catch { } }
    }
}
if (-not $script:ok) { exit 1 }
exit 0
