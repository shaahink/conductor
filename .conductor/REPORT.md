# Conductor — Charkh - the wheel: what the owner still does by hand becomes machinery run report

_Updated 2026-08-27 09:20 UTC · branch `feat/charkh` · HEAD `8c19a1d`_

**Status:** Completed
**Stage:** CH5 — Ship Charkh with the machinery it built · attempts used 0
**Checkpoints:** 14/14 done · **Sessions run:** 9 · **Cost:** $129.1987 (agent $129.1204 + gates $0.0784) · **Tokens:** 2,087,759 in / 860,853 out
**Confirmed phases:** CH1, CH2, CH3, CH4, CH5
**Channels:** telegram ready · github ready · courier DEAD
**⚠ Channel DEAD — courier:** no courier is running on this machine. Start one: conductor courier restart · fix: `conductor courier restart`

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| CH1 | CI green, and the reason it was not | ██████████ 3/3 | confirmed ✓ |
| CH2 | The tour that matches the engine - and knows when it does not | ██████████ 2/2 | confirmed ✓ |
| CH3 | The docs say what shipped | ██████████ 3/3 | confirmed ✓ |
| CH4 | The machinery - the era-close stops being prose | ██████████ 4/4 | confirmed ✓ |
| CH5 | Ship Charkh with the machinery it built | ██████████ 2/2 | confirmed ✓ |

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

