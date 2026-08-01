# SF7.1 — the field-notes closure ledger

Session 39, 2026-08-01. The last outstanding part of SF7.1: spec line 492, "the three field-notes
files gain a closure ledger (finding -> stage that fixed it -> commit)".

## What landed

Each of the three logs gained a `## Closure ledger` section — 31 rows, one per numbered finding:

| File | Findings | Rows |
|---|---|---|
| `docs/dev/FIELD-NOTES-2026-07-29-devcontext.md` | 20 | 20 |
| `docs/dev/FIELD-NOTES-2026-07-29-sk-platform.md` | 7 | 7 |
| `docs/dev/FIELD-NOTES-2026-07-30-sk-fleet-round-four.md` | 4 | 4 |

Each row carries finding -> stage -> commit sha -> one line on what actually closed it.

## How the map was measured, and why not from Appendix B

The era spec's Appendix B already carried a finding -> stage index, but it was written BEFORE the
work. The map here was derived instead from the commits, several of which cite their finding number
in their own body. Scanning every `master..HEAD` commit body for finding references:

```
8dd1aa3 :: devcontext #9
6d805e1 :: devcontext #10
33d1f81 :: devcontext #10 and #11
5c357b2 :: devcontext #20
17f5627 :: devcontext #20
04e092a :: devcontext #14 ; devcontext #20
58bf293 :: round-four #4
e6b15c7 :: devcontext #16
ac70123 :: sk-platform #1
49451ed :: sk #3
cfdb1ad :: devcontext #15
c3e0813 :: sk #3
1ce4ba7 :: sk #3 ; devcontext #14
ba9b523 :: devcontext #12
3645780 :: devcontext #18 and #19 ; sk-platform #2
```

The remaining sixteen findings were matched by reading the candidate stage's commit body against the
finding text — e.g. round-four #3 ("`conductor log` cannot read its own log") lands on `87d7fcd`,
whose body says "`conductor log` and `conductor bg logs` could not read a LIVE log. Both asked for
`FileShare.Read`, which on Windows does not permit the writer's Write handle."

### Three corrections to Appendix B, and one correction to a prior session's note

1. **sk-platform #3 had two halves; the index named one.** Appendix B mapped it to SC4.2 alone.
   `c3e0813` (SC4.3) also says "sk #3 verbatim" — SC4.2 made a claim count as progress, SC4.3 made
   a commit in a declared satellite repo count. Both were needed.
2. **devcontext #10 and #11.** Appendix B had `#10 -> SC7` (no checkpoint) and `#11 -> SC7.1`.
   `33d1f81` (SC7.1) answers both by name; `6d805e1` (SC7.2) is #10's display half. #11's actual
   delivery is `SessionRunner.NoteOutsideRepoWrite` (`src/Conductor/Core/Orchestration/SessionRunner.cs:461`),
   and `git log -S NoteOutsideRepoWrite -- src/` returns exactly one commit: `33d1f81`.
3. **devcontext #14** was mapped to bare `SC6`; it is SC6.1 `04e092a` plus SC4.2 `1ce4ba7`.

**Correction to ledger note 191 (session 38):** that note warned that Appendix B's `#5 -> SC5.2` was
wrong and that #5's real answer was only SF6.1. It is not wrong. `SessionWatchdog.cs:47-58` carries a
`const string Remedy` whose doc comment opens "SC5.2:" and whose text is finding #5's suggestion (a)
verbatim, plus (b) "a live bg child counts as proof of life";
`git log -S Remedy -- src/Conductor/Core/SessionWatchdog.cs` returns `e6b15c7`. #5 closes at SC5.2
(engine half) AND SF6.1 (prompt half). The warning was not laundered into the ledger.

## One finding closed with a stated remainder

devcontext #19 made two suggestions. SC2.2 `603fbbb` took the first — `RunState.NextAttemptNumber`
is now the single source both log lines read. The second ("consider making a phase-gate RED's repair
session not consume the stage's delivery budget") was considered and NOT adopted. The row says so,
and `TheOneHalfClosedFindingStillSaysWhatWasNotDone` pins that it keeps saying so.

## The pins, and proof they bite

`tests/Conductor.Tests/SF7_1DocsMatchRealityTests.FieldNotes.cs` — 4 new tests:

- `EveryFieldNoteFindingHasExactlyOneClosureRow` — every numbered finding has exactly one row; no
  row cites a finding that does not exist; no finding has two rows.
- `EveryCommitTheClosureLedgersCiteExists` — every sha in every ledger passes
  `git cat-file -e <sha>^{commit}` in this checkout.
- `TheOneHalfClosedFindingStillSaysWhatWasNotDone` — the #19 remainder survives edits.
- `TheEraSpecIndexDefersToTheMeasuredLedger` — Appendix B keeps pointing at the ledgers and keeps
  the two corrected mappings.

A green suite proves nothing about a pin, so both structural pins were driven red on purpose. Row 3
was deleted from the round-four ledger and `58bf293` was replaced with `deadbee`:

```
Failed  EveryCommitTheClosureLedgersCiteExists [2 s]
   1 commit(s) cited by a closure ledger do not exist in this history:
   FIELD-NOTES-2026-07-30-sk-fleet-round-four.md:deadbee

Failed  EveryFieldNoteFindingHasExactlyOneClosureRow [30 ms]
   docs/dev/FIELD-NOTES-2026-07-30-sk-fleet-round-four.md: finding(s) 3 have no closure-ledger row.
   Say which stage closed it and with which commit, or say it is still open and name an owner.
```

The mutation was reverted by hand (not `git checkout`, which would have taken the whole new ledger
with it) and the file re-verified.

## Gate

```
dotnet build Conductor.slnx -clp:ErrorsOnly   ->  Build succeeded. 0 Warning(s) 0 Error(s)
dotnet test  --filter FullyQualifiedName~SF7_1DocsMatchRealityTests
             ->  Passed! Failed: 0, Passed: 16, Skipped: 0, Total: 16
```

16 = the 12 from sessions 37/38 plus these 4. No test was weakened, skipped or deleted.
