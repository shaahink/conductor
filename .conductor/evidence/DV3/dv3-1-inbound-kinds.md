# DV3.1 — the inbound message kinds, on the wire

**Session 6, stage DV3, attempt 1. 2026-08-25.**

The defect this closes is not a wrong answer, it is *no* answer. `TgMessage` carried `message_id`,
`text` and `chat` and nothing else (`TelegramApi/UpdateDtos.cs:19-23`, before this commit), so a
voice note sent to the bot was not refused, not logged and not acknowledged — it was **invisible**,
findings §1.2 gap 2. Everything below is measured against a real `TelegramService` long-polling a
loopback stand-in for api.telegram.org.

## What was built

| Piece | Where | What it does |
|---|---|---|
| The message kinds | `TelegramApi/UpdateDtos.cs` (`TgMessage`) | `caption`, `voice`, `audio`, `document`, `photo[]`, `reply_to_message`, `message_thread_id` |
| One file shape | `TelegramApi/FileDtos.cs` | `TgFileRef` (all four kinds share `file_id`/`file_size`/`mime_type`/`file_name`), `TgFile`, `TgFileResponse` |
| The channel-agnostic note | `Messaging/InboundNote.cs` | `InboundMediaKind`, `InboundMedia`, `InboundNote` — no Bot API type, no HTTP |
| What the bot says back | `Messaging/InboundAck.cs` | `Received` / `Refused` / `NotYours`, all pure text-in-text-out |
| The surface's inbound half | `Messaging/RemoteSurface.Inbound.cs` | `HandleNoteAsync` — acknowledges, never routes, never steers |
| The wire protocol | `TelegramService.Inbound.cs` | classify → `getFile` → download → disk, with the cap checked three times |
| The Bot API's ceiling | `TelegramLimits.cs` | `MaxDownloadBytes = 20 MB`, `TooBigReason`, `NotFetchedReason` |

## The proof

`tests/Conductor.Tests/DV3_1InboundKindsTests.cs` — 12 tests, all through the stub seam
(`TelegramConfig.ApiBaseUrl`), scratch token, scratch chat ids, a temp repo per test.

```
dotnet test Conductor.slnx --no-build --filter "FullyQualifiedName~DV3_1InboundKindsTests"
Passed!  - Failed: 0, Passed: 12, Skipped: 0, Total: 12, Duration: 1 s
  .conductor/bg-logs/DV3.1 suite final-20260825-220743103.log
```

Each kind, end to end — the update is deserialised from the JSON Telegram really sends, fetched
through `getFile`, and the bytes are compared on disk:

| Test | What it pins |
|---|---|
| `A_voice_note_is_downloaded_and_acknowledged_by_kind` | 3,000 bytes land at `<stateDir>/inbox/media/501-voice.oga`, byte-for-byte equal; the reply says "Voice note received", 2.9 KB, 42s |
| `An_audio_file_keeps_its_own_name_and_is_not_called_a_voice_note` | `502-standup.mp3`; the reply says "Audio", never "Voice note" |
| `A_document_lands_on_disk_and_its_caption_is_the_notes_text` | `503-acceptance.pdf`; the caption becomes the note's text (41 chars, in the log line) and comes back in the ack |
| `A_photo_is_fetched_at_the_largest_size_offered` | of a 120 B and a 9,000 B size, the 9,000 B one is fetched — the thumbnail is not what the sender meant |
| `A_reply_to_a_push_and_a_forum_topic_both_survive_the_wire` | `reply to 4242`, `topic 77` on the inbound log line — the two zero-typing routing hints DV3.4 needs |

The 20 MB cap, refused **by name**, twice over — the message declares the size, and the API refuses
one that did not:

