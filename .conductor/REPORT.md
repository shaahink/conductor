# Conductor — Sarban face - the watcher and the surfaces run report

_Updated 2026-08-01 16:10 UTC · branch `feat/sarban` · HEAD `d8d80f8`_

**Status:** Idle
**Stage:** SF6 — The prompt bank compounds · attempts used 0
**Checkpoints:** 22/24 done · **Sessions run:** 36 · **Cost:** $276.3182 (agent $276.1096 + gates $0.2086) · **Tokens:** 5,063,658 in / 1,558,561 out
**Confirmed phases:** SF0, SF1, SF2, SF3, SF4, SF5, SF6

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
| SF7 | Ship the era | ░░░░░░░░░░ 0/2 | todo |

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

<details><summary>SF7 — Ship the era (0/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF7.1 | The docs match the code — plan-config advisor default, tracker runtime files, operating supervision section, NEXT-FEATURES refresh — the field notes carry a closure ledger, and the era CHANGELOG is written | ⬜ TODO | - |
| SF7.2 | feat/sarban is merged to master by the owner, the release is tagged through the SC8 pipeline, and the installed conductor version matches the releases page | ⬜ TODO | - |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 7 | SF1 | Deliver | 1 | 07-31 23:11 | 0:22 | RolledOver |  | 0 |  | $11.9132 |  | 174,960/82,162 |
| 8 | SF1 | Fix | 2 | 07-31 23:41 | 0:06 | KilledByUser |  | 0 |  |  |  |  |
| 9 | SF2 | Deliver | 1 | 08-01 00:04 | 0:15 | RolledOver |  | 0 |  | $5.5132 |  | 126,902/1,912 |
| 10 | SF2 | Deliver | 1 | 08-01 00:20 | 0:18 | Advanced | SF2.1 | 4 | engine-fast:OK · face-fast:OK | $7.1733 | $0.0075 | 244,321/55,057 |
| 11 | SF2 | Deliver | 1 | 08-01 00:39 | 0:13 | Advanced | SF2.2 | 3 | engine-fast:OK · face-fast:OK | $6.2995 | $0.0082 | 129,922/49,189 |
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

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-01 14:08:25  • session #31 SF5 Deliver started (attempt 1/8)
08-01 14:32:18  ▪ gate engine-fast pass [session]  (1m31s)
08-01 14:32:19  ▪ gate face-fast pass [session]  (7.7s)
08-01 14:32:20  • session #31 SF5 → Advanced · done SF5.4 · 3 commit(s)  (23m54s)
08-01 14:45:08  ▪ gate engine-fast pass [phase]  (0.0s)
08-01 14:45:08  ▪ gate face-fast pass [phase]  (0.0s)
08-01 14:45:09  ▪ gate engine-full FAIL [phase]  (5m55s)
08-01 14:45:09  ▪ gate face-full pass [phase]  (32.6s)
08-01 14:45:10  • session #32 SF5 Fix started (attempt 2/8)
08-01 15:09:30  • session #32 SF5 → RolledOver  (24m19s)
08-01 15:17:18  ▪ gate engine-fast pass [phase]  (1m27s)
08-01 15:17:18  ▪ gate face-fast pass [phase]  (49.8s)
08-01 15:17:18  ▪ gate engine-full pass [phase]  (5m12s)
08-01 15:17:18  ▪ gate face-full pass [phase]  (11.3s)
08-01 15:17:18  ✓ checkpoint SF5.4 confirmed
08-01 15:17:18  ▸ stage SF5 confirmed  (3h01m50s)
08-01 15:17:20  ▸ stage SF6 entered — The prompt bank compounds
08-01 15:17:21  • session #33 SF6 Deliver started (attempt 1/4)
08-01 15:41:06  ▪ gate engine-fast pass [session]  (1m03s)
08-01 15:41:06  ▪ gate face-fast pass [session]  (10.5s)
08-01 15:41:07  • session #33 SF6 → Advanced · done SF6.1 · 1 commit(s)  (23m46s)
08-01 15:41:07  • session #34 SF6 Deliver started (attempt 1/4)
08-01 16:02:19  ▪ gate engine-fast pass [session]  (1m00s)
08-01 16:02:20  ▪ gate face-fast pass [session]  (55.1s)
08-01 16:02:21  • session #34 SF6 → Advanced · done SF6.2 · 2 commit(s)  (21m13s)
08-01 16:02:23  • session #35 SF6 Deliver started (attempt 1/4)
08-01 16:27:29  • session #35 SF6 → RolledOver  (25m06s)
08-01 16:42:35  ▪ gate engine-fast pass [phase]  (1m22s)
08-01 16:42:35  ▪ gate face-fast pass [phase]  (52.3s)
08-01 16:42:35  ▪ gate engine-full FAIL [phase]  (6m24s)
08-01 16:42:35  ▪ gate face-full pass [phase]  (47.8s)
08-01 16:42:37  • session #36 SF6 Fix started (attempt 2/4)
08-01 17:05:42  ▪ gate engine-fast pass [session]  (1m12s)
08-01 17:05:42  ▪ gate face-fast pass [session]  (56.9s)
08-01 17:05:43  • session #36 SF6 → Progress · 2 commit(s)  (23m06s)
08-01 17:10:50  ▪ gate engine-fast pass [phase]  (0.0s)
08-01 17:10:51  ▪ gate face-fast pass [phase]  (0.0s)
08-01 17:10:51  ▪ gate engine-full pass [phase]  (4m47s)
08-01 17:10:51  ▪ gate face-full pass [phase]  (16.2s)
08-01 17:10:51  ✓ checkpoint SF6.2 confirmed
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 36 · retries 4 (11 %) · overall Warn
⚠ [context-saturation] session #3: 24,790,251 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #4: 21,397,049 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #5: 36,996,007 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 24,716,690 context tokens (≥ 20,000,000)
⚠ [gate-oscillation] gate 'engine-full' flipped pass/fail 8x
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/sarban
working tree: M .conductor/REPORT.md, M SARBAN-FACE-TRACKER.md
vs upstream: up to date
```

### Commits by session

- **s25 (SF4 Fix)** — 2 commit(s):
  - [`9e608d6`](https://github.com/shaahink/conductor/commit/9e608d6) docs(tracker): the red is closed, and the filtered battery that let it in (SF4)
  - [`758c2d2`](https://github.com/shaahink/conductor/commit/758c2d2) test(telegram): SC1 expected the wire text FU-OWNER-11 stopped sending (SF4)
- **s28 (SF5 Deliver)** — 2 commit(s):
  - [`299ae8b`](https://github.com/shaahink/conductor/commit/299ae8b) docs(tracker): SF5.2 closes, and the fuse that had to be a file (SF5.2)
  - [`4efedac`](https://github.com/shaahink/conductor/commit/4efedac) feat(watch): the babysitter belongs in the plan, not a shell history (SF5.2)
- **s29 (SF5 Deliver)** — 3 commit(s):
  - [`02460a9`](https://github.com/shaahink/conductor/commit/02460a9) docs(tracker): SF5.3 closes, and the wake that had to outlive the fuse (SF5.3)
  - [`65846c6`](https://github.com/shaahink/conductor/commit/65846c6) docs(watch): remote supervision, proven against a listener that is not us (SF5.3)
  - [`2cd9083`](https://github.com/shaahink/conductor/commit/2cd9083) feat(watch): the wake leaves the machine (SF5.3)
- **s30 (SF5 Deliver)** — 3 commit(s):
  - [`3365138`](https://github.com/shaahink/conductor/commit/3365138) docs(tracker): SF5.4 parts 1+2, and the face picker that is left (SF5.4)
  - [`9adb15f`](https://github.com/shaahink/conductor/commit/9adb15f) feat(ps): the engine says which run it is, in its title (SF5.4)
  - [`a599d26`](https://github.com/shaahink/conductor/commit/a599d26) feat(ps): the fleet is visible, and the probe leads (SF5.4)
- **s31 (SF5 Deliver)** — 3 commit(s):
  - [`eae70d9`](https://github.com/shaahink/conductor/commit/eae70d9) docs(tracker): SF5 closes with the picker, and the file that was not the run (SF5.4)
  - [`e60f380`](https://github.com/shaahink/conductor/commit/e60f380) feat(face): the picker reads live, and the face says which run it took (SF5.4)
  - [`3f0ff2e`](https://github.com/shaahink/conductor/commit/3f0ff2e) feat(face): the face finds the run by probing, and asks when it cannot tell (SF5.4)
- **s33 (SF6 Deliver)** — 1 commit(s):
  - [`8dd1aa3`](https://github.com/shaahink/conductor/commit/8dd1aa3) feat(prompts): the built-ins carry the field lessons, and every line is paid for (SF6.1)
- **s34 (SF6 Deliver)** — 2 commit(s):
  - [`09ae2f1`](https://github.com/shaahink/conductor/commit/09ae2f1) docs(tracker): SF6.2 closes, and the ceiling was never the bank's fault (SF6.2)
  - [`4b894c1`](https://github.com/shaahink/conductor/commit/4b894c1) feat(prompts): the bank becomes choosable, and the packs that never rendered (SF6.2)
- **s36 (SF6 Fix)** — 2 commit(s):
  - [`d8d80f8`](https://github.com/shaahink/conductor/commit/d8d80f8) fix(prompts): the fix template stops ordering a move the board refuses (SF6)
  - [`be0394d`](https://github.com/shaahink/conductor/commit/be0394d) fix(prompts): the budget test that a five-digit pid could fail, and two anchors that moved (SF6)

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

> SESSION-RESULT: All three `engine-full` reds are green with nothing weakened — full suite **1756 passed / 0 failed** at `be0394d`, and the prompt-prose suites 68/68 at `d8d80f8`. The budget red was not SF6.3's prose: I measured the session template byte-identical across the SF6.3 file split (2585 bytes both sides) and `ToolContract.cs` unchanged since SF6.1, the commit that wrote the guard — so the real defect was that the guard sat ~1 char from its own limit against `Environment.ProcessId`, whose width changes every run, and a 5-digit test-host pid tipped it to exactly 8000. I bought real headroom by compressing tools-block prose with every field lesson kept, and **tightened** the ceiling 8…

## Tracker handoff

```
last: **session 36, FIX - all three battery reds green, nothing weakened.** Full engine suite
  **1756 passed / 0 failed** at be0394d. (a) The 8000-char budget red was NOT SF6.3's prose: the
  session template is byte-identical across the split and ToolContract has not changed since SF6.1.
  The only term that varies per run is `Environment.ProcessId`, so the guard had ~1 char of margin
  and a 5-digit test-host pid tipped it. Fixed by compressing tools-block prose with every lesson
  kept, and the guard was TIGHTENED 8000 -> **7900**. (b) SC4.4 re-anchored on headings the current
  built-in has, one assertion added, none removed - bug **22 closed**. (c) SF6.3's switch enumerator
  now LOCATES the switch across `PromptBuilder*.cs` instead of naming a file that moved.
next: **SF7.1** - docs reconciled with reality. red: nothing known.
  TRAP measured here: a fix session CANNOT follow its own step 1 - `task --in-progress` is refused on
  a DONE card. Leave it claimed, re-claim with fresh evidence; never `--todo` a real delivery. fix.md
  now says so. TRAP: prompt bytes are the tightest budget in the system - deliver 7803, fix 7731
  against 7900; adding a template line still means cutting one. Sessions carry `CONDUCTOR_PLAN`, so
  `cd $rig; conductor doctor` measures THIS repo - pass --plan.
  open: bugs **#15 #16 #17 #18 #19 #20 #21**.
```
