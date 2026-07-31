# Conductor — Sarban core - the engine says what it knows run report

_Updated 2026-07-31 17:20 UTC · branch `feat/sarban` · HEAD `2fea703`_

**Status:** Completed
**Stage:** SC8 — The program knows what it is and can update itself · attempts used 0
**Checkpoints:** 26/26 done · **Sessions run:** 28 · **Cost:** $360.1431 (agent $359.9821 + gates $0.1610) · **Tokens:** 5,319,262 in / 2,337,913 out
**Confirmed phases:** SC1, SC2, SC3, SC4, SC5, SC6, SC7, SC8

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| SC1 | Telegram actually delivers | ██████████ 3/3 | confirmed ✓ |
| SC2 | Truthful surfaces | ██████████ 4/4 | confirmed ✓ |
| SC3 | Config traps die at authoring time | ██████████ 4/4 | confirmed ✓ |
| SC4 | Verdicts judge the work, not the environment | ██████████ 4/4 | confirmed ✓ |
| SC5 | The engine can wait, detach, and correct the board | ██████████ 4/4 | confirmed ✓ |
| SC6 | Clean history without lying about it | ██████████ 2/2 | confirmed ✓ |
| SC7 | The transcript captures structure | ██████████ 2/2 | confirmed ✓ |
| SC8 | The program knows what it is and can update itself | ██████████ 3/3 | confirmed ✓ |

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

