# KS0.3 / bug #16 - reproduction: a gate must never fail trying to rebuild the running engine.
#
# The failure this closes is not primarily a broken build. It is the MESSAGE the broken build
# prints: "The file is locked by: conductor (12345)". An agent reads that, concludes a stale
# orphan is holding a lock, hunts for it and kills it - and the process it kills is the run
# supervising it. That has already cost this repo a session. SF0.3 fixed the agent's half by
# publishing CONDUCTOR_PID; this is the engine's half, and it moves the build instead of
# explaining the crash.
#
# The rig is the real shape, not a mock: an engine image sitting INSIDE the tree its own gate
# builds, and a running binary that the gate's build has to overwrite.
#
#   red   rig: the PUBLISHED engine, copied into the rig tree, runs `conductor gate`. It issues
#              the gate command byte for byte, MSBuild cannot overwrite the running image, the
#              gate FAILS and the operator is told a file is locked.
#   green rig: the FRESH BUILD, copied into the identical rig tree, runs the identical gate.
#              GateRunner asks ShadowBuild, the command is redirected to an artifacts path
#              outside the tree, the build never touches the running image, and the gate PASSES.
#
# Both engines run from inside the tree, so exactly one variable differs between the rigs: the
# code under test.
#
# The binary left running is `lockdemo`, a five-line console app, NOT a conductor image - the
# lock is real, the gate failure is real, and no conductor process is ever put at risk. Each rig
# gets its own CONDUCTOR_STATE_HOME and its own plan, passed with -p, with CONDUCTOR_PLAN
# cleared (trap 4), so the operator's catalogue and the driving run are untouched. Temp only.
#
# Windows PowerShell 5.1 compatible, ASCII only.

[CmdletBinding()]
param(
    [string]$Root = (Join-Path $env:TEMP ("ks0-3-bug16-" + [guid]::NewGuid().ToString("N").Substring(0, 8))),
    [string]$FreshDir = "C:\code\conductor\src\Conductor\bin\Debug\net10.0",
    [string]$PublishedDir = "C:\Users\shahi\AppData\Local\Programs\conductor"
)

$ErrorActionPreference = "Stop"

# The engine's own log is open while we read it.
function Read-Shared([string]$path) {
    if (-not (Test-Path $path)) { return "" }
    try {
        $fs = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        $sr = New-Object IO.StreamReader($fs)
        $text = $sr.ReadToEnd()
        $sr.Close(); $fs.Close()
        return $text
    }
    catch { return "" }
}

