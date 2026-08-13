# KS0.3 - reproduction: lessons.md must never say the same thing twice.
#
# Red  : the PRE-K1.3 writer, taken verbatim out of git (6acea2c^) and compiled on its own, so the
#        red half cannot be accused of being a straw man. Its TrimToCap re-parsed content that already
#        contained the new entry and then emitted that entry again, so every append that crossed the
#        byte cap duplicated itself. That is how K7-32 came to be on file twice, and LessonsBattery
#        pastes the newest rules into every following prompt - a duplicate is rent, charged per session.
# Green: today's LessonsManager over the same 12 appends, via the pinned tests in
#        tests/Conductor.Tests/KS0_3LessonsAppendTests.cs.
#
# Temp only; nothing here touches C:/code/conductor state. PowerShell 5.1 compatible, ASCII only.

[CmdletBinding()]
param(
    [string]$Repo = "C:\code\conductor",
    [string]$Root = (Join-Path $env:TEMP ("ks0-3-lessons-" + [guid]::NewGuid().ToString("N").Substring(0, 8)))
)

$ErrorActionPreference = "Stop"
$PreK13 = "6acea2c^:src/Conductor/Core/LessonsManager.cs"

New-Item -ItemType Directory -Force -Path $Root | Out-Null
Write-Host "scratch: $Root"

# ---- red: the old writer, straight out of git -------------------------------------------------
$proj = Join-Path $Root "old"
New-Item -ItemType Directory -Force -Path $proj | Out-Null

Push-Location $Repo
$old = & git show $PreK13
Pop-Location
if (-not $old) { throw "could not read $PreK13 out of git" }
[IO.File]::WriteAllLines((Join-Path $proj "LessonsManager.cs"), $old)

[IO.File]::WriteAllText((Join-Path $proj "old.csproj"), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>Old</RootNamespace>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <AnalysisMode>None</AnalysisMode>
    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <RunAnalyzersDuringBuild>false</RunAnalyzersDuringBuild>
  </PropertyGroup>
</Project>
"@)

# The same 12 appends the pinned test drives, against the same 1024-byte cap.
[IO.File]::WriteAllText((Join-Path $proj "Program.cs"), @"
using System.Text;
using Conductor.Core;

var dir = args[0];
var lessons = new LessonsManager(dir, maxBytes: 1024);
for (var i = 1; i <= 12; i++)
    lessons.Append("KS0", i,
        `$"SESSION-RESULT: session {i} landed something.\n- Never let rule number {i} out of your " +
        `$"sight, because the ratchet does not forgive a missing measurement and the next session " +
        `$"pays for it twice over in wasted context.\n");

var text = File.ReadAllText(Path.Combine(dir, "lessons.md"), Encoding.UTF8);
var dupes = 0;
for (var i = 1; i <= 12; i++)
{
    var needle = `$"rule number {i} out of your";
    var hits = 0; var at = 0;
    while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { hits++; at += needle.Length; }
    if (hits > 1) { dupes++; Console.WriteLine(`$"  rule {i} appears {hits} times"); }
}
Console.WriteLine(`$"DUPLICATED_RULES={dupes}");
return dupes > 0 ? 1 : 0;
"@)

$data = Join-Path $Root "old-data"
New-Item -ItemType Directory -Force -Path $data | Out-Null

Write-Host ""
Write-Host "RED   pre-K1.3 writer ($PreK13), 12 appends over a 1024-byte cap:"
$redOut = & dotnet run --project $proj -- $data 2>&1
$redOut | Where-Object { $_ -match "rule \d+ appears|DUPLICATED|error" } | ForEach-Object { Write-Host "      $_" }
# Parsed, not inferred from the exit code: a BUILD failure is also non-zero and must not read as red.
$redLine = $redOut | Where-Object { $_ -match "^DUPLICATED_RULES=(\d+)$" } | Select-Object -First 1
$redDupes = if ($redLine -match "^DUPLICATED_RULES=(\d+)$") { [int]$Matches[1] } else { -1 }

# ---- green: today's writer, through the pinned tests -------------------------------------------
Write-Host ""
Write-Host "GREEN today's LessonsManager, same 12 appends, through the pinned tests:"
Push-Location $Repo
& dotnet test Conductor.slnx --no-build --filter "FullyQualifiedName~KS0_3LessonsAppendTests" 2>&1 |
    Where-Object { $_ -match "Passed!|Failed!|error" } | ForEach-Object { Write-Host "      $_" }
$greenOk = ($LASTEXITCODE -eq 0)
Pop-Location

Write-Host ""
if ($redDupes -gt 0 -and $greenOk) { Write-Host "PASS - the old writer duplicates, today's does not"; exit 0 }
if ($redDupes -lt 0) { Write-Host "UNEXPECTED: the pre-K1.3 writer did not report at all (build failure?)" }
if ($redDupes -eq 0) { Write-Host "UNEXPECTED: the pre-K1.3 writer did not duplicate" }
if (-not $greenOk) { Write-Host "FAIL: the pinned tests are red" }
exit 1
