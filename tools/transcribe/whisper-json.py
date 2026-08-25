#!/usr/bin/env python3
"""DV3.3 - the local transcribe command this repo ships, in conductor's contract.

Point `courier.transcribe.command` (or CONDUCTOR_TRANSCRIBE_COMMAND) at it:

    python tools/transcribe/whisper-json.py {audio}

It prints ONE json object on stdout and nothing else:

    {"text": "...", "language": "en",
     "segments": [{"start": 0.0, "end": 3.2, "text": "...", "confidence": 0.87,
                   "avg_logprob": -0.14, "no_speech_prob": 0.01}]}

`confidence` is what conductor marks the doubtful stretches from; it is exp(avg_logprob),
which is a probability, damped when the model also thought the stretch might be silence.
Every diagnostic goes to stderr, because stdout is the contract.

Local and offline: faster-whisper on this machine's GPU, no API key, nothing leaves the box
(findings 1.6). The awkward parts below are not decoration - each one is a failure this
machine has already produced:

  - CUDA DLLs: ctranslate2 dies with `cublas64_12.dll is not found` unless the pip nvidia
    lib dirs are on PATH BEFORE the import. os.add_dll_directory alone is not enough on
    Windows.
  - condition_on_previous_text=True makes whisper loop a phrase and swallow the rest of a
    long note, silently. It is forced off.
  - large-v3 at int8_float16: `small` turns English technical terms into noise, and fp16
    large-v3 does not fit in 4 GB of VRAM.
"""

from __future__ import annotations

import argparse
import glob
import json
import math
import os
import site
import subprocess
import sys
import tempfile
from pathlib import Path

_NVIDIA_SUBDIRS = ("cublas", "cudnn", "cuda_nvrtc", "cuda_runtime")


def _arm_cuda() -> None:
    roots: list[str] = []
    try:
        candidates = list(site.getsitepackages())
    except Exception:
        candidates = []
    user_site = site.getusersitepackages()
    if isinstance(user_site, str):
        candidates.append(user_site)
    for sp in candidates:
        for sub in _NVIDIA_SUBDIRS:
            d = os.path.join(sp, "nvidia", sub, "bin")
            if os.path.isdir(d):
                roots.append(d)
    for d in glob.glob(os.path.join(sys.prefix, "Lib", "site-packages", "nvidia", "*", "bin")):
        if d not in roots:
            roots.append(d)
    if roots:
        os.environ["PATH"] = os.pathsep.join(roots) + os.pathsep + os.environ.get("PATH", "")
        for d in roots:
            try:
                os.add_dll_directory(d)  # type: ignore[attr-defined]
            except (AttributeError, OSError):
                pass


_arm_cuda()

from faster_whisper import WhisperModel  # noqa: E402


def find_ffmpeg() -> str | None:
    """ffmpeg on PATH, or the fixed install this machine keeps."""
    from shutil import which

    return which("ffmpeg") or (r"C:\ffmpeg\ffmpeg.exe" if os.path.isfile(r"C:\ffmpeg\ffmpeg.exe") else None)


def to_wav(src: Path) -> Path:
    """16 kHz mono wav. Opus straight into ctranslate2 works on some builds and not others;
    one ffmpeg call removes the question."""
    ffmpeg = find_ffmpeg()
    if ffmpeg is None:
        return src
    out = Path(tempfile.gettempdir()) / (src.stem + ".conductor.wav")
    subprocess.run(
        [ffmpeg, "-y", "-loglevel", "error", "-i", str(src), "-ac", "1", "-ar", "16000", str(out)],
        check=True,
    )
    return out


def confidence(segment) -> float:
    """A probability, from the two numbers whisper actually reports.

    exp(avg_logprob) is the mean per-token probability. A segment the model also thought was
    probably silence is damped by (1 - no_speech_prob): a hallucinated phrase over background
    noise scores well on logprob and badly here, which is exactly the stretch a reader should
    not trust."""
    p = math.exp(getattr(segment, "avg_logprob", -1.0) or -1.0)
    silence = getattr(segment, "no_speech_prob", 0.0) or 0.0
    return max(0.0, min(1.0, p * (1.0 - min(0.95, silence))))


def main() -> int:
    ap = argparse.ArgumentParser(description="Transcribe one audio file as conductor's json contract.")
    ap.add_argument("audio", type=Path)
    ap.add_argument("-m", "--model", default="large-v3")
    ap.add_argument("-l", "--language", default=None, help="whisper language code; omit to detect")
    ap.add_argument("--cpu", action="store_true", help="skip the GPU")
    args = ap.parse_args()

    if not args.audio.is_file():
        print(json.dumps({"text": "", "segments": [], "error": "no such file"}))
        print(f"no such file: {args.audio}", file=sys.stderr)
        return 2

    wav = to_wav(args.audio)
    device = "cpu" if args.cpu else "cuda"
    try:
        model = WhisperModel(args.model, device=device, compute_type="int8_float16" if device == "cuda" else "int8")
        segments, info = transcribe(model, wav, args.language)
    except Exception as exc:  # noqa: BLE001 - a lazy CUDA failure raises here, not at build
        if device == "cpu":
            raise
        print(f"gpu failed ({exc}); retrying on cpu", file=sys.stderr)
        model = WhisperModel(args.model, device="cpu", compute_type="int8")
        segments, info = transcribe(model, wav, args.language)

    out = {
        "text": " ".join(s.text.strip() for s in segments).strip(),
        "language": getattr(info, "language", None),
        "segments": [
            {
                "start": round(s.start, 2),
                "end": round(s.end, 2),
                "text": s.text.strip(),
                "confidence": round(confidence(s), 4),
                "avg_logprob": round(getattr(s, "avg_logprob", 0.0) or 0.0, 4),
                "no_speech_prob": round(getattr(s, "no_speech_prob", 0.0) or 0.0, 4),
            }
            for s in segments
        ],
    }
    print(json.dumps(out, ensure_ascii=False))
    print(f"# {len(out['segments'])} segment(s), language {out['language']}", file=sys.stderr)
    return 0


def transcribe(model: WhisperModel, wav: Path, language: str | None):
    segments, info = model.transcribe(
        str(wav),
        language=language,
        beam_size=5,
        temperature=[0.0, 0.2, 0.4, 0.6, 0.8, 1.0],
        vad_filter=True,
        vad_parameters=dict(min_silence_duration_ms=700, speech_pad_ms=300),
        condition_on_previous_text=False,  # never True: silent repetition collapse
        compression_ratio_threshold=2.2,
        repetition_penalty=1.1,
    )
    return list(segments), info


if __name__ == "__main__":
    sys.exit(main())
