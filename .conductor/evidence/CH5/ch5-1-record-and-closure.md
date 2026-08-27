# CH5.1 — the internal record, the closure ledger, and this era's own budget

**Measured 2026-08-27, session 8, on `feat/charkh`.** Three deliverables, each derived from something
a reader can re-run. Nothing in this file is read off a doc comment.

---

## 1. The record — ARCHITECTURE.md and docs/dev reconciled

`ARCHITECTURE.md` had not been touched by this era at all: `git diff --stat master..feat/charkh`
listed 68 files and it was not among them.

### What was added

| Where | What |
|---|---|
| `ARCHITECTURE.md` §"What Charkh added, and where it lives" | seven-row surface table — `Core/Release/` (10 files), the four `Ci*` files, `GithubIdentity.OwnerMarker`, `tools/ch3/`, the demo manifest — plus the paragraph on why `ci-status.json` bends DV1.1's "derived, never stored" rule on purpose |
| `ARCHITECTURE.md` §The seams | re-counted at CH5.1: **still thirteen** `public interface I*`. Charkh added two areas and no seam |
| `ARCHITECTURE.md` §Where do I add X | two rows — an era-close precondition or act, and an observation about the outside world |
| `ARCHITECTURE.md` §The seams, GitHub bullet | `github ci` named as the third GitHub read and the first that is not about a write |
| `docs/dev/adr/0005` | third addendum: the decision stands because the observation never becomes run state |
| `docs/dev/README.md` | "No era is open" replaced by Charkh as the design authority; the ARCHITECTURE row re-dated to CH5.1; a row for `CHARKH-PLAN-2026-08-26.md` |

### The measurements behind it

```
$ grep -rn "public interface I" src/Conductor.Core --include=*.cs | wc -l
13
   IAgentProvider ICloudCli ICourierSource IEventSink IMessageChannel IPlanner
   IProgressProvider IProgressSink IPromptBattery IReportsStartOutcome IRunNotifier
   IRunStore ITranscriber
$ grep -rn "#pragma\s\+warning\s\+disable" src --include=*.cs | wc -l
31
$ grep -rn "^\s*\[Fact\|^\s*\[Theory" tests --include=*.cs | wc -l
3044
$ cat tools/gates/ratchet-baseline.json      ->  minTests 1932 · maxPragmas 31
```

**Finding 1 — the architecture doc reported a green gate as red, for two eras.**
`ARCHITECTURE.md:552` read *"`maxPragmas` **38** (only ever falls, and is red at 43 today — bug #44,
an owner decision)"*. Measured: the ceiling is **31**, the live count is **31** (at the ceiling, not
above it), and **bug #44 is `fixed`**. KS6.2 ratcheted 38 → 31 by proving 14 of the 45 disables dead
and the doc never followed. Corrected in place with the measurement quoted.

`minTests` **1932** is correct as a floor but the suite carries **3044** — 1112 above it. Stated in
the doc rather than silently raised: raising a gate floor is a gate change and was not this
checkpoint's ask.

**Finding 2 — a pin asserted the opposite of its own intent.**
`SF7_1DocsMatchRealityTests.TheContributorIndexNamesNoDesignAuthorityWhileNoEraIsOpen` required the
literal sentence "No era is open" in `docs/dev/README.md`. That sentence stopped being true the
moment Charkh launched, so the test's *intent* (the index must not point a reader at the wrong design
authority) and its *assertion* had come apart. It is a property now —
`TheContributorIndexClaimsTheDesignAuthorityTheTreeActuallyHas`: an era brief is a `docs/dev/` file
matching `^[A-Z][A-Z0-9]*-PLAN-\d{4}-\d{2}-\d{2}\.md$`, its presence **is** the fact, and each branch
asserts the claim that state demands. The prefix is one word on purpose:
`NEXT-ERA-VERIFIED-PLAN-2026-08-07.md` is a research note, not a brief. The doc-move act inside
`release perform` is what carries a brief to `history/`, so the test flips back on its own when CH5.2
runs.

This is the CH5 spec's own decision applied to itself: *a property test beats an example test where
the vocabulary moves.*

---

## 2. The closure ledger — `.conductor/followups.md`, "Charkh closure ledger — 2026-08-27 (CH5.1)"

