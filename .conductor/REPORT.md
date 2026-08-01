# Conductor — Sarban face - the watcher and the surfaces run report

_Updated 2026-08-01 18:07 UTC · branch `feat/sarban` · HEAD `5a71373`_

**Status:** AwaitingOwner
**Stage:** SF7 — Ship the era · attempts used 1
**Checkpoints:** 24/24 done · **Sessions run:** 41 · **Cost:** $297.2402 (agent $296.9826 + gates $0.2575) · **Tokens:** 5,573,400 in / 1,747,626 out
**Confirmed phases:** SF0, SF1, SF2, SF3, SF4, SF5, SF6
**Pending:** full-battery phase gate for SF7

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| SF0 | The ledger closes - the core run's leftovers | ██████████ 4/4 | confirmed ✓ |
| SF1 | The face sheds dead weight | ██████████ 3/3 | confirmed ✓ |
| SF2 | The face tells the truth kindly - state, time, money | ██████████ 3/3 | confirmed ✓ |
| SF3 | Reading a session becomes cheap | ██████████ 3/3 | confirmed ✓ |
| SF4 | The human queue is a first-class surface | ██████████ 2/2 | confirmed ✓ |
| SF5 | Supervision without a polling meter | ██████████ 4/4 | confirmed ✓ |
| SF6 | The prompt bank compounds | ██████████ 3/3 | confirmed ✓ |
| SF7 | Ship the era | ██████████ 2/2 | gating… |

