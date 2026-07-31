# Conductor — Sarban face - the watcher and the surfaces run report

_Updated 2026-07-31 20:26 UTC · branch `feat/sarban` · HEAD `3e53a3c`_

**Status:** Idle
**Stage:** SF0 — The ledger closes - the core run's leftovers · attempts used 0 · working ▸ SF0.3
**Checkpoints:** 2/24 done · **Sessions run:** 2 · **Cost:** $23.8761 (agent $23.8590 + gates $0.0171) · **Tokens:** 387,424 in / 145,379 out

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| SF0 | The ledger closes - the core run's leftovers | █████░░░░░ 2/4 | **← active** |
| SF1 | The face sheds dead weight | ░░░░░░░░░░ 0/3 | todo |
| SF2 | The face tells the truth kindly - state, time, money | ░░░░░░░░░░ 0/3 | todo |
| SF3 | Reading a session becomes cheap | ░░░░░░░░░░ 0/3 | todo |
| SF4 | The human queue is a first-class surface | ░░░░░░░░░░ 0/2 | todo |
| SF5 | Supervision without a polling meter | ░░░░░░░░░░ 0/4 | todo |
| SF6 | The prompt bank compounds | ░░░░░░░░░░ 0/3 | todo |
| SF7 | Ship the era | ░░░░░░░░░░ 0/2 | todo |

<details><summary>SF0 — The ledger closes - the core run's leftovers (2/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF0.1 | Bugs 6 and 11 die as a class — an inert plan key is either wired to its documented meaning or rejected at load, never readable-and-ignored — and bug 2 plus FU-OWNER-12 stop the notification path lying: no start line for a service that early-returned, and one logged sentence at run start saying whether pushes can be delivered at all | ✅ DONE | [`5217986`](https://github.com/shaahink/conductor/commit/5217986) |
| SF0.2 | Bug 10 — a claim made during a Verify or Audit session is counted, stamped and confirmed like any other, with the empty-string GateSummary evidence fallback fixed in the same change — plus bug 4 (a phase-gate RED names the session kind it actually queues), bug 3 (a confirmed last stage completes instead of spinning forever) and bug 8 (the harness git helper asserts its exit code, so NewCommits assertions stop being vacuous) | ✅ DONE | - |
| SF0.3 | Bugs 9, 5, 12 and 13 — one pid-liveness policy everywhere including MCP, bg status survives an uninspectable pid, bg start stops leaking the caller's stdout handle, bg logs reads a live log — and FU-OWNER-9's self-PID guard lands with the locked-by-conductor warning in the fix prompt | ⬜ TODO | - |
| SF0.4 | Open bugs survive the run that found them — a new run in this repo sees the previous run's open rows, and run-ended says how many are open — and every remaining followups.md row is fixed, closed with its evidence, or re-homed to a living owner, with FU-F1-07 verified against SC8's scanning verb-parity test and FU-B10-2 measured from the core run's own sessions | ⬜ TODO | - |

</details>

