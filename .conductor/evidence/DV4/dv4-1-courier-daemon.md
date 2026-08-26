# DV4.1 — the courier: one bot, always awake, outliving the run

Session 9, 2026-08-26. Branch `feat/divan`.

## What the checkpoint asked for, and where each half is proven

| Acceptance | Proof |
|---|---|
| a `conductor courier` verb that owns `CONDUCTOR_TELEGRAM_TOKEN` and polls with no run live | `src/Conductor/Commands/CourierCommand.cs`; live proof steps 3–6, three separate `conductor.exe` processes, no engine and no plan |
| routes to per-project inboxes via an **explicit allowlist**, not the state catalogue | `CourierSettings.Allowed()` → `ProjectDirectory(only:)`; tests `An_explicit_project_list_replaces_the_catalogue_entirely`, `A_catalogued_project_that_is_not_on_the_allowlist_is_refused_and_parked` |
| durable poll offset in the state home | `src/Conductor.Core/Courier/CourierOffset.cs` at `<state home>/courier/offset.json`; test `The_offset_is_durable_and_advances_past_the_delivery_it_handled`; live proof step 5 (a second process is served nothing) |
| dedup by `update_id` | note filed under the delivery id; `InboxStore.Has` short-circuit + `Append`'s non-overwriting rename |
| **kill between receive and acknowledge, restart, the note files exactly once** | test `A_kill_between_receive_and_acknowledge_files_the_note_exactly_once`; live proof step 6 |
| the 24-hour Telegram retention limit stated in docs | `docs/cli.md` "The courier", `docs/operating.md` §2, and `courier status` itself; pinned by `The_twenty_four_hour_retention_limit_is_stated_in_the_docs_and_by_the_verb` |

## The exactly-once argument, in three parts

1. The offset is **durable** — a file, not a field. `TelegramService._offset` was correct only
   because it lived exactly as long as the run that owned the poll loop (findings §6.2).
2. It is written **after** the delivery is handled, never before. `getUpdates?offset=N` is
   Telegram's confirmation that everything below N is done with, so writing N+1 first would discard
   a note on any crash in between. Written after, a kill replays exactly one update.
3. The replay is **harmless**: the note is filed under its `update_id`, `InboxStore.Has` refuses the
   work in front of the write and `InboxStore.Append`'s non-overwriting rename refuses the write
   itself. `DeadLetterBox.Park` is now idempotent per note id for the same reason — its filename
   carries the arrival instant, so the one note nobody could file was the one that could double.

## Test evidence

    dotnet test Conductor.slnx --filter "DV4_1CourierTests|DV3_2InboxStoreTests|DV3_4|ArchitectureTests"
    Passed! - Failed: 0, Passed: 50, Skipped: 0

    dotnet test Conductor.slnx --filter "DV4_1|DV3_1|DV3_2|DV3_4|KS11_1|K7_2|SF7_1|ArchitectureTests|Completion|KS8_2|KS2_1|KS0_2"
    Passed! - Failed: 0, Passed: 243, Skipped: 0

18 new `[Fact]`/`[Theory]` attributes in `tests/Conductor.Tests/DV4_1CourierTests.cs`.

## Live proof — three real processes

`tools/dv4/dv4-1-live-proof.ps1`, log at `.conductor/evidence/DV4/dv4-1-live-proof.log`.
Scratch state home, scratch repo, scratch token, a stub Bot API on loopback that serves updates
by offset exactly as api.telegram.org does. Nothing dials Telegram; nothing touches this repo.

- **Process A** `courier run --once` → `2 received, 1 filed`; offset on disk `{"offset": 3}`
- **Process B** `courier run --once` → `0 received` — the offset survived the process boundary
- the offset is put back to `0`, which is the state a kill between receive and acknowledge leaves
- **Process C** → `2 received, 0 filed, 1 already filed`; inbox: **1 note, 1 index line, 1 media file**

## Two defects the live proof found that the in-process test did not

Both were real, both are fixed, and both now have a test:

1. **An orphan media file on every replay.** The courier adopted the note's audio into the project
   inbox *before* `Append` could refuse the duplicate, so the replay left a second copy
   (`2-voice.oga` **and** `2-voice-2.oga`) that no note referenced — and `inbox prune` deletes the
   files a note *names*, so nothing would ever have removed it. Fixed with `InboxStore.Has(id)`
   short-circuiting before `AdoptMedia`, plus `CourierDaemon.Discard` for the narrow race after it.
   The kill test now asserts the media directory, not just the note and index counts.
2. **The offset file was PascalCase while `courier.json` beside it is camelCase**, and
   System.Text.Json matches property names case-sensitively — so a hand-edited `{"offset": 400}`
   deserialised to 0 with no error at all. Reading 0 replays rather than skips, so it was the safe
   direction of a silent failure, which is still a silent failure. Fixed and pinned.

## Seam and ratchet notes

- `TelegramMediaFetcher.cs` and `TelegramCourierSource.cs` are declared in
  `KS11_1SeamBoundaryTests.AdapterFiles`. Nothing under `src/Conductor.Core/Courier/` names a
  Telegram type in code — the 409 crosses the seam as `CourierConflictException`.
- `Program.cs` sat **on** CA1505's floor: the registration alone made MI exactly 20 and the rule
  needs above 20. Fixed as `VerbRewrites.cs`'s own remarks prescribe — `HubWhenBare` and
  `RunRecordVerbs` moved out of Program's local functions into that type. The SF7.1 pins got
  **stronger**: both now run the engine's own rewrite by reflection instead of mirroring one and
  regex-matching the other's source.
- Architecture baseline untouched: every new file is under the 500-line and 3-type ceilings.

## Not in this checkpoint

`courier install|uninstall|restart` as a Scheduled Task is DV4.2; the loopback seam and
`CourierChannel` are DV4.3; promotion by button is DV4.4. `courier run` is a foreground process
today, which is what DV4.2 wraps.
