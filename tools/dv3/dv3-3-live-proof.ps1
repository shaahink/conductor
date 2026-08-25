# DV3.3 live proof - a REAL .ogg through the fresh build, transcribed by the real model.
#
# What it proves, end to end, with nothing stubbed:
#   1. a voice-shaped .ogg is filed into a project's inbox   (conductor inbox add)
#   2. it reads as UNTRANSCRIBED until something reads it out (conductor inbox list)
#   3. the configured local command - faster-whisper large-v3 on this machine's GPU -
#      turns it into words, and low-confidence stretches come back MARKED
#   4. the audio is still there beside its transcript sidecar
#   5. prune is the only deletion path, and it deletes nothing without --yes
#
# Scratch only: its own repo under the temp directory, its own plan, its own state dir.
# It never touches this repo's .conductor, never starts a run, and never aims a
# run-control verb at anything. ASCII only (Windows PowerShell 5.1).

param(
    [string]$OutDir  = (Join-Path $env:TEMP "dv33-rig"),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path,
    [string]$Say = "Conductor should refuse a file over twenty megabytes by name, and keep the audio beside the transcript."
)

$ErrorActionPreference = "Stop"
$env:CONDUCTOR_PLAN = $null            # trap 3: never inherit another rig's plan

$exe = Join-Path $RepoRoot "src\Conductor\bin\Debug\net10.0\conductor.exe"
if (-not (Test-Path $exe)) { throw "build first: dotnet build Conductor.slnx  (missing $exe)" }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null
$repo = Join-Path $OutDir "rig"
New-Item -ItemType Directory -Path $repo | Out-Null
Set-Content -Path (Join-Path $repo "TRACKER.md") -Value "# dv33 rig" -Encoding ASCII

$plan = Join-Path $repo "conductor.plan.json"
$planJson = @{
    name    = "dv33-rig"
    repo    = $repo
    tracker = "TRACKER.md"
    stages  = @(@{ id = "DV1"; title = "rig"; sessions = 1 })
    # Never invoked - no run is ever started here - but plan validation wants a shape.
    agent   = @{ command = "echo"; args = @("{prompt}") }
} | ConvertTo-Json -Depth 6
Set-Content -Path $plan -Value $planJson -Encoding ASCII

# ---- 1. a real recording: this machine's own voice, spoken into a wav, encoded as opus/ogg ----
$wav = Join-Path $OutDir "note.wav"
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.Rate = -1
$synth.SetOutputToWaveFile($wav)
$synth.Speak($Say)
$synth.Dispose()

$ffmpeg = if (Get-Command ffmpeg -ErrorAction SilentlyContinue) { "ffmpeg" } elseif (Test-Path "C:\ffmpeg\ffmpeg.exe") { "C:\ffmpeg\ffmpeg.exe" } else { $null }
if (-not $ffmpeg) { throw "no ffmpeg - needed to make the .ogg a phone would send" }
$ogg = Join-Path $OutDir "note.ogg"
& $ffmpeg -y -loglevel error -i $wav -c:a libopus -b:a 32k $ogg
if (-not (Test-Path $ogg)) { throw "ffmpeg produced no .ogg" }
"[rig] spoke $($Say.Length) chars into $ogg ($((Get-Item $ogg).Length) bytes)"

# ---- 2..5. the fresh build, one verb at a time ----
function Step($title, [scriptblock]$body) {
    ""
    "=== $title ==="
    & $body 2>&1 | ForEach-Object { $_.ToString() }
    "(exit $LASTEXITCODE)"
}

Step "inbox add --file note.ogg" { & $exe inbox add --file $ogg --text "the live proof" -p $plan }
Step "inbox list (before)"       { & $exe inbox list -p $plan }

$env:CONDUCTOR_TRANSCRIBE_COMMAND = "python `"$RepoRoot\tools\transcribe\whisper-json.py`" {audio}"
Step "inbox transcribe --all"    { & $exe inbox transcribe --all -p $plan }
Step "inbox show --id 1"         { & $exe inbox show --id 1 -p $plan }
Step "inbox list (after)"        { & $exe inbox list -p $plan --full }

# ---- the same REAL segment, read strictly: the marks are drawn from the model's own numbers,
# not from a fixture. A clean recording is not doubtful at 0.45, so the floor is raised to 0.95 and
# the identical stretch comes back wrapped. ----
$plan2 = Join-Path $repo "strict.plan.json"
$plan2Json = @{
    name    = "dv33-rig-strict"
    repo    = $repo
    tracker = "TRACKER.md"
    stages  = @(@{ id = "DV1"; title = "rig"; sessions = 1 })
    agent   = @{ command = "echo"; args = @("{prompt}") }
    courier = @{ transcribe = @{ confidenceFloor = 0.95 } }
} | ConvertTo-Json -Depth 6
Set-Content -Path $plan2 -Value $plan2Json -Encoding ASCII

Step "inbox add (again) then transcribe with confidenceFloor 0.95" {
    & $exe inbox add --file $ogg -p $plan2
    & $exe inbox transcribe --id 2 -p $plan2
}

$inbox = Join-Path $repo ".conductor\inbox"
Step "what is on disk" {
    Get-ChildItem -Path $inbox -Recurse -File | ForEach-Object { $_.FullName.Substring($inbox.Length + 1) + "  " + $_.Length + " bytes" }
}
Step "the stored note"      { Get-Content (Join-Path $inbox "notes\1.json") -Raw }
Step "the sidecar"          { Get-ChildItem (Join-Path $inbox "media") -Filter *.transcript.json | ForEach-Object { Get-Content $_.FullName -Raw } }
Step "prune --id 1 (no --yes: deletes nothing)" { & $exe inbox prune --id 1 -p $plan }
Step "the audio is still there" { Test-Path (Join-Path $inbox "media\1-note.ogg") }
Step "prune --id 1 --yes"   { & $exe inbox prune --id 1 --yes -p $plan }
Step "and now it is gone"   { Test-Path (Join-Path $inbox "media\1-note.ogg") }

""
"[rig] done. scratch tree: $OutDir"
