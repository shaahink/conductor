# Conductor — Sarban core - the engine says what it knows run report

_Updated 2026-07-31 14:29 UTC · branch `feat/sarban` · HEAD `bf83cf4`_

**Status:** Idle
**Stage:** SC6 — Clean history without lying about it · attempts used 0
**Checkpoints:** 21/26 done · **Sessions run:** 23 · **Cost:** $297.5890 (agent $297.4610 + gates $0.1280) · **Tokens:** 4,386,625 in / 1,876,769 out
**Confirmed phases:** SC1, SC2, SC3, SC4, SC5, SC6

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| SC1 | Telegram actually delivers | ██████████ 3/3 | confirmed ✓ |
| SC2 | Truthful surfaces | ██████████ 4/4 | confirmed ✓ |
| SC3 | Config traps die at authoring time | ██████████ 4/4 | confirmed ✓ |
| SC4 | Verdicts judge the work, not the environment | ██████████ 4/4 | confirmed ✓ |
| SC5 | The engine can wait, detach, and correct the board | ██████████ 4/4 | confirmed ✓ |
| SC6 | Clean history without lying about it | ██████████ 2/2 | confirmed ✓ |
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

<details> ✅<summary>SC4 — Verdicts judge the work, not the environment (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC4.1 | The battery waits for the session's tracked bg children to exit, and retries a failed required gate once before declaring GatesRed; the failure line carries duration vs last passing duration | ✅ DONE | [`ba9b523`](https://github.com/shaahink/conductor/commit/ba9b523) |
| SC4.2 | NoProgress requires no commits AND no newly-DONE checkpoints; chore conductor commits are excluded from the verdict's commit count | ✅ DONE | [`1ce4ba7`](https://github.com/shaahink/conductor/commit/1ce4ba7) |
| SC4.3 | satelliteRepos are diffed for hasCommits; the gate cache key covers the gate's own working directory HEAD and its command text; skipIfFresh accounts for a dirty tree | ✅ DONE | [`c3e0813`](https://github.com/shaahink/conductor/commit/c3e0813) |
| SC4.4 | Queued injections render at the top of the composed prompt, and a gate-failures block they supersede is stamped SUPERSEDED or dropped | ✅ DONE | [`cfdb1ad`](https://github.com/shaahink/conductor/commit/cfdb1ad) |

</details>

<details> ✅<summary>SC5 — The engine can wait, detach, and correct the board (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC5.1 | conductor task --blocked-until with a reason yields a BlockedUntil outcome the run loop honours by sleeping and respawning once, burning no attempt; the wait is visible on status, state and the report | ✅ DONE | [`ac70123`](https://github.com/shaahink/conductor/commit/ac70123) |
| SC5.2 | conductor run --detach spawns the engine into its own process group, prints pid and control-plane url, and survives its launching shell; the stall warning names the likely cause and the remedy | ✅ DONE | [`d496179`](https://github.com/shaahink/conductor/commit/d496179) |
| SC5.3 | task --todo, --blocked, --skipped and --amend exist through the shared task-writes path, and --in-progress reports the post-fold status instead of unconditional success | ✅ DONE | [`2e06530`](https://github.com/shaahink/conductor/commit/2e06530) |
| SC5.4 | bg logs on an agent row points at that session's stream file, and bg status runtimes are computed in one timezone | ✅ DONE | [`58bf293`](https://github.com/shaahink/conductor/commit/58bf293) |

</details>

<details> ✅<summary>SC6 — Clean history without lying about it (2/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC6.1 | Pure status-transition updates no longer land commits, and any squash runs after the stage's final state write | ✅ DONE | [`04e092a`](https://github.com/shaahink/conductor/commit/04e092a) |
| SC6.2 | The squash works on a dirty tree, reports real counts, logs git stderr and exit code on failure, un-marks the stage on failure, aborts a half-started rebase, and degrades gracefully off Windows | ✅ DONE | [`5c357b2`](https://github.com/shaahink/conductor/commit/5c357b2) |

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
| 16 | SC4 | Deliver | 1 | 07-31 09:45 | 0:14 | Advanced | SC4.4 | 2 | engine-fast:OK · face-fast:OK | $4.8776 | $0.0045 | 107,838/44,470 |
| 17 | SC4 | Fix | 2 | 07-31 10:02 | 0:10 | Progress |  | 1 | engine-fast:OK · face-fast:OK | $4.2441 | $0.0044 | 110,901/37,667 |
| 18 | SC5 | Deliver | 1 | 07-31 10:15 | 0:36 | Advanced | SC5.1 | 1 | engine-fast:OK · face-fast:OK | $21.5187 | $0.0051 | 256,885/98,705 |
| 19 | SC5 | Deliver | 1 | 07-31 10:52 | 0:46 | Advanced | SC5.2 | 3 | engine-fast:OK · face-fast:OK | $16.0120 | $0.0008 | 203,565/112,747 |
| 20 | SC5 | Deliver | 1 | 07-31 11:39 | 0:35 | Advanced | SC5.3 | 3 | engine-fast:OK · face-fast:OK | $19.9087 | $0.0070 | 256,628/103,130 |
| 21 | SC5 | Deliver | 1 | 07-31 12:41 | 0:28 | Advanced | SC5.4 | 2 | engine-fast:OK · face-fast:OK | $10.5666 | $0.0076 | 168,870/76,777 |
| 22 | SC6 | Deliver | 1 | 07-31 13:13 | 0:27 | Advanced | SC6.1 | 2 | engine-fast:OK · face-fast:OK | $11.4474 | $0.0055 | 187,779/73,830 |
| 23 | SC6 | Deliver | 1 | 07-31 13:41 | 0:43 | Advanced | SC6.2 | 2 | engine-fast:OK · face-fast:OK | $12.9192 | $0.0023 | 191,080/96,632 |

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
07-31 11:14:10  • session #17 SC4 → Progress · 1 commit(s)  (11m33s)
07-31 11:15:42  ▪ gate engine-fast pass [phase]  (0.0s)
07-31 11:15:42  ▪ gate face-fast pass [phase]  (0.0s)
07-31 11:15:42  ▪ gate engine-full pass [phase]  (1m28s)
07-31 11:15:42  ▪ gate face-full pass [phase]  (1.4s)
07-31 11:15:42  ▸ stage SC4 confirmed  (2h12m51s)
07-31 11:15:42  ▸ stage SC5 entered — The engine can wait, detach, and correct the board
07-31 11:15:42  • session #18 SC5 Deliver started (attempt 1/6)
07-31 11:52:44  ▪ gate engine-fast pass [session]  (39.3s)
07-31 11:52:44  ▪ gate face-fast pass [session]  (11.8s)
07-31 11:52:44  • session #18 SC5 → Advanced · done SC5.1 · 1 commit(s)  (37m01s)
07-31 11:52:45  • session #19 SC5 Deliver started (attempt 1/6)
07-31 12:39:44  ▪ gate engine-fast pass [session]  (4.1s)
07-31 12:39:44  ▪ gate face-fast pass [session]  (3.6s)
07-31 12:39:45  • session #19 SC5 → Advanced · done SC5.2 · 3 commit(s)  (47m00s)
07-31 12:39:45  • session #20 SC5 Deliver started (attempt 1/6)
07-31 13:16:55  ▪ gate engine-fast pass [session]  (39.9s)
07-31 13:16:55  ▪ gate face-fast pass [session]  (30.4s)
07-31 13:16:56  • session #20 SC5 → Advanced · done SC5.3 · 3 commit(s)  (37m10s)
07-31 13:16:56  § owner approval requested — SC5
07-31 13:16:57  ◆ plan reloaded — v2 · 8 stages · 4 gates
07-31 13:41:06  § owner approval granted — SC5
07-31 13:41:06  • session #21 SC5 Deliver started (attempt 1/6)
07-31 14:11:11  ▪ gate engine-fast pass [session]  (41.9s)
07-31 14:11:11  ▪ gate face-fast pass [session]  (33.9s)
07-31 14:11:12  • session #21 SC5 → Advanced · done SC5.4 · 2 commit(s)  (30m05s)
07-31 14:13:05  ▪ gate engine-fast pass [phase]  (0.0s)
07-31 14:13:05  ▪ gate face-fast pass [phase]  (0.0s)
07-31 14:13:05  ▪ gate engine-full pass [phase]  (1m42s)
07-31 14:13:05  ▪ gate face-full pass [phase]  (6.8s)
07-31 14:13:05  ▸ stage SC5 confirmed  (2h57m22s)
07-31 14:13:05  ▸ stage SC6 entered — Clean history without lying about it
07-31 14:13:06  • session #22 SC6 Deliver started (attempt 1/4)
07-31 14:41:57  ▪ gate engine-fast pass [session]  (51.0s)
07-31 14:41:57  ▪ gate face-fast pass [session]  (4.3s)
07-31 14:41:58  • session #22 SC6 → Advanced · done SC6.1 · 2 commit(s)  (28m52s)
07-31 14:41:58  • session #23 SC6 Deliver started (attempt 1/4)
07-31 15:25:32  ▪ gate engine-fast pass [session]  (4.1s)
07-31 15:25:32  ▪ gate face-fast pass [session]  (19.0s)
07-31 15:25:33  • session #23 SC6 → Advanced · done SC6.2 · 2 commit(s)  (43m35s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 23 · retries 2 (9 %) · overall Warn
⚠ [context-saturation] session #13: 29,057,299 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #15: 20,145,044 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #18: 32,959,198 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #19: 22,309,531 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #20: 29,596,722 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #3: 28,499,145 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 20,274,223 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #8: 35,438,955 context tokens (≥ 20,000,000)
⚠ [gate-oscillation] gate 'engine-full' flipped pass/fail 3x
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/sarban
working tree: M .conductor/REPORT.md, M SARBAN-CORE-TRACKER.md
```

### Commits by session

- **s16 (SC4 Deliver)** — 2 commit(s):
  - [`d96963e`](https://github.com/shaahink/conductor/commit/d96963e) docs(sarban): hand SC5.1 the completed SC4, and the red-gate fix-prompt rig
  - [`cfdb1ad`](https://github.com/shaahink/conductor/commit/cfdb1ad) fix(sc4): put the human correction above the evidence it corrects
- **s17 (SC4 Fix)** — 1 commit(s):
  - [`49451ed`](https://github.com/shaahink/conductor/commit/49451ed) fix(sc4): stop a live gate standing on the verdict bug SC4.2 removed
- **s18 (SC5 Deliver)** — 1 commit(s):
  - [`ac70123`](https://github.com/shaahink/conductor/commit/ac70123) feat(sc5): let a session say "blocked until T" and have the engine wait
- **s19 (SC5 Deliver)** — 3 commit(s):
  - [`8948a10`](https://github.com/shaahink/conductor/commit/8948a10) docs(sarban): hand SC5.3 the detached run loop and three rig-found traps
  - [`e6b15c7`](https://github.com/shaahink/conductor/commit/e6b15c7) feat(sc5): let a run outlive the shell that launched it
  - [`d496179`](https://github.com/shaahink/conductor/commit/d496179) ﻿fix(sc5): split the two files SC5.1 pushed past the architecture ratchet
- **s20 (SC5 Deliver)** — 3 commit(s):
  - [`a685719`](https://github.com/shaahink/conductor/commit/a685719) docs(sarban): hand SC5.4 the board work and two things the rig taught
  - [`9024e57`](https://github.com/shaahink/conductor/commit/9024e57) feat(sc5): make every board move go one way and report what happened
  - [`2e06530`](https://github.com/shaahink/conductor/commit/2e06530) chore(plan): commit the owner's mid-session plan rewrite
- **s21 (SC5 Deliver)** — 2 commit(s):
  - [`884ea3e`](https://github.com/shaahink/conductor/commit/884ea3e) docs(sarban): hand SC6 the clean-history work and what the timezone taught
  - [`58bf293`](https://github.com/shaahink/conductor/commit/58bf293) feat(sc5): point bg logs at the session stream and put the clock in one zone
- **s22 (SC6 Deliver)** — 2 commit(s):
  - [`17f5627`](https://github.com/shaahink/conductor/commit/17f5627) docs(sarban): hand SC6.2 the dirty tree the ordering fix exposed
  - [`04e092a`](https://github.com/shaahink/conductor/commit/04e092a) fix(sc6): stop committing the engine's opinion of itself, and squash last
- **s23 (SC6 Deliver)** — 2 commit(s):
  - [`bf83cf4`](https://github.com/shaahink/conductor/commit/bf83cf4) docs(sarban): hand SC7 the transcript work and what the squash rewrite taught
  - [`5c357b2`](https://github.com/shaahink/conductor/commit/5c357b2) fix(sc6): squash by rebuilding, not rebasing, and say what it did

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

> SESSION-RESULT: **SC6.2 landed and is claimed** (`conductor task --done SC6.2 --evidence .conductor/evidence/SC6/SC6.2-honest-safe-squash.md`), closing stage SC6. The squash no longer rebases at all: `Git.SquashChoreCommits` reads the stage's commit range once, groups consecutive `chore(conductor):` commits, rebuilds the tail with `git commit-tree` reusing the trees that already exist (fixup semantics — first commit's message and authorship, last commit's tree), and moves the branch with a compare-and-swap `update-ref` after writing `ORIG_HEAD`. Nothing is ever checked out, which is what makes a dirty tree irrelevant, a conflict unreachable, and a half-finished rebase impossible to leave beh…

## Tracker handoff

```
last: **SC6.2 landed - SC6 is closed.** The squash no longer rebases: it groups consecutive
  chore(conductor): commits, rebuilds the tail with `git commit-tree` reusing the trees that already
  exist (fixup semantics - first message and authorship, last tree), then moves the branch with a
  compare-and-swap `update-ref`. Nothing is ever checked out, so a dirty tree is irrelevant and a
  conflict unreachable. It refuses a merge in the range or a range not ending at HEAD, aborts a
  rebase it finds in progress, writes ORIG_HEAD, and marks a stage squashed only after one that
  worked - the failure line carries git's exit code, its stderr and the failing command.
gate: rig `%TEMP%\sarban-proofs\sc62`, same baseline, published engine vs FRESH build: `git rebase
  returned non-zero` becomes `squashed 2 chore(conductor): commits into 1 (4 commits -> 3)`, chore
  commits 5 to 2, and the unstaged + staged + untracked work all intact. Scoped 48/0, plus 41/0 for
  ProcessRunner's own consumers. Evidence .conductor/evidence/SC6/SC6.2-honest-safe-squash.md.
next: **SC7.1** - structured tool events in transcript.jsonl (name plus extracted fields, values
  truncated and JSON never cut), schema v2 that still reads v1, out-of-repo writes in the verdict.
know: the spec asked the squash to DEGRADE off Windows; there is nothing left to degrade, it launches
  only git - SquashResult.Commands records every process it starts and a test asserts they are all
  git. No non-Windows box ran it. Rig trap that cost a rerun: an agent .cmd invoking the PATH
  conductor SHIM without `call` transfers control and never returns, so the BEFORE rig silently lost
  every line after the claim. `RunOptions.Once` returns before the loop reaches the pending phase
  gate, so a stage-close test needs MaxSessions. Bugs 2,3,4,5,6,8,9,10,11,12 open (5 is fixed in
  source, only the published engine still crashes).
```