<details> ✅<summary>CH4 — The machinery - the era-close stops being prose (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| CH4.1 | Release preflight as a verb: every precondition DV7.3 measured by hand becomes something the engine measures - ff-only merge, a CHANGELOG section the extractor exits 0 on, no live conductor process, migration versions matching, the courier's token scope and task state, the run whose backfill is owed - as a verdict per line with a non-zero exit when any line is red | ✅ DONE | [`cf8997f`](https://github.com/shaahink/conductor/commit/cf8997f) |
| CH4.2 | The mechanical acts performed and the judgement acts refused BY NAME: the CHANGELOG rename, the tag, the ff-only merge and the doc move with its tracker/planDoc/readOrder repoint are performed; the version number, single-vs-split release and corpus inclusion are stopped at and named. An act that needs the owner is never silently skipped - that failure is exactly what KS12.3 was | ✅ DONE | [`cf8997f`](https://github.com/shaahink/conductor/commit/cf8997f) |
| CH4.3 | A backfill can no longer vandalise another run's board: the retire sweep is scoped to the run being synced, or a backfill that would retire another run's checkpoints is refused with what it would have closed. Measured 2026-08-26: the edge run's dry run reported 23 retired against exactly Divan's 23 checkpoints. Then the edge run's own GitHub record is written | ✅ DONE | - |
| CH4.4 | The owner runbook becomes the preflight's output rather than a document written from scratch each era, generated from its own measurements and carrying the exact commands - the DV7.3 and KS12.3 artifacts are the shape being replaced | ✅ DONE | [`35a4555`](https://github.com/shaahink/conductor/commit/35a4555) |

</details>

<details> ✅<summary>CH5 — Ship Charkh with the machinery it built (2/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| CH5.1 | The internal record: ARCHITECTURE.md and docs/dev reconciled for everything Charkh changed, a closure ledger naming every bug and followup closed this era or its living owner, and this run's budget re-measured through a fresh build against a sqlite3 BACKUP COPY of the store and written into TOKEN-BUDGET-TUNING as the number the next era compiles against | ✅ DONE | [`77b9547`](https://github.com/shaahink/conductor/commit/77b9547) |
| CH5.2 | The era closed USING CH4's machinery rather than by hand: the preflight run, the runbook it generated, the mechanical acts performed and the refused ones parked with the owner. Anything the machinery got wrong is recorded as a finding - that is worth more than the checkpoint | ✅ DONE | [`35043fe`](https://github.com/shaahink/conductor/commit/35043fe) |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | CH1 | Deliver | 1 | 08-26 22:08 | 0:59 | Advanced | CH1.1 CH1.2 | 5 | engine-fast:OK · face-fast:OK | $27.1701 | $0.0119 | 310,589/176,545 |
| 2 | CH1 | Deliver | 1 | 08-26 23:09 | 0:59 | Advanced | CH1.3 | 4 | engine-fast:OK · face-fast:OK | $15.4400 | $0.0091 | 205,846/101,976 |
| 3 | CH2 | Deliver | 1 | 08-27 00:16 | 0:24 | Advanced | CH2.1 CH2.2 | 3 | engine-fast:OK · face-fast:OK | $9.3501 | $0.0070 | 151,249/73,219 |
| 4 | CH3 | Deliver | 1 | 08-27 00:51 | 0:47 | Advanced | CH3.1 CH3.2 CH3.3 | 4 | engine-fast:OK · face-fast:OK | $26.9553 | $0.0088 | 321,102/176,471 |
| 5 | CH4 | Deliver | 1 | 08-27 01:45 | 0:47 | Advanced | CH4.1 CH4.2 | 4 | engine-fast:OK · face-fast:OK | $27.4686 | $0.0082 | 323,895/170,309 |
| 6 | CH4 | Deliver | 1 | 08-27 02:33 | 5:36 | TimedOut |  | 3 |  |  |  | 224,343/1,325 |
| 7 | CH4 | Resume | 2r1 | 08-27 08:10 | 0:07 | Advanced | CH4.4 | 3 | engine-fast:OK · face-fast:OK | $6.3494 | $0.0086 | 251,390/16,229 |
| 8 | CH5 | Deliver | 1 | 08-27 08:23 | 0:23 | Advanced | CH5.1 | 4 | engine-fast:OK · face-fast:OK | $10.9359 | $0.0146 | 188,430/88,826 |
| 9 | CH5 | Deliver | 1 | 08-27 08:49 | 0:17 | Advanced | CH5.2 | 3 | engine-fast:OK · face-fast:OK | $5.4510 | $0.0101 | 110,915/55,953 |

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 9 | 178.9M | 98.5% | $129.20 | 13 | 13.8M | $9.94 |
| stage CH1 | 2 | 61.7M | 98.7% | $42.63 | 3 | 20.6M | $14.21 |
| stage CH2 | 1 | 12.2M | 98.2% | $9.36 | 2 | 6.11M | $4.68 |
| stage CH3 | 1 | 39.1M | 98.7% | $26.96 | 3 | 13M | $8.99 |
| stage CH4 | 3 | 45.8M | 98.3% | $33.83 | 3 | 15.3M | $11.28 |
| stage CH5 | 2 | 20M | 97.8% | $16.41 | 2 | 9.98M | $8.21 |
| 2026-08 | 9 | 178.9M | 98.5% | $129.20 | 13 | 13.8M | $9.94 |

_Where the money goes: agent $129.12 (100%) · gate $0.08 (0%) · blended $0.72/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-27 02:45:20  ✓ checkpoint CH3.2 confirmed
08-27 02:45:20  ✓ checkpoint CH3.3 confirmed
08-27 02:45:20  ▸ stage CH3 confirmed  (53m34s)
08-27 02:45:20  ▸ stage CH4 entered — The machinery - the era-close stops being prose
08-27 02:45:20  • session #5 CH4 Deliver started (attempt 1/10)
08-27 03:33:49  ▪ gate engine-fast pass [session]  (1m04s)
08-27 03:33:49  ▪ gate face-fast pass [session]  (18.0s)
08-27 03:33:50  • session #5 CH4 → Advanced · done CH4.1,CH4.2 · 4 commit(s)  (48m29s)
08-27 03:33:51  • session #6 CH4 Deliver started (attempt 1/10)
08-27 09:10:07  • session #6 CH4 → TimedOut · 3 commit(s)  (5h36m16s)
08-27 09:10:11  • session #7 CH4 Resume started (attempt 2/10)
08-27 09:18:41  ▪ gate engine-fast pass [session]  (1m04s)
08-27 09:18:41  ▪ gate face-fast pass [session]  (21.2s)
08-27 09:18:42  • session #7 CH4 → Advanced · done CH4.4 · 3 commit(s)  (8m31s)
08-27 09:23:32  ▪ gate engine-fast pass [phase]  (0.0s)
08-27 09:23:32  ▪ gate face-fast pass [phase]  (0.0s)
08-27 09:23:32  ▪ gate engine-full pass [phase]  (4m44s)
08-27 09:23:32  ▪ gate face-full pass [phase]  (2.4s)
08-27 09:23:32  ✓ checkpoint CH4.1 confirmed
08-27 09:23:32  ✓ checkpoint CH4.2 confirmed
08-27 09:23:32  ✓ checkpoint CH4.4 confirmed
08-27 09:23:32  ▸ stage CH4 confirmed  (6h38m11s)
08-27 09:23:33  ▸ stage CH5 entered — Ship Charkh with the machinery it built
08-27 09:23:33  • session #8 CH5 Deliver started (attempt 1/4)
08-27 09:49:23  ▪ gate engine-fast pass [session]  (2m00s)
08-27 09:49:23  ▪ gate face-fast pass [session]  (25.9s)
08-27 09:49:24  • session #8 CH5 → Advanced · done CH5.1 · 4 commit(s)  (25m51s)
08-27 09:49:24  • session #9 CH5 Deliver started (attempt 1/4)
08-27 10:09:04  ▪ gate engine-fast pass [session]  (1m16s)
08-27 10:09:04  ▪ gate face-fast pass [session]  (24.5s)
08-27 10:09:04  • session #9 CH5 → Advanced · done CH5.2 · 3 commit(s)  (19m39s)
08-27 10:15:02  ▪ gate engine-fast pass [phase]  (0.0s)
08-27 10:15:02  ▪ gate face-fast pass [phase]  (0.0s)
08-27 10:15:02  ▪ gate engine-full pass [phase]  (5m48s)
08-27 10:15:02  ▪ gate face-full pass [phase]  (2.8s)
08-27 10:15:02  § owner approval requested — CH5
08-27 10:20:20  § owner approval granted — CH5
08-27 10:20:20  ✓ checkpoint CH5.1 confirmed
08-27 10:20:20  ✓ checkpoint CH5.2 confirmed
08-27 10:20:20  ▸ stage CH5 confirmed  (56m47s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 9 · retries 1 (11 %) · overall Warn
⚠ [context-saturation] session #1: 39,286,951 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #2: 21,648,664 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #4: 38,650,669 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #5: 38,222,976 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 22,020,833 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/charkh
working tree: M .conductor/REPORT.md, M plans/charkh/TRACKER.md
vs upstream: up to date
```

### Commits by session

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
- **s5 (CH4 Deliver)** — 4 commit(s):
  - [`542f068`](https://github.com/shaahink/conductor/commit/542f068) docs(CH4): the handoff - CH4.1 and CH4.2 are closed, CH4.3 is diagnosed
  - [`c0dcad5`](https://github.com/shaahink/conductor/commit/c0dcad5) feat(CH4.2): perform what is mechanical, name what is judgement
  - [`a660c3a`](https://github.com/shaahink/conductor/commit/a660c3a) docs(CH4.1): the handoff block for CH4.2
  - [`cf8997f`](https://github.com/shaahink/conductor/commit/cf8997f) feat(CH4.1): the era-close checklist gets a failure mode
- **s6 (CH4 Deliver)** — 3 commit(s):
  - [`5019261`](https://github.com/shaahink/conductor/commit/5019261) docs(CH4.3): the handoff - the sweep is scoped, the write is ordered
  - [`74cfe3c`](https://github.com/shaahink/conductor/commit/74cfe3c) docs(CH4.3): the evidence - the A/B that names 23, then 14
  - [`f4022f6`](https://github.com/shaahink/conductor/commit/f4022f6) feat(CH4.3): the retire sweep asks whose board it is
- **s7 (CH4 Resume)** — 3 commit(s):
  - [`d170504`](https://github.com/shaahink/conductor/commit/d170504) docs(CH4.4): the handoff - CH4 is closed, two acts are owed to CH5
  - [`d1c9b81`](https://github.com/shaahink/conductor/commit/d1c9b81) docs(CH4.4): the evidence, the two generated runbooks, and the verb's row
  - [`35a4555`](https://github.com/shaahink/conductor/commit/35a4555) feat(CH4.4): the runbook is generated, not written
- **s8 (CH5 Deliver)** — 4 commit(s):
  - [`11efa61`](https://github.com/shaahink/conductor/commit/11efa61) docs(CH5.1): the handoff - the record is reconciled, CH5.2 closes the era
  - [`f1c7ea5`](https://github.com/shaahink/conductor/commit/f1c7ea5) docs(CH5.1): this era's own numbers, and the caveat that moves all of them
  - [`980ccfc`](https://github.com/shaahink/conductor/commit/980ccfc) docs(CH5.1): the closure ledger, and the row that stopped being re-homed
  - [`77b9547`](https://github.com/shaahink/conductor/commit/77b9547) docs(CH5.1): the record says what the engine does, measured not repeated
- **s9 (CH5 Deliver)** — 3 commit(s):
  - [`8c19a1d`](https://github.com/shaahink/conductor/commit/8c19a1d) docs(CH5.2): the handoff - the era is closed, the acts that are left are the owner's
  - [`ae9678e`](https://github.com/shaahink/conductor/commit/ae9678e) docs(CH5.2): the era closed through its own machinery, and four findings
  - [`35043fe`](https://github.com/shaahink/conductor/commit/35043fe) fix(CH5.2): the release notes exist before the tag can, bug #88

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

> **CH5.2 done — era closed through its own verbs, bug #88 fixed, four findings**
> - bug #88 fixed, proven by scratch-rig A/B not inference: changelog act moves from "placeholder (2 non-blank line(s))" to "rename [Unreleased] to [0.6.0]"; all four mechanical acts read "will run", acts verdict OWNER / exit 2
> - nothing merged, tagged, moved, installed, pushed or backfilled — release perform refuses before planning while this run is live, so the mechanical acts are parked by the engine and version/split/corpus/reinstall/publish by the owner
> - four findings filed: #93 high (real courier DOWN, exit 1, restart-on-failure never fired, no log anywhere), #94 perform refuses its own dry run, #95 an era-close act nobody taught it, #96 wrong remedy text
>
> artefacts: CHANGELOG.md, .conductor/evidence/CH5/ch5-2-era-close.md, .conductor/evidence/CH5/ch5-2-bug88-ab.txt, .conductor/evidence/CH5/ch5-2-preflight.txt, .conductor/evidence/CH5/ch5-2-perform-dryrun.txt, .conductor/evidence/CH5/ch5-2-charkh-runbook.md, .conductor/evidence/CH5/ch5-2-runbook-tag-rehearsal.md, 35043fe
>
> evidence: .conductor/evidence/CH5/ch5-2-era-close.md
>
> gaps: the era-close is unperformed by design — tag/merge/docmove wait for this run to end, version/split/corpus/reinstall/publish are the owner's; bug #93 means the courier is down on this machine right now

## Tracker handoff

```
last: CH5.2 DONE - the last checkpoint of the plan. Bug #88 fixed in 35043fe: CHANGELOG [Unreleased]
  now carries what Charkh landed, written from `master..feat/charkh` (32 commits), 108 non-blank
  lines where there were 2. Proven by A/B in a scratch rig, not inference - same plan, same
  `--tag 0.6.0` dry run, CHANGELOG.md the only difference: the changelog act moves from "a
  placeholder (2 non-blank line(s))" to "rename [Unreleased] to [0.6.0]", and here all four
  mechanical acts read "will run", acts verdict OWNER / exit 2. Nothing was merged, tagged, moved,
  installed, pushed or backfilled: `release perform` refuses before it plans anything while this run
  is live, so the mechanical acts are parked by the ENGINE, and version/split/corpus/reinstall/
  publish are parked because they are the owner's. Four findings filed: #93 high (the real courier
  died with exit 1, restart-on-failure did not fire, no log anywhere - it is DOWN right now and
  Telegram drops undelivered notes after 24h), #94, #95, #96.
next: nothing is owed to a session. The close is `.conductor/evidence/CH5/ch5-2-era-close.md`.
```
