# Baton — Conductor-Discovered Debt & Followups

**Generated:** 2026-07-08 by Conductor Baton cross-project audit.
**Updated:** 2026-07-09 — reordered by C-session (post-B12 cleanup plan).
**Read order:** this file → `CONDUCTOR-START.md` handoff → `docs/workflows/conductor-post-b12-workflow.md`.

This file records every open followup from audits across B0-B11, grouped by C-session.
All items below are resolved in the Conductor Debt plan (`.conductor/plans/conductor-debt.plan.json`).

---

## B4.7 — Async engine ratchet (FU-B0-1, FU-B1-2, FU-B0-2)

**Session size:** large (~60 min)
**Files touched:**
- `src/Conductor/Core/Orchestrator.cs` (make RunAsync, replace blocking calls)
- `src/Conductor/Core/AgentSession.cs` (async start)
- `src/Conductor/Core/Planning/ScriptProvider.cs` (CT through Read, split streams)
- `src/Conductor/Core/Planning/IProgressProvider.cs` (add CancellationToken to Read)
- `src/Conductor/Core/ProcessRunner.cs` (stdout/stderr split)
- `.editorconfig` (ratchet MA0045 to error, MA0002 to error)
- `tests/Conductor.Tests/` (async+CT tests)

**Background (from B0+B1 handovers, tracked in followups.md since B0):**
These have been re-homed twice (B0→B2→now):
- **FU-B0-1 (MA0045):** ~28 sync-over-async sites. B0 said "B2 will fix this during the async/Host/DI rework." B2 added Host/DI/logging but kept the Orchestrator run loop synchronous. MA0045 is still at `suggestion`. Each stage that ships without this ratchet adds more sync-over-async debt.
- **FU-B1-2:** `IProgressProvider.Read` has no `CancellationToken` — `ScriptProvider` spawns a process with only a timeout, not the run's cancellation token, so Ctrl+C won't interrupt a long normaliser mid-read.
- **FU-B1-1:** `ScriptProvider` interleaves stdout+stderr into one buffer. A script that writes any progress/warning to stderr corrupts the JSON parse. The provider is resilient (clear error, no crash), but brittle. Splitting streams is a `ProcessRunner` signature change.
- **FU-B0-2 (MA0002):** ~38 `StringComparison.Ordinal` missing sites. Still at `suggestion`.

**Context for the agent — what the B2 attempt looked like and why it stopped:**
B2.5 introduced `ConductorHost.Build` with `Microsoft.Extensions.Hosting` + Serilog structured logging, and wired correlation scopes. However, the Orchestrator's `Run()` method remained synchronous — it blocks on `Task` via `.GetAwaiter().GetResult()` per the pre-existing pattern. The B2 auditor explicitly noted: "The engine is still synchronous; the async ratchet (MA0045) is a live debt that keeps growing with each stage. A dedicated async pass is overdue." B12 (parallel lanes) is the designated stage but this work is large enough to be its own session.

**Gate:**
- MA0045 ratcheted to `error` (all 28 sites either fixed or suppressed with documented reason)
- MA0002 ratcheted to `error` (38 sites)
- `IProgressProvider.Read(CancellationToken)`: CT plumbed through to ScriptProvider process
- `ProcessRunner` splits stdout/stderr; test proves stderr warning doesn't corrupt JSON
- Ctrl+C during progress read correctly cancels the process (not just the timeout)
- Build 0w/0e, test count ≥ current + new CT/stream tests
- Dry-run through stable driver: prompt compiles, stage select works

**Checkpoint:** `B4.7 — async engine ratchet closed; MA0045/MA0002 at error`

---

## B4.8 — Integration test harness (FU-B3-1, FU-B3-2, FU-B3-5)

