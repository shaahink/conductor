# Sarban core - the engine says what it knows Phase Tracker

**Plan:** Sarban core - the engine says what it knows | **Branch:** `feat/sarban` | **Design doc:** docs/history/CONDUCTOR-SARBAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: **SC5.1 landed - the engine can wait.** `task --blocked-until <iso> --reason <text>` (CLI +
  MCP `task_blocked_until`) writes a session-scoped event; the verdict reads it alongside
  kill/stall/timeout, parks `RunState.BlockedUntilUtc`, and the run loop sleeps at the session
  boundary then spawns exactly one session. No attempt burned, no fix queued. Bounded on purpose:
  24h ceiling, 3 consecutive blocks then NeedsHuman.
gate: live rig `%TEMP%\sarban-proofs\sc51` on the FRESH build - `session #1 BlockedUntil ... attempts
  stay 0/2` / `window opened after 1.8m asleep` / `session #2 start - Deliver R1 attempt 1/2`.
  9 SC51 tests + 26 McpTaskServer green; StatusCommand 26, ControlPlane 39, StateCompat 1 green.
  Evidence .conductor/evidence/SC5/SC5.1-blocked-until.md.
next: **SC5.2** - `run --detach` into its own process group, printing pid + control-plane url,
  surviving its launching shell; stall warning names cause and remedy.
know: TRAP the live rig caught and no unit test did - `GET /state` folds the event log and re-stamps
  transient control fields BY HAND in `ControlPlaneServer.State.cs`. A new RunState field reaches
  status, REPORT.md and the SSE snapshot but arrives NULL on the Face's wire until you add it beside
  `AttentionSinceUtc`. Prove any new field with a real socket, not a DashboardSnapshot assertion.
  Also: in a cmd rig `echo x=%ERRORLEVEL%> f` writes an EMPTY file - a digit before `>` is a stream
  redirect; use `>f echo x=%ERRORLEVEL%`. face-go still renders status `Waiting` via its default
  branch and ignores `blockedUntilUtc`/`blockedReason` - a face-tracker item, not core.
  Bugs 2,3,4,5,6,8,9,10,11 open.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 26 |
| Done | 0 |
| Claimed (unconfirmed) | 15 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### SC1 — Telegram actually delivers

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC1.1 | The engine starts Telegram on every run path: a configured live run delivers a real session-end push and answers two-way status, and a regression test drives the real run-start path | DONE | b7d6eb4 | engine-fast:OK · face-fast:OK |
| SC1.2 | /telegram/status carries a derived willDeliver verdict; POST /telegram/test routes through the real send queue or loudly says it bypassed it; StartAsync logs on both outcomes naming any missing half | DONE | 160f731 | engine-fast:OK · face-fast:OK |
| SC1.3 | Late token or telegram-block configuration takes effect without a full restart, or every surface honestly says restart required — including the NoOp-service swap path; the chat-id bootstrap is documented | DONE | 7d7372e | docs/dev/FINDING-2026-07-31-host-logger-isolation.md |

### SC2 — Truthful surfaces

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC2.1 | conductor status never reports a healthy run as interrupted during the verdict window — a gate executing counts as engine liveness — with a regression test | DONE | a3e970e | engine-fast:OK · face-fast:OK |
| SC2.2 | Sticky failure fields carry timestamps or clear; phase-gate lines emit the canonical gates GREEN or RED token with an honest no-gates-configured state; attempt numbering agrees across the two log lines; doctor warns on zero-gate stages | DONE | 603fbbb | engine-fast:OK · face-fast:OK |
| SC2.3 | /state carries in-flight session spend plus costSpent, costCap, costRemaining, meanSessionCost, checkpointsRemaining, and window-vs-lifetime spend after a budget approval | DONE | 55da220 | engine-fast:OK · face-fast:OK |
| SC2.4 | A completed run leaves RUN-SUMMARY.md; report and status work offline from run.db; conductor log reads a live log without crashing; the SSE streams tail incrementally instead of re-reading the backlog every second | DONE | 87d7fcd | engine-fast:OK · face-fast:OK |

