<#
.SYNOPSIS
  Renders docs/assets/demo.gif - the dashboard tour under the README's H1.

.DESCRIPTION
  The frames are not mock-ups. Each one is a COMMITTED GOLDEN from face-go's rendering tests
  (face-go/internal/tui/testdata/golden/*.golden) - the exact bytes View() produced for that
  screen, diffed byte-for-byte by `go test ./internal/tui/ -run TestGolden` on every CI run. So
  the demo cannot drift from the real Face without a test going red first: regenerate the goldens
  (`go test ./internal/tui/ -run TestGolden -update`), re-run this, and the GIF is current again.

  Goldens are ANSI-stripped, so the GIF is monochrome. That is the trade for frames that are
  provably real. For a full-colour recording of the live binary use VHS instead:

      vhs docs/assets/demo.tape        # needs vhs + ttyd + ffmpeg (macOS/Linux)

  ttyd has no Windows build, which is why the committed asset is produced this way on the
  Windows-first dev box.

.PARAMETER Font
  Any monospace TTF with box-drawing coverage. Cascadia Mono ships with Windows Terminal.

.EXAMPLE
  powershell -File tools/demo/make-demo-gif.ps1
#>
[CmdletBinding()]
param(
    [string]$Font            = "$env:WINDIR\Fonts\CascadiaMono.ttf",
    [string]$Out             = "docs/assets/demo.gif",
    [int]   $FontSize        = 15,
    [double]$SecondsPerFrame = 2.2
)

$ErrorActionPreference = "Stop"

# The tour, in order. Each name is a file in the golden dir, minus the extension.
$frames = @(
    "home_demo",      # the landing page: run, budget, gates, workspace, next steps
    "agent",          # the live agent transcript
    "kanban",         # the work board
    "kanban_detail",  # one card, with the prompt block it contributes
    "timeline",       # what happened, when
    "plan_stages",    # editing the plan without restarting the run
    "palette"         # the ':' command palette - every control verb
)

$repoRoot  = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$goldenDir = Join-Path $repoRoot "face-go\internal\tui\testdata\golden"
$outPath   = Join-Path $repoRoot $Out

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    throw "ffmpeg is not on PATH. It must be built with libfreetype (the drawtext filter)."
}
if (-not (Test-Path $Font)) { throw "font not found: $Font" }
if (-not (Test-Path $goldenDir)) { throw "golden dir not found: $goldenDir" }

# ffmpeg filtergraphs treat ':' as an argument separator and '\' as an escape, so a Windows font
# path has to be spelled with forward slashes and an escaped drive colon.
$fontArg = ($Font -replace '\\', '/') -replace '^([A-Za-z]):', '$1\:'

$work = Join-Path ([IO.Path]::GetTempPath()) ("conductor-demo-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work -Force | Out-Null

try {
    $i = 0
    foreach ($name in $frames) {
        $src = Join-Path $goldenDir "$name.golden"
        if (-not (Test-Path $src)) { throw "golden not found: $src (was it renamed?)" }

        $i++
        $txt = Join-Path $work ("f{0:d2}.txt" -f $i)
        $png = Join-Path $work ("f{0:d2}.png" -f $i)

        # drawtext reads the file raw; copy it in verbatim so the frame is byte-identical.
        Copy-Item $src $txt

        # 110 cols x 34 rows at fontsize 15 in Cascadia Mono lands inside 1040x680 with margins.
        # line_spacing=0 because the font's own leading already matches a terminal cell.
        # expansion=none so a literal '%{...}' in a frame is never treated as a drawtext expression.
        $vf = "drawtext=fontfile='$fontArg':textfile='" + (($txt -replace '\\','/') -replace '^([A-Za-z]):','$1\:') +
              "':x=16:y=12:fontsize=$FontSize" + ":fontcolor=0xc9d1d9:line_spacing=0:expansion=none"

        & ffmpeg -hide_banner -loglevel error `
            -f lavfi -i "color=c=0x0d1117:s=1040x680" `
            -vf $vf -frames:v 1 -y $png
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed rendering frame $name" }
        Write-Host ("  frame {0,2}  {1}" -f $i, $name)
    }

    New-Item -ItemType Directory -Path (Split-Path $outPath) -Force | Out-Null

    # One palette generated across ALL frames (stats_mode=full) so colours do not shift between
    # them, then applied without dithering - flat terminal text dithers into mud otherwise.
    $rate = [Math]::Round(1.0 / $SecondsPerFrame, 4)
    & ffmpeg -hide_banner -loglevel error `
        -framerate $rate -i (Join-Path $work "f%02d.png") `
        -filter_complex "[0:v]split[a][b];[a]palettegen=stats_mode=full[p];[b][p]paletteuse=dither=none" `
        -loop 0 -y $outPath
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed assembling the gif" }

    $kb = [Math]::Round((Get-Item $outPath).Length / 1KB, 1)
    Write-Host ""
    Write-Host ("wrote {0}  ({1} frames, {2}s each, {3} KB)" -f $Out, $frames.Count, $SecondsPerFrame, $kb) -ForegroundColor Green
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
