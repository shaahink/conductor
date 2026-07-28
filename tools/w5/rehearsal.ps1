<#
.SYNOPSIS
  W5.1 -- the credential-free dress rehearsal. Drives the REAL conductor.exe from a markdown plan
  document to a finished run, exercising every in-flight lever on the way, for zero model spend.

.DESCRIPTION
  W1-W4 each proved a mechanism with a test. This proves the PRODUCT: one process, started once,
  taken from "here is a document describing some work" to a completed run with a RunFinished event
  -- which no conductor run had ever emitted before this checkpoint.

  It is deliberately an out-of-process driver against the shipped binary and the real HTTP control
  plane. W2.1's lesson is that a harness we wrote ourselves is too lenient to be evidence: every
  MCP wire test was green while a live agent could not reach a single tool. So nothing here calls
  into engine classes -- the levers go over HTTP with the run's own write token, the claims come
  from `conductor task --done` inside the worker, and the assertions read run.db back through
  `conductor report --query`.

  Phases:
    1  scaffold a hermetic scratch git repo
    2  `conductor init --from-idea TOY-PLAN.md`   (W4.2 + W4.1: idea -> drivable plan, no hand edits)
    3  stand a fake agent + fake advisor into the plan (the only hand edit, and it is the harness's)
    4  `conductor doctor`                        (work coverage must be ok before anything runs)
    5  `conductor run --headless --paused`       (one process, started once, never restarted)
    6  levers while paused:  card context (3) . per-card QA dials (5) . plan edit (2)
    7  resume, then levers IN FLIGHT: stage-level card add (4) . AI split . confirm children
    8  wait for the run to finish on its own
    9  assert: exit 0 . RunFinished in run.db . every card confirmed . the levers took effect

  Owner acceptance criteria are tagged (1)..(5) at each check so the evidence maps onto them.

  ASCII ONLY (Windows PowerShell 5.1 reads a BOM-less UTF-8 script as ANSI and one stray byte
  tears the next string literal).

.PARAMETER Exe
  Path to conductor.exe. Default: the repo's Debug build. Built if missing unless -NoBuild.

.PARAMETER TimeoutMinutes
  How long to wait for the run to finish by itself (default 15).

.PARAMETER Keep
  Keep the scratch repo and print its path.

.PARAMETER EvidenceOut
  Optional path for a machine-readable JSON summary of the checks.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools/w5/rehearsal.ps1 -Keep
#>
[CmdletBinding()]
param(
    [string]$Exe,
    [int]$TimeoutMinutes = 15,
    [switch]$Keep,
    [switch]$NoBuild,
    [string]$EvidenceOut
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$agentScript = Join-Path $repoRoot "tools\w5\agent.ps1"
$advisorScript = Join-Path $repoRoot "tools\w5\advisor.ps1"
foreach ($p in @($agentScript, $advisorScript)) {
    if (-not (Test-Path $p)) { throw "rehearsal helper not found: $p" }
}

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

# --- 1. engine ------------------------------------------------------------------------------------
if (-not $Exe) { $Exe = Join-Path $repoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe" }
if (-not (Test-Path $Exe)) {
    if ($NoBuild) { throw "engine exe not found at $Exe and -NoBuild was passed" }
    Section "build engine"
    & dotnet build (Join-Path $repoRoot "src\Conductor\Conductor.csproj") -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "engine build failed (exit $LASTEXITCODE)" }
}
$Exe = (Resolve-Path $Exe).Path
Write-Host ("engine: " + $Exe)

$scratch = Join-Path ([IO.Path]::GetTempPath()) ("conductor-w5-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
$runProc = $null
try {
    # --- 2. scratch repo --------------------------------------------------------------------------
    Section "scratch repo"
    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    Write-Host $scratch
    git -C $scratch init -q -b main
    git -C $scratch config user.email "w5@conductor.local"
    git -C $scratch config user.name  "w5 rehearsal"
    Set-Content -Path (Join-Path $scratch "README.md") -Value "# rehearsal scratch repo" -Encoding ascii
    # Engine + harness artifacts are ignored, exactly as a real driven repo would ignore them:
    # an untracked log left in the worktree reads to the verdict engine as "dirty after a green
    # session", which is a true observation about a fake problem.
    Set-Content -Path (Join-Path $scratch ".gitignore") `
        -Value @(".conductor/", ".w5-agent.log", "run.out.log", "run.err.log") -Encoding ascii
    git -C $scratch add -A
    git -C $scratch commit -q -m "chore: scratch scaffold" --no-gpg-sign

    # The idea, as a document. Three stages, five checkpoints, no tracker table anywhere -- the
    # W4.1 parser lifts the work out of this and the tracker is generated from it later.
    $planDoc = @"
# Toy delivery plan

A three-stage toy the rehearsal can actually finish.

## T1 - Foundations - the first slice of behaviour
- **T1.1** a greeting module exists
- **T1.2** the greeting is covered by a test

## T2 - Wiring - connect the slice to the entry point
- **T2.1** the entry point calls the greeting

## T3 - Polish - make it presentable
- **T3.1** the readme documents the greeting
- **T3.2** the changelog names the release
"@
    $docPath = Join-Path $scratch "TOY-PLAN.md"
    Set-Content -Path $docPath -Value $planDoc -Encoding ascii

    # --- 3. init --from-idea ----------------------------------------------------------------------
    Section "conductor init --from-idea TOY-PLAN.md"
    & $Exe init -o $scratch --name "w5-rehearsal" --repo $scratch --from-idea $docPath
    $initCode = $LASTEXITCODE
    $planPath = Join-Path $scratch "conductor.plan.json"
    Check "(1) init --from-idea exited 0" ($initCode -eq 0) ("exit " + $initCode)
    Check "(1) plan scaffolded" (Test-Path $planPath) $planPath

    # --- 4. stand in the fake agent + advisor -----------------------------------------------------
    # The only hand edit in the whole rehearsal, and it is the harness's, not the plan's: point the
    # scaffold at a token-free agent and advisor. No work is authored anywhere.
    Section "stand in the fake agent + advisor"
    $plan = Get-Content $planPath -Raw | ConvertFrom-Json
    $plan.agent = [ordered]@{
        command  = "powershell"
        args     = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Slash $agentScript),
                     "-Repo", (Slash $scratch), "-Exe", (Slash $Exe), "-Prompt", "{prompt}")
        provider = "opencode"
    }
    $plan | Add-Member -NotePropertyName advisor -NotePropertyValue ([ordered]@{
        enabled        = $true
        command        = "powershell"
        args           = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Slash $advisorScript), "-Prompt", "{prompt}")
        output         = "text"
        timeoutMinutes = 2
    }) -Force
    # One always-green offline gate, so the battery genuinely runs on every session.
    $plan | Add-Member -NotePropertyName gates -NotePropertyValue @(
        [ordered]@{ name = "smoke"; command = "git --version"; tier = "fast"; timeoutMinutes = 2 }
    ) -Force
    $plan | Add-Member -NotePropertyName gatePolicy -NotePropertyValue "perSession" -Force
    $plan | Add-Member -NotePropertyName report -NotePropertyValue ([ordered]@{ commit = $false; push = $false }) -Force
    $plan | Add-Member -NotePropertyName limits -NotePropertyValue ([ordered]@{
        maxRunCostUsd = 1.0
        sessionTimeoutMinutes = 5
        stallMinutes = 5
    }) -Force
    ($plan | ConvertTo-Json -Depth 20) | Set-Content -Path $planPath -Encoding ascii
    Write-Host "agent + advisor + one gate wired"

    # --- 5. doctor --------------------------------------------------------------------------------
    Section "conductor doctor"
    $doctor = & $Exe doctor -p $planPath 2>&1
    $doctorText = ($doctor | Out-String)
    $doctor | ForEach-Object { Write-Host $_ }
    Check "(1) doctor sees no uncovered/orphaned work" ($doctorText -notmatch "orphan") $doctorText

    # --- 6. start the run (paused, one process, never restarted) ----------------------------------
    Section "conductor run --headless --paused"
    $runLogOut = Join-Path $scratch "run.out.log"
    $runLogErr = Join-Path $scratch "run.err.log"
    # The .NET API rather than Start-Process: a -PassThru process object does not reliably surface
    # ExitCode (it reads as empty, which is indistinguishable from a crash), and the exit code IS one
    # of the things under test.
    # .Arguments, not .ArgumentList: Windows PowerShell 5.1 runs on .NET Framework, where
    # ProcessStartInfo has no ArgumentList (it is null, and adding to it throws).
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Exe
    $psi.Arguments = ('run -p "{0}" --headless --paused --no-face' -f $planPath)
    $psi.WorkingDirectory = $scratch
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $runProc = [System.Diagnostics.Process]::Start($psi)
    # Drain both pipes on background threads: a full pipe buffer deadlocks the child, and this run
    # logs continuously for minutes.
    $outWriter = [IO.StreamWriter]::new($runLogOut, $false, [Text.Encoding]::UTF8)
    $errWriter = [IO.StreamWriter]::new($runLogErr, $false, [Text.Encoding]::UTF8)
    $outWriter.AutoFlush = $true; $errWriter.AutoFlush = $true
    $pump = {
        param($reader, $writer)
        while ($null -ne ($line = $reader.ReadLine())) { $writer.WriteLine($line) }
    }
    $outJob = [PowerShell]::Create().AddScript($pump).AddArgument($runProc.StandardOutput).AddArgument($outWriter)
    $errJob = [PowerShell]::Create().AddScript($pump).AddArgument($runProc.StandardError).AddArgument($errWriter)
    $outHandle = $outJob.BeginInvoke()
    $errHandle = $errJob.BeginInvoke()
    Write-Host ("run pid " + $runProc.Id)

    $discovery = Join-Path $scratch ".conductor\control-plane.json"
    $deadline = (Get-Date).AddSeconds(90)
    while (-not (Test-Path $discovery) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    Check "control plane published its discovery file" (Test-Path $discovery) $discovery
    if (-not (Test-Path $discovery)) { throw "no control plane -- see $runLogOut / $runLogErr" }
    $cp = Get-Content $discovery -Raw | ConvertFrom-Json
    $base = "http://127.0.0.1:" + $cp.port
    $hdr = @{ "X-Conductor-Token" = $cp.token }
    Write-Host ("control plane: " + $base)

    function Get-Json($path) { Invoke-RestMethod -Uri ($base + $path) -Method Get -TimeoutSec 30 }
    function Post-Json($path, $body) {
        Invoke-RestMethod -Uri ($base + $path) -Method Post -Headers $hdr -ContentType "application/json" `
            -Body ($body | ConvertTo-Json -Depth 8 -Compress) -TimeoutSec 120
    }
    function Wait-For($label, $test, $seconds) {
        $d = (Get-Date).AddSeconds($seconds)
        while ((Get-Date) -lt $d) {
            try { if (& $test) { return $true } } catch { }
            Start-Sleep -Milliseconds 400
        }
        Write-Host ("  (timed out waiting for " + $label + ")") -ForegroundColor Yellow
        return $false
    }

    $seen = Wait-For "the board to populate" { (Get-Json "/tasks").tasks.Count -ge 5 } 60
    $board = Get-Json "/tasks"
    $boardIds = @($board.tasks | ForEach-Object { $_.taskId })
    Check "(1) plan in -> Kanban out: 5 cards from the document, none hand-authored" `
        ($seen -and (@("T1.1","T1.2","T2.1","T3.1","T3.2") | Where-Object { $boardIds -notcontains $_ }).Count -eq 0) `
        ("board: " + ($boardIds -join ", "))

    # --- 7. levers while paused -------------------------------------------------------------------
    Section "levers (paused)"

    # (3) the card detail IS the prompt: attach context, then read the rendered prompt section back.
    $ctx = "Deliver a module named greet, and keep the public surface to one function."
    $edit = Post-Json "/tasks/edit" @{ taskId = "T1.1"; context = $ctx }
    Check "(3) card context accepted" ($edit.ok -eq $true) ($edit.error)
    $blocks = Get-Json "/prompt/blocks?task=T1.1"
    Check "(3) the prompt section rendered for the card carries that context" `
        ($blocks.promptSection -and $blocks.promptSection.Contains($ctx)) ("promptSection: " + $blocks.promptSection)

    # (5) per-card QA dials: one card verified, its sibling delivered with no verify step at all.
    $qaOff = Post-Json "/tasks/edit" @{ taskId = "T1.2"; qa = "off" }
    $qaOn  = Post-Json "/tasks/edit" @{ taskId = "T2.1"; qa = "verify" }
    Check "(5) per-card QA dials accepted (T1.2 off, T2.1 verify)" `
        (($qaOff.ok -eq $true) -and ($qaOn.ok -eq $true)) ("" + $qaOff.error + " " + $qaOn.error)

    # (2) tweak the plan itself; the board and the tracker must follow without a restart.
    $newTitle = "Polish and release"
    $planEdit = Post-Json "/plan/edit" @{ edits = @(@{ target = "stage"; id = "T3"; field = "title"; value = $newTitle }) }
    Check "(2) plan edit accepted" ($planEdit.ok -eq $true) ($planEdit.error)
    # The reload lands at the loop's session boundary, which a paused loop still reaches (it drains
    # control and applies reloads before parking) -- so this must hold WITHOUT resuming.
    $titleLanded = Wait-For "the stage title to reach the live projection" {
        ((Get-Json "/state").stages | Where-Object { $_.id -eq "T3" }).title -eq $newTitle
    } 60
    Check "(2) the plan edit reached the live projection with no restart" $titleLanded $newTitle

    # --- 8. resume, then the in-flight levers -----------------------------------------------------
    Section "resume"
    $null = Post-Json "/control" @{ command = "resume" }
    $firstSession = Wait-For "session 1 to start" { (Get-Json "/state").sessionNumber -ge 1 } 240
    Check "the engine ran its first session" $firstSession ""

    Section "levers (in flight)"
    # (4) "we've realised there is another requirement" -- added at STAGE level, mid-run.
    $added = Post-Json "/tasks/add" @{ stageId = "T2"; title = "the newly realised requirement"; order = 0 }
    Check "(4) stage-level card added mid-run" ($added.ok -eq $true) ($added.error)
    $newCard = $added.taskId
    Write-Host ("new card: " + $newCard)

    # ...and split by the advisor: proposal only, each child confirmed through the ordinary add.
    $split = Post-Json "/tasks/split" @{ taskId = $newCard }
    Check "(4) the advisor proposed children for it" (($split.ok -eq $true) -and ($split.subtasks.Count -ge 2)) ($split.error)
    $childTitles = @()
    foreach ($child in $split.subtasks) {
        $c = Post-Json "/tasks/add" @{ checkpointId = $newCard; title = $child.title; order = 0 }
        if ($c.ok) { $childTitles += $child.title }
    }
    Check "(4) both proposed children confirmed onto the board" ($childTitles.Count -ge 2) ($childTitles -join " | ")

    # (5) the QA dial, flipped while the engine is RUNNING, on a card two stages ahead: T3.1 would
    # otherwise be verified like T1.1 was.
    $qaLate = Post-Json "/tasks/edit" @{ taskId = "T3.1"; qa = "off" }
    Check "(5) a card's QA dial flipped mid-run" ($qaLate.ok -eq $true) ($qaLate.error)

    # (3) a card's context, attached mid-run, must reach the prompt of the session that delivers it.
    $lateCtx = "Mid-run note: mention the greeting's one function by name."
    $ctxLate = Post-Json "/tasks/edit" @{ taskId = "T3.2"; context = $lateCtx }
    Check "(3) a card's context edited mid-run" ($ctxLate.ok -eq $true) ($ctxLate.error)

    $pickedUp = Wait-For "the new card to be claimed" {
        $t = (Get-Json "/tasks").tasks | Where-Object { $_.taskId -eq $newCard }
        $t -and ($t.status -eq "done")
    } 600
    Check "(4) the engine scheduled and delivered the card added in flight, no restart" $pickedUp $newCard

    # --- 9. let it finish -------------------------------------------------------------------------
    Section "waiting for the run to finish on its own"
    $exited = $runProc.WaitForExit($TimeoutMinutes * 60 * 1000)
    if (-not $exited) {
        Write-Host ("run did not finish within " + $TimeoutMinutes + " minutes -- stopping it") -ForegroundColor Yellow
        try { $runProc.Kill($true) } catch { }
    }
    Check "the run finished by itself" $exited ("timeout " + $TimeoutMinutes + "m")
    if ($exited) { Check "the run exited 0" ($runProc.ExitCode -eq 0) ("exit " + $runProc.ExitCode) }
    foreach ($w in @($outWriter, $errWriter)) { try { $w.Flush(); $w.Dispose() } catch { } }

    Section "run log (tail)"
    if (Test-Path $runLogOut) { Get-Content $runLogOut -Tail 40 | ForEach-Object { Write-Host $_ } }

    # --- 10. read the evidence back out of run.db -------------------------------------------------
    # The events table's `type` column holds the CLR event name (RunFinished), not the JSON
    # discriminator (runFinished) -- query the column, not the wire format.
    Section "assertions from run.db"
    $q = { param($sql) (& $Exe report -p $planPath --query $sql 2>&1 | Out-String) }

    $finished = & $q "SELECT payload FROM events WHERE type = 'RunFinished'"
    Write-Host $finished
    Check "RunFinished is in the event log -- the first run ever to emit it" `
        ($finished -match "RunFinished" -or $finished -match "Completed") $finished
    Check "RunFinished records a Completed run with every checkpoint done" `
        (($finished -match "Completed") -and ($finished -notmatch '"checkpointsDone":\s*0')) $finished

    $last = & $q "SELECT type FROM events WHERE type <> 'TokenDelta' ORDER BY seq DESC LIMIT 1"
    Check "RunFinished is the LAST event -- the run ended deliberately, not mid-session" `
        ($last -match "RunFinished") $last

    $confirmed = & $q "SELECT COUNT(*) AS confirmed FROM events WHERE type = 'CheckpointConfirmed'"
    Check "checkpoints were independently CONFIRMED, not just claimed" `
        (($confirmed -notmatch "no rows") -and ($confirmed -notmatch '\|\s*0\s*\|')) $confirmed

    $status = (& $Exe status -p $planPath --no-llm 2>&1 | Out-String)
    Write-Host $status
    Check "status reads the run back as complete" ($status -match "Completed") $status

    # the tracker is a generated view: it must show the edited stage title and every row DONE
    $trackerPath = Join-Path $scratch "TRACKER.md"
    $tracker = if (Test-Path $trackerPath) { Get-Content $trackerPath -Raw } else { "" }
    Check "(2) the generated tracker carries the mid-run plan edit" ($tracker -match [regex]::Escape($newTitle)) ""
    Check "(2) the generated tracker shows no TODO rows left" ($tracker -notmatch '\|\s*TODO\s*\|') ""

    # (5) the QA dial reached the card: T1.2 said `off`, so the session that delivered it got NO
    # verify; T2.1 said `verify`, so the one that delivered T2.1 did. Read off the sessions table by
    # what each Deliver session claimed (newly_done) and whether a Verify followed it.
    $sessions = & $q "SELECT number, kind, stage_id, newly_done FROM sessions ORDER BY number"
    Write-Host $sessions
    Check "(5) a Verify session ran (the dial can turn verification ON)" ($sessions -match "Verify") $sessions
    # Ask SQL what came NEXT after the session that claimed each card, rather than parsing a rendered
    # table: the dial's whole claim is "this card gets verified, that one does not".
    $nextAfter = {
        param($card)
        & $q ("SELECT COALESCE((SELECT s2.kind FROM sessions s2 WHERE s2.number = s1.number + 1), 'NONE') AS next_kind " +
              "FROM sessions s1 WHERE s1.newly_done = '" + $card + "'")
    }
    $afterOff = & $nextAfter "T1.2"
    $afterOn = & $nextAfter "T2.1"
    Check "(5) the card that said qa=off was delivered with NO verify after it" `
        (($afterOff -notmatch "no rows") -and ($afterOff -notmatch "Verify")) ("next after T1.2: " + $afterOff)
    Check "(5) the card that said qa=verify got a Verify session right after it" `
        ($afterOn -match "Verify") ("next after T2.1: " + $afterOn)
    $afterLate = & $nextAfter "T3.1"
    Check "(5) the dial flipped MID-RUN reached the card two stages later" `
        (($afterLate -notmatch "no rows") -and ($afterLate -notmatch "Verify")) ("next after T3.1: " + $afterLate)

    # (3) the mid-run context edit is in the prompt of the session that delivered that card -- read
    # off the prompt file on disk, which is the bytes the agent actually received.
    $t32Session = (& $q "SELECT number FROM sessions WHERE newly_done = 'T3.2'") -replace '[^0-9]', ''
    $promptFile = Get-ChildItem (Join-Path $scratch ".conductor\sessions") -Recurse -Filter "prompt.md" -ErrorAction SilentlyContinue |
        Where-Object { (Get-Content $_.FullName -Raw) -match [regex]::Escape($lateCtx) } | Select-Object -First 1
    Check "(3) the mid-run context edit reached the prompt the agent received" `
        ($null -ne $promptFile) ("session claiming T3.2: " + $t32Session)

    $agentLog = Join-Path $scratch ".w5-agent.log"
    if (Test-Path $agentLog) {
        Section "agent log (the claim path, from inside the worker)"
        Get-Content $agentLog | ForEach-Object { Write-Host $_ }
        $agentText = (Get-Content $agentLog -Raw)
        Check "CONDUCTOR_PLAN reached the child env (claims needed no -p)" `
            ($agentText -match "CONDUCTOR_PLAN=\S") ""
        Check "every in-worker claim succeeded" ($agentText -notmatch "-> exit [1-9]") ""
    }
}
catch {
    # A throw before any Check would otherwise print PASS: nothing had failed yet, because nothing
    # had run. An aborted rehearsal is a failed rehearsal.
    Check "the rehearsal ran to the end without throwing" $false ($_ | Out-String)
    Write-Host ($_ | Out-String) -ForegroundColor Red
}
finally {
    if ($runProc -and -not $runProc.HasExited) { try { $runProc.Kill($true) } catch { } }
    Section "verdict"
    if ($script:ok) { Write-Host "W5.1 REHEARSAL: PASS" -ForegroundColor Green }
    else { Write-Host "W5.1 REHEARSAL: FAIL" -ForegroundColor Red }
    if ($EvidenceOut) {
        [ordered]@{
            pass    = $script:ok
            scratch = $scratch
            checks  = $script:checks
        } | ConvertTo-Json -Depth 6 | Set-Content -Path $EvidenceOut -Encoding ascii
        Write-Host ("evidence: " + $EvidenceOut)
    }
    if ($Keep) { Write-Host ("scratch kept: " + $scratch) -ForegroundColor Yellow }
    else { Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue }
}

if (-not $script:ok) { exit 1 }
