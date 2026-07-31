# Sarban core - the engine says what it knows Phase Tracker

**Plan:** Sarban core - the engine says what it knows | **Branch:** `feat/sarban` | **Design doc:** docs/history/CONDUCTOR-SARBAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: **SC5.3 landed - the board is two-way and every move reports the truth.** All `conductor task`
  moves go through `TaskWrites.BuildStatusChange` (the validator /tasks/update and MCP task_update
  use) and answer with the card's POST-FOLD status: a refused move prints what the card really is
  and exits 1 - that covers `--in-progress` AND `--done`. New `--todo|--blocked|--skipped`, plus
  `--amend <id> --note <text>`, which APPENDS a stamped correction to the card context the next
  session's composed prompt carries. `blocked` joined ValidStatuses (the fold always allowed it).
gate: live rig `%TEMP%\sarban-proofs\sc53` on the FRESH build ran all 14 verb cases from inside a
  real session (`sc53-verbs.log` holds the exit codes); the amendment appears in that run's
  `session-023.prompt.md`. Full suite 1321 passed / 0 failed, ratchet OK. Evidence
  .conductor/evidence/SC5/SC5.3-board-writes.md.
next: **SC5.4** - `bg logs` on an agent row points at that session's stream file; `bg status`
  runtimes computed in one timezone.
know: making `--skipped` REACHABLE forced three surfaces to learn the word - the tracker rendered it
  TODO, the row regex is built from the status vocabulary so the row would not have parsed at all
  (and WorkGraphSync archives what the tracker stops declaring), and the scheduler asked only
  is-it-done. SKIPPED now counts as settled in StageDone/AllDone; BLOCKED deliberately does not.
  The rig - not a test - caught `task --list` printing a skipped card as TODO; every status switch
  here ending in a catch-all TODO arm is a candidate lie. `plans/conductor-sarban-core.plan.json`
  arrived ALREADY modified: the owner ran a plan write at 12:54 (log: ReloadPlan) raising
  maxRunCostUsd 260 to 450. Committed as its own chore commit, not reverted; ratchet rule 3d fires
  on that rewrite because the gate commands got JSON-escaped, though their VALUES are unchanged.
  Bugs 2,3,4,5,6,8,9,10,11 open (5 is fixed in source, only the published engine still crashes).


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 26 |
| Done | 0 |
| Claimed (unconfirmed) | 17 |

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
| SC5.1 | conductor task --blocked-until with a reason yields a BlockedUntil outcome the run loop honours by sleeping and respawning once, burning no attempt; the wait is visible on status, state and the report | DONE | ac70123 | engine-fast:OK · face-fast:OK |
| SC5.2 | conductor run --detach spawns the engine into its own process group, prints pid and control-plane url, and survives its launching shell; the stall warning names the likely cause and the remedy | DONE | d496179 | engine-fast:OK · face-fast:OK |
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
