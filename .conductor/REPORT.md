# Conductor — Charkh - the wheel: what the owner still does by hand becomes machinery run report

_Updated 2026-08-27 01:45 UTC · branch `feat/charkh` · HEAD `252f3e9`_

**Status:** Idle
**Stage:** CH3 — The docs say what shipped · attempts used 0
**Checkpoints:** 8/14 done · **Sessions run:** 4 · **Cost:** $78.9523 (agent $78.9155 + gates $0.0368) · **Tokens:** 988,786 in / 528,211 out
**Confirmed phases:** CH1, CH2, CH3
**Channels:** telegram ready · github ready · courier ready

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| CH1 | CI green, and the reason it was not | ██████████ 3/3 | confirmed ✓ |
| CH2 | The tour that matches the engine - and knows when it does not | ██████████ 2/2 | confirmed ✓ |
| CH3 | The docs say what shipped | ██████████ 3/3 | confirmed ✓ |
| CH4 | The machinery - the era-close stops being prose | ░░░░░░░░░░ 0/4 | todo |
| CH5 | Ship Charkh with the machinery it built | ░░░░░░░░░░ 0/2 | todo |

<details> ✅<summary>CH1 — CI green, and the reason it was not (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| CH1.1 | The rendered board page is one document whatever the checkout did to the source: the inline CSS constant is normalised to LF at load, and a test asserts the PROPERTY (Render output carries no carriage return) rather than the symptom, so the next raw string literal in that file cannot reintroduce it silently | ✅ DONE | [`1232ea0`](https://github.com/shaahink/conductor/commit/1232ea0) |
| CH1.2 | A plan file in this repo is loadable on a fresh clone: the three KS1_4DoctorPlanLintsTests that load this repo's own plan and Validate it stop depending on an absolute machine path, by whichever of the two routes the checkpoint records as chosen, pinned by a test that would fail on the old form | ✅ DONE | [`1232ea0`](https://github.com/shaahink/conductor/commit/1232ea0) |
| CH1.3 | The local battery and CI can no longer disagree in silence: a divergence between what a run's gates just proved and what CI says about the same commit surfaces where the run can see it - the report header, the owner queue - in the DV1.1 channel-health shape, proven on a seeded divergence. Exit is CI green on Windows and Linux for master | ✅ DONE | [`3750f9a`](https://github.com/shaahink/conductor/commit/3750f9a) |

</details>

<details> ✅<summary>CH2 — The tour that matches the engine - and knows when it does not (2/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| CH2.1 | docs/assets/demo.gif re-recorded against the v0.5.0 Face through the VHS container at the covered 1176x736 geometry, with the tape extended to the surfaces the last two eras added (the courier, the inbox, the board page, the hub); Docker verified FIRST with exact output, and if it cannot be made to work that is filed with the output rather than worked around | ✅ DONE | [`13a5bfe`](https://github.com/shaahink/conductor/commit/13a5bfe) |
| CH2.2 | Staleness becomes a gate rather than a thing somebody notices: a manifest of what the GIF was recorded from and a check that fails when the product has moved past it - payesh's social-card pattern ported, which is why payesh's cards were caught and conductor's GIF was not | ✅ DONE | [`13a5bfe`](https://github.com/shaahink/conductor/commit/13a5bfe) |

</details>

<details> ✅<summary>CH3 — The docs say what shipped (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| CH3.1 | The published surface reconciled against the INSTALLED v0.5.0 engine rather than against intent: README first, then cli.md, operating.md, plan-config.md, quickstart.md, troubleshooting.md, tracker.md and the docs/README.md index; the courier is a real always-on process now and the docs still offer it as a possibility | ✅ DONE | [`ea75bda`](https://github.com/shaahink/conductor/commit/ea75bda) |
| CH3.2 | Every reference resolves: the rule for the plans' notes prose citing the two moved briefs decided once, applied consistently and recorded in docs/dev/README.md; every relative link in docs/, every path in a test message and every contracts reference swept. Frozen run artifacts under .conductor are a record - reported, never rewritten | ✅ DONE | [`ea75bda`](https://github.com/shaahink/conductor/commit/ea75bda) |
| CH3.3 | SF7_1DocsMatchRealityTests extended to every verb and config key this era adds, each new assertion proven RED on a seeded stale doc - the negative control is the point of the battery | ✅ DONE | [`ea75bda`](https://github.com/shaahink/conductor/commit/ea75bda) |

</details>

<details><summary>CH4 — The machinery - the era-close stops being prose (0/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| CH4.1 | Release preflight as a verb: every precondition DV7.3 measured by hand becomes something the engine measures - ff-only merge, a CHANGELOG section the extractor exits 0 on, no live conductor process, migration versions matching, the courier's token scope and task state, the run whose backfill is owed - as a verdict per line with a non-zero exit when any line is red | ⬜ TODO | - |
| CH4.2 | The mechanical acts performed and the judgement acts refused BY NAME: the CHANGELOG rename, the tag, the ff-only merge and the doc move with its tracker/planDoc/readOrder repoint are performed; the version number, single-vs-split release and corpus inclusion are stopped at and named. An act that needs the owner is never silently skipped - that failure is exactly what KS12.3 was | ⬜ TODO | - |
| CH4.3 | A backfill can no longer vandalise another run's board: the retire sweep is scoped to the run being synced, or a backfill that would retire another run's checkpoints is refused with what it would have closed. Measured 2026-08-26: the edge run's dry run reported 23 retired against exactly Divan's 23 checkpoints. Then the edge run's own GitHub record is written | ⬜ TODO | - |
| CH4.4 | The owner runbook becomes the preflight's output rather than a document written from scratch each era, generated from its own measurements and carrying the exact commands - the DV7.3 and KS12.3 artifacts are the shape being replaced | ⬜ TODO | - |

</details>

<details><summary>CH5 — Ship Charkh with the machinery it built (0/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| CH5.1 | The internal record: ARCHITECTURE.md and docs/dev reconciled for everything Charkh changed, a closure ledger naming every bug and followup closed this era or its living owner, and this run's budget re-measured through a fresh build against a sqlite3 BACKUP COPY of the store and written into TOKEN-BUDGET-TUNING as the number the next era compiles against | ⬜ TODO | - |
| CH5.2 | The era closed USING CH4's machinery rather than by hand: the preflight run, the runbook it generated, the mechanical acts performed and the refused ones parked with the owner. Anything the machinery got wrong is recorded as a finding - that is worth more than the checkpoint | ⬜ TODO | - |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | CH1 | Deliver | 1 | 08-26 22:08 | 0:59 | Advanced | CH1.1 CH1.2 | 5 | engine-fast:OK · face-fast:OK | $27.1701 | $0.0119 | 310,589/176,545 |
| 2 | CH1 | Deliver | 1 | 08-26 23:09 | 0:59 | Advanced | CH1.3 | 4 | engine-fast:OK · face-fast:OK | $15.4400 | $0.0091 | 205,846/101,976 |
| 3 | CH2 | Deliver | 1 | 08-27 00:16 | 0:24 | Advanced | CH2.1 CH2.2 | 3 | engine-fast:OK · face-fast:OK | $9.3501 | $0.0070 | 151,249/73,219 |
| 4 | CH3 | Deliver | 1 | 08-27 00:51 | 0:47 | Advanced | CH3.1 CH3.2 CH3.3 | 4 | engine-fast:OK · face-fast:OK | $26.9553 | $0.0088 | 321,102/176,471 |

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 4 | 113.1M | 98.7% | $78.95 | 8 | 14.1M | $9.87 |
| stage CH1 | 2 | 61.7M | 98.7% | $42.63 | 3 | 20.6M | $14.21 |
| stage CH2 | 1 | 12.2M | 98.2% | $9.36 | 2 | 6.11M | $4.68 |
| stage CH3 | 1 | 39.1M | 98.7% | $26.96 | 3 | 13M | $8.99 |
| 2026-08 | 4 | 113.1M | 98.7% | $78.95 | 8 | 14.1M | $9.87 |

_Where the money goes: agent $78.92 (100%) · gate $0.04 (0%) · blended $0.70/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-27 00:09:42  ▪ gate engine-fast pass [session]  (1m27s)
08-27 00:09:42  ▪ gate face-fast pass [session]  (31.7s)
08-27 00:09:43  • session #1 CH1 → Advanced · done CH1.1,CH1.2 · 5 commit(s)  (1h01m24s)
08-27 00:09:44  • session #2 CH1 Deliver started (attempt 1/6)
08-27 01:11:01  ▪ gate engine-fast pass [session]  (1m08s)
08-27 01:11:01  ▪ gate face-fast pass [session]  (22.3s)
08-27 01:11:01  • session #2 CH1 → Advanced · done CH1.3 · 4 commit(s)  (1h01m17s)
08-27 01:16:01  ▪ gate engine-fast pass [phase]  (0.0s)
08-27 01:16:01  ▪ gate face-fast pass [phase]  (0.0s)
08-27 01:16:01  ▪ gate engine-full pass [phase]  (4m53s)
08-27 01:16:01  ▪ gate face-full pass [phase]  (3.2s)
08-27 01:16:01  ✓ checkpoint CH1.1 confirmed
08-27 01:16:01  ✓ checkpoint CH1.2 confirmed
08-27 01:16:01  ✓ checkpoint CH1.3 confirmed
08-27 01:16:01  ▸ stage CH1 confirmed  (2h07m43s)
08-27 01:16:02  ▸ stage CH2 entered — The tour that matches the engine - and knows when it does not
08-27 01:16:02  • session #3 CH2 Deliver started (attempt 1/4)
08-27 01:41:23  ▪ gate engine-fast pass [session]  (1m06s)
08-27 01:41:23  ▪ gate face-fast pass [session]  (3.1s)
08-27 01:41:24  • session #3 CH2 → Advanced · done CH2.1,CH2.2 · 3 commit(s)  (25m21s)
08-27 01:51:44  ▪ gate engine-fast pass [phase]  (0.0s)
08-27 01:51:44  ▪ gate face-fast pass [phase]  (0.0s)
08-27 01:51:44  ▪ gate engine-full pass [phase]  (5m01s)
08-27 01:51:44  ▪ gate face-full pass [phase]  (23.2s)
08-27 01:51:45  ✓ checkpoint CH2.1 confirmed
08-27 01:51:45  ✓ checkpoint CH2.2 confirmed
08-27 01:51:45  ▸ stage CH2 confirmed  (35m42s)
08-27 01:51:45  ▸ stage CH3 entered — The docs say what shipped
08-27 01:51:45  • session #4 CH3 Deliver started (attempt 1/6)
08-27 02:40:38  ▪ gate engine-fast pass [session]  (1m05s)
08-27 02:40:38  ▪ gate face-fast pass [session]  (22.4s)
08-27 02:40:38  • session #4 CH3 → Advanced · done CH3.1,CH3.2,CH3.3 · 4 commit(s)  (48m53s)
08-27 02:45:19  ▪ gate engine-fast pass [phase]  (0.0s)
08-27 02:45:19  ▪ gate face-fast pass [phase]  (0.0s)
08-27 02:45:19  ▪ gate engine-full pass [phase]  (4m36s)
08-27 02:45:19  ▪ gate face-full pass [phase]  (2.4s)
08-27 02:45:20  ✓ checkpoint CH3.1 confirmed
08-27 02:45:20  ✓ checkpoint CH3.2 confirmed
08-27 02:45:20  ✓ checkpoint CH3.3 confirmed
08-27 02:45:20  ▸ stage CH3 confirmed  (53m34s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 4 · retries 0 (0 %) · overall Warn
⚠ [context-saturation] session #1: 39,286,951 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #2: 21,648,664 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #4: 38,650,669 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/charkh
working tree: M .conductor/REPORT.md, M plans/charkh/TRACKER.md
vs upstream: up to date
```

### Commits by session

- **s1 (CH1 Deliver)** — 5 commit(s):
  - [`151b429`](https://github.com/shaahink/conductor/commit/151b429) feat(CH1.3): what CI said about THIS commit, beside whether it ran the same battery
  - [`69eda91`](https://github.com/shaahink/conductor/commit/69eda91) fix(CH1.2): declare the split half as the config exception its other half is
  - [`aeea6e0`](https://github.com/shaahink/conductor/commit/aeea6e0) feat(CH1.3): the two batteries can no longer differ in silence
  - [`bd307ee`](https://github.com/shaahink/conductor/commit/bd307ee) fix(CH1.2): a plan in this repo loads on a fresh clone, not just this machine
  - [`1232ea0`](https://github.com/shaahink/conductor/commit/1232ea0) fix(CH1.1): the board page is one document whatever the checkout did
- **s2 (CH1 Deliver)** — 4 commit(s):
  - [`b4a2092`](https://github.com/shaahink/conductor/commit/b4a2092) docs(CH1.3): the handoff - CH1 is closed, CI is green on master
  - [`656a06e`](https://github.com/shaahink/conductor/commit/656a06e) chore(CH1.3): the exit, captured - CI green on windows and linux for master
  - [`349a3a5`](https://github.com/shaahink/conductor/commit/349a3a5) fix(CH1.3): a flush that says an event is durable now means it
  - [`3750f9a`](https://github.com/shaahink/conductor/commit/3750f9a) fix(CH1.3): the suppression ceiling was made, not moved
- **s3 (CH2 Deliver)** — 3 commit(s):
  - [`ac60b4c`](https://github.com/shaahink/conductor/commit/ac60b4c) docs(CH2): the handoff - CH2 is closed, docker was a stopped daemon
  - [`b373dee`](https://github.com/shaahink/conductor/commit/b373dee) feat(CH2.2): the GIF now fails the build when the product moves past it
  - [`13a5bfe`](https://github.com/shaahink/conductor/commit/13a5bfe) feat(CH2.1): the tour visits the courier, the inbox and the run switcher
- **s4 (CH3 Deliver)** — 4 commit(s):
  - [`252f3e9`](https://github.com/shaahink/conductor/commit/252f3e9) docs(CH3): the handoff - CH3 is closed, full suite 3513/3513
  - [`0fb578a`](https://github.com/shaahink/conductor/commit/0fb578a) test(CH3.3): the docs battery learns to see its own second assembly
  - [`2e280fa`](https://github.com/shaahink/conductor/commit/2e280fa) feat(CH3.2): a path is rewritten only when something still reads it
  - [`ea75bda`](https://github.com/shaahink/conductor/commit/ea75bda) feat(CH3.1): the docs are diffed against a binary, not read and agreed with

## Phase handovers (audit)

- `.conductor/handovers/B0.md`
- `.conductor/handovers/B1.md`
- `.conductor/handovers/B10.md`
- `.conductor/handovers/B11.md`
- `.conductor/handovers/B2.md`
- `.conductor/handovers/B3.md`
- `.conductor/handovers/B4.md`
- `.conductor/handovers/B5.md`
- `.conductor/handovers/B6.md`
- `.conductor/handovers/B7.md`
- `.conductor/handovers/B8.md`
- `.conductor/handovers/B9.md`
- `.conductor/handovers/F0.md`
- `.conductor/handovers/F1.md`
- `.conductor/handovers/F2.md`
- `.conductor/handovers/F4.md`

## Last gate run

engine-fast:cached · face-fast:cached · engine-full:OK · face-full:OK

## Last session result

> **CH3 closed — docs diffed against binaries and disk, three new pinned bars**
> - CH3.1: two-binary CLI diff found `report --query` (deleted at SF1.2, still offered), 13 flags on no verb's row, `github ci --branch` unreleased, and a GIF caption a day stale
> - CH3.2: the rule — a path is rewritten only if something still reads it; 3185 references swept, 431 in the record reported and untouched, where-it-went table derived into docs/dev/README.md
> - CH3.3: artifact scan widened past its own assembly (six undocumented artifacts), per-verb flag placement, README caption pinned to the manifest — each seen red on a seeded doc
>
> artefacts: ea75bda, 2e280fa, 0fb578a, 252f3e9, tools/ch3/{dump-help.ps1, docs-surface-diff.py, link-sweep.py, sweep-ignore.txt}
>
> evidence: .conductor/evidence/CH3/CH3.1-published-surface-reconciled.md, .conductor/evidence/CH3/CH3.2-references-resolve.md, .conductor/evidence/CH3/CH3.3-docs-battery-extended.md
>
> gaps: bug 87 (courier status says "running: yes" then tells you to start it) and bug 88 (CHANGELOG [Unreleased] still "Nothing yet" after CH1+CH2 — CH4.1 owns it) filed, not fixed. `docs-surface-diff.py` against the installed v0.5.0 exits 1 by design until `github ci` ships; CH5 must delete the "not in the released binary yet" phrase from cli.md and operating.md when it tags. `dotnet build` through the …

## Tracker handoff

```
last: CH3 IS CLOSED. CH3.1, CH3.2 and CH3.3 all DONE, evidence under .conductor/evidence/CH3/.
method: the docs were DIFFED against binaries, never read and agreed with. tools/ch3/ holds both
  sweeps and both are re-runnable: dump-help.ps1 + docs-surface-diff.py (the CLI surface, per verb,
  both directions) and link-sweep.py (3185 references, four kinds, two zones).
rule (CH3.2, now in docs/dev/README.md): a path is rewritten iff something still READS it. The
  record - closed eras, docs/history/, ci-health/, .conductor/, every ADR and finding - is REPORTED,
  never rewritten; 431 broken references were found there and none was touched. The bridge is the
  where-it-went table, derived by link-sweep.py --redirects.
found: report --query was deleted at SF1.2 and operating.md still offered it. 13 flags named on no
  row of the verb declaring them. SIX runtime artifacts undocumented because the scan only read
  src/Conductor. README's GIF caption still described the pre-CH2.1 tour. All fixed and pinned.
trap: dotnet build through the PowerShell tool resolves the repo as C:\Code\conductor and prints
  748 phantom analyzer errors; through the Bash tool it is clean. Build in Bash, or --no-build.
suite: full battery green - dotnet test Conductor.slnx, Passed 3513 / 3513, 3m59s.
next: CH4.1. Bugs 87 (courier status contradicts itself) and 88 (CHANGELOG [Unreleased] is empty
  after CH1+CH2; CH4.1 owns that precondition) are filed and open.
```
