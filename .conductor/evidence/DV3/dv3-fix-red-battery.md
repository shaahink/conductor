# DV3 — the red battery after session 7, and what was actually wrong

Session 8 (fix). Conductor ran `engine-full` independently after session 7 and it came back RED:
**7 failed, 3261 passed**, and it failed TWICE — the first attempt and the SC4.1 retry. Nothing here
was flake; every one of the seven was a real assertion about a real thing.

Session 7's handoff said `red: none known. Full battery not run by this session`. That is how seven
reds shipped: four of the seven are checks that only the FULL suite runs.

## The seven, and the four causes under them

| # | Test | Cause |
|---|---|---|
| 1 | `DV3_2InboxStoreTests.Concurrent_writers_neither_lose_a_note_nor_duplicate_one` | the index wrote over itself |
| 2 | `ArchitectureTests.NoFileGrowsPastItsTypeCeilingOrItsRecordedDebt` | `Transcriber.cs` 4 types, `NoteRouter.cs` 4 types (allowed 3) |
| 3 | `ArchitectureTests.NoFileGrowsPastItsLineCeilingOrItsRecordedDebt` | `TelegramService.cs` 504 lines (ceiling 500) |
| 4 | `SF7_1…PlanConfigDocDocumentsEveryKeyThePlanSchemaDeclares` | `courier.transcribe` has a section, no row |
| 5 | `SF7_1…DeletingOneDocumentedRowMakesTheDerivationNameThatExactKey` | same key |
| 6 | `SF7_1…TheOperatingGuideFullCommandReferenceNamesEveryShippedVerb` | `inbox` not in operating.md §2 |
| 7 | `B11_2…Completion_ContainsAllRegisteredVerbs_Exhaustive` | `inbox` not in the completion verb string |

## 1. The defect: an append-mode stream is not an append

`InboxStore.AppendIndexLine` opened the index with `FileMode.Append` and `FileShare.ReadWrite`.

A .NET `FileStream` in append mode resolves the end of the file **when the handle opens**, and
advances that position only by its OWN writes. `FileShare.ReadWrite` then lets a second writer open a
second handle — at the same length. The two do not write after each other. They write **over** each
other, and what lands on disk is one JSON line spliced through the middle of another:

```
Collection: [···, 1, 2, null, 3, 4, ···]      ← the null is a line that is no longer JSON
```

This is worse than the failure the store was designed around. `All()` exists precisely to repair a
**missing** index line — the crash window between the atomic rename and the append is a known,
handled state. It can do nothing at all with a **corrupt** one.

**Fix.** The writer takes the file: `FileShare.Read` admits readers and refuses other writers, so the
losing writer retries (12 attempts, 2ms–24ms backoff) instead of interleaving. The append itself is a
handle open and one small write, so the window it holds is microseconds. And the reader gives the
file back: `IndexedIds` used `File.ReadLines`, which asks for `FileShare.Read` and would have locked
every appender out for the length of the scan — it now opens `FileShare.ReadWrite` through
`ReadLinesShared`, so a reader never costs a writer a line.

Both helpers are `public static` and must stay so: MA0045 is an **error** in this repo for blocking
IO in a private method, and this store is synchronous all the way up by design (the `IPromptBattery`
seam above it is a property, so an async store would only push a sync-over-async wait one layer out).

Evidence it is causal, not timing luck: the gate failed on both the first attempt and the retry —
100% — and the test now passes 8/8 consecutive runs:

```
run 1..8 : Passed!  - Failed: 0, Passed: 10, Total: 10   (≈290 ms each)
FAILURES: 0 / 8
```

## 2–3. The ratchets: split, never baselined

DV3.3 and DV3.4 grew three files past ceilings the ratchet has held for eras. **No baseline entry was
added** — a recorded debt is for a god class that predates the rule, not for a file that grew today.

- `TranscriptionOutcome.cs` — `TranscriptionStatus` + `TranscriptionOutcome`, the shape a CALLER
  handles, apart from the thing that makes one (`ITranscriber`, `LocalCommandTranscriber`).
- `DeadLetterBox.cs` — the box, out of `NoteRouter.cs`.
- `TelegramService.TestConnection.cs` — the Test button's leg, 110 lines. `TelegramService.cs` 504 → 394.
  Declared in `KS11_1SeamBoundaryTests.AdapterFiles` alongside the other six partials: the seam rule is
  "only the declared adapter names a Telegram type", and this file **is** the adapter.

## 4. A section is not a row

`courier` had a whole documented section in `docs/plan-config.md` — the jsonc block, the stdout
contract, the confidence marks. It still failed, and the derivation was right to fail it:
`SF7_1…HasRow` reads the **first cell of a markdown table row**, and a section only ever satisfies a
ROOT key. A block's children need rows. Added one for `transcribe`.

## 5. A verb ships in three places

`inbox` was registered in `Program.cs` and reached neither list that pins a verb: the completion verb
string and operating.md's "Full command reference". Both are now filled — which is exactly what those
two tests exist to force, since a verb an agent's own reference page does not name is a capability
nobody can be told about.

## Also found, filed, not fixed

**Bug #75 (high)** — `conductor note` stores only the FIRST LINE of a multi-line note. Ledger entries
465, 466 and 467 (session 7, DV3) each survive as their header alone: *"DV3.4 ACCEPTANCE, declared
before editing. Done means:"* and nothing after it. The acceptance criteria a fix session was meant to
inherit are gone. Single-line notes from the same era store whole. Until it is fixed: **write every
`conductor note` as one line.**

## The battery, run by this session

Two full runs, both through `conductor bg` (trap 17), both `dotnet test Conductor.slnx`:

```
after the source fixes   Failed: 1, Passed: 3267, Total: 3268   (3 m 52 s)
                         └─ KS11_1SeamBoundaryTests.Only_the_declared_adapter_files_name_a_telegram_type
                            the new TelegramService partial was not yet in AdapterFiles

after declaring it       Failed: 0, Passed: 3268, Total: 3268   (4 m 5 s)   ← .conductor/evidence/DV3/dv3-fix-engine-full-green.log
```

Total count is 3268 in both the red battery and the green one: **no test was added, deleted, skipped
or relaxed to get here.** The measurement that caught the index defect was already correct — it was
the code that was wrong. `face-full` is untouched: this session changed no Go source.
