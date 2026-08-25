# DV3.2 — the per-project inbox: a note that survives the run

**Session 6, stage DV3, attempt 1. 2026-08-25.** Landed after DV3.1 in the same session.

DV3.1 made a voice note visible. This makes it *survive*: a durable store under
`.conductor/inbox`, an append-only index deduped by `update_id`, a read cursor, and an
`InboxBattery` on the existing `IPromptBattery` seam that carries the owner's words into the next
session's prompt — quoted, framed, and provably so.

## What was built

| Piece | Where | What it does |
|---|---|---|
| The record | `Inbox/InboxNote.cs` | `InboxNote` (id, received, chat, kind, text, media, reply-to, topic) + `InboxCursor` |
| The store | `Inbox/InboxStore.cs` | temp-file-plus-rename writes, `index.jsonl` append-only, `cursor.json`, index repair |
| The battery | `Inbox/InboxBattery.cs` | unseen notes verbatim (capped), the rest counted, framed and quoted |
| The write path | `Messaging/RemoteSurface.Inbound.cs` | files the note before acknowledging it; a duplicate delivery is not acknowledged twice |
| The read path | `PromptBuilder.cs`, `Orchestration/SessionComposer.cs` | the five-argument `BatterySection` overload, and the seen mark at the session boundary |

Layout on disk, per project:

```
<stateDir>/inbox/
  notes/<update_id>.json     one file per note, written temp-then-renamed
  media/<msgId>-<name>       DV3.1's downloads, beside the notes
  index.jsonl                append-only, one JSON object per line
  cursor.json                seenThroughId + the session that took delivery
```

No `.gitignore` change, now or ever: `.conductor/.gitignore` is `*` with a three-entry allowlist,
this repo is public, and an `!inbox/` entry would push the owner's transcripts to the world
(findings §6.1). DV3.1's `The_repos_conductor_gitignore_has_no_allowlist_entry_for_the_inbox` pins
that absence and still passes.

## The proof

```
dotnet test Conductor.slnx --no-build --filter "FullyQualifiedName~DV3_2"
Passed!  - Failed: 0, Passed: 32, Skipped: 0, Total: 32
  .conductor/bg-logs/DV3.2 sweep 3-20260825-222730455.log
```

### The store — `DV3_2InboxStoreTests`

| Test | What it pins |
|---|---|
| `A_filed_note_is_a_file_on_disk_and_a_line_in_the_index` | the note is its own JSON file and exactly one index line |
| `The_same_update_id_filed_twice_lands_once` | findings §6.2 — a courier replay is refused, the first text wins, no temp file is left |
| `Concurrent_writers_neither_lose_a_note_nor_duplicate_one` | 40 ids offered by 80 concurrent writers: exactly 40 win, 40 notes exist, every index line is whole JSON and every id is named |
| `A_note_whose_index_line_was_lost_is_still_read_and_the_index_is_repaired` | the crash window between the rename and the index append cannot make a note invisible |
| `The_cursor_moves_over_what_was_read_and_deletes_nothing` | a cursor, not a delete; the session number is recorded |
| `The_cursor_never_goes_backwards` | a straggler with a stale view cannot re-surface a read note |
| `A_corrupt_cursor_reads_as_nothing_seen_rather_than_everything_seen` | the failure that repeats a note beats the one that loses it |
| `The_battery_carries_the_oldest_notes_verbatim_and_counts_the_rest` | findings §6.6 — 5 unread, 2 carried, "3 more unread note(s)" said out loud |
| `A_clipped_note_says_it_was_clipped` | a silent clip reads as the owner having said less than they did |

### The architecture test — `DV3_2InboxFencingTests`

Findings §1.8 asks for a test that proves the fencing and framing are *always* present. It is
written as the KS4.1 habit — prove the absence — over eight adversarial notes, each a real
technique: closing the fence, forging a battery heading, forging **the frame itself**, issuing
control verbs, every line ending a phone can produce, pre-quoted text, HTML/markdown, and empty.

The property asserted is structural, not a string search:

- every line **inside** a fence starts with the quote marker; and
- every line **outside** every fence is engine text in a shape this battery generates (the frame,
  a `note N · … :` header, a fence, the count line, or blank).

