# Sarban core - the engine says what it knows Phase Tracker

**Plan:** Sarban core - the engine says what it knows | **Branch:** `feat/sarban` | **Design doc:** docs/history/CONDUCTOR-SARBAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: **SC1.3 claimed** (7d7372e engine+face, 4620370 goldens). SC1 is now complete: a late token
  and a late telegram block both reach the RUNNING engine - TelegramService re-resolves both and
  restarts itself, ConductorHost always registers the real service so a block added mid-run has
  somewhere to land, and restartRequired names the one case a live save cannot fix. Evidence:
  .conductor/evidence/SC1/SC1.3-late-config-takes-effect.md
gate: build clean 0 warnings, 172/172 scoped, ratchet OK at 37 pragmas, face build+vet+tests green.
next: **SC2.1** - status must stop calling a healthy run interrupted during the exit-to-verdict
  window; a gate executing counts as engine liveness. StatusReportBuilder scans spawned pids only.
know: TelegramService is restartable now - the send Channel and the CTS are recreated per start
  (a completed channel drops everything silently), and start/stop/reload are serialised by the
  _gate semaphore. Every run without a telegram block now logs one extra INFO line, deliberately.
  Bug 2 is still open: 'Run services started: TelegramService' prints even when that service
  early-returned; SC2's honest-surfaces work is the natural home for it.
rig: live proofs at TEMP/sarban-proofs/sc13 (plan-swap) and sc13b (control-plane token). sc13b's
  run-engine.cmd clears CONDUCTOR_TELEGRAM_TOKEN for the child. A staged secrets.local.json must
  use PascalCase TelegramToken - lowercase does not bind and looks exactly like a missing token.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 26 |
| Done | 0 |
| Claimed (unconfirmed) | 2 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### SC1 — Telegram actually delivers

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC1.1 | The engine starts Telegram on every run path: a configured live run delivers a real session-end push and answers two-way status, and a regression test drives the real run-start path | DONE | b7d6eb4 | engine-fast:OK · face-fast:OK |
| SC1.2 | /telegram/status carries a derived willDeliver verdict; POST /telegram/test routes through the real send queue or loudly says it bypassed it; StartAsync logs on both outcomes naming any missing half | DONE | 160f731 | engine-fast:OK · face-fast:OK |
| SC1.3 | Late token or telegram-block configuration takes effect without a full restart, or every surface honestly says restart required — including the NoOp-service swap path; the chat-id bootstrap is documented | TODO | - | - |

### SC2 — Truthful surfaces

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC2.1 | conductor status never reports a healthy run as interrupted during the verdict window — a gate executing counts as engine liveness — with a regression test | TODO | - | - |
| SC2.2 | Sticky failure fields carry timestamps or clear; phase-gate lines emit the canonical gates GREEN or RED token with an honest no-gates-configured state; attempt numbering agrees across the two log lines; doctor warns on zero-gate stages | TODO | - | - |
| SC2.3 | /state carries in-flight session spend plus costSpent, costCap, costRemaining, meanSessionCost, checkpointsRemaining, and window-vs-lifetime spend after a budget approval | TODO | - | - |
| SC2.4 | A completed run leaves RUN-SUMMARY.md; report and status work offline from run.db; conductor log reads a live log without crashing; the SSE streams tail incrementally instead of re-reading the backlog every second | TODO | - | - |

### SC3 — Config traps die at authoring time

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC3.1 | doctor FAILS when agent.model is set without the model token in both args and resumeArgs; unknown RunIf or SkipIf tokens fail at plan load naming the valid vocabulary | TODO | - | - |
| SC3.2 | plan set refuses an absent leaf key without --create, suggests the dotted path when one nested leaf matches, warns before stripping comments, and reaches the live engine or prints the exact reload command | TODO | - | - |
| SC3.3 | A literal brace in stage notes or promptExtra is caught by doctor at plan load; at runtime an unresolved placeholder parks the run and writes the refusal to conductor.log; a double brace escapes to a literal | TODO | - | - |
| SC3.4 | The default advisor invocation works headless or is refused loudly at load with a doctor line; plan-config.md matches the code | TODO | - | - |

### SC4 — Verdicts judge the work, not the environment

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC4.1 | The battery waits for the session's tracked bg children to exit, and retries a failed required gate once before declaring GatesRed; the failure line carries duration vs last passing duration | TODO | - | - |
| SC4.2 | NoProgress requires no commits AND no newly-DONE checkpoints; chore conductor commits are excluded from the verdict's commit count | TODO | - | - |
| SC4.3 | satelliteRepos are diffed for hasCommits; the gate cache key covers the gate's own working directory HEAD and its command text; skipIfFresh accounts for a dirty tree | TODO | - | - |
| SC4.4 | Queued injections render at the top of the composed prompt, and a gate-failures block they supersede is stamped SUPERSEDED or dropped | TODO | - | - |

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
