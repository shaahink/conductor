# Conductor — Sarban core - the engine says what it knows run report

_Updated 2026-07-31 09:45 UTC · branch `feat/sarban` · HEAD `314e868`_

**Status:** Idle
**Stage:** SC4 — Verdicts judge the work, not the environment · attempts used 0 · working ▸ SC4.4
**Checkpoints:** 14/26 done · **Sessions run:** 15 · **Cost:** $196.0575 (agent $195.9668 + gates $0.0908) · **Tokens:** 2,903,079 in / 1,232,811 out
**Confirmed phases:** SC1, SC2, SC3

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| SC1 | Telegram actually delivers | ██████████ 3/3 | confirmed ✓ |
| SC2 | Truthful surfaces | ██████████ 4/4 | confirmed ✓ |
| SC3 | Config traps die at authoring time | ██████████ 4/4 | confirmed ✓ |
| SC4 | Verdicts judge the work, not the environment | ████████░░ 3/4 | **← active** |
| SC5 | The engine can wait, detach, and correct the board | ░░░░░░░░░░ 0/4 | todo |
| SC6 | Clean history without lying about it | ░░░░░░░░░░ 0/2 | todo |
| SC7 | The transcript captures structure | ░░░░░░░░░░ 0/2 | todo |
| SC8 | The program knows what it is and can update itself | ░░░░░░░░░░ 0/3 | todo |