**Session size:** large (~60 min)
**Files touched:**
- `tests/Conductor.Tests/OrchestratorHarness.cs` (new — fake agent + temp git repo)
- `tests/Conductor.Tests/OrchestratorIntegrationTests.cs` (new — process-control loop)
- `tests/Conductor.Tests/CancellationTests.cs` (new — B3.5 Ctrl+C)
- `src/Conductor/Core/Orchestrator.cs` (minor — expose Hooks or internal visible)
- `src/Conductor/Core/Progress.cs` (mid-session control feedback)

**Background (from B3 audit, `.conductor/handovers/B3.md` §4):**
The Orchestrator has **no integration test at all.** Every B3 "gate" test mutates `RunState` by hand and never exercises `HandleControl`, `RunSession`, or the control loop. The B3 audit explicitly noted:

> `RollbackRefusesIfDirty` calls `Git.IsDirty(...)` and asserts nothing — its own comment admits "the real test is … manual smoke-test."

Additionally:
- **FU-B3-2:** B3.5 graceful Ctrl+C is **unproven by any test** — no `B3.5-gate.txt` exists, no cancellation test, no state-saved + resume-queued + exit-130 verification.
- **FU-B3-5:** Control verbs arriving mid-session are silently dropped. `retry-stage`/`rollback`/`pause-after-stage`/`goto` are consumed (control.json deleted) but the guard fails and the operator gets **no feedback**. The file is consumed without effect and the user thinks it worked.

**Context — what the B3 approach was and why it didn't happen in B3:**
B3.5 shipped as code-only: the cancellation block (`Run`'s cancel + `RunSession`'s internal `ct` handling → `QueueResume` + exit 130) looks correct on inspection but was never tested. The B3 audit was time-boxed and prioritized the 2 "green-by-luck" bugs (approval park conflation, control-file null crash) over the harness build. The harness is large infrastructure work — it needs a fake agent that produces deterministic output, a temp git repo that the Orchestrator can commit to, and a way to assert control-flow outcomes.

**Gate:**
- `OrchestratorHarness`: fake agent + temp git repo; Orchestrator runs a full session cycle
- Process-control integration tests: `goto` re-runs the stage, `rollback` resets to stage-start head, `retry-stage` resets attempts, budget park→approve→run→re-park loop
- Cancellation test: `CancellationTokenSource.Cancel()` mid-session → state saved (Status=Idle or Paused), resume queued, exit code 130
- Mid-session control: rejected verbs produce a log message AND write to conductor.log (not silent)
- Build 0w/0e, test count is the test count

**Checkpoint:** `B4.8 — orchestrator integration harness + cancellation tested + control feedback`

---

## B5.1 — LiveMetrics consumer + event-log authoritative cutover (FU-B2-1, FU-B3-4)

**Session size:** medium (~45 min)
**Files touched:**
- `src/Conductor/Core/Events/LiveMetrics.cs` (wire to dashboard)
- `src/Conductor/Ui/LiveDashboard.cs` / `DashboardRenderer.cs` (consume from log)
- `src/Conductor/Core/Orchestrator.cs` (emit Rollback event, wire metrics consumer)
- `src/Conductor/Core/Events/ConductorEvent.cs` (add Rollback event type, if missing)
- `tests/Conductor.Tests/EventLogTests.cs` (Rollback event round-trip)

**Background (from B2+B3 handovers):**
- **FU-B2-1:** `LiveMetrics.ForSession` / `RunWide` are called only from tests. The live dashboard still reads `agent.Tokens*` directly; the projection is correct (B2 audit fixed the `sessionId` bug) but unproven end-to-end. B5 must wire it.
- **FU-B3-4:** `rollback` is recorded to `conductor.log` + Serilog but there is **no `ConductorEvent`** for it, so the event-log timeline / report / Telegram will not show a rollback. B3.3 trap explicitly says "log the reset" — an event is how v2 logs it.

**Gate:**
- Dashboard token line reads from `LiveMetrics.RunWide` (not `agent.Tokens*`)
- Integration test: full session → log → `LiveMetrics.ForSession` matches dashboard after replay
- `Rollback` event type in schema, emitted on `git reset --hard` (ratchet: not destructive-action gated, this is observability)
- Build 0w/0e, existing event log tests pass

