# KS3.5 live proof: `conductor demo --from <spec-kit tasks.md>` drives a converted board to
# completion. Re-runnable. Three isolation rules this repo has already paid for:
#   * CONDUCTOR_PLAN beats the CWD (trap 4/bug 20), and in this session it points at the DRIVING
#     run's plan - the demo's fake-agent children call `conductor task --done`, so a leaked value
#     would claim rows on the live karvansara run. Cleared here, for this process tree only.
#   * CONDUCTOR_TELEGRAM_TOKEN would page the owner from a throwaway run. Cleared.
#   * The engine under test is the FRESH BUILD (`dotnet run --project src/Conductor`), never the
#     `conductor` on PATH - that one is the published engine driving this session.
# The demo pins its own state inside the throwaway directory, so nothing reaches the machine
# catalogue; -o puts that directory under the system temp dir, never inside this repo.
param(
    [string]$Repo = 'C:\code\conductor',
    [string]$Out  = (Join-Path ([System.IO.Path]::GetTempPath()) ('ks35-demo-' + [guid]::NewGuid().ToString('N').Substring(0, 8)))
)

$ErrorActionPreference = 'Continue'
Remove-Item Env:\CONDUCTOR_PLAN -ErrorAction SilentlyContinue
Remove-Item Env:\CONDUCTOR_TELEGRAM_TOKEN -ErrorAction SilentlyContinue
Set-Location $Repo

Write-Output "OUT=$Out"
Write-Output "CONDUCTOR_PLAN=[$($env:CONDUCTOR_PLAN)]"

dotnet run --project src/Conductor -- demo `
    --from tests/Conductor.Tests/fixtures/speckit/tasks.md `
    --keep -o $Out
$code = $LASTEXITCODE

Write-Output "EXIT=$code"
Write-Output '--- TRACKER.md ---'
Get-Content (Join-Path $Out 'TRACKER.md') -ErrorAction SilentlyContinue
Write-Output '--- catalogue check (must be absent from the machine home) ---'
$cat = Join-Path $env:LOCALAPPDATA 'conductor\catalogue.json'
if (Test-Path $cat) {
    $hit = (Get-Content $cat -Raw) -match [regex]::Escape($Out)
    Write-Output "CATALOGUE_MENTIONS_DEMO=$hit"
} else {
    Write-Output 'CATALOGUE_MENTIONS_DEMO=no-catalogue'
}
exit $code
