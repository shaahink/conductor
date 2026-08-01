# Conductor — Sarban face - the watcher and the surfaces run report

_Updated 2026-08-01 10:30 UTC · branch `feat/sarban` · HEAD `8580cca`_

**Status:** Running
**Stage:** SF4 — The human queue is a first-class surface · attempts used 0 · working ▸ SF4.2
**Checkpoints:** 14/24 done · **Sessions run:** 23 · **Cost:** $200.0852 (agent $199.9678 + gates $0.1173) · **Tokens:** 3,541,694 in / 1,084,263 out
**Confirmed phases:** SF0, SF1, SF2, SF3

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| SF0 | The ledger closes - the core run's leftovers | ██████████ 4/4 | confirmed ✓ |
| SF1 | The face sheds dead weight | ██████████ 3/3 | confirmed ✓ |
| SF2 | The face tells the truth kindly - state, time, money | ██████████ 3/3 | confirmed ✓ |
| SF3 | Reading a session becomes cheap | ██████████ 3/3 | confirmed ✓ |
| SF4 | The human queue is a first-class surface | █████░░░░░ 1/2 | **← active** |
| SF5 | Supervision without a polling meter | ░░░░░░░░░░ 0/4 | todo |
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

<details><summary>SF4 — The human queue is a first-class surface (1/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF4.1 | OWNER-QUEUE.md and GET /owner/queue collect every open human item — HUMAN lines, ownerGates, parks with age, blocked-until waits — each saying what it unblocks and the command that clears it, regenerated at session boundaries | ✅ DONE | - |
| SF4.2 | The face surfaces the owner queue with age and unblocks, and a newly-arrived item pushes to Telegram | 🔄 IN PROGRESS | - |

</details>

<details><summary>SF5 — Supervision without a polling meter (0/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF5.1 | conductor watch blocks silently and returns or fires a hook only on the wake set — park, circuit breaker, budget park, phase RED twice on a stage, engine gone, run ended — with a json brief of about thirty lines and a timeout heartbeat | ⬜ TODO | - |
| SF5.2 | A supervisor plan block runs a configured command on wake with the brief on stdin; operating.md carries the wake and dont-wake table and the standing-order pattern | ⬜ TODO | - |
| SF5.3 | The remote supervision pattern is documented and proven once end to end — a wake reaching a remote listener — with an honest note of what stays manual | ⬜ TODO | - |
| SF5.4 | conductor ps lists every run on the machine from the control-plane discovery files; process titles carry repo and run id; the face offers a run picker when more than one control plane answers | ⬜ TODO | - |

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

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-01 02:40:22  • session #13 SF3 → RolledOver  (20m35s)
08-01 02:40:22  • session #14 SF3 Deliver started (attempt 1/6)
08-01 02:57:38  ▪ gate engine-fast pass [session]  (1m06s)
08-01 02:57:38  ▪ gate face-fast pass [session]  (12.1s)
08-01 02:57:39  • session #14 SF3 → Advanced · done SF3.1 · 3 commit(s)  (17m17s)
08-01 02:57:40  • session #15 SF3 Deliver started (attempt 1/6)
08-01 03:17:21  ▪ gate engine-fast pass [session]  (58.4s)
08-01 03:17:21  ▪ gate face-fast pass [session]  (6.4s)
08-01 03:17:22  • session #15 SF3 → Advanced · done SF3.2 · 3 commit(s)  (19m42s)
08-01 03:17:22  • session #16 SF3 Deliver started (attempt 1/6)
08-01 03:49:15  • session #16 SF3 → RolledOver  (31m52s)
08-01 03:49:15  • session #17 SF3 Deliver started (attempt 1/6)
08-01 04:07:33  ▪ gate engine-fast pass [session]  (1m07s)
08-01 04:07:33  ▪ gate face-fast pass [session]  (5.9s)
08-01 04:07:34  • session #17 SF3 → Progress · 2 commit(s)  (18m18s)
08-01 04:07:34  • session #18 SF3 Deliver started (attempt 1/6)
08-01 04:28:56  • session #18 SF3 → RolledOver  (21m22s)
08-01 04:28:57  • session #19 SF3 Deliver started (attempt 1/6)
08-01 04:44:37  ▪ gate engine-fast pass [session]  (1m04s)
08-01 04:44:37  ▪ gate face-fast pass [session]  (8.2s)
08-01 04:44:38  • session #19 SF3 → Advanced · done SF3.3 · 3 commit(s)  (15m41s)
08-01 04:48:51  ▪ gate engine-fast pass [phase]  (0.0s)
08-01 04:48:51  ▪ gate face-fast pass [phase]  (0.0s)
08-01 04:48:51  ▪ gate engine-full pass [phase]  (3m52s)
08-01 04:48:51  ▪ gate face-full pass [phase]  (16.0s)
08-01 04:48:51  ✓ checkpoint SF3.3 confirmed
08-01 04:48:54  ▸ stage SF3 confirmed  (2h29m07s)
08-01 04:48:55  ▸ stage SF4 entered — The human queue is a first-class surface
08-01 04:48:55  ■ needs human — agent asked for a human in the tracker handoff (HUMAN: line) — resolve, then run `conductor resume`
08-01 10:00:13  • session #20 SF4 Deliver started (attempt 1/4)
08-01 10:27:27  • session #20 SF4 → RolledOver  (27m14s)
08-01 10:27:28  • session #21 SF4 Deliver started (attempt 1/4)
08-01 10:51:24  ▪ gate engine-fast pass [session]  (1m28s)
08-01 10:51:24  ▪ gate face-fast pass [session]  (1m08s)
08-01 10:51:25  • session #21 SF4 → Progress · 2 commit(s)  (23m57s)
08-01 10:51:26  • session #22 SF4 Deliver started (attempt 1/4)
08-01 11:15:54  ▪ gate engine-fast pass [session]  (1m14s)
08-01 11:15:55  ▪ gate face-fast pass [session]  (11.8s)
08-01 11:15:56  • session #22 SF4 → Progress · 3 commit(s)  (24m29s)
08-01 11:15:56  • session #23 SF4 Deliver started (attempt 1/4)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 23 · retries 1 (4 %) · overall Warn
⚠ [context-saturation] session #3: 24,790,251 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #4: 21,397,049 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #5: 36,996,007 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 24,716,690 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/sarban
working tree: M .conductor/REPORT.md, M SARBAN-FACE-TRACKER.md, M face-go/internal/tui/testdata/golden/attention.golden, M face-go/internal/tui/testdata/golden/default.golden, M face-go/internal/tui/testdata/golden/help.golden, M face-go/internal/tui/testdata/golden/home_demo.golden, M face-go/internal/tui/testdata/golden/home_disconnected.golden, M face-go/internal/tui/testdata/golden/home_offline_lastrun.golden (+5 more)
```

### Commits by session

- **s11 (SF2 Deliver)** — 3 commit(s):
  - [`3dc1cd8`](https://github.com/shaahink/conductor/commit/3dc1cd8) docs(tracker): SF2.2 handoff - two wire time formats, one clock vocabulary, and a UTC label that was never true
  - [`d02559c`](https://github.com/shaahink/conductor/commit/d02559c) test(face): rebaseline the nine frames the one clock vocabulary changed
  - [`f05791b`](https://github.com/shaahink/conductor/commit/f05791b) feat(face): one clock vocabulary - a parser for both wire formats, a date when it is not today, and three timestamps that were on the wire and on no screen
- **s12 (SF2 Deliver)** — 3 commit(s):
  - [`da28eb0`](https://github.com/shaahink/conductor/commit/da28eb0) docs(tracker): SF2.3 handoff - a budget block the Face never read, and an assertion inverted on the record
  - [`9cfe572`](https://github.com/shaahink/conductor/commit/9cfe572) test(face): rebaseline the frames the honest money changed, plus one new one
  - [`ef1620f`](https://github.com/shaahink/conductor/commit/ef1620f) feat(face): money honesty - OVER in dollars, a window that is not the lifetime, and a cost the Face can stand behind
- **s14 (SF3 Deliver)** — 3 commit(s):
  - [`0159bb4`](https://github.com/shaahink/conductor/commit/0159bb4) docs(tracker): SF3.1 claimed, SF3.2 data half landed with the rendering design in the ledger
  - [`e8fb296`](https://github.com/shaahink/conductor/commit/e8fb296) feat(face): read the card's identity and meta off the wire (SF3.2, part 1)
  - [`352bc1a`](https://github.com/shaahink/conductor/commit/352bc1a) feat(engine): the board's card meta - who moved a card, when, and how many times
- **s15 (SF3 Deliver)** — 3 commit(s):
  - [`3dd1b2b`](https://github.com/shaahink/conductor/commit/3dd1b2b) docs(tracker): SF3.2 claimed complete, SF3.3 is next with FU-OWNER-10 riding it
  - [`88a966a`](https://github.com/shaahink/conductor/commit/88a966a) test(face): rebaseline the six kanban frames, plus two new ones
  - [`3e7c4b3`](https://github.com/shaahink/conductor/commit/3e7c4b3) feat(face): the board answers where-are-we (SF3.2, part 2 - the rendering)
- **s17 (SF3 Deliver)** — 2 commit(s):
  - [`87a47c9`](https://github.com/shaahink/conductor/commit/87a47c9) docs(tracker): SF3.3 face wire mirror landed, the four renderers remain
  - [`7f2c4a7`](https://github.com/shaahink/conductor/commit/7f2c4a7) feat(face): the wire's git block and build identity, decoded (SF3.3, part 2a)
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

> SESSION-RESULT: Landed **parts 2 and 3 of SF4.2's four deliverables**, each committed with evidence. `bc7ff3f` closes **FU-OWNER-11**: every Telegram message is now stamped with plan name and session number at `TelegramService.SendAsync` — the one point every push, daily digest, `/status` reply, callback answer and test message crosses, so no existing call site can forget it and none added later can opt out (prefixing at `PushAsync`, as the previous handoff suggested, would have left the command replies and digest anonymous); a new run-start push and the existing run-end message carry repo, branch and `BuildInfo.Current.Full`, read off the assembly's own stamp rather than a hand-maintained c…

## Tracker handoff

```
last: **SF4.2 parts 2 and 3 of 4.** `bc7ff3f` FU-OWNER-11 - every push is stamped with plan+session
  at `TelegramService.SendAsync` (the ONE point every push, digest, /status reply and test message
  crosses, so nothing can forget); new run-start push plus run-end carry repo + `BuildInfo.Current`.
  `0d2ce47` - a NEW owner-queue item pushes to Telegram, diffed in `OwnerQueue.Write` against a
  seen-set kept as an HTML comment inside OWNER-QUEUE.md itself (no second source of truth; delete
  the file and it re-announces rather than going quiet). 12 new tests, 28/28 green.
next: **the last quarter, and it is all Go - face-go is untouched.** (a) Face owner-queue surface
  off `GET /owner/queue`: Home section when short, full-pane view on the free key `w` when long -
  do NOT add an 11th tab, SF1.3 capped it at ten - each row with age ("unknown" must never render
  as just-now) and what it unblocks; update the cmdbar help legend too (trap 11). (b) The Go half of
  FU-OWNER-13: `reloadPending` renders as *waiting*, not *unconfigured*. Read face-go/STYLE.md first.
green: neighbour battery **156/156** - session 21's 18 reds were the full disk, confirmed, do not
  re-run it. C: now has 2.8 GB free. Build clean, 0 warnings.
red: nothing.
open: bugs **#15 #16 #17 #18 #19**.
```