<details> ✅<summary>SC1 — Telegram actually delivers (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC1.1 | The engine starts Telegram on every run path: a configured live run delivers a real session-end push and answers two-way status, and a regression test drives the real run-start path | ✅ DONE | [`b7d6eb4`](https://github.com/shaahink/conductor/commit/b7d6eb4) |
| SC1.2 | /telegram/status carries a derived willDeliver verdict; POST /telegram/test routes through the real send queue or loudly says it bypassed it; StartAsync logs on both outcomes naming any missing half | ✅ DONE | [`160f731`](https://github.com/shaahink/conductor/commit/160f731) |
| SC1.3 | Late token or telegram-block configuration takes effect without a full restart, or every surface honestly says restart required — including the NoOp-service swap path; the chat-id bootstrap is documented | ✅ DONE | [`7d7372e`](https://github.com/shaahink/conductor/commit/7d7372e) |

</details>

<details> ✅<summary>SC2 — Truthful surfaces (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC2.1 | conductor status never reports a healthy run as interrupted during the verdict window — a gate executing counts as engine liveness — with a regression test | ✅ DONE | [`a3e970e`](https://github.com/shaahink/conductor/commit/a3e970e) |
| SC2.2 | Sticky failure fields carry timestamps or clear; phase-gate lines emit the canonical gates GREEN or RED token with an honest no-gates-configured state; attempt numbering agrees across the two log lines; doctor warns on zero-gate stages | ✅ DONE | [`603fbbb`](https://github.com/shaahink/conductor/commit/603fbbb) |
| SC2.3 | /state carries in-flight session spend plus costSpent, costCap, costRemaining, meanSessionCost, checkpointsRemaining, and window-vs-lifetime spend after a budget approval | ✅ DONE | [`55da220`](https://github.com/shaahink/conductor/commit/55da220) |
| SC2.4 | A completed run leaves RUN-SUMMARY.md; report and status work offline from run.db; conductor log reads a live log without crashing; the SSE streams tail incrementally instead of re-reading the backlog every second | ✅ DONE | [`87d7fcd`](https://github.com/shaahink/conductor/commit/87d7fcd) |

</details>

<details> ✅<summary>SC3 — Config traps die at authoring time (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC3.1 | doctor FAILS when agent.model is set without the model token in both args and resumeArgs; unknown RunIf or SkipIf tokens fail at plan load naming the valid vocabulary | ✅ DONE | [`d4c9103`](https://github.com/shaahink/conductor/commit/d4c9103) |
| SC3.2 | plan set refuses an absent leaf key without --create, suggests the dotted path when one nested leaf matches, warns before stripping comments, and reaches the live engine or prints the exact reload command | ✅ DONE | [`587eadd`](https://github.com/shaahink/conductor/commit/587eadd) |
| SC3.3 | A literal brace in stage notes or promptExtra is caught by doctor at plan load; at runtime an unresolved placeholder parks the run and writes the refusal to conductor.log; a double brace escapes to a literal | ✅ DONE | [`503d7e6`](https://github.com/shaahink/conductor/commit/503d7e6) |
| SC3.4 | The default advisor invocation works headless or is refused loudly at load with a doctor line; plan-config.md matches the code | ✅ DONE | [`abe0eb1`](https://github.com/shaahink/conductor/commit/abe0eb1) |

</details>

<details><summary>SC4 — Verdicts judge the work, not the environment (3/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC4.1 | The battery waits for the session's tracked bg children to exit, and retries a failed required gate once before declaring GatesRed; the failure line carries duration vs last passing duration | ✅ DONE | [`ba9b523`](https://github.com/shaahink/conductor/commit/ba9b523) |
| SC4.2 | NoProgress requires no commits AND no newly-DONE checkpoints; chore conductor commits are excluded from the verdict's commit count | ✅ DONE | [`1ce4ba7`](https://github.com/shaahink/conductor/commit/1ce4ba7) |
| SC4.3 | satelliteRepos are diffed for hasCommits; the gate cache key covers the gate's own working directory HEAD and its command text; skipIfFresh accounts for a dirty tree | ✅ DONE | - |
| SC4.4 | Queued injections render at the top of the composed prompt, and a gate-failures block they supersede is stamped SUPERSEDED or dropped | ⬜ TODO | - |

</details>

<details><summary>SC5 — The engine can wait, detach, and correct the board (0/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC5.1 | conductor task --blocked-until with a reason yields a BlockedUntil outcome the run loop honours by sleeping and respawning once, burning no attempt; the wait is visible on status, state and the report | ⬜ TODO | - |
| SC5.2 | conductor run --detach spawns the engine into its own process group, prints pid and control-plane url, and survives its launching shell; the stall warning names the likely cause and the remedy | ⬜ TODO | - |
| SC5.3 | task --todo, --blocked, --skipped and --amend exist through the shared task-writes path, and --in-progress reports the post-fold status instead of unconditional success | ⬜ TODO | - |
| SC5.4 | bg logs on an agent row points at that session's stream file, and bg status runtimes are computed in one timezone | ⬜ TODO | - |

</details>

<details><summary>SC6 — Clean history without lying about it (0/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC6.1 | Pure status-transition updates no longer land commits, and any squash runs after the stage's final state write | ⬜ TODO | - |
| SC6.2 | The squash works on a dirty tree, reports real counts, logs git stderr and exit code on failure, un-marks the stage on failure, aborts a half-started rebase, and degrades gracefully off Windows | ⬜ TODO | - |

</details>

<details><summary>SC7 — The transcript captures structure (0/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC7.1 | Tool events are stored structured — name plus extracted fields, values truncated, JSON never cut — with back-compat reading of old lines; writes outside the repo are counted and noted in the session verdict | ⬜ TODO | - |
| SC7.2 | The provider emits one-liner tool lines on the wire, and a per-session digest is computed, stored and served on /sessions matching the spec's worked example | ⬜ TODO | - |

</details>

<details><summary>SC8 — The program knows what it is and can update itself (0/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC8.1 | conductor version and GET /version report semver, git sha and build date stamped at build; install.ps1 prints the version before and after | ⬜ TODO | - |
| SC8.2 | Tag-height versioning is automatic and reconciled with release.yml so a released binary answers with its tag; CHANGELOG.md carries a section per release | ⬜ TODO | - |
| SC8.3 | conductor update downloads and safely swaps the matching release binary, refusing while a run is live; doctor reports update-available | ⬜ TODO | - |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | SC1 | Deliver | 1 | 07-31 01:48 | 0:23 | Advanced | SC1.1 | 2 | engine-fast:OK · face-fast:OK | $9.6547 | $0.0088 | 160,368/73,647 |
| 2 | SC1 | Deliver | 1 | 07-31 02:13 | 0:28 | Advanced | SC1.2 | 2 | engine-fast:OK · face-fast:OK | $10.1037 | $0.0045 | 162,317/83,343 |
| 3 | SC1 | Deliver | 1 | 07-31 02:43 | 0:40 | Advanced | SC1.3 | 3 | engine-fast:OK · face-fast:OK | $19.5776 | $0.0049 | 247,269/114,099 |
| 4 | SC1 | Fix | 2 | 07-31 03:26 | 0:15 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $4.9738 | $0.0141 | 103,230/44,384 |
| 5 | SC2 | Deliver | 1 | 07-31 03:46 | 0:55 | Advanced | SC2.1 | 2 | engine-fast:OK · face-fast:OK | $12.5819 | $0.0095 | 187,251/84,278 |
| 6 | SC2 | Deliver | 1 | 07-31 04:43 | 0:23 | Advanced | SC2.2 | 3 | engine-fast:OK · face-fast:OK | $13.9238 | $0.0085 | 195,137/73,903 |
| 7 | SC2 | Deliver | 1 | 07-31 05:08 | 0:29 | Advanced | SC2.3 | 2 | engine-fast:OK · face-fast:OK | $11.8482 | $0.0037 | 196,445/75,881 |
| 8 | SC2 | Deliver | 1 | 07-31 05:38 | 0:36 | Advanced | SC2.4 | 1 | engine-fast:OK · face-fast:OK | $23.5470 | $0.0048 | 290,900/116,641 |
| 9 | SC3 | Deliver | 1 | 07-31 06:17 | 0:18 | Advanced | SC3.1 | 2 | engine-fast:OK · face-fast:OK | $7.6133 | $0.0041 | 148,043/61,242 |
| 10 | SC3 | Deliver | 1 | 07-31 06:36 | 0:26 | Advanced | SC3.2 | 2 | engine-fast:OK · face-fast:OK | $11.5864 | $0.0042 | 175,423/83,786 |
| 11 | SC3 | Deliver | 1 | 07-31 07:04 | 0:27 | Advanced | SC3.3 | 2 | engine-fast:OK · face-fast:OK | $13.5575 | $0.0041 | 214,329/81,757 |
| 12 | SC3 | Deliver | 1 | 07-31 07:32 | 0:27 | Advanced | SC3.4 | 2 | engine-fast:OK · face-fast:OK | $11.9983 | $0.0061 | 194,134/85,689 |
| 13 | SC4 | Deliver | 1 | 07-31 08:02 | 0:38 | Advanced | SC4.1 | 1 | engine-fast:OK · face-fast:OK | $19.6442 | $0.0048 | 266,945/99,065 |
| 14 | SC4 | Deliver | 1 | 07-31 08:42 | 0:30 | Advanced | SC4.2 | 1 | engine-fast:OK · face-fast:OK | $11.2110 | $0.0044 | 165,557/70,587 |
| 15 | SC4 | Deliver | 1 | 07-31 09:13 | 0:31 | Advanced | SC4.3 | 2 | engine-fast:OK · face-fast:OK | $14.1455 | $0.0043 | 195,731/84,509 |

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
07-31 07:16:16  • session #8 SC2 → Advanced · done SC2.4 · 1 commit(s)  (37m34s)
07-31 07:17:48  ▪ gate engine-fast pass [phase]  (0.0s)
07-31 07:17:48  ▪ gate face-fast pass [phase]  (0.0s)
07-31 07:17:48  ▪ gate engine-full pass [phase]  (1m23s)
07-31 07:17:48  ▪ gate face-full pass [phase]  (6.2s)
07-31 07:17:48  ▸ stage SC2 confirmed  (2h31m03s)
07-31 07:17:49  ▸ stage SC3 entered — Config traps die at authoring time
07-31 07:17:49  • session #9 SC3 Deliver started (attempt 1/6)
07-31 07:36:55  ▪ gate engine-fast pass [session]  (37.8s)
07-31 07:36:55  ▪ gate face-fast pass [session]  (3.1s)
07-31 07:36:56  • session #9 SC3 → Advanced · done SC3.1 · 2 commit(s)  (19m06s)
07-31 07:36:56  • session #10 SC3 Deliver started (attempt 1/6)
07-31 08:04:39  ▪ gate engine-fast pass [session]  (38.9s)
07-31 08:04:39  ▪ gate face-fast pass [session]  (3.3s)
07-31 08:04:40  • session #10 SC3 → Advanced · done SC3.2 · 2 commit(s)  (27m43s)
07-31 08:04:40  • session #11 SC3 Deliver started (attempt 1/6)
07-31 08:32:32  ▪ gate engine-fast pass [session]  (37.0s)
07-31 08:32:32  ▪ gate face-fast pass [session]  (4.4s)
07-31 08:32:32  • session #11 SC3 → Advanced · done SC3.3 · 2 commit(s)  (27m52s)
07-31 08:32:33  • session #12 SC3 Deliver started (attempt 1/6)
07-31 09:01:18  ▪ gate engine-fast pass [session]  (35.6s)
07-31 09:01:18  ▪ gate face-fast pass [session]  (25.2s)
07-31 09:01:18  • session #12 SC3 → Advanced · done SC3.4 · 2 commit(s)  (28m45s)
07-31 09:02:49  ▪ gate engine-fast pass [phase]  (0.0s)
07-31 09:02:49  ▪ gate face-fast pass [phase]  (0.0s)
07-31 09:02:49  ▪ gate engine-full pass [phase]  (1m26s)
07-31 09:02:49  ▪ gate face-full pass [phase]  (2.7s)
07-31 09:02:49  ▸ stage SC3 confirmed  (1h45m00s)
07-31 09:02:50  ▸ stage SC4 entered — Verdicts judge the work, not the environment
07-31 09:02:50  • session #13 SC4 Deliver started (attempt 1/6)
07-31 09:42:06  ▪ gate engine-fast pass [session]  (42.4s)
07-31 09:42:06  ▪ gate face-fast pass [session]  (5.8s)
07-31 09:42:07  • session #13 SC4 → Advanced · done SC4.1 · 1 commit(s)  (39m16s)
07-31 09:42:07  • session #14 SC4 Deliver started (attempt 1/6)
07-31 10:13:16  ▪ gate engine-fast pass [session]  (40.6s)
07-31 10:13:16  ▪ gate face-fast pass [session]  (3.7s)
07-31 10:13:16  • session #14 SC4 → Advanced · done SC4.2 · 1 commit(s)  (31m09s)
07-31 10:13:17  • session #15 SC4 Deliver started (attempt 1/6)
07-31 10:45:34  ▪ gate engine-fast pass [session]  (39.6s)
07-31 10:45:34  ▪ gate face-fast pass [session]  (3.5s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 15 · retries 1 (7 %) · overall Warn
⚠ [context-saturation] session #13: 29,057,299 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #3: 28,499,145 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 20,274,223 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #8: 35,438,955 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/sarban
working tree: clean
```

### Commits by session

- **s8 (SC2 Deliver)** — 1 commit(s):
  - [`87d7fcd`](https://github.com/shaahink/conductor/commit/87d7fcd) fix(sc2): let the run outlive the engine
- **s9 (SC3 Deliver)** — 2 commit(s):
  - [`4c69541`](https://github.com/shaahink/conductor/commit/4c69541) docs(sarban): hand SC3.2 the plan-set traps and a corrected condition finding
  - [`d4c9103`](https://github.com/shaahink/conductor/commit/d4c9103) fix(sc3): refuse the two config traps at authoring time
- **s10 (SC3 Deliver)** — 2 commit(s):
  - [`69aae8f`](https://github.com/shaahink/conductor/commit/69aae8f) docs(sarban): hand SC3.3 the brace landmine and a dead advisor key
  - [`587eadd`](https://github.com/shaahink/conductor/commit/587eadd) fix(sc3): make plan set refuse what nothing reads, and reach the live run
- **s11 (SC3 Deliver)** — 2 commit(s):
  - [`dcd7055`](https://github.com/shaahink/conductor/commit/dcd7055) docs(sarban): hand SC3.4 the advisor, and the ratchet lesson from SC3.3
  - [`503d7e6`](https://github.com/shaahink/conductor/commit/503d7e6) fix(sc3): make a literal brace prose, and a broken prompt a park
- **s12 (SC3 Deliver)** — 2 commit(s):
  - [`b93fc06`](https://github.com/shaahink/conductor/commit/b93fc06) docs(sarban): hand SC4.1 the settled battery, and what the advisor rig could not reproduce
  - [`abe0eb1`](https://github.com/shaahink/conductor/commit/abe0eb1) fix(sc3): make the shipped advisor answer, and a dead advisor key fatal
- **s13 (SC4 Deliver)** — 1 commit(s):
  - [`ba9b523`](https://github.com/shaahink/conductor/commit/ba9b523) fix(sc4): make the battery settle before it judges, and retry a red gate once
- **s14 (SC4 Deliver)** — 1 commit(s):
  - [`1ce4ba7`](https://github.com/shaahink/conductor/commit/1ce4ba7) fix(sc4): make a claim count as progress, and conductor's own commits not
- **s15 (SC4 Deliver)** — 2 commit(s):
  - [`314e868`](https://github.com/shaahink/conductor/commit/314e868) docs(sarban): hand SC4.4 the satellite-aware verdict, and the two-session rig recipe
  - [`c3e0813`](https://github.com/shaahink/conductor/commit/c3e0813) fix(sc4): count the sibling repo, key a gate on its own world, and see uncommitted work

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

> SESSION-RESULT: **SC4.3 landed and claimed** (`c3e0813`, handoff `314e868`, pushed to `feat/sarban`), all three parts of the checkpoint, each proven by a live BEFORE/AFTER on scratch rigs under `%TEMP%\sarban-proofs\sc43` — published engine vs fresh build on byte-identical rigs. **(1) satelliteRepos:** a session committing only in a declared sibling went from `commits 0 → NoProgress` to `commits 1 (incl. 1 in satellite repo(s): …) → Progress`; new `SessionProgress.WorkCommits` (= `ExcludeBookkeeping(NewCommits + SatelliteCommits)`) is now the single progress signal read by the verdict, `WorkflowVarsFactory.HasCommits`, `FailureCircuitBreaker` and `IdenticalStallPattern`, `rec.NewCommits` sta…

## Tracker handoff

```
last: **SC4.3 claimed**, all three parts. New `SessionProgress.WorkCommits` =
  `ExcludeBookkeeping(NewCommits + SatelliteCommits)` is now THE progress signal - verdict,
  `WorkflowVarsFactory.HasCommits`, FailureCircuitBreaker and IdenticalStallPattern all read that
  one function. New `plan.satelliteRepos`; doctor FAILS on a satellite that is missing or non-git.
  `GateRunner.CacheKey` = head + cwd HEAD + optional `gate.watchPaths` mtime + command digest, and
  GateOrchestrator now FILES under the same key it looks up under. skipIfFresh reads
  `Git.MostRecentChangeTime` (last commit OR a newer uncommitted edit, artifact excluded).
gate: build clean; scoped `dotnet test` over 24 touched/neighbouring classes: 267 passed, 0 failed,
  0 skipped (incl. ArchitectureTests). Nothing weakened. Evidence .conductor/evidence/SC4/, seven
  rigs under TEMP/sarban-proofs/sc43, BEFORE published vs AFTER fresh build on each.
next: **SC4.4** - queued injections render at the TOP of the composed prompt, right after the role
  line; a `gateFailures` block an injection supersedes is stamped SUPERSEDED or dropped. Seams:
  `PromptBuilder` + `InstructionQueue.ConsumeAll` (SessionRunner.cs, just before the prompt write).
know: satellite rows are `<sha> <subject> [<label>]` - the label is a SUFFIX because
  `Git.SubjectOf` strips a leading hex token, so a prefix would hide `chore(conductor):` from
  SC4.2's filter. A two-session rig needs `verifyEachDelivery:false` AND an explicit one-step
  `defaultWorkflow`; verifyEachDelivery alone still queued a Verify session, which runs no battery.
  A rig parked on NeedsHuman refuses a second run - delete its `.conductor` to re-run.
  Bug #9 filed: `McpTaskServer.IsProcessAliveMcp` answers DEAD for a pid it cannot open, inverting
  the policy SC4.1 set in PidLiveness. Bugs 2,3,4,5,6,8,9 open.
```