function New-Rig([string]$label, [string]$engineSource) {
    $rig = Join-Path $Root $label
    $demo = Join-Path $rig "lockdemo"
    $stateDir = Join-Path $rig ".conductor"
    $home_ = Join-Path $rig "state-home"
    New-Item -ItemType Directory -Force -Path $rig, $demo, $stateDir, $home_ | Out-Null

    # An empty Directory.Build.props stops MSBuild walking up out of the rig. Without it a temp
    # project inherits whatever analyzer settings live above %TEMP% and the build dies on a style
    # rule instead of on the lock this script is measuring (CA1305 killed the bug-27 rig's first run).
    [IO.File]::WriteAllText((Join-Path $rig "Directory.Build.props"), "<Project></Project>")
    [IO.File]::WriteAllText((Join-Path $rig "Directory.Build.targets"), "<Project></Project>")

    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <AnalysisMode>None</AnalysisMode>
    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
  </PropertyGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $demo "lockdemo.csproj"), $csproj)
    [IO.File]::WriteAllText((Join-Path $demo "Program.cs"),
        "// lockdemo - holds its own image open while a gate tries to rebuild it.`r`nSystem.Threading.Thread.Sleep(180000);`r`n")

    # The engine image goes INSIDE the tree. That is the whole precondition of bug #16: without it
    # there is nothing for a gate to overwrite and nothing for ShadowBuild to react to.
    $engine = Join-Path $rig "engine"
    Copy-Item -Path $engineSource -Destination $engine -Recurse -Force

    $csprojPath = Join-Path $demo "lockdemo.csproj"
    $plan = @{
        name     = "ks0-3-$label"
        repo     = $rig
        tracker  = "TRACKER.md"
        stateDir = $stateDir
        agent    = @{ command = "cmd"; args = @("/c", "echo", "{prompt}") }
        stages   = @(@{ id = "S1"; title = "the only stage"; sessions = 1 })
        gates    = @(@{
                name           = "build"
                command        = "dotnet build `"$csprojPath`""
                tier           = "fast"
                timeoutMinutes = 5
            })
    } | ConvertTo-Json -Depth 8
    $planPath = Join-Path $rig "rig.plan.json"
    [IO.File]::WriteAllText($planPath, $plan)
    [IO.File]::WriteAllText((Join-Path $rig "TRACKER.md"),
        "# tracker`r`n`r`n| ID | Title | Status |`r`n|---|---|---|`r`n| S1.1 | a row | TODO |`r`n")

    return [pscustomobject]@{
        Label = $label; Rig = $rig; Demo = $demo; Csproj = $csprojPath
        Plan = $planPath; Home = $home_; StateDir = $stateDir
        Engine = (Join-Path $engine "conductor.exe")
    }
}

function Invoke-Rig($rig) {
    # 1. Build lockdemo once, so there is an image to lock.
    $warm = & dotnet build $rig.Csproj -v q --nologo 2>&1
    if ($LASTEXITCODE -ne 0) { throw "warm build of lockdemo failed:`r`n$($warm -join "`r`n")" }
    $exe = Join-Path $rig.Demo "bin\Debug\net10.0\lockdemo.exe"
    $dll = Join-Path $rig.Demo "bin\Debug\net10.0\lockdemo.dll"
    if (-not (Test-Path $exe)) { throw "lockdemo.exe not produced at $exe" }
    $stampBefore = (Get-Item $dll).LastWriteTimeUtc

    # 2. Touch the source, so the gate's build MUST recompile and copy over the running image.
    #    Without this MSBuild finds the output up to date, skips the copy, and the lock never fires.
    Add-Content -Path (Join-Path $rig.Demo "Program.cs") -Value "// touched $(Get-Date -Format o)"

    # 3. Hold the image open. Started here, so its pid is ours to stop (never by name - trap 3).
    $held = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 2

    $out = Join-Path $rig.Rig "gate-stdout.txt"
    $code = $null
    try {
        $env:CONDUCTOR_PLAN = $null          # trap 4: never let an inherited plan decide
        $env:CONDUCTOR_STATE_HOME = $rig.Home
        & $rig.Engine gate -p $rig.Plan *> $out
        $code = $LASTEXITCODE
    }
    finally {
        Remove-Item Env:\CONDUCTOR_STATE_HOME -ErrorAction SilentlyContinue
        if ($held -and -not $held.HasExited) { Stop-Process -Id $held.Id -Force -ErrorAction SilentlyContinue }
    }

    $stdout = Read-Shared $out
    $log = Read-Shared (Join-Path $rig.StateDir "conductor.log")
    $both = $stdout + "`r`n" + $log
    $stampAfter = if (Test-Path $dll) { (Get-Item $dll).LastWriteTimeUtc } else { $null }

    # Take the shadow path from what the ENGINE printed, not from a glob of the temp directory: a
    # leftover directory from an earlier run of this script has the same name shape and would read
    # as a pass. The engine names the path it chose; that exact path is what has to exist, and it
    # has to sit outside the tree it just built.
    $shadow = $null
    $m = [regex]::Match($both, '--artifacts-path\s+"([^"]+)"')
    if (-not $m.Success) { $m = [regex]::Match($both, 'building to (.+?) instead') }
    if ($m.Success) { $shadow = $m.Groups[1].Value.Trim() }
    $shadowOk = $shadow -and (Test-Path $shadow) -and
                (-not $shadow.ToLowerInvariant().StartsWith($rig.Rig.ToLowerInvariant()))

    return [pscustomobject]@{
        Label      = $rig.Label
        Engine     = $rig.Engine
        ExitCode   = $code
        Locked     = ($both -match "locked by|being used by another process|MSB3027|MSB3021")
        Redirected = ($both -match "--artifacts-path" -or $both -match "building to .+ instead")
        Shadow     = $shadow
        ShadowOk   = [bool]$shadowOk
        ImageTouched = ($stampAfter -ne $null -and $stampAfter -ne $stampBefore)
        Tail       = (($both -split "`r?`n" | Where-Object { $_ -match "locked by|artifacts-path|gate build|FAIL|PASS|error MSB" } | Select-Object -First 6) -join "`r`n    ")
    }
}

Write-Host "rig root: $Root"
Write-Host ""

if (-not (Test-Path (Join-Path $FreshDir "conductor.exe"))) {
    throw "no fresh build at $FreshDir - run: dotnet build Conductor.slnx"
}
if (-not (Test-Path (Join-Path $PublishedDir "conductor.exe"))) {
    throw "no published engine at $PublishedDir"
}

$results = @()
$results += Invoke-Rig (New-Rig "red-published" $PublishedDir)
$results += Invoke-Rig (New-Rig "green-fresh"   $FreshDir)

foreach ($r in $results) {
    Write-Host ("  {0}" -f $r.Label)
    Write-Host ("    engine        : {0}" -f $r.Engine)
    Write-Host ("    gate exit     : {0}" -f $r.ExitCode)
    Write-Host ("    lock reported : {0}" -f $r.Locked)
    Write-Host ("    redirected    : {0}" -f $r.Redirected)
    Write-Host ("    shadow root   : {0}" -f $(if ($r.Shadow) { "$($r.Shadow) (exists, outside the tree: $($r.ShadowOk))" } else { "(none)" }))
    Write-Host ("    running image overwritten : {0}" -f $r.ImageTouched)
    if ($r.Tail) { Write-Host ("    {0}" -f $r.Tail) }
    Write-Host ""
}

$red = $results[0]
$green = $results[1]

$ok = ($red.ExitCode -ne 0) -and $red.Locked -and (-not $red.Redirected) `
    -and ($green.ExitCode -eq 0) -and $green.Redirected -and $green.ShadowOk `
    -and (-not $green.ImageTouched)

if ($ok) {
    Write-Host "PASS - the published engine's gate dies on the lock it created; this build moves the"
    Write-Host "       build outside the tree, passes, and never writes the running image."
    exit 0
}

Write-Host "FAIL - expected red=(exit!=0, locked, not redirected) green=(exit 0, redirected, shadow root, image untouched)"
exit 1
