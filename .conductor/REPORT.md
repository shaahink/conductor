# Conductor — Sarban face - the watcher and the surfaces run report

_Updated 2026-08-01 13:08 UTC · branch `feat/sarban` · HEAD `3365138`_

**Status:** Idle
**Stage:** SF5 — Supervision without a polling meter · attempts used 0 · working ▸ SF5.4
**Checkpoints:** 18/24 done · **Sessions run:** 30 · **Cost:** $239.7581 (agent $239.5913 + gates $0.1667) · **Tokens:** 4,356,925 in / 1,325,784 out
**Confirmed phases:** SF0, SF1, SF2, SF3, SF4

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| SF0 | The ledger closes - the core run's leftovers | ██████████ 4/4 | confirmed ✓ |
| SF1 | The face sheds dead weight | ██████████ 3/3 | confirmed ✓ |
| SF2 | The face tells the truth kindly - state, time, money | ██████████ 3/3 | confirmed ✓ |
| SF3 | Reading a session becomes cheap | ██████████ 3/3 | confirmed ✓ |
| SF4 | The human queue is a first-class surface | ██████████ 2/2 | confirmed ✓ |
| SF5 | Supervision without a polling meter | ████████░░ 3/4 | **← active** |
| SF6 | The prompt bank compounds | ░░░░░░░░░░ 0/3 | todo |
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

