# DV3.3 — transcription: a local command, and the doubt kept

**Checkpoint:** Transcription: configured local command (faster-whisper on this machine's GPU),
per-segment confidence marked in the stored note, unset command files the note untranscribed with
audio kept and the reply saying so; `conductor inbox prune` is the only deletion path; a real `.ogg`
transcribes in the rig.

**Session:** divan #7 · 2026-08-25 · branch `feat/divan` · commits `4b1f04a` + this one.

---

## 1. The acceptance, line by line

| Acceptance | Where it lives | Proof |
|---|---|---|
| a configured LOCAL command turns audio into words | `CourierConfig.cs` (`courier.transcribe.command`, `CONDUCTOR_TRANSCRIBE_COMMAND`), `Transcriber.cs:95` | live rig §3, wire test `A_voice_note_comes_back_transcribed_…` |
| per-segment confidence MARKED in the stored note | `Transcript.cs:86` `Marked(floor)`, floor at `:52` | live rig §3 (strict pass), `Only_the_low_confidence_segments_are_marked` |
| the numbers survive beside the audio | `InboxStore.cs:171` `AttachTranscript` writes `<media>.transcript.json` | live rig §3 sidecar, `A_transcript_is_attached_beside_the_audio_and_the_audio_stays` |
| command unset → filed untranscribed, audio kept, reply says so | `RemoteSurface.Inbound.cs:44-46`, `InboundAck.NotTranscribed()` | wire test `With_no_command_configured_…` |
| command fails/hangs/hears nothing → same, with the reason | `Transcriber.cs:112,117,123` | 4 tests, and wire test `A_failing_command_still_leaves_the_note_and_says_why` |
| `conductor inbox prune` is the ONLY deletion path | `InboxStore.cs:236`, `InboxCommand.cs` | architecture test `Prune_is_the_only_code_in_the_engine_that_deletes_an_inbox_file`, live rig §4 |
| a real `.ogg` transcribes in the rig | `tools/transcribe/whisper-json.py` | live rig §3 — 29.0 s on the GPU, confidence 0.8672 |

Bug **#74** (the battery named `conductor inbox list` and no such verb existed) is closed by this
checkpoint: `src/Conductor/Commands/InboxCommand.cs` registers `inbox` in `Program.cs:155` with
`list`, `show`, `add`, `transcribe` and `prune`.

---

## 2. What the code DOES (measured, not read off a comment)

- **The contract is stdout.** `Transcript.Parse` (`Transcript.cs:139`) takes conductor's JSON
  (`{"text":…,"segments":[{"start","end","text","confidence"}]}`), accepts faster-whisper's raw
  `avg_logprob` as `exp(avg_logprob)`, and treats anything else as a plain transcript **with no
  marks at all**. A confidence conductor cannot read is never invented.
- **Filed first, transcribed second.** `RemoteSurface.HandleNoteAsync` files the note (untranscribed)
  and acknowledges it before any transcription starts; `TranscribeAsync` attaches afterwards
  (`RemoteSurface.Inbound.cs:70`). A machine that dies mid-transcription loses the transcript, never
  the message — and "untranscribed audio" is already a supported, named state.
- **The transcriber cannot throw into the poll loop.** Every exception path in
  `LocalCommandTranscriber` returns a `Failed` outcome carrying a sentence (`Transcriber.cs:128`),
  and the timeout kills the process tree (`:180`).
- **The marks reach the prompt with their meaning attached.** `InboxBattery.Header` now writes
  `TRANSCRIBED from audio, confidence NN% — [?: …] marks a stretch the transcriber was unsure of`
  on the note's engine-text header line — outside the quoted block, so the fencing property DV3.2
  pinned is unchanged.

---

## 3. The live proof — a real `.ogg`, the real model, the fresh build

`tools/dv3/dv3-3-live-proof.ps1`, run as a tracked bg child (`dv33-live2`, log in
`.conductor/bg-logs/`). Scratch repo + scratch plan + own state dir under `%TEMP%\dv33-rig`; the
engine binary is **this working tree's** build (`src/Conductor/bin/Debug/net10.0/conductor.exe`),
never the `conductor` on PATH. No run was started, no run-control verb was used, this repo's
`.conductor` was not touched.

Speech was synthesised on this machine (SAPI), encoded to Opus/OGG by ffmpeg — 29 608 bytes — and
put through the verbs:

```
=== inbox add --file note.ogg ===
filed note 1 in ...\rig\.conductor\inbox - run `conductor inbox transcribe --id 1` to read it out

=== inbox list (before) ===
| 1 | 2026-08-25 22:57 | voice | unread untranscribed | the live proof |

=== inbox transcribe --all ===
transcribing ...\.conductor\inbox\media\1-note.ogg ...
transcribe: python exited 0 after 29.0s, 343 chars out
transcribed 1 (confidence 87%)
the live proof
Conductor should refuse a file over 20 MB by name, and keep the audio beside the transcript.
```

The stored note, from disk — caption kept, transcript beside it, **audio still there**:

```json
{
  "Id": 1, "ChatId": "local", "Kind": "voice",
  "Text": "the live proof\r\nConductor should refuse a file over 20 MB by name, and keep the audio beside the transcript.",
  "MediaPath": "media/1-note.ogg",
  "TranscriptPath": "media/1-note.ogg.transcript.json",
  "TranscriptConfidence": 0.8672
}
```

The sidecar, beside the audio, holding the numbers the marks are drawn from:

```json
{ "text": "Conductor should refuse a file over 20 MB by name, and keep the audio beside the transcript.",
  "language": "en", "confidence": 0.8672, "confidenceFloor": 0.45, "doubtful": 0,
  "segments": [ { "start": 0, "end": 6.48, "text": "Conductor should refuse a file over 20 MB …",
                  "confidence": 0.8672 } ] }
```

**The marks, drawn from the model's own numbers.** A clean recording is not doubtful at the default
floor, so the same real audio was re-filed and read with `courier.transcribe.confidenceFloor: 0.95`.
The identical stretch comes back wrapped — nothing here is a fixture:

```
=== inbox add (again) then transcribe with confidenceFloor 0.95 ===
filed note 2 in ...\rig\.conductor\inbox
transcribing ...\.conductor\inbox\media\2-note.ogg ...
transcribe: python exited 0 after 20.4s, 343 chars out
transcribed 2 (confidence 87%, 1 unsure stretch(es) marked [?: …])
[?: Conductor should refuse a file over 20 MB by name, and keep the audio beside the transcript.]
```

Note the transcriber's own reading of the sentence: the spoken words were "twenty megabytes" and
whisper wrote "20 MB". That is exactly the class of difference the marks exist for.

## 4. Prune, and only prune

```
=== prune --id 1 (no --yes: deletes nothing) ===
  1 · 2026-08-25 · voice · the live proof  Conductor should refuse a file over 20 MB by name, …
1 note(s), 3 file(s) would be deleted. Nothing was. Add --yes to do it.
=== the audio is still there ===   True
=== prune --id 1 --yes ===
pruned 1 note(s), 3 file(s) deleted from ...\rig\.conductor\inbox.
=== and now it is gone ===         False
```

The absence is proven by test, in the KS4.1 habit — the sweep asserts it FOUND deletions before
asserting none of them is outside the prune path, so the theory beside it cannot be vacuously true:

- `Prune_is_the_only_code_in_the_engine_that_deletes_an_inbox_file` — every `src/**/*.cs` that names
  `InboxStore`/`InboxNote` is swept for `File.Delete(`/`Directory.Delete(`; the only hits are
  `InboxStore.Prune` and `InboxStore.TryDelete` (a temp file this store just wrote).
- `Exactly_one_verb_calls_prune` — the only caller in the engine is `InboxCommand.cs`.

## 5. Tests

```
dotnet test Conductor.slnx --filter FullyQualifiedName~DV3_3   →  23 passed, 0 failed
dotnet test Conductor.slnx --filter FullyQualifiedName~DV3     →  67 passed, 0 failed
dotnet test Conductor.slnx --filter FullyQualifiedName~Prompt   → 115 passed, 0 failed  (the battery seam)
dotnet test Conductor.slnx --filter FullyQualifiedName~Telegram →  74 passed, 0 failed
dotnet build Conductor.slnx -clp:ErrorsOnly -nr:false           →  0 errors, 0 warnings
```

23 new tests: 20 over the transcript, the command and the store (`DV3_3TranscriptionTests`), 3 on the
wire through a real `TelegramService` against a loopback stand-in (`DV3_3TranscriptionWireTests`) —
transcribed, not-configured and failed, each asserting both what the sender heard and what is on
disk. Scratch token, scratch chat ids, `TelegramConfig.ApiBaseUrl` as the seam: no real bot, no real
chat.

## 6. What is NOT done here

- **Routing is DV3.4.** A note still files against the project whose engine received it; reply-to-a-
  push, sticky `/project`, topics and the dead-letter directory are the next checkpoint.
- **Transcription blocks the poll loop** for the length of one command (29 s here; minutes for a long
  note). That is correct for DV3.3 — the note is already durable and the sender was already
  acknowledged — and it MOVES when DV4.1 gives polling to the courier daemon.
- **No `.conductor/.gitignore` entry for the inbox**, now or ever (findings §6.1). The transcripts
  this checkpoint produces are exactly what must not ship from a public repo; the DV3.1 test pinning
  that absence still passes.
