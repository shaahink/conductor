# PK1.1 live proof - the courier is its own executable, and `conductor courier run` is an alias.
#
# What it proves, in order:
#   1. `dotnet build Conductor.slnx` produces BOTH binaries, conductor-courier.exe beside conductor.exe
#   2. the boundary rule names a violation when one is seeded into the REAL courier csproj
#      (a ProjectReference to Conductor.Planning), then goes green again once it is removed
#   3. the fresh build's `conductor courier run` starts conductor-courier.exe as a CHILD process
#      (Win32_Process: name, parent pid, command line), which writes its own presence record
#   4. terminating the alias (what the scheduler's End does to a pre-D1 task) ends the courier too
#   5. negative control for 4: with the job assignment removed from the alias, the same test FAILS
#
# Scratch only: its own state home and courier port under TEMP, a fake token, a Bot API base URL
# nothing listens on. It never touches the real courier, its home, its task or its token, never
# publishes, and restores every source file it perturbs (and rebuilds) before it exits.
# ASCII only (Windows PowerShell 5.1).

param(
    [string]$OutDir   = (Join-Path $env:TEMP "pk11-rig"),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
)

$ErrorActionPreference = "Stop"
$env:CONDUCTOR_PLAN = $null
Set-Location $RepoRoot

function Section($title) { ""; "==== $title ====" }
# Native stderr under "Stop" is a terminating error in Windows PowerShell 5.1, so the two native
# calls run under "Continue" and are judged by their exit codes instead.
function Build() {
    $ErrorActionPreference = "Continue"
    $out = & dotnet build Conductor.slnx -clp:ErrorsOnly 2>&1
    if ($LASTEXITCODE -ne 0) { $out; throw "build failed" }
}
function TestOne($filter) {
    $ErrorActionPreference = "Continue"
    $out = & dotnet test tests/Conductor.Tests/Conductor.Tests.csproj --no-build --filter $filter 2>&1
    $script:lastTestExit = $LASTEXITCODE
    $out | ForEach-Object { "$_" } | Select-String -Pattern "Passed |Failed |Error Message|->|outlived|Total tests|Passed:|Failed:" | ForEach-Object { "  " + $_.Line.Trim() }
}
$results = New-Object System.Collections.ArrayList
function Check($name, $ok, $detail) {
    $verdict = "FAIL"
    if ($ok) { $verdict = "PASS" }
    $line = "{0}  {1}  {2}" -f $verdict, $name, $detail
    [void]$results.Add($line)
    $line
}
function Utf8NoBom() { New-Object Text.UTF8Encoding($false) }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null
"PK1.1 proof at $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')) on commit $(git rev-parse --short HEAD)"

# ---- 1. both binaries ----------------------------------------------------------------------
Section "1. dotnet build produces both binaries"
Build
$bin = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0"
$engine  = Join-Path $bin "conductor.exe"
$courier = Join-Path $bin "conductor-courier.exe"
Get-Item $engine, $courier, (Join-Path $RepoRoot "src\Conductor.Courier\bin\Debug\net10.0\conductor-courier.exe") |
    ForEach-Object { "  {0}  {1} bytes  {2}" -f $_.FullName, $_.Length, $_.LastWriteTimeUtc.ToString("u") }
Check "engine apphost built" (Test-Path $engine) $engine
Check "courier apphost built beside the engine" (Test-Path $courier) $courier
$refs = @(Select-String -Path "src\Conductor.Courier\Conductor.Courier.csproj" -Pattern '<(Project|Package)Reference\s+Include="([^"]+)"' |
    ForEach-Object { $_.Matches[0].Groups[1].Value + " " + $_.Matches[0].Groups[2].Value })
"  courier csproj references: " + ($refs -join "; ")
Check "courier csproj references Conductor.Core only" (($refs.Count -eq 1) -and ($refs[0] -like "Project*Conductor.Core.csproj")) ($refs -join "; ")

# ---- 2. seeded boundary violation ------------------------------------------------------------
Section "2. the boundary rule names a seeded violation"
$csproj = Join-Path $RepoRoot "src\Conductor.Courier\Conductor.Courier.csproj"
$original = [IO.File]::ReadAllText($csproj)
try {
    $anchor = '<ProjectReference Include="..\Conductor.Core\Conductor.Core.csproj" />'
    $seeded = $original.Replace($anchor, $anchor + "`r`n    " + '<ProjectReference Include="..\Conductor.Planning\Conductor.Planning.csproj" />')
    if ($seeded -eq $original) { throw "seed anchor not found" }
    [IO.File]::WriteAllText($csproj, $seeded, (Utf8NoBom))
    "  seeded: ProjectReference ..\Conductor.Planning\Conductor.Planning.csproj"
    Build
    $seedOut = TestOne "FullyQualifiedName~ArchitectureBoundaryTests.TheCourierReferencesCoreAndNothingElse"
    $seedOut
    Check "seeded violation turns the rule red" ($lastTestExit -ne 0) ("exit=" + $lastTestExit)
    Check "the failure names the seeded reference" (($seedOut -join "`n") -match "Conductor.Planning.csproj") "Conductor.Planning.csproj"
}
finally {
    [IO.File]::WriteAllText($csproj, $original, (Utf8NoBom))
}
Build
TestOne "FullyQualifiedName~ArchitectureBoundaryTests.TheCourier|FullyQualifiedName~ArchitectureBoundaryTests.TheEngineNeverLinksTheCourier"
Check "rule green again with the seed removed" ($lastTestExit -eq 0) ("exit=" + $lastTestExit)