<details><summary>SF5 — Supervision without a polling meter (3/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF5.1 | conductor watch blocks silently and returns or fires a hook only on the wake set — park, circuit breaker, budget park, phase RED twice on a stage, engine gone, run ended — with a json brief of about thirty lines and a timeout heartbeat | ✅ DONE | - |
| SF5.2 | A supervisor plan block runs a configured command on wake with the brief on stdin; operating.md carries the wake and dont-wake table and the standing-order pattern | ✅ DONE | [`4efedac`](https://github.com/shaahink/conductor/commit/4efedac) |
| SF5.3 | The remote supervision pattern is documented and proven once end to end — a wake reaching a remote listener — with an honest note of what stays manual | ✅ DONE | [`2cd9083`](https://github.com/shaahink/conductor/commit/2cd9083) |
| SF5.4 | conductor ps lists every run on the machine from the control-plane discovery files; process titles carry repo and run id; the face offers a run picker when more than one control plane answers | 🔄 IN PROGRESS | - |

</details>

<details><summary>SF6 — The prompt bank compounds (0/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF6.1 | The built-in session and fix templates carry the field lessons: in-progress first, claim before handoff, deferred-MCP fallback on one line, long commands under conductor bg, the anchor-commit rule for multi-repo plans | ⬜ TODO | - |
| SF6.2 | The prompt bank under plans/ is pruned, enriched from the rounds — proof-note pattern, owner-block alternate completions, the unblocks voice — and indexed so it is choosable | ⬜ TODO | - |
| SF6.3 | conductor init scaffolds the refreshed template set with telegram and supervisor hints, and its output passes doctor clean | ⬜ TODO | - |

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
| 1 | SF0 | Deliver | 1 | 07-31 19:15 | 0:22 | Advanced | SF0.1 | 1 | engine-fast:OK · face-fast:OK | $10.7229 | $0.0085 | 176,730/68,746 |
| 2 | SF0 | Deliver | 1 | 07-31 19:38 | 0:45 | Advanced | SF0.2 | 2 | engine-fast:OK · face-fast:OK | $13.1360 | $0.0086 | 210,694/76,633 |
| 3 | SF0 | Deliver | 1 | 07-31 20:26 | 0:39 | Advanced | SF0.3 | 3 | engine-fast:OK · face-fast:OK | $17.4303 | $0.0051 | 240,056/105,248 |
| 4 | SF0 | Deliver | 1 | 07-31 21:06 | 0:39 | Advanced | SF0.4 | 3 | engine-fast:OK · face-fast:OK | $15.2114 | $0.0068 | 225,894/90,712 |
| 5 | SF1 | Deliver | 1 | 07-31 21:50 | 0:29 | Advanced | SF1.1 | 3 | engine-fast:OK · face-fast:OK | $23.1233 | $0.0064 | 236,481/90,307 |
| 6 | SF1 | Deliver | 1 | 07-31 22:21 | 0:32 | Advanced | SF1.2 | 3 | engine-fast:OK · face-fast:OK | $17.5303 | $0.0054 | 248,779/107,715 |
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

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-01 10:51:26  • session #22 SF4 Deliver started (attempt 1/4)
08-01 11:15:54  ▪ gate engine-fast pass [session]  (1m14s)
08-01 11:15:55  ▪ gate face-fast pass [session]  (11.8s)
08-01 11:15:56  • session #22 SF4 → Progress · 3 commit(s)  (24m29s)
08-01 11:15:56  • session #23 SF4 Deliver started (attempt 1/4)
08-01 11:30:48  • session #23 SF4 → RolledOver  (14m51s)
08-01 11:30:48  • session #24 SF4 Deliver started (attempt 1/4)
08-01 11:48:47  ▪ gate engine-fast pass [session]  (1m19s)
08-01 11:48:47  ▪ gate face-fast pass [session]  (7.0s)
08-01 11:48:48  • session #24 SF4 → Advanced · done SF4.2 · 3 commit(s)  (18m00s)
08-01 11:58:26  ▪ gate engine-fast pass [phase]  (0.0s)
08-01 11:58:26  ▪ gate face-fast pass [phase]  (0.0s)
08-01 11:58:26  ▪ gate engine-full FAIL [phase]  (4m54s)
08-01 11:58:26  ▪ gate face-full pass [phase]  (18.0s)
08-01 11:58:27  • session #25 SF4 Fix started (attempt 2/4)
08-01 12:10:44  ▪ gate engine-fast pass [session]  (1m17s)
08-01 12:10:44  ▪ gate face-fast pass [session]  (36.0s)
08-01 12:10:45  • session #25 SF4 → Progress · 2 commit(s)  (12m17s)
08-01 12:15:16  ▪ gate engine-fast pass [phase]  (0.0s)
08-01 12:15:16  ▪ gate face-fast pass [phase]  (0.0s)
08-01 12:15:17  ▪ gate engine-full pass [phase]  (4m22s)
08-01 12:15:17  ▪ gate face-full pass [phase]  (6.1s)
08-01 12:15:17  ✓ checkpoint SF4.2 confirmed
08-01 12:15:26  ▸ stage SF4 confirmed  (7h26m31s)
08-01 12:15:28  ▸ stage SF5 entered — Supervision without a polling meter
08-01 12:15:28  • session #26 SF5 Deliver started (attempt 1/8)
08-01 12:33:48  • session #26 SF5 → RolledOver  (18m19s)
08-01 12:33:48  • session #27 SF5 Deliver started (attempt 1/8)
08-01 13:00:08  • session #27 SF5 → RolledOver  (26m20s)
08-01 13:00:09  • session #28 SF5 Deliver started (attempt 1/8)
08-01 13:19:45  ▪ gate engine-fast pass [session]  (1m14s)
08-01 13:19:45  ▪ gate face-fast pass [session]  (20.3s)
08-01 13:19:46  • session #28 SF5 → Advanced · done SF5.2 · 2 commit(s)  (19m36s)
08-01 13:19:46  • session #29 SF5 Deliver started (attempt 1/8)
08-01 13:43:06  ▪ gate engine-fast pass [session]  (1m19s)
08-01 13:43:06  ▪ gate face-fast pass [session]  (8.0s)
08-01 13:43:07  • session #29 SF5 → Advanced · done SF5.3 · 3 commit(s)  (23m20s)
08-01 13:43:07  • session #30 SF5 Deliver started (attempt 1/8)
08-01 14:08:24  ▪ gate engine-fast pass [session]  (1m38s)
08-01 14:08:24  ▪ gate face-fast pass [session]  (13.1s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 30 · retries 2 (7 %) · overall Warn
⚠ [context-saturation] session #3: 24,790,251 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #4: 21,397,049 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #5: 36,996,007 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 24,716,690 context tokens (≥ 20,000,000)
⚠ [gate-oscillation] gate 'engine-full' flipped pass/fail 4x
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/sarban
working tree: M .conductor/REPORT.md
vs upstream: up to date
```

### Commits by session

- **s19 (SF3 Deliver)** — 3 commit(s):
  - [`5d88b21`](https://github.com/shaahink/conductor/commit/5d88b21) docs(tracker): SF3.3 claimed complete, SF3 closes and SF4.1 is next
  - [`1579880`](https://github.com/shaahink/conductor/commit/1579880) test(face): rebaseline the session detail, and pin the jumped-stage rail (SF3.3)
  - [`f91fa5e`](https://github.com/shaahink/conductor/commit/f91fa5e) feat(face): what a session landed, and when the run left the declared order (SF3.3, part 2d)
- **s21 (SF4 Deliver)** — 2 commit(s):
  - [`1eeda0b`](https://github.com/shaahink/conductor/commit/1eeda0b) docs(tracker): SF4.2 part 1 landed, and the disk that failed 18 tests
  - [`f0d12bb`](https://github.com/shaahink/conductor/commit/f0d12bb) fix(telegram): a queued reload says so, instead of advising the edit you just made (FU-OWNER-13)
- **s22 (SF4 Deliver)** — 3 commit(s):
  - [`57a79f1`](https://github.com/shaahink/conductor/commit/57a79f1) docs(tracker): SF4.2 is 3 of 4, and the last quarter is all Go
  - [`0d2ce47`](https://github.com/shaahink/conductor/commit/0d2ce47) feat(owner): a queue item that arrives while you are away pushes to Telegram (SF4.2)
  - [`bc7ff3f`](https://github.com/shaahink/conductor/commit/bc7ff3f) feat(telegram): every push says which plan, which session, which repo, which build (FU-OWNER-11)
- **s24 (SF4 Deliver)** — 3 commit(s):
  - [`9fe57f0`](https://github.com/shaahink/conductor/commit/9fe57f0) docs(tracker): SF4.2 closes, and the frame that caught what green tests missed
  - [`017c8a9`](https://github.com/shaahink/conductor/commit/017c8a9) feat(face): prove the owner queue against a live engine, and finish FU-OWNER-13's second layer (SF4.2)
  - [`d61da19`](https://github.com/shaahink/conductor/commit/d61da19) test(face): rebaseline the eleven goldens the owner-queue key moved (SF4.2)
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

engine-fast:OK · face-fast:OK

## Last session result

> SESSION-RESULT: Landed two of SF5.4's three parts, both committed, pushed, and green. `conductor ps` (`a599d26`) lists every conductor run on the machine — repo, plan, run id, port, pid, status, uptime — plus `--json`; a live drive of the fresh build listed **both** real runs on this box (this repo on 4317 pid 35412, `C:/Code/sk-studio` on 4318 pid 19056), evidence at `.conductor/evidence/SF5/SF5.4-ps-live.log`. The design turns on a measured finding, not the spec's assumed mechanism: this repo's live run serves its control plane with **no** `.conductor/control-plane.json`, so a discovery-file walk would have missed the very run it runs inside — the port probe of 4317-4336 leads (`GET /state…

## Tracker handoff

```
last: **SF5.4 parts 1+2 landed, checkpoint still IN PROGRESS** (`a599d26`, `9adb15f`).
  `conductor ps` lists every run on the machine; **the port probe leads, not a discovery-file walk** -
  measured: this repo's live run serves 4317 with **no** `.conductor/control-plane.json`, so a file
  scan would miss the run it runs inside. `GET /state` already carries plan/run id/repo/state dir;
  the discovery file only enriches (pid), one naming another port is ignored, `conductor.lock` is the
  fallback. Read-only: loopback GETs, no token, no POST. A lock with no plane lists as `no control
  plane`. Engine process titles now carry repo + stage + run id, restored on exit.
next: **the face run picker** - the last piece of SF5.4. `conductor face` reads ONLY the local
  discovery file, so it is **broken in this repo today** (says "no live run" at a live run). Feed it
  from `FleetScan`; when more than one plane answers, hand the fleet to the Go face for a picker
  (suggest env `CONDUCTOR_FLEET` json, so tokens stay off the process listing). Then a live title capture.
green: SF5_4FleetTests **45**, SF5 + harness **132**. Live drive of MY build listed both runs on this
  box (this repo 4317 pid 35412, sk-studio 4318): `.conductor/evidence/SF5/SF5.4-ps-live.log`.
red: nothing. open: bugs **#15 #16 #17 #18 #19 #20**.
```