### SC3 — Config traps die at authoring time

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC3.1 | doctor FAILS when agent.model is set without the model token in both args and resumeArgs; unknown RunIf or SkipIf tokens fail at plan load naming the valid vocabulary | DONE | d4c9103 | engine-fast:OK · face-fast:OK |
| SC3.2 | plan set refuses an absent leaf key without --create, suggests the dotted path when one nested leaf matches, warns before stripping comments, and reaches the live engine or prints the exact reload command | DONE | 587eadd | engine-fast:OK · face-fast:OK |
| SC3.3 | A literal brace in stage notes or promptExtra is caught by doctor at plan load; at runtime an unresolved placeholder parks the run and writes the refusal to conductor.log; a double brace escapes to a literal | DONE | 503d7e6 | engine-fast:OK · face-fast:OK |
| SC3.4 | The default advisor invocation works headless or is refused loudly at load with a doctor line; plan-config.md matches the code | DONE | abe0eb1 | engine-fast:OK · face-fast:OK |

### SC4 — Verdicts judge the work, not the environment

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC4.1 | The battery waits for the session's tracked bg children to exit, and retries a failed required gate once before declaring GatesRed; the failure line carries duration vs last passing duration | DONE | ba9b523 | engine-fast:OK · face-fast:OK |
| SC4.2 | NoProgress requires no commits AND no newly-DONE checkpoints; chore conductor commits are excluded from the verdict's commit count | DONE | 1ce4ba7 | .conductor/evidence/SC4/SC4-red-battery-w43-rig.md |
| SC4.3 | satelliteRepos are diffed for hasCommits; the gate cache key covers the gate's own working directory HEAD and its command text; skipIfFresh accounts for a dirty tree | DONE | c3e0813 | engine-fast:OK · face-fast:OK |
| SC4.4 | Queued injections render at the top of the composed prompt, and a gate-failures block they supersede is stamped SUPERSEDED or dropped | DONE | cfdb1ad | engine-fast:OK · face-fast:OK |

### SC5 — The engine can wait, detach, and correct the board

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC5.1 | conductor task --blocked-until with a reason yields a BlockedUntil outcome the run loop honours by sleeping and respawning once, burning no attempt; the wait is visible on status, state and the report | TODO | - | - |
| SC5.2 | conductor run --detach spawns the engine into its own process group, prints pid and control-plane url, and survives its launching shell; the stall warning names the likely cause and the remedy | TODO | - | - |
| SC5.3 | task --todo, --blocked, --skipped and --amend exist through the shared task-writes path, and --in-progress reports the post-fold status instead of unconditional success | TODO | - | - |
| SC5.4 | bg logs on an agent row points at that session's stream file, and bg status runtimes are computed in one timezone | TODO | - | - |

### SC6 — Clean history without lying about it

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC6.1 | Pure status-transition updates no longer land commits, and any squash runs after the stage's final state write | TODO | - | - |
| SC6.2 | The squash works on a dirty tree, reports real counts, logs git stderr and exit code on failure, un-marks the stage on failure, aborts a half-started rebase, and degrades gracefully off Windows | TODO | - | - |

### SC7 — The transcript captures structure

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC7.1 | Tool events are stored structured — name plus extracted fields, values truncated, JSON never cut — with back-compat reading of old lines; writes outside the repo are counted and noted in the session verdict | TODO | - | - |
| SC7.2 | The provider emits one-liner tool lines on the wire, and a per-session digest is computed, stored and served on /sessions matching the spec's worked example | TODO | - | - |

### SC8 — The program knows what it is and can update itself

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC8.1 | conductor version and GET /version report semver, git sha and build date stamped at build; install.ps1 prints the version before and after | TODO | - | - |
| SC8.2 | Tag-height versioning is automatic and reconciled with release.yml so a released binary answers with its tag; CHANGELOG.md carries a section per release | TODO | - | - |
| SC8.3 | conductor update downloads and safely swaps the matching release binary, refusing while a run is live; doctor reports update-available | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