| Test | What it pins |
|---|---|
| `A_file_over_the_twenty_megabyte_cap_is_refused_by_name_before_any_fetch` | a 26,214,400-byte `walkthrough.mp4`: the reply names the file, "25 MB", "20 MB" and "Your message was kept"; `GetFileCalls == 0` and the media dir does not exist |
| `A_file_the_api_itself_refuses_still_names_the_file_and_the_cap` | no declared size, `getFile` answers "Bad Request: file is too big": the reply carries Telegram's own sentence plus the cap, and nothing is written |

The boundary:

| Test | What it pins |
|---|---|
| `An_observer_may_not_file_and_nothing_of_theirs_is_downloaded` | findings §1.8 — the profile gate runs in FRONT of the fetch, so an unauthorised sender cannot put bytes on this machine; `GetFileCalls == 0`, and they are told by name |
| `A_document_name_that_is_a_traversal_cannot_escape_the_media_directory` | `file_name: "../../../../plan.json"` stores as `509-plan.json` inside the media dir; no `plan.json` appears in the repo or state dir |
| `Plain_text_still_takes_the_old_command_path` | KS11.1's golden-replay standard: `/abort` still answers "Confirm abort?", no inbound-note line, no `getFile` |
| `A_note_files_even_with_two_way_control_switched_off` | a note is a RECORD, not a control verb — filing must not need `enableTwoWay`, or the safest plans would be the ones that cannot receive feedback |
| `The_repos_conductor_gitignore_has_no_allowlist_entry_for_the_inbox` | findings §6.1 / trap 6 — `.conductor/.gitignore` is `*` with an allowlist and there is no `inbox` entry. This repo is public; a future "fix" for the invisible directory would ship the owner's voice notes to the world |

## The seam held, and the ratchet proved it

The first pass put the sentence *"is over Telegram's 20 MB limit"* in `Messaging/InboundAck.cs`, and
`KS11_1SeamBoundaryTests.The_seam_contains_no_telegram_identifier_anywhere` failed on it — a string
literal, in the channel-agnostic half, naming the messenger. That is exactly the regression the rule
exists to catch and it was not weakened to pass: the ceiling and both refusal sentences moved to
`TelegramLimits.cs` (an adapter file), `InboundAck.Refused(name, why)` kept only the *shape* of a
refusal, and `TelegramService.Inbound.cs` was added to the test's `AdapterFiles` list because it
genuinely is one.

```
dotnet test Conductor.slnx --no-build --filter
  "FullyQualifiedName~Telegram|~KS11|~K5_|~DV2_3|~Architecture|~Remote|~FuOwner11|~SF5_3"
Passed!  - Failed: 0, Passed: 362, Skipped: 0, Total: 362, Duration: 14 s
  .conductor/bg-logs/DV3.1 neighbours 2-20260825-220715342.log
```

That sweep includes `ArchitectureTests` (500-line ceiling, 3 types per file — every new file is
inside both) and the three `KS11_1SeamBoundaryTests` rules.

## Decisions a later checkpoint should know about

- **The cap is checked three times**: against the size the message declares (before any round trip),
  against the size `getFile` reports, and against the bytes that actually arrive
  (`CopyCappedAsync`). The first two trust a number the other end supplied; the third does not.
- **`ChatProfiles.MayFile`** is the one place filing policy lives, and it is where findings §6.7's
  proposed `reporter` profile would be granted if the owner ever accepts it.
- **Media lands under `<stateDir>/inbox/media/<messageId>-<name>`**. DV3.2 owns the transcript beside
  it, the index and the read cursor; the directory was chosen so DV3.2 does not have to move it.
- **A sender-supplied `file_name` is scrubbed to an ASCII leaf**, capped at 80 chars, with leading
  and trailing dots stripped — and the file extension is re-attached deliberately, because the first
  version of `Scrub` ate the dot and stored every voice note extensionless. The test caught it.
- **DV3.1 acknowledges; it does not store.** `RemoteSurface.HandleNoteAsync` is the single place
  DV3.2's durable write goes, and the acknowledgement wording ("Your message was kept") is already
  written for it.
