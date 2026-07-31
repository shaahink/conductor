# Conductor — Sarban face - the watcher and the surfaces run report

_Updated 2026-07-31 19:38 UTC · branch `feat/sarban` · HEAD `5217986`_

**Status:** Idle
**Stage:** SF0 — The ledger closes - the core run's leftovers · attempts used 0 · working ▸ SF0.2
**Checkpoints:** 1/24 done · **Sessions run:** 1 · **Cost:** $10.7314 (agent $10.7229 + gates $0.0085) · **Tokens:** 176,730 in / 68,746 out

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| SF0 | The ledger closes - the core run's leftovers | ██░░░░░░░░ 1/4 | **← active** |
| SF1 | The face sheds dead weight | ░░░░░░░░░░ 0/3 | todo |
| SF2 | The face tells the truth kindly - state, time, money | ░░░░░░░░░░ 0/3 | todo |
| SF3 | Reading a session becomes cheap | ░░░░░░░░░░ 0/3 | todo |
| SF4 | The human queue is a first-class surface | ░░░░░░░░░░ 0/2 | todo |
| SF5 | Supervision without a polling meter | ░░░░░░░░░░ 0/4 | todo |
| SF6 | The prompt bank compounds | ░░░░░░░░░░ 0/3 | todo |
| SF7 | Ship the era | ░░░░░░░░░░ 0/2 | todo |

<details><summary>SF0 — The ledger closes - the core run's leftovers (1/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SF0.1 | Bugs 6 and 11 die as a class — an inert plan key is either wired to its documented meaning or rejected at load, never readable-and-ignored — and bug 2 plus FU-OWNER-12 stop the notification path lying: no start line for a service that early-returned, and one logged sentence at run start saying whether pushes can be delivered at all | ✅ DONE | - |
| SF0.2 | Bug 10 — a claim made during a Verify or Audit session is counted, stamped and confirmed like any other, with the empty-string GateSummary evidence fallback fixed in the same change — plus bug 4 (a phase-gate RED names the session kind it actually queues), bug 3 (a confirmed last stage completes instead of spinning forever) and bug 8 (the harness git helper asserts its exit code, so NewCommits assertions stop being vacuous) | ⬜ TODO | - |
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

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
07-31 20:15:18  ◆ run started · Sarban face - the watcher and the surfaces
07-31 20:15:19  ▸ stage SF0 entered — The ledger closes - the core run's leftovers
07-31 20:15:19  • session #1 SF0 Deliver started (attempt 1/6)
07-31 20:38:57  ▪ gate engine-fast pass [session]  (48.1s)
07-31 20:38:57  ▪ gate face-fast pass [session]  (36.5s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 1 · retries 0 (0 %) · overall Ok
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

> SESSION-RESULT: SF0.1 delivered and claimed with evidence at `.conductor/evidence/SF0/SF0.1-inert-keys-and-the-notification-path.md` (commit `5217986`, pushed to `feat/sarban`) — bug 6's two inert model pins deleted and refused at plan load with doctor naming the key that works, bug 11's `verifyEachDelivery` wired as the lowest-precedence input to `EffectiveSkipVerification` with the phantom `ShouldVerify` deleted, bug 2's false `Run services started: TelegramService` replaced by an honest started/not-started pair via the new `IReportsStartOutcome`, and FU-OWNER-12's readiness sentence now logged once at run start in `TelegramReadiness`' words. Nothing is red: build clean, 87 scoped tests pa…

## Tracker handoff

```
last: **SF0.1 CLAIMED** — bugs 6, 11, 2 + FU-OWNER-12. Bug 6's two model pins are DELETED and
  refused at plan load (bug-7's `[JsonExtensionData]` precedent; deleting the property is the
  load-bearing half — `plan set` validates off the type graph). Bug 11's `verifyEachDelivery` is
  WIRED as lowest-precedence input to `EffectiveSkipVerification` — not deleted, because
  `conductor-maestro.plan.json:117` has set it false since M3; the phantom `ShouldVerify` is gone.
stage: **SF0 IN PROGRESS** (attempt 1). Evidence: `.conductor/evidence/SF0/`.
gate: not run by me (conductor owns it). Fast loop green: build clean, 87 tests pass across the new
  `SF0_1InertPlanKeysTests` + DefaultQaPolicy/Advisor/PlanSet/ItemQa/QaDial suites.
next: **SF0.2** — bug 10 (a claim made during Verify/Audit belongs to no session) with the
  `rec.GateSummary ?? completed` empty-string evidence fallback fixed in the SAME change, plus bugs
  4, 3 and 8. Read the carried-forward bug table in `.conductor/followups.md`; those eleven are NOT
  in your run.db.
trap: reusable proof rig at `%TEMP%\sarban-proofs\sf01` — wire a fake agent as THREE args
  (`"/c"`, absolute `.cmd`, `"{prompt}"`); combining them makes cmd fail silently and every verdict
  reads `commits 0`. `ClearProviders()` means ILogger lines never reach `conductor.log` — only
  `_ctx.Log` does. A pipe in a `dotnet test --filter` needs PowerShell's `--%` before `conductor bg`.
  Second conductor run live on this machine: no install.ps1, no killing pids unchecked, own port.
```