**Bugs.** 43 rows, every one from `run.db` via MCP `run_query`, not from a document. Charkh filed
nine (#84–#92) and fixed three (#84 CH4.3 `f4022f6`, #85 CH1.3 `349a3a5`, #89 CH4). The other forty
open bugs each carry a living owner; three (#41, #47, #83) are owned in `shaahink/payesh`, a repo no
stage here touches.

**Followups.** 91 rows describe **57 distinct ids** (bug #81, still open), so the census resolves each
id by its **last** row. 53 read closed or retired; four were living. Nothing in the file had been
touched between DV7.3 and CH5.1.

**Finding 3 — FU-OWNER-14 stopped being re-homed.** The reinstall row was moved four times —
SF7.2 → K7.2 → KS10.3 → KS12.3 — and its owner is now a closed stage, which is exactly the failure
this file's own 2026-07-28 triage names: *a row pointing at a stage that will never open again is a
row nobody will ever clear.* It closes here because CH4.2 (`c0dcad5`) made `reinstall` one of five
named owner acts in `ReleasePerform.OwnerOrder` (`:40`), printed with its command on every
`release perform` and rendered by `release runbook`. The remainder is stated: the act itself is owed
at CH5.2 and is still the owner's.

**And two rows recorded rather than acted on.** Bug **#60** ("analyzer ratchet RED, pragma-src 33
against a bar of 31") measures **green** today — left open on purpose, because closing it from a
measurement alone names no fix. Bug **#61** (`CONDUCTOR_RUN_DB` does not redirect the measuring
verbs) is what CH5.1's own budget measurement had to work around.

**The pin.** `EveryBugInTheCharkhClosureLedgerNamesAnOwnerExactlyOnce` holds two things: every bug row
names an owner, and **each id appears exactly once** — bug #81's defect applied to the bug half of
the file. The row count is deliberately not pinned to a literal; that is how the "No era is open"
assertion rotted.

---

## 3. The budget — re-measured through a fresh build, against a backup copy

Trap 18's precondition checked first: `MigrationRunner.CurrentVersion` is **15** (`MigrationRunner.cs:11`)
and the store's `schema_version` reads **15**. The live file was never opened by the fresh build:

```
$ sqlite3 "<stateHome>/runs/conductor-karvansara-core---the-open-door-308cfb9b/run.db" \
      ".backup 'C:/Users/shahi/AppData/Local/Temp/ch51/run-backup.db'"
$ dotnet run --project src/Conductor --no-build -- budget <backup> [--json]
$ dotnet run --project src/Conductor --no-build -- money  <backup>
```

The path is passed **positionally** because `CONDUCTOR_RUN_DB` does not redirect these verbs
(bug #61). Raw output committed beside this file: `ch5-1-budget-fresh-build.txt`,
`ch5-1-budget-fresh-build.json`, `ch5-1-money-fresh-build.txt`.

| | |
|---|---|
| window | sessions 1–7, cap **42M**, nudge **37.95M** (0.9036) |
| costed sessions | **6** of 7 |
| tokens · cost | **158.95M** · **$112.79** (cap 300) |
| checkpoints | **11** counted of **12** DONE |
| tokens/checkpoint | **14.45M** (Divan: 18.84M — down 23%) |
| floor · median closer · largest | 7.13M · 30.34M · 39.77M |
| rollovers | **0** |
| nudged / ended clean | **3 / 3** |
| wrap-up | 0.80M · **1.20M** · 1.81M (n=3) |
| findings | **none** — `prescription.findings` is `[]` |
| **prescription** | **45M / 0.95** (nudge 42.75M, headroom 2.25M) |

Written into `docs/dev/TOKEN-BUDGET-TUNING.md` **§14** as the number the next era compiles against,
with the caution that the headroom clears the *median* wrap-up 1.9× but only the *largest* observed
one 1.24× — so if the next era's first session is killed at the ceiling, the ratio moves before the
cap.

**Finding 4 — a `TimedOut` session records no cost rows at all (bug #92, filed here, high).**
Session 6 ran **5 h 36 m** across **140 turns**, landed three commits, and
`SELECT … FROM costs WHERE run_id = '858b…' AND session_number = 6` returns **nothing**. The
checkpoint it claimed (CH4.3) is missing from the count too — `money` reports CH4 as 3 checkpoints
where 4 are DONE. So the prescription above is derived from **6 of 7 sessions and 11 of 12
checkpoints**: numerator and denominator are both short, in the same direction. §14 says so in its
first subsection rather than in a footnote, because every number under it inherits the caveat.

**Finding 5 — the rail converted, and the verb had nothing to complain about.** Three sessions were
nudged and three ended clean; zero rollovers; `findings` empty. §13 had to spend a paragraph
correcting the verb's own "THE RAIL IS DELIVERED AND IGNORED" text for Divan. Here there is no killed
set to describe.

**The pin.** `TheCharkhBudgetSectionAgreesWithTheRawOutputItQuotes` derives §14's cap and ratio from
`ch5-1-budget-fresh-build.json` rather than restating them, checks the pair appears in the **heading**
a reader skims and in the copyable JSON block, and asserts the cap the window measured is still the
cap `plans/charkh/core.plan.json` declares — a window is only evidence for a prescription if the cap
it measured was the cap in force.

---

## Gates

`dotnet build Conductor.slnx` — **0 errors, 0 warnings**.
`dotnet test --filter FullyQualifiedName~SF7_1DocsMatchReality` — **45/45**, up from 43 (two pins
added, one converted from a literal to a property).

## Commits

- `77b9547` docs(CH5.1): the record says what the engine does, measured not repeated
- `980ccfc` docs(CH5.1): the closure ledger, and the row that stopped being re-homed
- this commit — §14 and its pin