<details><summary>SF1 — The face sheds dead weight (0/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF1.1 | Verifier scores are served by a real endpoint and the Report tab renders them without SQL | ⬜ TODO | - |
| SF1.2 | The Dev SQL console and its traces are gone — tab, /report/query, report --query — while MCP run_query stays for chat and the two non-SQL Dev panels are re-homed, not deleted | ⬜ TODO | - |
| SF1.3 | The face has at most ten tabs after a written consolidation note: Console folds into Agent as a raw toggle, Timeline merges with Sessions into one history surface; keys, help and goldens regenerated | ⬜ TODO | - |

</details>

<details><summary>SF2 — The face tells the truth kindly - state, time, money (0/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF2.1 | Home shows one honest connection line with age, start-a-run instructions only when no run exists, a last-run summary card when offline, one Connected definition, and consistent path casing | ⬜ TODO | - |
| SF2.2 | One shared time formatter renders local time with relative age and a date when not today; the Timeline UTC mislabel is fixed and the previously-unrendered timestamps render | ⬜ TODO | - |
| SF2.3 | Over-budget renders as OVER never zero-percent headroom; window and lifetime spend are distinguished; the top bar shows in-flight session cost live; the attempts marker has a legend | ⬜ TODO | - |

</details>

<details><summary>SF3 — Reading a session becomes cheap (0/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF3.1 | Tool calls render as one-liners and each session has a digest panel — tool mix, files touched, claims, bg-purpose storyline; fold is rune-safe | ⬜ TODO | - |
| SF3.2 | The kanban groups by stage with the active stage highlighted, card meta visible unselected, column totals, skips separated from Done, in-column scroll, and a you-are-here ribbon | ⬜ TODO | - |
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

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
07-31 20:15:18  ◆ run started · Sarban face - the watcher and the surfaces
07-31 20:15:19  ▸ stage SF0 entered — The ledger closes - the core run's leftovers
07-31 20:15:19  • session #1 SF0 Deliver started (attempt 1/6)
07-31 20:38:57  ▪ gate engine-fast pass [session]  (48.1s)
07-31 20:38:57  ▪ gate face-fast pass [session]  (36.5s)
07-31 20:38:58  • session #1 SF0 → Advanced · done SF0.1 · 1 commit(s)  (23m38s)
07-31 20:38:58  • session #2 SF0 Deliver started (attempt 1/6)
07-31 21:26:24  ▪ gate engine-fast pass [session]  (1m14s)
07-31 21:26:24  ▪ gate face-fast pass [session]  (11.7s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 2 · retries 0 (0 %) · overall Ok
✓ no health concerns detected
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/sarban
working tree: clean
```

### Commits by session

- **s1 (SF0 Deliver)** — 1 commit(s):
  - [`5217986`](https://github.com/shaahink/conductor/commit/5217986) fix(plan): inert model pins die at load, verifyEachDelivery finally decides, and a run says whether it can notify
- **s2 (SF0 Deliver)** — 2 commit(s):
  - [`3e53a3c`](https://github.com/shaahink/conductor/commit/3e53a3c) docs(tracker): SF0.2 handoff - what landed, what SF0.3 picks up, and the live-proof recipe
  - [`fdd78ae`](https://github.com/shaahink/conductor/commit/fdd78ae) fix(verdict): every claim gets a session, the RED line names what it queues, and a confirmed last stage stops spinning

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

> SESSION-RESULT: **SF0.2 landed and is claimed** (`conductor task --done SF0.2 --evidence .conductor/evidence/SF0/SF0.2-verdict-counts-every-claim.md`), commits `fdd78ae` (fix) and `3e53a3c` (handoff), pushed to `feat/sarban`; working tree clean. All four of the core run's bugs in this checkpoint are fixed, and the three *engine* bugs were first **reproduced against the pre-fix engine at `5217986` with the identical test code** — pre-fix 3 failed / 13 passed (bug 3's hang killed at 4m1s by the test's own deadline), fixed 16/16, full suite 1485/1485. **Bug 10:** `EvaluateSessionAsync` read the work graph on the delivery path only, so a checkpoint claimed during a Verify or Audit session was co…

## Tracker handoff

```
last: **SF0.2 CLAIMED** (commit `fdd78ae`) — bugs 10, 4, 3, 8. All three ENGINE bugs were first
  reproduced against the engine at `5217986` with the same test code, then fixed: pre-fix 3 failed
  / 13 passed (bug 3's hang killed at 4m1s by the test's own deadline), fixed 16/16, suite 1485/1485.
  Bug 10's rider is bigger than the ledger says: the dead `GateSummary` fallback was OVERWRITING the
  agent's `--evidence` path with the battery token — that is why SF0.1's row reads `engine-fast:OK`.
stage: **SF0 IN PROGRESS** (attempt 1). Evidence: `.conductor/evidence/SF0/` (gitignored, local).
gate: not run by me (conductor owns it). Fast loop green: build clean, full suite 1485/1485.
next: **SF0.3** — bugs 9, 5, 12, 13 (one pid-liveness policy incl. MCP, bg status survives an
  uninspectable pid, bg start's leaked stdout handle, bg logs on a LIVE log) + FU-OWNER-9's self-PID
  guard. Read the carried-forward bug table in `.conductor/followups.md`; those rows are NOT in run.db.
trap: for a live proof, copy `SC51BlockedUntilLiveTests` — `conductor.exe` sits beside the test
  assembly, the plan is serialised so the CLI and engine share one run.db, a marker file switches the
  fake agent's behaviour, and the agent is THREE args (`"/c"`, absolute `.cmd`, `"{prompt}"`). In cmd
  put the redirect FIRST or the digit before `>` is read as a stream number. `IProgressSink` is 4
  members — a recording sink costs ten lines. A pipe in `--filter` needs PowerShell's `--%`.
  Second conductor run live on this machine: no install.ps1, no killing pids unchecked, own port.
```