<details> ✅<summary>SF0 — The ledger closes - the core run's leftovers (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF0.1 | Bugs 6 and 11 die as a class — an inert plan key is either wired to its documented meaning or rejected at load, never readable-and-ignored — and bug 2 plus FU-OWNER-12 stop the notification path lying: no start line for a service that early-returned, and one logged sentence at run start saying whether pushes can be delivered at all | ✅ DONE | [`5217986`](https://github.com/shaahink/conductor/commit/5217986) |
| SF0.2 | Bug 10 — a claim made during a Verify or Audit session is counted, stamped and confirmed like any other, with the empty-string GateSummary evidence fallback fixed in the same change — plus bug 4 (a phase-gate RED names the session kind it actually queues), bug 3 (a confirmed last stage completes instead of spinning forever) and bug 8 (the harness git helper asserts its exit code, so NewCommits assertions stop being vacuous) | ✅ DONE | [`fdd78ae`](https://github.com/shaahink/conductor/commit/fdd78ae) |
| SF0.3 | Bugs 9, 5, 12 and 13 — one pid-liveness policy everywhere including MCP, bg status survives an uninspectable pid, bg start stops leaking the caller's stdout handle, bg logs reads a live log — and FU-OWNER-9's self-PID guard lands with the locked-by-conductor warning in the fix prompt | ✅ DONE | [`c84ccfc`](https://github.com/shaahink/conductor/commit/c84ccfc) |
| SF0.4 | Open bugs survive the run that found them — a new run in this repo sees the previous run's open rows, and run-ended says how many are open — and every remaining followups.md row is fixed, closed with its evidence, or re-homed to a living owner, with FU-F1-07 verified against SC8's scanning verb-parity test and FU-B10-2 measured from the core run's own sessions | ✅ DONE | [`d5b81cb`](https://github.com/shaahink/conductor/commit/d5b81cb) |

</details>

<details> ✅<summary>SF1 — The face sheds dead weight (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF1.1 | Verifier scores are served by a real endpoint and the Report tab renders them without SQL | ✅ DONE | [`9d993ef`](https://github.com/shaahink/conductor/commit/9d993ef) |
| SF1.2 | The Dev SQL console and its traces are gone — tab, /report/query, report --query — while MCP run_query stays for chat and the two non-SQL Dev panels are re-homed, not deleted | ✅ DONE | [`8f96ef2`](https://github.com/shaahink/conductor/commit/8f96ef2) |
| SF1.3 | The face has at most ten tabs after a written consolidation note: Console folds into Agent as a raw toggle, Timeline merges with Sessions into one history surface; keys, help and goldens regenerated | ✅ DONE | - |

</details>

<details> ✅<summary>SF2 — The face tells the truth kindly - state, time, money (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF2.1 | Home shows one honest connection line with age, start-a-run instructions only when no run exists, a last-run summary card when offline, one Connected definition, and consistent path casing | ✅ DONE | [`93611dd`](https://github.com/shaahink/conductor/commit/93611dd) |
| SF2.2 | One shared time formatter renders local time with relative age and a date when not today; the Timeline UTC mislabel is fixed and the previously-unrendered timestamps render | ✅ DONE | [`f05791b`](https://github.com/shaahink/conductor/commit/f05791b) |
| SF2.3 | Over-budget renders as OVER never zero-percent headroom; window and lifetime spend are distinguished; the top bar shows in-flight session cost live; the attempts marker has a legend | ✅ DONE | [`ef1620f`](https://github.com/shaahink/conductor/commit/ef1620f) |

</details>

<details> ✅<summary>SF3 — Reading a session becomes cheap (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF3.1 | Tool calls render as one-liners and each session has a digest panel — tool mix, files touched, claims, bg-purpose storyline; fold is rune-safe | ✅ DONE | [`352bc1a`](https://github.com/shaahink/conductor/commit/352bc1a) |
| SF3.2 | The kanban groups by stage with the active stage highlighted, card meta visible unselected, column totals, skips separated from Done, in-column scroll, and a you-are-here ribbon | ✅ DONE | [`3e7c4b3`](https://github.com/shaahink/conductor/commit/3e7c4b3) |
| SF3.3 | Branch, dirty state, ahead-behind and HEAD sha are on the wire and in the face; session history shows commit subjects; the sidebar cues execution-vs-declared stage order | ✅ DONE | [`f91fa5e`](https://github.com/shaahink/conductor/commit/f91fa5e) |

</details>

<details> ✅<summary>SF4 — The human queue is a first-class surface (2/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF4.1 | OWNER-QUEUE.md and GET /owner/queue collect every open human item — HUMAN lines, ownerGates, parks with age, blocked-until waits — each saying what it unblocks and the command that clears it, regenerated at session boundaries | ✅ DONE | - |
| SF4.2 | The face surfaces the owner queue with age and unblocks, and a newly-arrived item pushes to Telegram | ✅ DONE | [`d61da19`](https://github.com/shaahink/conductor/commit/d61da19) |

</details>

<details> ✅<summary>SF5 — Supervision without a polling meter (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF5.1 | conductor watch blocks silently and returns or fires a hook only on the wake set — park, circuit breaker, budget park, phase RED twice on a stage, engine gone, run ended — with a json brief of about thirty lines and a timeout heartbeat | ✅ DONE | - |
| SF5.2 | A supervisor plan block runs a configured command on wake with the brief on stdin; operating.md carries the wake and dont-wake table and the standing-order pattern | ✅ DONE | [`4efedac`](https://github.com/shaahink/conductor/commit/4efedac) |
| SF5.3 | The remote supervision pattern is documented and proven once end to end — a wake reaching a remote listener — with an honest note of what stays manual | ✅ DONE | [`2cd9083`](https://github.com/shaahink/conductor/commit/2cd9083) |
| SF5.4 | conductor ps lists every run on the machine from the control-plane discovery files; process titles carry repo and run id; the face offers a run picker when more than one control plane answers | ✅ DONE | [`3f0ff2e`](https://github.com/shaahink/conductor/commit/3f0ff2e) |

</details>

<details> ✅<summary>SF6 — The prompt bank compounds (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF6.1 | The built-in session and fix templates carry the field lessons: in-progress first, claim before handoff, deferred-MCP fallback on one line, long commands under conductor bg, the anchor-commit rule for multi-repo plans | ✅ DONE | [`8dd1aa3`](https://github.com/shaahink/conductor/commit/8dd1aa3) |
| SF6.2 | The prompt bank under plans/ is pruned, enriched from the rounds — proof-note pattern, owner-block alternate completions, the unblocks voice — and indexed so it is choosable | ✅ DONE | [`4b894c1`](https://github.com/shaahink/conductor/commit/4b894c1) |
| SF6.3 | conductor init scaffolds the refreshed template set with telegram and supervisor hints, and its output passes doctor clean | ✅ DONE | - |

</details>

<details> ✅<summary>SF7 — Ship the era (2/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF7.1 | The docs match the code — plan-config advisor default, tracker runtime files, operating supervision section, NEXT-FEATURES refresh — the field notes carry a closure ledger, and the era CHANGELOG is written | ✅ DONE | [`37a75ef`](https://github.com/shaahink/conductor/commit/37a75ef) |
| SF7.2 | feat/sarban is merged to master by the owner, the release is tagged through the SC8 pipeline, and the installed conductor version matches the releases page | ✅ DONE | [`7d8b327`](https://github.com/shaahink/conductor/commit/7d8b327) |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 12 | SF2 | Deliver | 1 | 08-01 00:54 | 0:19 | Advanced | SF2.3 | 3 | engine-fast:OK · face-fast:OK | $5.9701 | $0.0076 | 113,302/47,111 |
| 13 | SF3 | Deliver | 1 | 08-01 01:19 | 0:20 | RolledOver |  | 0 |  | $5.6967 |  | 132,842/2,205 |
| 14 | SF3 | Deliver | 1 | 08-01 01:40 | 0:15 | Advanced | SF3.1 | 3 | engine-fast:OK · face-fast:OK | $6.1892 | $0.0078 | 133,904/46,736 |
| 15 | SF3 | Deliver | 1 | 08-01 01:57 | 0:18 | Advanced | SF3.2 | 3 | engine-fast:OK · face-fast:OK | $5.9556 | $0.0065 | 114,930/57,516 |
| 16 | SF3 | Deliver | 1 | 08-01 02:17 | 0:31 | RolledOver |  | 0 |  | $5.7775 |  | 126,846/2,452 |
| 17 | SF3 | Deliver | 1 | 08-01 02:49 | 0:16 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $5.9184 | $0.0073 | 119,065/42,337 |
| 18 | SF3 | Deliver | 1 | 08-01 03:07 | 0:21 | RolledOver |  | 0 |  | $5.8529 |  | 118,847/2,848 |
| 19 | SF3 | Deliver | 1 | 08-01 03:28 | 0:14 | Advanced | SF3.3 | 3 | engine-fast:OK · face-fast:OK | $6.0631 | $0.0072 | 129,054/48,297 |
| 20 | SF4 | Deliver | 1 | 08-01 09:00 | 0:27 | RolledOver |  | 0 |  | $5.8124 |  | 131,809/1,841 |
| 21 | SF4 | Deliver | 1 | 08-01 09:27 | 0:21 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $6.1325 | $0.0157 | 119,875/46,987 |
| 22 | SF4 | Deliver | 1 | 08-01 09:51 | 0:22 | Progress |  | 3 | engine-fast:OK · face-fast:OK | $6.6960 | $0.0087 | 166,359/55,772 |
| 23 | SF4 | Deliver | 1 | 08-01 10:15 | 0:14 | RolledOver |  | 0 |  | $5.8501 |  | 120,122/2,480 |
| 24 | SF4 | Deliver | 1 | 08-01 10:30 | 0:16 | Advanced | SF4.2 | 3 | engine-fast:OK · face-fast:OK | $6.0372 | $0.0086 | 109,716/45,426 |
| 25 | SF4 | Fix | 2 | 08-01 10:58 | 0:10 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $3.4157 | $0.0114 | 72,338/26,184 |
| 26 | SF5 | Deliver | 1 | 08-01 11:15 | 0:18 | RolledOver |  | 0 |  | $5.9470 |  | 141,879/1,823 |
| 27 | SF5 | Deliver | 1 | 08-01 11:33 | 0:26 | RolledOver |  | 0 |  | $5.9398 |  | 132,849/1,867 |
| 28 | SF5 | Deliver | 1 | 08-01 12:00 | 0:17 | Advanced | SF5.2 | 2 | engine-fast:OK · face-fast:OK | $5.4927 | $0.0095 | 104,311/43,176 |
| 29 | SF5 | Deliver | 1 | 08-01 12:19 | 0:21 | Advanced | SF5.3 | 3 | engine-fast:OK · face-fast:OK | $6.5167 | $0.0088 | 141,509/64,444 |
| 30 | SF5 | Deliver | 1 | 08-01 12:43 | 0:23 | Progress |  | 3 | engine-fast:OK · face-fast:OK | $6.2744 | $0.0111 | 112,629/58,601 |
| 31 | SF5 | Deliver | 1 | 08-01 13:08 | 0:22 | Advanced | SF5.4 | 3 | engine-fast:OK · face-fast:OK | $6.6372 | $0.0099 | 141,596/63,361 |
| 32 | SF5 | Fix | 2 | 08-01 13:45 | 0:24 | RolledOver |  | 0 |  | $5.9833 |  | 128,284/2,129 |
| 33 | SF6 | Deliver | 1 | 08-01 14:17 | 0:22 | Advanced | SF6.1 | 1 | engine-fast:OK · face-fast:OK | $6.1405 | $0.0074 | 106,949/54,821 |
| 34 | SF6 | Deliver | 1 | 08-01 14:41 | 0:19 | Advanced | SF6.2 | 2 | engine-fast:OK · face-fast:OK | $5.5353 | $0.0116 | 104,010/52,742 |
| 35 | SF6 | Deliver | 1 | 08-01 15:02 | 0:25 | RolledOver |  | 0 |  | $6.0288 |  | 114,436/2,225 |
| 36 | SF6 | Fix | 2 | 08-01 15:42 | 0:20 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $6.1931 | $0.0129 | 111,458/57,499 |
| 37 | SF7 | Deliver | 1 | 08-01 16:10 | 0:23 | Progress |  | 3 | engine-fast:OK · face-fast:OK | $6.2933 | $0.0093 | 125,335/52,531 |
| 38 | SF7 | Deliver | 1 | 08-01 16:36 | 0:21 | Progress |  | 5 | engine-fast:OK · face-fast:OK | $5.9680 | $0.0081 | 113,825/50,135 |
| 39 | SF7 | Deliver | 1 | 08-01 16:59 | 0:14 | Advanced | SF7.1 | 2 | engine-fast:OK · face-fast:OK | $4.2768 | $0.0075 | 94,593/42,661 |
| 40 | SF7 | Deliver | 1 | 08-01 17:31 | 0:12 | Advanced | SF7.2 | 1 | engine-fast:OK · face-fast:OK | $2.9755 | $0.0112 | 105,342/29,627 |
| 41 | SF7 | Fix | 2 | 08-01 17:53 | 0:07 | Progress |  | 1 | engine-fast:OK · face-fast:OK | $1.3595 | $0.0128 | 70,647/14,111 |

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-01 17:05:42  ▪ gate engine-fast pass [session]  (1m12s)
08-01 17:05:42  ▪ gate face-fast pass [session]  (56.9s)
08-01 17:05:43  • session #36 SF6 → Progress · 2 commit(s)  (23m06s)
08-01 17:10:50  ▪ gate engine-fast pass [phase]  (0.0s)
08-01 17:10:51  ▪ gate face-fast pass [phase]  (0.0s)
08-01 17:10:51  ▪ gate engine-full pass [phase]  (4m47s)
08-01 17:10:51  ▪ gate face-full pass [phase]  (16.2s)
08-01 17:10:51  ✓ checkpoint SF6.2 confirmed
08-01 17:10:51  ▸ stage SF6 confirmed  (1h53m31s)
08-01 17:10:52  ▸ stage SF7 entered — Ship the era
08-01 17:10:52  • session #37 SF7 Deliver started (attempt 1/4)
08-01 17:36:23  ▪ gate engine-fast pass [session]  (1m06s)
08-01 17:36:23  ▪ gate face-fast pass [session]  (26.0s)
08-01 17:36:24  • session #37 SF7 → Progress · 3 commit(s)  (25m31s)
08-01 17:36:24  • session #38 SF7 Deliver started (attempt 1/4)
08-01 17:59:11  ▪ gate engine-fast pass [session]  (1m11s)
08-01 17:59:12  ▪ gate face-fast pass [session]  (9.3s)
08-01 17:59:13  • session #38 SF7 → Progress · 5 commit(s)  (22m48s)
08-01 17:59:13  • session #39 SF7 Deliver started (attempt 1/4)
08-01 18:14:59  ▪ gate engine-fast pass [session]  (1m08s)
08-01 18:14:59  ▪ gate face-fast pass [session]  (7.2s)
08-01 18:15:00  • session #39 SF7 → Advanced · done SF7.1 · 2 commit(s)  (15m46s)
08-01 18:15:00  ■ needs human — agent asked for a human in the tracker handoff (HUMAN: line) — resolve, then run `conductor resume`
08-01 18:28:40  ◆ plan reloaded — v2 · 8 stages · 4 gates
08-01 18:30:42  ◆ plan reloaded — v2 · 8 stages · 4 gates
08-01 18:31:01  • session #40 SF7 Deliver started (attempt 1/4)
08-01 18:45:44  ▪ gate engine-fast pass [session]  (1m01s)
08-01 18:45:44  ▪ gate face-fast pass [session]  (50.5s)
08-01 18:45:45  • session #40 SF7 → Advanced · done SF7.2 · 1 commit(s)  (14m44s)
08-01 18:53:31  ▪ gate engine-fast pass [phase]  (0.0s)
08-01 18:53:31  ▪ gate face-fast pass [phase]  (0.0s)
08-01 18:53:31  ▪ gate engine-full FAIL [phase]  (3m37s)
08-01 18:53:31  ▪ gate face-full pass [phase]  (9.9s)
08-01 18:53:32  • session #41 SF7 Fix started (attempt 2/4)
08-01 19:02:50  ▪ gate engine-fast pass [session]  (1m24s)
08-01 19:02:50  ▪ gate face-fast pass [session]  (44.1s)
08-01 19:02:52  • session #41 SF7 → Progress · 1 commit(s)  (9m19s)
08-01 19:07:06  ▪ gate engine-fast pass [phase]  (0.0s)
08-01 19:07:06  ▪ gate face-fast pass [phase]  (0.0s)
08-01 19:07:06  ▪ gate engine-full pass [phase]  (4m05s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 41 · retries 5 (12 %) · overall Warn
⚠ [context-saturation] session #3: 24,790,251 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #4: 21,397,049 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #5: 36,996,007 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 24,716,690 context tokens (≥ 20,000,000)
⚠ [gate-oscillation] gate 'engine-full' flipped pass/fail 10x
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/sarban
working tree: M .conductor/REPORT.md, M SARBAN-FACE-TRACKER.md
vs upstream: 1 ahead
```

### Commits by session

- **s33 (SF6 Deliver)** — 1 commit(s):
  - [`8dd1aa3`](https://github.com/shaahink/conductor/commit/8dd1aa3) feat(prompts): the built-ins carry the field lessons, and every line is paid for (SF6.1)
- **s34 (SF6 Deliver)** — 2 commit(s):
  - [`09ae2f1`](https://github.com/shaahink/conductor/commit/09ae2f1) docs(tracker): SF6.2 closes, and the ceiling was never the bank's fault (SF6.2)
  - [`4b894c1`](https://github.com/shaahink/conductor/commit/4b894c1) feat(prompts): the bank becomes choosable, and the packs that never rendered (SF6.2)
- **s36 (SF6 Fix)** — 2 commit(s):
  - [`d8d80f8`](https://github.com/shaahink/conductor/commit/d8d80f8) fix(prompts): the fix template stops ordering a move the board refuses (SF6)
  - [`be0394d`](https://github.com/shaahink/conductor/commit/be0394d) fix(prompts): the budget test that a five-digit pid could fail, and two anchors that moved (SF6)
- **s37 (SF7 Deliver)** — 3 commit(s):
  - [`d97546c`](https://github.com/shaahink/conductor/commit/d97546c) docs(tracker): SF7.1 part 1 - the evidence, and the three parts still owed (SF7.1)
  - [`d4f8993`](https://github.com/shaahink/conductor/commit/d4f8993) docs(operating): the known-gaps list stops being a July snapshot (SF7.1)
  - [`1ebb536`](https://github.com/shaahink/conductor/commit/1ebb536) docs(tracker): the runtime-files tree stops describing a run that does not happen (SF7.1)
- **s38 (SF7 Deliver)** — 5 commit(s):
  - [`3abe51c`](https://github.com/shaahink/conductor/commit/3abe51c) docs(tracker): SF7.1 part 2 - three parts landed, one field-notes ledger owed (SF7.1)
  - [`36e406e`](https://github.com/shaahink/conductor/commit/36e406e) docs(skill): the trust model the skill described is the opposite of the engine's (SF7.1)
  - [`de3256f`](https://github.com/shaahink/conductor/commit/de3256f) docs(changelog): the era entry, written from the commits not from memory (SF7.1)
  - [`65e59fa`](https://github.com/shaahink/conductor/commit/65e59fa) docs(followups): the closure ledger, and three rows that closed by naming nothing (SF7.1)
  - [`3268e54`](https://github.com/shaahink/conductor/commit/3268e54) docs(dev): the backlog stops promising what already shipped (SF7.1)
- **s39 (SF7 Deliver)** — 2 commit(s):
  - [`9f1ea98`](https://github.com/shaahink/conductor/commit/9f1ea98) docs(tracker): SF7.1 closed, and what SF7.2 is not free to do (SF7.1)
  - [`37a75ef`](https://github.com/shaahink/conductor/commit/37a75ef) docs(field-notes): every finding says which commit answered it (SF7.1)
- **s40 (SF7 Deliver)** — 1 commit(s):
  - [`7d8b327`](https://github.com/shaahink/conductor/commit/7d8b327) docs(tracker): SF7.2 closed - feat/sarban tagged v0.3.0 through the SC8 pipeline (SF7.2)
- **s41 (SF7 Fix)** — 1 commit(s):
  - [`5a71373`](https://github.com/shaahink/conductor/commit/5a71373) chore(release): cut the 0.3.0 section - the Sarban face era ships

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

> I'll pause here and wait for the background test-suite monitor to notify me when it completes.

## Tracker handoff

```
last: **session 40 - SF7.2 CLAIMED DONE**, commit `e897c2c` (on `master`, via a scratch worktree)
  + tag `v0.3.0`. `CHANGELOG.md` `[Unreleased]` cut to `[0.3.0] - 2026-08-01` (minor bump, same
  pattern as the 0.2.0/0.2.2 cuts). `git push origin v0.3.0` fired `release.yml` for real: guard +
  5 platform builds + attach-to-release all green (run 30710653729), binary self-reports
  `tag=0.3.0 binary=0.3.0+e897c2c7e1b0`. Release live: releases/tag/v0.3.0, 6 assets.
  Evidence: `.conductor/evidence/SF7/SF7.2-tag-release.md`.
era status: **all 24 SF checkpoints now claimed DONE.** Merge (`8286d63`) + tag (`v0.3.0`) both
  closed. Reinstall alone is deliberately outstanding — re-homed as `FU-OWNER-14` in
  `.conductor/followups.md` (owner runs `tools/install.ps1` once no other conductor run is live,
  then confirms `conductor version` matches the release page).
next: nothing plan-owned remains in SF7. If a session opens after this, it is confirmation/gate
  work, or the next era. red: `ci.yml` windows gate battery is flaky on
  `SF0_3PidsAndBackgroundWorkTests...NotDead` (bug **#23**, pre-existing, not release-blocking).
  open bugs: **#15 #16 #17 #18 #19 #20 #21 #23**.
```
