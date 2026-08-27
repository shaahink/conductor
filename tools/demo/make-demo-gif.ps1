<#
.SYNOPSIS
  Renders docs/assets/demo.gif - the dashboard tour under the README's H1.

.DESCRIPTION
  Records the LIVE Face binary running `--demo` (fully offline synthetic data: no engine, no
  credentials, no spend) through VHS, so the GIF is a real terminal session in real colour with
  real typing and real transitions - not a slideshow of stills.

  VHS needs ttyd, which has no Windows build. Rather than make that a blocker, this runs the
  official VHS container (ghcr.io/charmbracelet/vhs), which bundles vhs + ttyd + ffmpeg, and
  cross-compiles the Face for linux to run inside it. The only local prerequisites are Docker and
  Go. The tape itself - docs/assets/demo.tape - is what actually describes the tour; edit that to
  change what the GIF shows.

  Geometry note: the tape's 1176x736 gives the shell exactly 110x34, matching the terminal size
  face-go/internal/tui/golden_test.go renders every golden at. That is deliberate - it is the one
  size the Face's layout is actually test-covered at. See the comment block in the tape.

  History: this script used to assemble the GIF from face-go's committed golden frames via ffmpeg
  drawtext. That produced provably-real frames but two defects nothing caught: the goldens are
  ANSI-stripped so the result was monochrome, and Cascadia's ~24px line advance meant a 680px
  canvas fit only 28 of each frame's 34 rows, silently clipping the bottom six. Recording the live
  binary fixes both, and cannot drift from the real Face at all.

.PARAMETER Tape
  The VHS tape to run. Default docs/assets/demo.tape.

.PARAMETER Image
  VHS container image. Pinned by tag; pass a digest for full reproducibility.

.PARAMETER SkipBuild
  Reuse an existing face-go/bin/conductor-face (linux ELF) instead of cross-compiling.

.EXAMPLE
  powershell -File tools/demo/make-demo-gif.ps1
#>
[CmdletBinding()]
param(
    [string]$Tape  = "docs/assets/demo.tape",
    [string]$Image = "ghcr.io/charmbracelet/vhs:latest",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$tapePath = Join-Path $repoRoot $Tape
$linuxBin = Join-Path $repoRoot "face-go\bin\conductor-face"

function Step($t) { Write-Host ""; Write-Host ("=== " + $t + " ===") -ForegroundColor Cyan }
function Die($t)  { Write-Host ""; Write-Host ("STOP: " + $t) -ForegroundColor Red; exit 1 }

if (-not (Test-Path $tapePath)) { Die "tape not found: $tapePath" }

# --- prerequisites -------------------------------------------------------------------------------
Step "prerequisites"
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Die "docker is not on PATH." }
docker version --format '{{.Server.Version}}' 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "docker daemon is not running - starting Docker Desktop..." -ForegroundColor Yellow
    docker desktop start
    if ($LASTEXITCODE -ne 0) { Die "could not start the docker daemon. Start Docker Desktop and re-run." }
}
Write-Host ("docker  " + (docker version --format '{{.Server.Version}}')) -ForegroundColor Green

if (-not $SkipBuild) {
    if (-not (Get-Command go -ErrorAction SilentlyContinue)) { Die "go is not on PATH (needed to cross-compile the Face; pass -SkipBuild if you already have a linux binary)." }
    Write-Host ("go      " + (go version)) -ForegroundColor Green
}

# --- cross-compile the Face for linux -------------------------------------------------------------
# The tape runs `./bin/conductor-face --demo` inside the container. bin/ is gitignored, and the
# extensionless name does not collide with the Windows conductor-face.exe sitting beside it.
if (-not $SkipBuild) {
    Step "cross-compile Face for linux"
    Push-Location (Join-Path $repoRoot "face-go")
    try {
        $env:GOOS = "linux"; $env:GOARCH = "amd64"
        & go build -o bin/conductor-face ./cmd/conductor-face/
        $code = $LASTEXITCODE
    } finally {
        Remove-Item Env:\GOOS, Env:\GOARCH -ErrorAction SilentlyContinue
        Pop-Location
    }
    if ($code -ne 0) { Die "go build failed" }
    $mb = [Math]::Round((Get-Item $linuxBin).Length / 1MB, 1)
    Write-Host ("built face-go/bin/conductor-face  ({0} MB, linux/amd64)" -f $mb) -ForegroundColor Green
} elseif (-not (Test-Path $linuxBin)) {
    Die "-SkipBuild given but face-go/bin/conductor-face does not exist."
}

# --- record ---------------------------------------------------------------------------------------
Step "vhs"
Write-Host "recording the live Face - this takes about half a minute..."
docker run --rm -v "${repoRoot}:/vhs" -w /vhs $Image $Tape
if ($LASTEXITCODE -ne 0) { Die "vhs failed" }

# --- report -----------------------------------------------------------------------------------------
$outLine = (Get-Content $tapePath | Select-String -Pattern '^\s*Output\s+(.+)$').Matches[0].Groups[1].Value.Trim('"', ' ')
$outPath = Join-Path $repoRoot $outLine
if (-not (Test-Path $outPath)) { Die "vhs reported success but $outLine is missing" }

$kb = [Math]::Round((Get-Item $outPath).Length / 1KB, 1)
Write-Host ""
Write-Host ("wrote {0}  ({1} KB)" -f $outLine, $kb) -ForegroundColor Green
Write-Host "GitHub caps inline README images at 10 MB; keep an eye on that if you lengthen the tape." -ForegroundColor DarkGray

# --- the manifest (CH2.2) ---------------------------------------------------------------------------
# docs/assets/demo.manifest.json records what this GIF was recorded FROM, and
# face-go/internal/tui/demo_tour_test.go fails the build when the Face has moved past it. It is
# refreshed HERE, as the recording's last step, and nowhere else: a manifest that can be refreshed
# without re-recording is a check that can be silenced by editing the thing it checks.
#
# The default tape is the only one whose recording the manifest describes. A -Tape run (the CH2.1
# verification tape, say) leaves it alone rather than telling it the README's GIF is something it
# is not.
if ($Tape -ne "docs/assets/demo.tape") {
    Write-Host ("manifest not refreshed: this was a -Tape run ({0}), not the README recording." -f $Tape) -ForegroundColor DarkGray
    return
}
Step "manifest"
if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    Die "go is not on PATH, so docs/assets/demo.manifest.json cannot be refreshed. The GIF is written but the staleness check will now fail - re-run this script with go available."
}
Push-Location (Join-Path $repoRoot "face-go")
try {
    & go test ./internal/tui -run TestDemoGifStillShowsTheFaceItWasRecordedFrom -write-demo-manifest -count=1
    $code = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($code -ne 0) { Die "the manifest could not be written (see the go test output above)." }
Write-Host "refreshed docs/assets/demo.manifest.json - commit it WITH the GIF." -ForegroundColor Green