**Checkpoint:** `B5.1 — LiveMetrics wired; rollback event on the spine`

---

## B5.2 — Budget persistence + orphaned resume hardening (FU-B3-3, FU-B2-3)

**Session size:** medium (~35 min)
**Files touched:**
- `src/Conductor/Models/RunState.cs` (persist cumulative run cost baseline)
- `src/Conductor/Core/Orchestrator.cs` (restore budget on start, decide orphaned policy)
- `src/Conductor/Core/Events/EventLog.cs` (verify empty-session-id handling)
- `tests/Conductor.Tests/RunStateTests.cs` (budget persistence)
- `tests/Conductor.Tests/OrchestratorIntegrationTests.cs` (budget bypass test)

**Background (from B2+B3 handovers):**
- **FU-B3-3:** `_runCostUsd`/`_runTokens` reset to 0 at each `Run()` start. A run killed mid-accrual before it parks restarts its count from 0 — so a run split across restarts can exceed `maxRunCostUsd`/`maxRunTokens` without ever parking. The park survives restart, but the accumulators don't. Decide: per-process (current, safe since park is persisted) OR per-logical-run (more accurate, needs persisted cumulative baseline).
- **FU-B2-3:** Event-log recovery of an orphaned `SessionStarted` with empty `AgentSessionId` queues a resume that starts a **fresh** session, not a true resume. This is safe-ish (no data loss) but silently wrong. The path only triggers on a double-hard crash where `state.json` lost the record.

**Gate:**
- Decision on budget model documented (ADR or comment). If per-logical-run: write test proving a killed-then-resumed run parks at the combined total, not the per-run accrued.
- Orphaned resume: if `AgentSessionId` is empty, either skip (log warning) or mark NeedsHuman — not silently start a fresh session.
- Build 0w/0e, tests pass

**Checkpoint:** `B5.2 — budget model decided + tested; orphaned-resume hardened`

---

## B6.x — Heartbeat cleanup + report as source (B6.3 prerequisite)

**Note:** This is already planned as B6.3 in `docs/baton/stages/B6.md`. This entry provides context from the live observations.

**Session size:** medium (~40 min)
**Files touched:**
- `src/Conductor/Core/Reporter.cs` (heartbeat amend logic)
- `src/Conductor/Core/Orchestrator.cs` (PeriodicTimer migration)
- `docs/baton/stages/B6.md` (update B6.3 design)

**Background (from cross-project audit + live observation):**
The current design (`Orchestrator.cs:305-332`) uses a simple `DateTime.UtcNow` comparison inside a 400ms polling loop. BATON-BRIEF.md §250 specifies `PeriodicTimer` as the v2 target. The no-op dedup (`Reporter.cs:172`) only strips the `_Updated` timestamp line — any other state change produces a new commit, so ~6-8 heartbeat commits per long session interleave with real feature commits.

Across the 3 live projects, heartbeat commits constituted 93% (Loom), 65% (Baton), and 95% (Shamshir) of all git commits. This makes `git log` effectively useless without `--grep` filtering.

**Heartbeat was temporarily disabled** (2026-07-08, `heartbeatMinutes: 0` across all 3 plan JSONs) to keep git history clean while investigating the DNS outage — this confirms the feature gap: there is no runtime toggle.

**Gate for B6.3:**
- Heartbeat updates produce no new commits on the feature branch (verified by commit count over a simulated long session)
- OR: heartbeat commits go to a dedicated report ref/branch, keeping feature branch clean
- `PeriodicTimer` replaces `Thread.Sleep` polling (per BATON-BRIEF.md §250)
- Report renders progress bars / collapsible per-stage sections

**Checkpoint:** per B6.3 plan in `docs/baton/stages/B6.md`

---

## Bx.1 — Followup registry automation

