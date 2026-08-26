# DV7.2 — the published surface, and the bar that now holds it

Session #21, 2026-08-26. **Delivered except the payesh harvest**, which is named as the remainder
below rather than dropped.

## 1. What was measured before anything was written

Divan vocabulary across the published docs, counted (`grep -ci`) before the checkpoint:

| file | courier | inbox | cloud | sarif |
|---|---|---|---|---|
| README.md | **0** | **0** | **0** | **0** |
| docs/README.md | **0** | **0** | **0** | **0** |
| docs/cli.md | 26 | 9 | **0** | 7 |
| docs/operating.md | 2 | 1 | **0** | **0** |
| docs/plan-config.md | 4 | 2 | 4 | 0 (n/a) |
| docs/quickstart.md | **0** | **0** | **0** | **0** |
| docs/troubleshooting.md | **0** | **0** | **0** | **0** |
| CHANGELOG.md | **0** | 1 | **0** | **0** |

`docs/plan-config.md` was the one page already correct: its `courier` and `cloud` sections match the
source exactly — 900 s and 0.45 against `CourierConfig.cs:32` and `Transcript.cs:52`, `enabled` false
/ `timeoutMinutes` 30 range 1–240 against `CloudLaneConfig.cs`. Nothing to fix, and it is the only
page a derived test (`PlanKeys.cs`, off `PlanKeySchema`) was already forcing.

## 2. What was written

- **README.md** — "While you're away" gains the courier as three commands and what it buys, the
  never-committed rule, the 24-hour Telegram limit, `/cloud` on the chat surface with the create/
  follow-up split, ledger issues + `--project` + `sarif` + `board.html`. Also one stale claim fixed:
  it pointed at `EDGE-TRACKER.md` as the live tracker.
- **docs/README.md** — index rows for the courier and for the two new plan blocks.
- **docs/cli.md** — `inbox parked` (absent), and a `## The cloud` section (absent) stating that there
  is deliberately no `conductor cloud` verb, why the create direction is refused, and the six git
  preflight verdicts.
- **docs/operating.md** — the courier **lifecycle** row (`install`/`restart`/`stop`/`uninstall`, all
  four absent from the page an agent is handed), a setup workflow, the owner's side of the inbox, and
  `/cloud` on the profile surface.
- **docs/troubleshooting.md** — the inbox and courier state files in "where the truth lives", and a
  nine-row table of the failure modes a daemon that outlives every run actually produces.
- **docs/quickstart.md** — §14 "…you think of something while the run is not running".
- **CHANGELOG.md** — Divan's entries added to the existing `## [Unreleased]` (9 Added, 3 Changed,
  9 Fixed). **The edge era's ~23 entries were not touched, moved or re-homed**: the split-or-single
  release call belongs to the owner, and KS12.3 has not shipped.

## 3. Two contradictions and one over-claim, corrected

Found by reading the engine rather than the neighbouring page:

1. `docs/operating.md` said **"`--project` refuses by design — see cli.md"** while `docs/cli.md`
   documented it as the working Projects v2 path DV6.2 landed. Two shipped pages, opposite answers.
2. The same row claimed re-running the backfill **"mints zero duplicates."** Bug **#79** measured the
   opposite inside GitHub's replica lag. The row now says what the engine does and names the bug.
3. `conductor github backfill` — an alias the engine accepts at `GithubCommand.cs:88` — appeared in
   neither page. Found by the new derivation, not by reading.

## 4. The bar: subverbs were structurally invisible, and are not now

`tests/Conductor.Tests/SF7_1DocsMatchRealityTests.Subverbs.cs`, three facts.

Every derivation this suite already had starts at `AddCommand<T>("verb")` in `Program.cs` — the verb
list (SC8.3, K7.2), the long options (KS12.2, reflected from the settings type). **A subverb is not
registered there.** `conductor courier install` is a string in a `switch` inside `CourierCommand`, so
the courier's whole lifecycle could be — and was — missing from `docs/operating.md` for the entire era
that shipped it while every docs test stayed green.

The new derivation source-scans the six command files that declare `[CommandArgument(0, "[VERB]")]`
for the two dispatch shapes this repo uses — a switch arm (`"install" =>`, `"" or "status" =>`) and a
guard clause (`verb is not ("sync" or "backfill" or "sarif")`). **29 pairs.** Aliases on a switch arm
(`"new" or "add" or "file"`) demand only the first spelling; a guard clause demands every alternative,
because there each one is a spelling the engine accepts.

Documented = one line carrying a code span that names the parent verb **and** a code span naming the
subverb as a whole token, both inside backticks. Chosen against how the pages are actually written —
some spell the pair out (`` `inbox parked` ``), some compress the family
(`` `start|status|logs|stop` ``, `` `plan new/set/reload/add-stage/import` ``) — so the bar is met by
good documentation rather than by one mandated shape.

```
$ dotnet test Conductor.slnx --filter "FullyQualifiedName~Docs"
Passed!  -  Failed: 0, Passed: 45, Skipped: 0, Total: 45
```

## 5. Proven RED on a seeded stale doc — the live negative control

Two rows were deleted from the real files on disk (`inbox parked` from `docs/cli.md`; the courier
lifecycle row from `docs/operating.md` §2) and the suite re-run. Raw output:
`.conductor/evidence/DV7/dv7-2-negative-control.txt`.

```
Failed  TheCliReferenceNamesEverySubverbACommandDispatchesOn
  docs/cli.md never names 1 subverb(s) the engine dispatches on: inbox parked

Failed  TheOperatorCommandReferenceNamesEverySubverbToo
  docs/operating.md section 2 never names 4 subverb(s):
  courier install, courier restart, courier stop, courier uninstall

Failed  RemovingOneDocumentedSubverbMakesTheDerivationNameThatExactPair
Failed!  -  Failed: 3, Passed: 0, Total: 3
```

Both forward facts name **exactly** the deleted rows and nothing else. The third failing test is the
in-memory negative control refusing to run against an already-stale page, which is correct: it asserts
the page is clean before it seeds anything. The files were restored from a copy taken before the seed
and the suite is 45/45 again — no test, ceiling or golden was touched.

## 6. The remainder, stated rather than dropped

**The payesh harvest re-run (`C:/code/conductor-site`, a branch + a PR, never main) was not done.**
It is the one part of DV7.2 that lives in a satellite repo and needs its own harvest run against the
run store; this session spent its budget on the anchor repo's surface and the bar that holds it.
Everything else in the checkpoint is delivered. The next session takes that one item.
