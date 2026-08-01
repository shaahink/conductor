# Conductor — Sarban face - the watcher and the surfaces run report

_Updated 2026-08-01 01:57 UTC · branch `feat/sarban` · HEAD `0159bb4`_

**Status:** Idle
**Stage:** SF3 — Reading a session becomes cheap · attempts used 0 · working ▸ SF3.2
**Checkpoints:** 11/24 done · **Sessions run:** 14 · **Cost:** $145.9813 (agent $145.9093 + gates $0.0719) · **Tokens:** 2,394,787 in / 823,733 out
**Confirmed phases:** SF0, SF1, SF2

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| SF0 | The ledger closes - the core run's leftovers | ██████████ 4/4 | confirmed ✓ |
| SF1 | The face sheds dead weight | ██████████ 3/3 | confirmed ✓ |
| SF2 | The face tells the truth kindly - state, time, money | ██████████ 3/3 | confirmed ✓ |
| SF3 | Reading a session becomes cheap | ███░░░░░░░ 1/3 | **← active** |
| SF4 | The human queue is a first-class surface | ░░░░░░░░░░ 0/2 | todo |
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

<details><summary>SF3 — Reading a session becomes cheap (1/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF3.1 | Tool calls render as one-liners and each session has a digest panel — tool mix, files touched, claims, bg-purpose storyline; fold is rune-safe | ✅ DONE | - |
| SF3.2 | The kanban groups by stage with the active stage highlighted, card meta visible unselected, column totals, skips separated from Done, in-column scroll, and a you-are-here ribbon | 🔄 IN PROGRESS | - |
| SF3.3 | Branch, dirty state, ahead-behind and HEAD sha are on the wire and in the face; session history shows commit subjects; the sidebar cues execution-vs-declared stage order | ⬜ TODO | - |

</details>

<details><summary>SF4 — The human queue is a first-class surface (0/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF4.1 | OWNER-QUEUE.md and GET /owner/queue collect every open human item — HUMAN lines, ownerGates, parks with age, blocked-until waits — each saying what it unblocks and the command that clears it, regenerated at session boundaries | ⬜ TODO | - |
| SF4.2 | The face surfaces the owner queue with age and unblocks, and a newly-arrived item pushes to Telegram | ⬜ TODO | - |

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

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-01 00:41:50  ▪ gate engine-fast FAIL [phase]  (8.9s)
08-01 00:41:50  ▪ gate face-fast pass [phase]  (8.8s)
08-01 00:41:50  ▪ gate engine-full FAIL [phase]  (5m33s)
08-01 00:41:50  ▪ gate face-full pass [phase]  (26.5s)
08-01 00:41:51  • session #8 SF1 Fix started (attempt 2/4)
08-01 00:48:20  • session #8 SF1 → KilledByUser  (6m29s)
08-01 00:53:36  ◆ run resumed · Sarban face - the watcher and the surfaces
08-01 01:04:53  ▪ gate engine-fast pass [phase]  (1m11s)
08-01 01:04:53  ▪ gate face-fast pass [phase]  (0.0s)
08-01 01:04:53  ▪ gate engine-full pass [phase]  (4m06s)
08-01 01:04:53  ▪ gate face-full pass [phase]  (11.1s)
08-01 01:04:53  ✓ checkpoint SF1.2 confirmed
08-01 01:04:53  ▸ stage SF1 confirmed  (2h14m17s)
08-01 01:04:54  ▸ stage SF2 entered — The face tells the truth kindly - state, time, money
08-01 01:04:55  • session #9 SF2 Deliver started (attempt 1/6)
08-01 01:20:16  • session #9 SF2 → RolledOver  (15m21s)
08-01 01:20:17  • session #10 SF2 Deliver started (attempt 1/6)
08-01 01:39:39  ▪ gate engine-fast pass [session]  (1m06s)
08-01 01:39:39  ▪ gate face-fast pass [session]  (8.6s)
08-01 01:39:40  • session #10 SF2 → Advanced · done SF2.1 · 4 commit(s)  (19m23s)
08-01 01:39:40  • session #11 SF2 Deliver started (attempt 1/6)
08-01 01:54:55  ▪ gate engine-fast pass [session]  (1m14s)
08-01 01:54:55  ▪ gate face-fast pass [session]  (7.1s)
08-01 01:54:57  • session #11 SF2 → Advanced · done SF2.2 · 3 commit(s)  (15m16s)
08-01 01:54:57  • session #12 SF2 Deliver started (attempt 1/6)
08-01 02:15:39  ▪ gate engine-fast pass [session]  (1m09s)
08-01 02:15:39  ▪ gate face-fast pass [session]  (6.5s)
08-01 02:15:40  • session #12 SF2 → Advanced · done SF2.3 · 3 commit(s)  (20m42s)
08-01 02:19:45  ▪ gate engine-fast pass [phase]  (0.0s)
08-01 02:19:45  ▪ gate face-fast pass [phase]  (0.0s)
08-01 02:19:45  ▪ gate engine-full pass [phase]  (3m44s)
08-01 02:19:45  ▪ gate face-full pass [phase]  (16.9s)
08-01 02:19:45  ✓ checkpoint SF2.3 confirmed
08-01 02:19:45  ▸ stage SF2 confirmed  (1h14m51s)
08-01 02:19:46  ▸ stage SF3 entered — Reading a session becomes cheap
08-01 02:19:46  • session #13 SF3 Deliver started (attempt 1/6)
08-01 02:40:22  • session #13 SF3 → RolledOver  (20m35s)
08-01 02:40:22  • session #14 SF3 Deliver started (attempt 1/6)
08-01 02:57:38  ▪ gate engine-fast pass [session]  (1m06s)
08-01 02:57:38  ▪ gate face-fast pass [session]  (12.1s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 14 · retries 1 (7 %) · overall Warn
⚠ [context-saturation] session #3: 24,790,251 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #4: 21,397,049 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #5: 36,996,007 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 24,716,690 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/sarban
working tree: clean
```

### Commits by session

- **s3 (SF0 Deliver)** — 3 commit(s):
  - [`ccf65e5`](https://github.com/shaahink/conductor/commit/ccf65e5) docs(followups): close FU-OWNER-9 on SF0.3's evidence, and say plainly which third of it is still open
  - [`10e1812`](https://github.com/shaahink/conductor/commit/10e1812) docs(tracker): SF0.3 handoff - what landed, the two-half fix bug 12 needed, and the prompt-length cliff
  - [`c84ccfc`](https://github.com/shaahink/conductor/commit/c84ccfc) fix(bg): one pid-liveness policy including MCP, a bg start that lets go of the caller's pipe, a live log that reads, and a supervising pid the agent is told
- **s4 (SF0 Deliver)** — 3 commit(s):
  - [`ab37e2f`](https://github.com/shaahink/conductor/commit/ab37e2f) docs(tracker): SF0.4 handoff - SF0 closes, and the two rig traps that cost this session an hour
  - [`14c4be1`](https://github.com/shaahink/conductor/commit/14c4be1) docs(followups): reconcile every remaining row - fixed, closed with evidence, or re-homed to an owner that can act
  - [`d5b81cb`](https://github.com/shaahink/conductor/commit/d5b81cb) fix(bugs): an open bug outlives the RUN that found it, not just the session
- **s5 (SF1 Deliver)** — 3 commit(s):
  - [`a0f030a`](https://github.com/shaahink/conductor/commit/a0f030a) docs(tracker): SF1.1 handoff - the endpoint that unblocks SF1.2, and the two rig traps behind it
  - [`ada42a4`](https://github.com/shaahink/conductor/commit/ada42a4) test(face): rebaseline the two Report goldens for the typed scores section
  - [`9d993ef`](https://github.com/shaahink/conductor/commit/9d993ef) feat(scores): verifier scores get a real endpoint, so a rendered report stops needing SQL
- **s6 (SF1 Deliver)** — 3 commit(s):
  - [`ed4ed29`](https://github.com/shaahink/conductor/commit/ed4ed29) docs(tracker): SF1.2 handoff - twelve tabs, a new bug class, and a second writer in this tree
  - [`4bb6c24`](https://github.com/shaahink/conductor/commit/4bb6c24) test(face): rebaseline every golden for twelve tabs, plus Home's Wiring and Report's token table
  - [`8f96ef2`](https://github.com/shaahink/conductor/commit/8f96ef2) feat(face): delete the SQL console and re-home the two panels that were never the problem
- **s10 (SF2 Deliver)** — 4 commit(s):
  - [`6d2c024`](https://github.com/shaahink/conductor/commit/6d2c024) docs(tracker): SF2.1 handoff - one connection line, a run that outlives its engine, and a truncate() that eats meaning
  - [`bb58cc8`](https://github.com/shaahink/conductor/commit/bb58cc8) test(face): rebaseline every golden for the honest Home - one engine line, normalised paths, and a run that outlives its engine
  - [`2a6b1c3`](https://github.com/shaahink/conductor/commit/2a6b1c3) fix(face): the honest connection line was being truncated into a lie, and Next steps still offered a live agent with no engine
  - [`93611dd`](https://github.com/shaahink/conductor/commit/93611dd) feat(face): Home answers "what is happening and what should I do" - one honest connection line, and a run that outlives its engine
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

> SESSION-RESULT:** SF3.1 (the digest layer) is **claimed DONE** — session 13 had delivered it in `dee3074` + `6170612` but was killed before claiming; I verified it green (`go build`/`vet`/`test`, all 7 packages ok) and claimed it against `.conductor/evidence/SF3/SF3.1-summary.md`. SF3.2 is **IN PROGRESS with its data half landed in two commits**: `352bc1a` (engine) makes `TaskGraph` fold `SessionStarted` so every card knows the session whose work last moved it, plus `statusSinceUtc` and a per-card pickup count, with same→same treated as a metadata refresh that must not restamp or re-attribute — five new fold tests, 161 engine tests green across TaskGraph/W1/TaskView/ControlPlaneServer/McpTas…

## Tracker handoff

```
last: **SF3.1 CLAIMED** (session 13 delivered it and died before claiming; I re-ran the face fast
  loop, all 7 packages ok, and claimed it — `dee3074` + `6170612`, evidence
  `.conductor/evidence/SF3/SF3.1-summary.md`). **Do not redo SF3.1.**
half-done: **SF3.2, data half landed, rendering NOT started.** `352bc1a` engine —
  `TaskGraph` folds `SessionStarted` so a card knows the session that moved it, plus
  `statusSinceUtc` and per-card `attempts`; 161 engine tests green. `e8fb296` face —
  `api.TaskDto` now decodes `kind`/`stageId`/`confirmed` (served since W1.4, dropped until now)
  and the three new meta fields, with `TaskDto.Stage()`. Tree is clean: `tab_kanban.go` is
  untouched at HEAD, nothing half-written was left in it.
next: the rendering — stage grouping, active-stage mark, always-on card meta, `n/total` headers,
  the skipped shelf, in-column scroll, the you-are-here ribbon. **The full design is in the
  ledger note "SF3.2 session 14" (item 4) — read it before designing anything.**
gap: `internal/api/demo.go` tasks carry none of the new fields, so `--demo` shows the old board;
  `blocked` still maps to the TODO column and renders as a plain todo card.
open: bugs **#15 #16 #17 #18 #19** open. #18 is stale-ish; #19 (claims empty in every digest) is
  engine-side and unfixed.
```