# ---- 3 + 4. the alias as real processes ----------------------------------------------------
Section "3. conductor courier run starts conductor-courier.exe as a child"
$stateHome = Join-Path $OutDir "state-home"
New-Item -ItemType Directory -Path (Join-Path $stateHome "courier") | Out-Null
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start()
$deadApi = $probe.LocalEndpoint.Port; $probe.Stop()
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0); $probe.Start()
$courierPort = $probe.LocalEndpoint.Port; $probe.Stop()
$settings = @{
    projects = @(@{ plan = "pk11-scratch"; repo = $OutDir })
    chats = @(@{ chatId = "770000001"; profile = "admin" })
    pollIntervalSeconds = 2
    apiBaseUrl = "http://127.0.0.1:$deadApi"
} | ConvertTo-Json -Depth 4
Set-Content -Path (Join-Path $stateHome "courier\courier.json") -Value $settings -Encoding ASCII
"  scratch state home $stateHome, courier port $courierPort, Bot API http://127.0.0.1:$deadApi (nothing listens)"

$psi = New-Object System.Diagnostics.ProcessStartInfo $engine
$psi.Arguments = 'courier run --task-name "pk11-scratch"'
$psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.EnvironmentVariables["CONDUCTOR_STATE_HOME"] = $stateHome
$psi.EnvironmentVariables["CONDUCTOR_COURIER_PORT"] = "$courierPort"
$psi.EnvironmentVariables["CONDUCTOR_TELEGRAM_TOKEN"] = "111111:pk11-scratch-token"
if ($psi.EnvironmentVariables.ContainsKey("CONDUCTOR_PLAN")) { $psi.EnvironmentVariables.Remove("CONDUCTOR_PLAN") }
$alias = [System.Diagnostics.Process]::Start($psi)
$presencePath = Join-Path $stateHome "courier\courier.run.json"
$presence = $null
for ($i = 0; ($i -lt 60) -and (-not $presence); $i++) {
    Start-Sleep -Milliseconds 500
    if (Test-Path $presencePath) { $presence = Get-Content $presencePath -Raw | ConvertFrom-Json }
}
"  alias pid $($alias.Id)"
if ($presence) { "  presence record: " + ($presence | ConvertTo-Json -Compress) } else { "  presence record: (none)" }
$child = $null
if ($presence) { $child = Get-CimInstance Win32_Process -Filter "ProcessId = $($presence.pid)" }
if ($child) { "  courier process: pid {0}, name {1}, parent {2}, command line: {3}" -f $child.ProcessId, $child.Name, $child.ParentProcessId, $child.CommandLine }
Check "presence written by a separate process" ($presence -and ($presence.pid -ne $alias.Id)) ("presence pid " + $presence.pid)
Check "that process is conductor-courier.exe" ($child -and ($child.Name -eq "conductor-courier.exe")) $child.Name
Check "its parent is the alias" ($child -and ($child.ParentProcessId -eq $alias.Id)) ("parent " + $child.ParentProcessId)
Check "the courier bound the scratch port" ($presence -and ($presence.port -eq $courierPort)) ("port " + $presence.port)

Section "4. terminating the alias ends the courier"
$courierPid = $presence.pid
# TerminateProcess on the ONE pid this rig started - what the scheduler does to a task's process.
$alias.Kill()
$gone = $false
for ($i = 0; ($i -lt 30) -and (-not $gone); $i++) {
    Start-Sleep -Milliseconds 500
    $gone = -not (Get-Process -Id $courierPid -ErrorAction SilentlyContinue)
}
Check "courier exited with the alias" $gone ("courier pid $courierPid gone=$gone")
if (-not $gone) {
    $still = Get-CimInstance Win32_Process -Filter "ProcessId = $courierPid"
    if ($still -and ($still.ExecutablePath -like "$RepoRoot*")) { Stop-Process -Id $courierPid -Force }
}
"  courier.log (scratch):"
Get-Content (Join-Path $stateHome "courier\courier.log") | ForEach-Object { "    " + $_ }

# ---- 5. negative control for the job -------------------------------------------------------
Section "5. negative control: without the job assignment the kill-together test fails"
$runCs = Join-Path $RepoRoot "src\Conductor\Commands\CourierCommand.Run.cs"
$runOriginal = [IO.File]::ReadAllText($runCs)
try {
    $perturbed = $runOriginal.Replace("            job.Assign(courier);", "            // job.Assign(courier); // PK1.1 negative control")
    if ($perturbed -eq $runOriginal) { throw "job anchor not found" }
    [IO.File]::WriteAllText($runCs, $perturbed, (Utf8NoBom))
    Build
    TestOne "FullyQualifiedName~PK1_1CourierExecutableTests.KillingTheAliasTakesTheCourierWithIt"
    Check "without the job the courier outlives the alias (test red)" ($lastTestExit -ne 0) ("exit=" + $lastTestExit)
}
finally {
    [IO.File]::WriteAllText($runCs, $runOriginal, (Utf8NoBom))
    # the orphan the negative control leaves: only a conductor-courier.exe from THIS repo's test output
    Get-CimInstance Win32_Process -Filter "Name = 'conductor-courier.exe'" |
        Where-Object { $_.ExecutablePath -like (Join-Path $RepoRoot "tests\*") } |
        ForEach-Object { "  stopping the negative control's orphan: pid $($_.ProcessId) $($_.ExecutablePath)"; Stop-Process -Id $_.ProcessId -Force }
}
Build
TestOne "FullyQualifiedName~PK1_1CourierExecutableTests"
Check "restored: every PK1.1 process test green" ($lastTestExit -eq 0) ("exit=" + $lastTestExit)

Section "summary"
$results
"git status of src and tests (must show no perturbation):"
git status --short -- src tests
$failed = @($results | Where-Object { $_ -like "FAIL*" }).Count
"checks: $($results.Count), failed: $failed"
exit $failed