<details> ✅<summary>SC7 — The transcript captures structure (2/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC7.1 | Tool events are stored structured — name plus extracted fields, values truncated, JSON never cut — with back-compat reading of old lines; writes outside the repo are counted and noted in the session verdict | ✅ DONE | [`33d1f81`](https://github.com/shaahink/conductor/commit/33d1f81) |
| SC7.2 | The provider emits one-liner tool lines on the wire, and a per-session digest is computed, stored and served on /sessions matching the spec's worked example | ✅ DONE | [`6d805e1`](https://github.com/shaahink/conductor/commit/6d805e1) |

</details>

<details> ✅<summary>SC8 — The program knows what it is and can update itself (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| SC8.1 | conductor version and GET /version report semver, git sha and build date stamped at build; install.ps1 prints the version before and after | ✅ DONE | [`f45563e`](https://github.com/shaahink/conductor/commit/f45563e) |
| SC8.2 | Tag-height versioning is automatic and reconciled with release.yml so a released binary answers with its tag; CHANGELOG.md carries a section per release | ✅ DONE | [`094ece6`](https://github.com/shaahink/conductor/commit/094ece6) |
| SC8.3 | conductor update downloads and safely swaps the matching release binary, refusing while a run is live; doctor reports update-available | ✅ DONE | [`2fea703`](https://github.com/shaahink/conductor/commit/2fea703) |

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
| 24 | SC7 | Deliver | 1 | 07-31 14:29 | 0:28 | Advanced | SC7.1 | 2 | engine-fast:OK · face-fast:OK | $13.2517 | $0.0098 | 200,355/92,726 |
| 25 | SC7 | Deliver | 1 | 07-31 14:59 | 0:32 | Advanced | SC7.2 | 1 | engine-fast:OK · face-fast:OK | $13.7045 | $0.0076 | 213,018/94,092 |
| 26 | SC8 | Deliver | 1 | 07-31 15:38 | 0:36 | Advanced | SC8.1 | 4 | engine-fast:OK · face-fast:OK | $11.9962 | $0.0051 | 174,778/91,105 |
| 27 | SC8 | Deliver | 1 | 07-31 16:15 | 0:20 | Advanced | SC8.2 | 2 | engine-fast:OK · face-fast:OK | $6.5355 | $0.0044 | 119,896/62,287 |
| 28 | SC8 | Deliver | 1 | 07-31 16:37 | 0:35 | Advanced | SC8.3 | 1 | engine-fast:OK · face-fast:OK | $17.0332 | $0.0061 | 224,590/120,934 |

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
07-31 15:25:32  ▪ gate engine-fast pass [session]  (4.1s)
07-31 15:25:32  ▪ gate face-fast pass [session]  (19.0s)
07-31 15:25:33  • session #23 SC6 → Advanced · done SC6.2 · 2 commit(s)  (43m35s)
07-31 15:29:06  ▪ gate engine-fast pass [phase]  (0.0s)
07-31 15:29:06  ▪ gate face-fast pass [phase]  (0.0s)
07-31 15:29:06  ▪ gate engine-full pass [phase]  (3m26s)
07-31 15:29:06  ▪ gate face-full pass [phase]  (4.1s)
07-31 15:29:06  ▸ stage SC6 confirmed  (1h16m00s)
07-31 15:29:07  ▸ stage SC7 entered — The transcript captures structure
07-31 15:29:07  • session #24 SC7 Deliver started (attempt 1/6)
07-31 15:59:35  ▪ gate engine-fast pass [session]  (47.4s)
07-31 15:59:35  ▪ gate face-fast pass [session]  (50.6s)
07-31 15:59:36  • session #24 SC7 → Advanced · done SC7.1 · 2 commit(s)  (30m29s)
07-31 15:59:37  • session #25 SC7 Deliver started (attempt 1/6)
07-31 16:33:50  ▪ gate engine-fast pass [session]  (42.7s)
07-31 16:33:50  ▪ gate face-fast pass [session]  (33.3s)
07-31 16:33:51  • session #25 SC7 → Advanced · done SC7.2 · 1 commit(s)  (34m14s)
07-31 16:38:27  ▪ gate engine-fast pass [phase]  (0.0s)
07-31 16:38:27  ▪ gate face-fast pass [phase]  (0.0s)
07-31 16:38:27  ▪ gate engine-full pass [phase]  (4m29s)
07-31 16:38:27  ▪ gate face-full pass [phase]  (4.0s)
07-31 16:38:27  ▸ stage SC7 confirmed  (1h09m20s)
07-31 16:38:28  ▸ stage SC8 entered — The program knows what it is and can update itself
07-31 16:38:28  • session #26 SC8 Deliver started (attempt 1/6)
07-31 17:15:58  ▪ gate engine-fast pass [session]  (46.9s)
07-31 17:15:58  ▪ gate face-fast pass [session]  (4.4s)
07-31 17:15:58  • session #26 SC8 → Advanced · done SC8.1 · 4 commit(s)  (37m30s)
07-31 17:15:59  • session #27 SC8 Deliver started (attempt 1/6)
07-31 17:37:36  ▪ gate engine-fast pass [session]  (40.5s)
07-31 17:37:36  ▪ gate face-fast pass [session]  (3.9s)
07-31 17:37:36  • session #27 SC8 → Advanced · done SC8.2 · 2 commit(s)  (21m37s)
07-31 17:37:37  • session #28 SC8 Deliver started (attempt 1/6)
07-31 18:13:50  ▪ gate engine-fast pass [session]  (48.6s)
07-31 18:13:50  ▪ gate face-fast pass [session]  (11.9s)
07-31 18:13:51  • session #28 SC8 → Advanced · done SC8.3 · 1 commit(s)  (36m14s)
07-31 18:20:09  ▪ gate engine-fast pass [phase]  (0.0s)
07-31 18:20:09  ▪ gate face-fast pass [phase]  (0.0s)
07-31 18:20:09  ▪ gate engine-full pass [phase]  (5m59s)
07-31 18:20:09  ▪ gate face-full pass [phase]  (15.4s)
07-31 18:20:09  ▸ stage SC8 confirmed  (1h41m41s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 28 · retries 2 (7 %) · overall Warn
⚠ [context-saturation] session #13: 29,057,299 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #15: 20,145,044 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #18: 32,959,198 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #19: 22,309,531 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #20: 29,596,722 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #28: 23,531,055 context tokens (≥ 20,000,000)
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

- **s21 (SC5 Deliver)** — 2 commit(s):
  - [`884ea3e`](https://github.com/shaahink/conductor/commit/884ea3e) docs(sarban): hand SC6 the clean-history work and what the timezone taught
  - [`58bf293`](https://github.com/shaahink/conductor/commit/58bf293) feat(sc5): point bg logs at the session stream and put the clock in one zone
- **s22 (SC6 Deliver)** — 2 commit(s):
  - [`17f5627`](https://github.com/shaahink/conductor/commit/17f5627) docs(sarban): hand SC6.2 the dirty tree the ordering fix exposed
  - [`04e092a`](https://github.com/shaahink/conductor/commit/04e092a) fix(sc6): stop committing the engine's opinion of itself, and squash last
- **s23 (SC6 Deliver)** — 2 commit(s):
  - [`bf83cf4`](https://github.com/shaahink/conductor/commit/bf83cf4) docs(sarban): hand SC7 the transcript work and what the squash rewrite taught
  - [`5c357b2`](https://github.com/shaahink/conductor/commit/5c357b2) fix(sc6): squash by rebuilding, not rebasing, and say what it did
- **s24 (SC7 Deliver)** — 2 commit(s):
  - [`2a7b8a0`](https://github.com/shaahink/conductor/commit/2a7b8a0) docs(sarban): hand SC7.2 the digest work and the rig trap that nearly hit this repo
  - [`33d1f81`](https://github.com/shaahink/conductor/commit/33d1f81) feat(sc7): capture tool calls as structure, not a blob cut at 150 chars
- **s25 (SC7 Deliver)** — 1 commit(s):
  - [`6d805e1`](https://github.com/shaahink/conductor/commit/6d805e1) feat(sc7): render the wire readable and give every session a digest
- **s26 (SC8 Deliver)** — 4 commit(s):
  - [`1c2330f`](https://github.com/shaahink/conductor/commit/1c2330f) docs(sc8): fold the completion-parity lesson into the SC8.1 handoff
  - [`70c0bef`](https://github.com/shaahink/conductor/commit/70c0bef) feat(sc8): teach tab-completion the version verb
  - [`58c2685`](https://github.com/shaahink/conductor/commit/58c2685) docs(sc8): evidence and handoff for SC8.1
  - [`f45563e`](https://github.com/shaahink/conductor/commit/f45563e) feat(sc8): stamp the build, and let the engine say what it is
- **s27 (SC8 Deliver)** — 2 commit(s):
  - [`d38fb9a`](https://github.com/shaahink/conductor/commit/d38fb9a) docs(sc8): hand SC8.3 the update verb and the semver-comparison trap
  - [`094ece6`](https://github.com/shaahink/conductor/commit/094ece6) feat(sc8): let the tags decide the version, and make the release prove it
- **s28 (SC8 Deliver)** — 1 commit(s):
  - [`2fea703`](https://github.com/shaahink/conductor/commit/2fea703) feat(sc8): teach the engine to replace itself, and refuse to do it mid-run

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

> SESSION-RESULT: **SC8.3 landed and claimed** (commit `2fea703`, pushed to `feat/sarban`), completing stage SC8. `conductor update` is real: it reads a GitHub-shaped latest-release document, compares by a proper semver comparator (a tag-height prerelease like `2.1.1-alpha.0.7` sorts below `2.1.1` and above the `v2.1.0` it came from — string compare gets both halves wrong), picks this platform's row of the release matrix, verifies three-deep (asset name → sha256 against a new `SHA256SUMS.txt` release asset → *runs* the download and makes it answer `version --short` with the tag), then rename-dances the binary into place with rollback on every post-rename failure; `doctor` gained an `update` li…

## Tracker handoff

```
last: **SC8.3 landed** - stage SC8 is complete. `conductor update` lives in `Core/Update/` (SemVer,
  ReleaseClient, UpdateTarget, UpdateCheck, ArchiveUnpacker, BinarySwap, UpdateSafety) plus
  `Commands/UpdateCommand.cs`; doctor's line is `Commands/DoctorCommand.Update.cs`, a partial,
  because DoctorCommand.cs was 6 lines under the 500-line ceiling. Verification is three-deep:
  platform asset match, sha256 vs the release's new SHA256SUMS.txt, then RUN the download and make it
  answer `version --short` with the tag. Refusal = live engine lock OR another process on that image.
gate: rig `%TEMP%\sarban-proofs\sc83`. Two single-file publishes at 9.9.8 and 9.9.9 via
  `-p:MinVerVersionOverride=`, served from a local TcpListener feed via `CONDUCTOR_UPDATE_FEED`.
  Live lock -> exit 2 and sha256 IDENTICAL; lock cleared -> download, checksum, exec-verify, rename
  dance, sha256 CHANGED, `version` answers 9.9.9. doctor warns on the behind engine and is ok on the
  updated one. 123 scoped tests green. Evidence .conductor/evidence/SC8/SC8.3-update-swap-and-doctor.md.
next: **stage SC8 has no incomplete checkpoints.** Take the next stage the board gives you, or clear
  open bugs. Bug #14 (release could never compile) was filed and CLOSED here; 2,3,4,5,6,8,9,10,11,
  12,13 remain open.
know: The rig found a **release blocker** and it is fixed: `-p:PublishSingleFile=true` (release.yml's
  own flags) makes IL3000 on `Assembly.Location` an ERROR - BuildInfo and RunDetach both had one, and
  that workflow only runs on a `v*` tag so it had never compiled. Use `AppContext.BaseDirectory` +
  the assembly SIMPLE NAME, never a suppression; publish once locally if you touch publish flags.
  The verb-parity test now SCANS Program.cs instead of a hand-typed list, so a new verb is two places,
  not three. Windows: file globs still use DOS 8.3 rules (`x.old.*` misses `x.old.4123`) - filter in
  code; `Set-Location` does NOT move a child process's cwd, use `Start-Process -WorkingDirectory`.
```
