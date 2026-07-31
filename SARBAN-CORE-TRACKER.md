# Sarban core - the engine says what it knows Phase Tracker

**Plan:** Sarban core - the engine says what it knows | **Branch:** `feat/sarban` | **Design doc:** docs/history/CONDUCTOR-SARBAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

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


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 26 |
| Done | 0 |
| Claimed (unconfirmed) | 25 |

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
| SC5.3 | task --todo, --blocked, --skipped and --amend exist through the shared task-writes path, and --in-progress reports the post-fold status instead of unconditional success | DONE | 2e06530 | engine-fast:OK · face-fast:OK |
| SC5.4 | bg logs on an agent row points at that session's stream file, and bg status runtimes are computed in one timezone | DONE | 58bf293 | engine-fast:OK · face-fast:OK |

### SC6 — Clean history without lying about it

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC6.1 | Pure status-transition updates no longer land commits, and any squash runs after the stage's final state write | DONE | 04e092a | engine-fast:OK · face-fast:OK |
| SC6.2 | The squash works on a dirty tree, reports real counts, logs git stderr and exit code on failure, un-marks the stage on failure, aborts a half-started rebase, and degrades gracefully off Windows | DONE | 5c357b2 | engine-fast:OK · face-fast:OK |

### SC7 — The transcript captures structure

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC7.1 | Tool events are stored structured — name plus extracted fields, values truncated, JSON never cut — with back-compat reading of old lines; writes outside the repo are counted and noted in the session verdict | DONE | 33d1f81 | engine-fast:OK · face-fast:OK |
| SC7.2 | The provider emits one-liner tool lines on the wire, and a per-session digest is computed, stored and served on /sessions matching the spec's worked example | DONE | 6d805e1 | engine-fast:OK · face-fast:OK |

### SC8 — The program knows what it is and can update itself

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| SC8.1 | conductor version and GET /version report semver, git sha and build date stamped at build; install.ps1 prints the version before and after | DONE | f45563e | engine-fast:OK · face-fast:OK |
| SC8.2 | Tag-height versioning is automatic and reconciled with release.yml so a released binary answers with its tag; CHANGELOG.md carries a section per release | DONE | 094ece6 | engine-fast:OK · face-fast:OK |
| SC8.3 | conductor update downloads and safely swaps the matching release binary, refusing while a run is live; doctor reports update-available | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