Together those two admit no unquoted owner text anywhere. Stating it structurally matters: two of
the payloads are written to *equal* the frame and the fence, and a test that searched for the
payload's bytes would have called the engine's own frame a leak while a genuinely novel leak walked
past it. That is exactly what the first version of this test did, and it failed on its own payloads.

**The finding this checkpoint paid for.** `BatteryGroup.Fit` trims an over-budget section **at a
line boundary** (`PromptBattery.cs:137-150`). A section quoted only by a fence can therefore lose
its closing line, and everything above it stops being quoted — in the prompt of an agent running
unattended. Two mechanisms answer it, and both are tested:

1. every note line carries `> ` in addition to the fence, which a cut cannot undo;
2. the frame's critical sentence is a short **headline** on its own first line, so a cut that lands
   inside the frame does not leave the notes introduced by half a sentence.

`A_trim_that_lands_inside_the_notes_is_reachable_and_still_quoted` sweeps budgets 1000→2600 and
**asserts the dangerous case is reachable** before asserting it is safe: it requires at least one
budget where the section was trimmed with notes still in it, and at least one where the closing
fence was actually eaten (an odd number of bare fences in the render). Without those two
assertions the theory beside it would be vacuously true. Both hold; the test fails loudly if a
later change makes the case unreachable and the sweep stops proving anything.

### The checkpoint's other exit — filed with no run live

`A_note_filed_with_no_run_live_is_read_by_the_next_session_and_only_that_one`. Nothing is running:
no engine, no poll loop, no run. A note is written to the project's inbox exactly as a courier
would write it. Session 7's prompt is then compiled through the same `BatterySection(state, store,
checkpoints, stageId, inbox)` call `SessionComposer` makes, written to a real `session-007.prompt.md`,
and **read back off disk** (the M7 standard — the assertion reads the file, not memory). The note is
in it, quoted. The cursor then says `seenThroughId 4242, session 7`, session 8's battery does not
contain it, and the note is still on disk.

`The_prompt_preview_neither_shows_the_inbox_nor_moves_the_cursor` pins the other half: the control
plane's prompt preview calls the four-argument overload, so it neither surfaces nor consumes the
owner's unread notes. A read nobody can see is worse than no read at all.

### Nothing else moved

```
dotnet test Conductor.slnx --no-build --filter
  "~DV3_|~PromptBuilder|~PromptBattery|~M7|~KS7_5|~KS3_4|~Architecture|~KS11|~Telegram|~DV2_|~Remote|~Battery|~Session"
Passed!  - Failed: 0, Passed: 688, Skipped: 0, Total: 688, Duration: 1m 28s
  .conductor/bg-logs/DV3.2 neighbours-20260825-222747309.log
```

That sweep covers every suite over the four files this checkpoint touched outside its own: the
prompt builder and its batteries, session composition, the messenger seam and its boundary rules,
and the architecture ratchets.

## Two measurements that shaped the code

- **MA0045 exempts public methods.** Sync file I/O in a *private* helper is an error in this tree;
  the same call in a public method is not. The store's I/O helpers are public with honest names and
  a stated reason (`IPromptBattery.Section` is a property, so the seam is synchronous all the way
  up and an async store would only move a sync-over-async wait one layer out). No `#pragma` was
  added — the analyzer-debt ratchet counts them, and bug #60 already has that count at its bar.
- **CA1506 caught an optional parameter.** Adding `InboxStore? inbox = null` to the existing
  `BatterySection` put the type in the signature every caller binds to and pushed
  `ControlPlaneServer`'s class coupling from 240 to 241 — over its ratchet. The fix is a separate
  five-argument overload; the control plane calls the four-argument form and is coupled to nothing
  new. This is also why the preview cannot accidentally acquire an inbox.

## What DV3.2 does NOT do

- No `conductor inbox list` / `prune` verb yet. The battery's count line names `conductor inbox
  list`, which does not exist — DV3.3's row owns prune, and the verb should land with it.
- No transcription (DV3.3) and no routing (DV3.4): a note files against the project whose state dir
  the channel is configured with, which is correct for one run and is exactly what DV3.4 replaces.
- Marking is at prompt COMPOSITION. A session that is composed and then dies without reading its
  prompt has still marked its notes seen. Nothing is deleted, so they are recoverable from
  `.conductor/inbox/notes/`, but the cursor will not re-offer them.