**Session size:** medium (~40 min)
**Files touched:**
- `src/Conductor/Core/Progress.cs` (followup tracking model)
- `src/Conductor/Models/PlanConfig.cs` (followup config)
- New: `src/Conductor/Core/Planning/FollowupRegistry.cs`
- `tests/Conductor.Tests/FollowupRegistryTests.cs`

**Background (observed across all 3 projects):**
Every audit produces a handover with a "Concrete follow-ups" section and updates `.conductor/followups.md`. But followups are:
- Decentralized (per-repo, hand-maintained)
- Not cross-referenced to tracker rows
- Not visible in the TUI plan tree
- Not gated — an auditor can note a followup and the next session can miss it

The B3 handover has 6 open followups. The B2 handover has 4. Some (FU-B0-1) have been re-homed twice (B0→B2→"async lane") and tracking this manually is error-prone.

**Design:**
A machine-readable `followups.json` in `.conductor/` gets updated by audit sessions and read by the TUI. Each followup has: id, owning stage, severity, status (open/rehomed/closed), and a link to the handover that created it. The TUI plan tree shows a `⚠ N followups` badge per stage. The conductor's `SelectStage` can optionally skip stages with zero open followups before stages with open ones.

**Gate:**
- `followups.json` round-trips (create, update, close, re-home)
- TUI plan tree shows followup counts per stage
- Audit session produces followups that the next deliver session can see in its prompt context
- Build 0w/0e, new tests pass

**Checkpoint:** `Bx.1 — followup registry automated; TUI shows followup badges`

---

## Bx.2 — Plan JSON heartbeatMinutes as runtime-configurable control action

**Session size:** small (~20 min)
**Files touched:**
- `src/Conductor/Core/Progress.cs` (add `ToggleHeartbeat` to ControlAction)
- `src/Conductor/Core/Orchestrator.cs` (HandleControl: toggle plan.Report.HeartbeatMinutes)
- `src/Conductor/Ui/LiveDashboard.cs` (H key for heartbeat toggle)
- `src/Conductor/Commands/Commands.cs` (CLI: `conductor heartbeat on|off`)

**Background (from cross-project review, 2026-07-08):**
To keep git history clean during the DNS outage investigation, `heartbeatMinutes` was manually set to 0 in 3 plan JSONs. There is no way to toggle this at runtime — no control.json verb, no TUI keybinding, no environment variable. The conductor reads `PlanConfig` once at startup and never re-reads it. A `ToggleHeartbeat` control action that flips `plan.Report.HeartbeatMinutes` in the in-memory plan config (and writes the new value to the JSON) lets operators toggle heartbeats without restarting.

**Gate:**
- TUI `H` key toggles heartbeat on/off (dashboard footer reflects state)
- CLI `conductor heartbeat off` writes control.json; reading process picks it up on next poll cycle
- Plan JSON on disk is updated so next `conductor run` respects the choice
- Build 0w/0e

**Checkpoint:** `Bx.2 — heartbeat toggleable at runtime via TUI + CLI`

---

## Sweep — B0 audit deferred items (FU-B0-4, FU-B0-5)

**Session size:** small (~15 min)
**Files touched:**
- `scripts/fake-agent.ps1` (gatesred mode)
- `docs/baton/evidence/B0.4-gate.txt` (update)

**Background (from B0 handover):**
- **FU-B0-4:** `gatesred` fake-agent mode proves the no-commit → fix-session path (commits=0), NOT an actually-red gate (`gate build: exit 0` in the evidence). Adequate for B0's smoke gate, but misleading if a future session assumes it exercises real gate failure. Rename or add a true-red scenario.
- **FU-B0-5:** Smoke leaves the temp worktree dirty (`once-raw.txt`). Harmless (temp repo), cosmetic.

**Gate:**
- `gatesred` mode either renamed (e.g., `no-commits`) or enhanced to simulate a real gate failure
- Build 0w/0e, tests pass

**Checkpoint:** `Bsweep — B0 deferred fake-agent items closed`
