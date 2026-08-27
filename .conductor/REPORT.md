# Conductor — Charkh - the wheel: what the owner still does by hand becomes machinery run report

_Updated 2026-08-27 08:23 UTC · branch `feat/charkh` · HEAD `d170504`_

**Status:** Idle
**Stage:** CH4 — The machinery - the era-close stops being prose · attempts used 0
**Checkpoints:** 12/14 done · **Sessions run:** 7 · **Cost:** $112.7872 (agent $112.7336 + gates $0.0536) · **Tokens:** 1,788,414 in / 716,074 out
**Confirmed phases:** CH1, CH2, CH3, CH4
**Channels:** telegram ready · github ready · courier DEAD
**⚠ Channel DEAD — courier:** no courier is running on this machine. Start one: conductor courier restart · fix: `conductor courier restart`

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| CH1 | CI green, and the reason it was not | ██████████ 3/3 | confirmed ✓ |
| CH2 | The tour that matches the engine - and knows when it does not | ██████████ 2/2 | confirmed ✓ |
| CH3 | The docs say what shipped | ██████████ 3/3 | confirmed ✓ |
| CH4 | The machinery - the era-close stops being prose | ██████████ 4/4 | confirmed ✓ |
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

<details> ✅<summary>CH4 — The machinery - the era-close stops being prose (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| CH4.1 | Release preflight as a verb: every precondition DV7.3 measured by hand becomes something the engine measures - ff-only merge, a CHANGELOG section the extractor exits 0 on, no live conductor process, migration versions matching, the courier's token scope and task state, the run whose backfill is owed - as a verdict per line with a non-zero exit when any line is red | ✅ DONE | [`cf8997f`](https://github.com/shaahink/conductor/commit/cf8997f) |
| CH4.2 | The mechanical acts performed and the judgement acts refused BY NAME: the CHANGELOG rename, the tag, the ff-only merge and the doc move with its tracker/planDoc/readOrder repoint are performed; the version number, single-vs-split release and corpus inclusion are stopped at and named. An act that needs the owner is never silently skipped - that failure is exactly what KS12.3 was | ✅ DONE | [`cf8997f`](https://github.com/shaahink/conductor/commit/cf8997f) |
| CH4.3 | A backfill can no longer vandalise another run's board: the retire sweep is scoped to the run being synced, or a backfill that would retire another run's checkpoints is refused with what it would have closed. Measured 2026-08-26: the edge run's dry run reported 23 retired against exactly Divan's 23 checkpoints. Then the edge run's own GitHub record is written | ✅ DONE | - |
| CH4.4 | The owner runbook becomes the preflight's output rather than a document written from scratch each era, generated from its own measurements and carrying the exact commands - the DV7.3 and KS12.3 artifacts are the shape being replaced | ✅ DONE | [`35a4555`](https://github.com/shaahink/conductor/commit/35a4555) |

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
| 5 | CH4 | Deliver | 1 | 08-27 01:45 | 0:47 | Advanced | CH4.1 CH4.2 | 4 | engine-fast:OK · face-fast:OK | $27.4686 | $0.0082 | 323,895/170,309 |
| 6 | CH4 | Deliver | 1 | 08-27 02:33 | 5:36 | TimedOut |  | 3 |  |  |  | 224,343/1,325 |
| 7 | CH4 | Resume | 2r1 | 08-27 08:10 | 0:07 | Advanced | CH4.4 | 3 | engine-fast:OK · face-fast:OK | $6.3494 | $0.0086 | 251,390/16,229 |

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 7 | 158.9M | 98.6% | $112.79 | 11 | 14.4M | $10.25 |
| stage CH1 | 2 | 61.7M | 98.7% | $42.63 | 3 | 20.6M | $14.21 |
| stage CH2 | 1 | 12.2M | 98.2% | $9.36 | 2 | 6.11M | $4.68 |
| stage CH3 | 1 | 39.1M | 98.7% | $26.96 | 3 | 13M | $8.99 |
| stage CH4 | 3 | 45.8M | 98.3% | $33.83 | 3 | 15.3M | $11.28 |
| 2026-08 | 7 | 158.9M | 98.6% | $112.79 | 11 | 14.4M | $10.25 |

_Where the money goes: agent $112.73 (100%) · gate $0.05 (0%) · blended $0.71/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
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
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 7 · retries 1 (14 %) · overall Warn
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

> **CH4.3 and CH4.4 landed with live proof; CH4 closed, two acts owed to CH5**
> - CH4.3: retire sweep scoped by owner marker + local map; A/B proves 23→0 and 14→0 retired, bug 84 closed
> - CH4.4: `release runbook` renders from the same probes and planner; reflection-derived property test, negative control red
> - edge run's GitHub record deferred to after CH5's reinstall — installed 0.5.0 would retire 14 of Charkh's own cards
>
> artefacts: f4022f6, 74cfe3c, 5019261, 35a4555, d1c9b81, d170504
>
> evidence: .conductor/evidence/CH4/ch4-3-retire-scoped.md, .conductor/evidence/CH4/ch4-4-release-runbook.md
>
> gaps: CH4.3's second clause (edge run record) ordered after CH5 reinstall; bugs 90 and 91 open; full suite 3564/3564 green

## Tracker handoff

```
last: CH4.3 AND CH4.4 both DONE and claimed; CH4 is closed. CH4.3 scoped the retire sweep -
  GithubIdentity gains OwnerMarker (NOT RunMarker; the diary is found by scanning bodies for that),
  an issue is retirable only if that marker is ours OR this run's GithubMap points at that number,
  and everything else is REFUSED BY NAME on GithubSyncResult.RetireRefused. CH4.4 made the runbook
  the verb's output: `conductor release runbook` renders from the SAME probes preflight runs and the
  SAME planner perform uses, and two tests derive the act vocabulary BY REFLECTION, so an act wired
  to nothing fails the day it is added. Evidence: .conductor/evidence/CH4/ch4-3-retire-scoped.md,
  ch4-4-release-runbook.md, and ch4-4-charkh-runbook.md - the generated runbook itself.
owed to CH5: (a) CH4.3's second clause, writing the edge run's GitHub record, is ordered AFTER the
  reinstall - today's A/B says the installed 0.5.0 would retire 14 of Charkh's OWN cards; (b) bug 91
  - IsStillGoing counts needs_human as in flight, so the corpus act omits that very backfill.
next: CH5.1, then CH5.2 closes the era using ch4-4-charkh-runbook.md REGENERATED at the time.
```
