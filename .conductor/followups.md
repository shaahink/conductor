# Tracked followups

Living list of debt/ratchets deferred out of a phase, each with an owning stage. A later
fix/harden session (or the owning stage's opening) must clear these. Never silently drop a row —
close it with a commit ref or move it, don't delete it.

## Triage pass — 2026-07-28 (post-W6)

This file had not been touched since 2026-07-12, and every "owning stage" it named (B, F, M) has
since closed. That made it worse than stale: a row pointing at a stage that will never open again
is a row nobody will ever clear. The open-edges note in `AGENTS.md` (item 4) asked for a triage,
**not** execution — so this pass changed no code. Each row was checked against the tree as it
stands today and either closed with the evidence that closed it, or re-homed to an owner that can
still act.

**24 rows closed** — 16 on their merits, plus the eight Ink-era Face rows below as obsolete. Of 50
rows in this file, 38 are now closed and **12 remain open**. None of the 16 were closed by doing
work here: the eras that followed had already fixed them and nobody came back to say so. Two
examples of why that matters — FU-B0-1 and FU-B0-2 were still listed as deferred analyzer ratchets,
and both have read `severity = error` in `.editorconfig` since C2; FU-B3-2 ("graceful Ctrl+C is
unproven") was closed twice over, first by W3.3's live cancel test and again today by
`tools/w3/window-close.ps1`.

The 12 still open, so nobody has to re-derive the list: FU-B2-2, FU-B2-3, FU-B4-1, FU-B10-2,
FU-B11-2 (partial), FU-B11-3, FU-F0-2, FU-F0-3, FU-F1-03, FU-F1-06, FU-F1-07, FU-OWNER-9. Two are
`HUMAN:` (FU-B10-2 rides W5.2; FU-B11-3 needs real credentials), two want a deliberate lane
(FU-B2-2/FU-B2-3, both concurrency/recovery), one is a safety hole worth reading today
(FU-OWNER-9), and the rest are cosmetic.

Owners after this pass, since no B/F/M stage exists to own anything:

- **`on demand`** — real but cosmetic, or with a documented workaround. Fix when it bites; do not
  schedule a series for them.
- **`next era`** — real design debt that wants a deliberate lane (concurrency, recovery).
- **`HUMAN:`** — needs the owner to spend money or make a call.

The eight `FU-OWNER-*` Face rows are closed as **obsolete**: every one was filed in 2026-07-12
against the Ink TUI in `face/`, which M7 deleted. face-go is a different program with a different
render model, and re-filing UI complaints about a retired UI would be inventing debt. Where the
underlying concern outlived the implementation it is named on the row. `FU-OWNER-9` is not a Face
row at all and stays open.

## Opened by B0 (audit session, 2026-07-08)

### Analyzer ratchets (recorded in ADR-0001 §"Deliberately relaxed") — ratchet severity to `error`
| id | rule | sites | why deferred | owning stage | status |
|----|------|-------|--------------|--------------|--------|
| FU-B0-1 | MA0045 (sync-over-async I/O) | ~28 | Async-ifying the Orchestrator is signature-changing and *is* the B2 async/Host/DI rework. The deadlock-class twin MA0042 stays `error`. | B2 | CLOSED (C2) — `.editorconfig:48` reads `MA0045.severity = error`; the remaining sync boundaries carry justified pragmas (the ratchet caps them at 38, currently 37). |
| FU-B0-2 | MA0002 (explicit StringComparer) | ~38 | Cross-cutting, mechanical; low correctness risk under `InvariantGlobalization`. | post-B2 | CLOSED (C2) — `.editorconfig:49` reads `MA0002.severity = error`. |
| FU-B0-3 | MA0009 (regex ReDoS timeout) | ~7 | `TrackerParser` regex (the untrusted-input path) is reworked in B1.4; timeouts land naturally there. | B1.4 | CLOSED (bB1.4) — severity now `error`; every tracker regex carries `ProgressConventions.RegexTimeout` (2s); build green under it. |

Ratchet = flip the `.editorconfig` severity to `error` and fix every resulting site in that stage's
diff. Do NOT delete these rows until the severity is `error` and the build is green under it.

### Test-harness / smoke-loop gaps (found by B0 audit)
| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-B0-4 | `fake-agent.ps1` `gatesred` mode does not make gates actually red | It flips the tracker but skips the commit, so the `--once` verdict is `NoProgress` because **commits=0**, not because a gate failed (`gate build: exit 0` in `docs/baton/evidence/B0.4-gate.txt`). The fix-session path is exercised, but the mode name over-claims. To genuinely exercise a red gate, the smoke plan needs a gate the fake agent can fail (e.g. write a file that breaks `dotnet build`) — needs a smoke-plan design tweak, out of B0's diff budget. | B0 fix-lane / B3 (gate control) | CLOSED (C4) — renamed to `no-commits` (accurate name); added `true-red` mode that writes a compile-breaking .cs file. |
| FU-B0-5 | `--once` smoke leaves the temp worktree dirty | Scenario 1 logs `working tree left dirty after green session: M once-raw.txt`. Harmless for the token-free smoke (temp repo), but a cleaner harness would tidy or gitignore its raw-capture artifact. | B0 fix-lane | CLOSED (C4) — cosmetic only; temp repo, no production impact. |

### Pre-existing code smells confirmed by B0 (NOT introduced by B0; deliberately not fixed here to honour "no behaviour change" in a TFM-migration stage)
| id | item | location | owning stage | status |
|----|------|----------|--------------|--------|
| FU-B0-6 | Empty `catch { }` swallow in the raw-log writer/disposer (A15) | `src/Conductor/Core/AgentSession.cs:97`, `:253` | B2 (structured logging / error surfacing) | CLOSED (C3) — rewired in C3/C2 audit sweep. |
| FU-B0-7 | `CA1031` (catch general) landed at `suggestion` | many legit boundary catches; revisit once structured logging (B2) can surface them | B2 | CLOSED (C4) — B2 Serilog/ILogger<T> structured logging now surfaces all boundary catches. `suggestion` severity is appropriate; raising to warning would add noise without value. |

See `docs/baton/audits/B0-baseline.md` for the full architectural debt inventory (items §1–§12,
each with a file:line and an owning B-stage). Those are the design-level followups the later stages
own directly and are not duplicated here.

## Opened by B1 (audit session, 2026-07-08)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-B1-1 | `ScriptProvider` can't split stdout from stderr | `ProcessRunner.Run` interleaves both streams, so a normaliser that writes any progress/warning to stderr corrupts the JSON parse (surfaces as "not a JSON checkpoint array"). Provider stays resilient (clear error, no crash) and the "print ONLY JSON to stdout" contract is documented, but it's brittle. Splitting streams is a `ProcessRunner` signature change → land with structured logging. | B2 | CLOSED — the signature changed: `ProcResult(int ExitCode, string Output, string StdErr, bool TimedOut, TimeSpan Duration)` captures the two streams separately (`ProcessRunner.cs:6`), and `ScriptProvider` parses `result.Output` alone, so a normaliser writing to stderr can no longer corrupt the JSON parse. |
| FU-B1-2 | No `CancellationToken` through `IProgressProvider.Read` | Progress read is synchronous; `ScriptProvider` spawns a process with only a timeout, not the run's CT, so Ctrl+C won't interrupt a long normaliser mid-read. Consistent with FU-B0-1 (sync-over-async deferred); thread the CT with the B2 async/Host/DI pass. | B2 | CLOSED — `IProgressProvider.Read(PlanConfig plan, CancellationToken ct = default)` (`IProgressProvider.cs:18`); the CT reaches the provider. |
| FU-B1-3 | `ScriptProvider` trusts checkpoint content shape | Accepts a JSON array with empty ids / unknown status without validation (garbage-in → empty-id rows). Fine for a plan-owned script, but a stricter contract would fail louder. Low priority. | post-B2 | CLOSED (C4) — id and title now validated; empty/unknown status tolerated (conventions parser has fallback). |

Fixed in-phase by the B1 audit (no followup needed, recorded for the trail): shamshir `new-plan`
scaffold was undrivable (declared `P-0/P0/P1` stages but scaffolded `S1` rows) → now stage-coherent
+ `NewPlanScaffoldTests`; double-space/tab `IN PROGRESS` silently misclassified by the new status
vocabulary → whitespace-tolerant `StartsWithAny` + regression test. See `.conductor/handovers/B1.md`.

## Opened by B2 (audit session, 2026-07-08, session #17)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-B2-1 | `LiveMetrics` has no production consumer | `ForSession`/`RunWide` are called only from tests; the dashboard reads `agent.Tokens*` directly. The B2 audit FIXED the persisted-data bug (TokenDelta now carries `sessionId`), so the log is now correct � but the end-to-end "consumer folds live tokens from the log" loop is unproven by a real run. Wire it and prove against a recorded log. | B5 | CLOSED — `LiveMetrics` now has production consumers: `Core/Http/ControlPlaneServer.State.cs` (what the Face renders) and `Core/Events/Timeline.cs`. |
| FU-B2-2 | `RunStateProjection.FindInterruptedSession` assumes single-session | Tracks one "most recent unmatched start"; cannot represent two concurrently-interrupted sessions. Matches today's one-session-at-a-time model but is an undocumented invariant parallel-lane stages must revisit. | SF0.4 (was: next era, concurrency) | **CLOSED (SF0.4)** — the row's complaint was that the invariant was *undocumented*, not that the behaviour was wrong. It is documented now, at the code a parallel-lane era would read first: `RunStateProjection.cs:95-98` says "Considers only the highest-numbered unmatched start — a session can be interrupted at most once per run-instance." The engine still runs one session at a time by design, so there is nothing to fix until that changes; when it does, this doc comment is the thing that fails to be true. |
| FU-B2-3 | Orphaned-`SessionStarted` recovery may queue a non-resume | Event-log recovery of a `SessionStarted` with no matching `state.json` record synthesises a `SessionRecord` and queues a resume with the event's `AgentSessionId` � possibly empty ? starts a FRESH agent, not a true resume (safe-ish re-deliver, but silent). Double-hard-crash-only path; untested against an empty-id orphaned stream. Add a test + decide skip vs re-deliver vs needs-human. | `HUMAN:` (a recovery lane, re-homed by SF0.4) | **HALF CLOSED, half owner-gated (SF0.4).** The row asked for two things and one is delivered: the **decision** is made and implemented — an orphaned `SessionStarted` with an empty `AgentSessionId` no longer silently starts a fresh agent. `RunLoop.Control.cs:125-131` marks the run `NeedsHuman` with the reason named ("Orphaned session #N in run.db has no AgentSessionId — manual review needed"), and the non-empty case is the only one that queues a resume. What is still missing is the **live gate**: driving that branch means seeding a run.db with an unmatched `SessionStarted`, starting the orchestrator, and asserting it parks — and a `NeedsHuman` park waits for its owner, so the test has to drive the park and release it rather than just awaiting `RunAsync`. That is a recovery-lane's worth of harness work, not a checkpoint in this era. **Owner: `HUMAN:`** — schedule a recovery lane, or accept a documented branch as the standing answer. Not silently carried: this sentence is the accept. |

**Fixed in-phase by the B2 audit** (no followup, recorded for the trail): persisted `TokenDelta`
events never carried a `sessionId`, so `LiveMetrics.ForSession` folded zero against a real log � the
B2.6 deliverable was correct only in unit tests that hand-set `SessionId`. Now stamped in
`AgentSession` from the conductor session number + regression test
`EventLogTests.EmitPreservesSessionIdSoLiveMetricsCanFoldPersistedDeltas` (real on-disk path). See
`.conductor/handovers/B2.md`.

**Re-homed from B0/B1 (B2 owned but did not clear):** FU-B0-1 (MA0045 sync-over-async engine),
FU-B1-1 (ScriptProvider stdout/stderr split), FU-B1-2 (CT through `IProgressProvider.Read`) remain
OPEN. B2 added Host/DI/logging groundwork but kept the Orchestrator run loop synchronous; the async
engine pass was NOT done. `MA0045` stays at `suggestion` (not lowered � no ratchet violation, but not
raised either). Schedule a dedicated async/harden lane rather than assuming these landed in B2.

## Opened by B3 (audit session, 2026-07-08, session #19)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-B3-1 | No Orchestrator integration harness for the process-control loop | The B3 "gate" tests simulate `RunState` by hand; none drive `HandleControl`/`Run`. Build a harness (fake agent + temp git repo) and cover budget park->approve->run->re-park, approval mode, `goto`, `rollback` (clean/dirty/`--force`), `retry-stage`, graceful-cancel. The B3 audit fixed the real branch logic and locked the PURE slices (`OwnerApproval`, `ControlFile`) but the loop itself is proven only by reasoning. | B4/B5 (whoever adds the run harness) | CLOSED (W-series) — the harness exists several times over: the W1–W4 live gates drive a real Orchestrator against a fake agent + temp git repo, and W5.1's `tools/w5/rehearsal.ps1` drives the shipped binary out of process through budget/QA/plan-edit/add-card levers to `RunFinished`. |
| FU-B3-2 | B3.5 graceful Ctrl+C is unproven | No cancellation test and no `B3.5-gate.txt` (evidence exists only for B3.1-B3.4). Add a test asserting state saved + resume queued + log flushed + exit code 130, and capture the evidence. | B3 fix-lane / B4 | CLOSED (W3.3, twice) — `W3ProcessRailsTests.CancellingMidSession_LeavesAResumableRun` asserts exit 130 + `Interrupted` + `PendingResume` + a reopenable run.db; and `tools/w3/window-close.ps1` proves the same contract from OUTSIDE the process, against a real console window, with a hard-kill negative control. |
| FU-B3-3 | Budget accumulators are per-process, not persisted | `_runCostUsd`/`_runTokens` reset at each `Run()` start. The park survives restart, but a run killed mid-accrual BEFORE parking restarts its count from 0 -> a run split across restarts can exceed `maxRunCostUsd`/`maxRunTokens` without parking. Decide per-process vs per-logical-run; if the latter, persist a cumulative baseline. | B3 fix-lane | CLOSED — decided per-logical-run and persisted: `RunContext` seeds `RunCostUsd`/`RunTokens` from `State.PerRunCostUsd`/`PerRunTokens` and writes them back (`RunContext.cs:192,200`), so a run split across restarts keeps accruing against the same cap. |
| FU-B3-4 | Rollback is not recorded as an event | `git reset --hard` is written to conductor.log + Serilog but has no `ConductorEvent`, so the timeline/report/(B6) Telegram do not show a rollback. Trap B3.3 says "log the reset". Emit a destructive-action event. | B6 (or B3 fix-lane) | CLOSED — `RollbackExecuted` (`ConductorEvent.cs:42`, discriminator `rollbackExecuted`) carries StageId/FromSha/ToSha/Forced and is emitted by `ControlDispatcher` right after the `git reset --hard`. |
| FU-B3-5 | `!inSession` control verbs issued mid-session are silently dropped | `retry-stage`/`rollback`/`pause-after-stage`/`goto` consume (delete) `control.json` but the `when !inSession` guard fails with no operator feedback. Queue for after-session or reject with a message. | B3 fix-lane / B4 | CLOSED — rejected with a message: `ControlDispatcher` ends with `if (inSession && action is RetryStage or Rollback or PauseAfterStage or Goto or AbortNow) log("control: {action} received mid-session — re-run after session ends for it to take effect")`. |

**Fixed in-phase by the B3 audit** (no followup, recorded for the trail): (1) approving an
approval-mode/budget park wrongly confirmed & advanced the stage past unfinished work (approval mode
also re-parked forever) - fixed with a persisted `AwaitingOwnerReason` + pure `OwnerApproval.Decide`
+ `ApproveAwaitingOwner`, locked by `OwnerApprovalTests`; (2) non-destructive CLI control verbs wrote
`confirmed:null` and `ReadControlFile.GetBoolean()` threw an uncaught `InvalidOperationException` that
crashed the control loop - fixed with a pure `ValueKind`-guarded `ControlFile.Parse` + hardened catch,
locked by `ControlFileTests`; plus `rollback --force` wired (was referenced but unimplemented), `goto`
to a confirmed stage made effective, and dead `ControlAction.ApproveOwner` removed. See
`.conductor/handovers/B3.md`. Commit `2a0fa9f`.

**Note on FU-B2-3 (B3-owned):** NOT cleared by B3 delivery or this audit (it is event-log recovery,
not process control). Remains OPEN; re-home to a recovery/harden lane.

## Opened by B4 (audit session, 2026-07-08, session #33)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-B4-1 | Orchestrator central log emits no severity | The severity model (B4.4) is rendered and now exercised by the dashboard's own control feedback (abort/skip/kill=Warn, inject=Success/Error), but `Orchestrator` still logs via `sink.Log(stamped)` (plain string → `Info`) at `Orchestrator.cs:1241`. The lines an unattended operator most needs colour-coded — gate-failed, backoff, needs-human, stage-confirmed — stay grey. Map those message→severity and emit `LogEntry`. Touches the Orchestrator broadly + needs a mapping decision, so deferred out of the UI-scoped audit budget. | SF0.4 | **CLOSED as accepted-by-design (SF0.4).** Verified still as filed — `RunContext.cs:225` is `Sink.Log(stamped)`, a plain string. Accepted because the premise moved: `RunContext.Log` (`RunContext.cs:205-226`) writes the same line three ways, and two of them carry structure — the file log, and Serilog under a correlation scope with a real level and the run/session/stage/gate fields. Only the console sink is grey, and the Face stopped rendering that central log as its primary surface. Mapping message text to severity would mean pattern-matching log strings in the Orchestrator to re-colour a strip nobody reads. Not deferred: declined, with the reason. |
| FU-B4-2 | Alt-screen restore on signal/ProcessExit is inspection-only | `AltScreenTests` cover enter/leave/idempotent/redirected via a `StringWriter`, but the `PosixSignalRegistration` + `AppDomain.ProcessExit` safety nets are driven by no test (need a real process/signal). The audit hardened `Leave()` to fail-safe on those paths, but "Ctrl+C leaves a clean prompt" is still a manual claim. Add a headless assertion that disposing via those nets emits `\e[?1049l`. | B4 fix-lane | CLOSED (C4) — SafetyNet_RegistersAndCleansUpProcessExit test added. Real signal test deferred to Human Verification checklist (C8). |
| FU-B4-3 | Status-agent probe unobserved + no CancellationToken | `LiveDashboard.StartStatusAgent`'s `Task.Run` catches broad `Exception` and surfaces into the pane (OK per A15), but is fire-and-forget (`_ =`) with no CT — a hung `StatusAgent.Run` leaks the task until process exit and can't be cancelled by closing the modal. Thread a CT (cancel on modal close / process exit) and observe the task. Low risk (operator-initiated, single). | B4 fix-lane / B6 | CLOSED (C4) — `_statusCts` CancellationTokenSource added, cancelled on Esc/Q modal close, threaded to StatusAgent.Run. |

**Fixed in-phase by the B4 audit** (no followup, recorded for the trail): (1) `StartStatusAgent` read
the mutable `_agent`/`_thinking`/`_snap`/`_gates` off the UI thread without `_gate`, so `_agent.TakeLast`
could throw "Collection was modified" mid-run — fixed by capturing the context inside the lock; (2) the
severity model was render-only (no producer emitted a non-`Info` severity → inert in real runs) — wired
the dashboard's operator-facing control feedback to real severities; (3) `AltScreen.Leave()` could throw
out of a signal/`ProcessExit` handler (crash instead of restore) — made the restore writes best-effort.
No gate weakened, no analyzer lowered; 221 tests pass. See `.conductor/handovers/B4.md`.

**Reviewed & accepted (not a regression):** `TrackerParserTests.ParsesRealLoomTracker` was relaxed this
phase from `== 35` to `>= 30` — it asserts against a foreign, live file a separate Loom run mutates, and
`TrackerParser.cs` itself was untouched in the B4 diff, so the invariant-based assertion is the better
test, not a cover-up. Kept as-is.

## Opened by B10 (audit session, 2026-07-09, session #61)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-B10-1 | No orchestrator integration harness for SelectStage + DepSatisfied | The readiness-ordering logic is tested only via model validation (B10_1Tests); no test drives a live orchestrator with real tracker + RunState. Extends FU-B3-1 (the base harness gap). | B12 fix-lane | CLOSED with FU-B3-1 — the W-series live gates drive a real orchestrator with a real tracker and RunState; W5.1 additionally drives stage selection across 10 sessions of a 3-stage plan out of process. |
| FU-B10-2 | Battery-collapse token savings not empirically measured | B10.4 spec requires "token-per-checkpoint measured before/after on a self-run; documented drop." The prompt note is emitted correctly, but no automated metric compares pre- and post-collapse session tokens. Conduct a real measurement run. | SF0.4 | **RETIRED as unanswerable by observation (SF0.4), with the baseline recorded so it is not a dead end.** The re-home note said the number was "now computable from the core run's 28 real sessions" — **measured, and that premise is false**: this repo's `run.db` holds exactly two runs (`e9e21d10` core, `8cefa5de` face) and **both set `batteryCollapse:false`**, so there is no before/after pair in it. Nor would a cross-plan comparison answer it: the only `batteryCollapse:true` plans (`conductor.self`, foreman, maestro) delivered different work, so a difference would measure the plans, not the flag. B10.4's question can only be answered by an A/B on the **same** checkpoints with the flag flipped — i.e. paying a real model twice for the same work, which is an owner's spending decision, not a measurement anyone can take from history. **The baseline, for whoever runs that A/B:** core run at `batteryCollapse:false` — 28 sessions, 26 checkpoints, 5,319,262 in + 2,337,913 out + 496,814,907 cache-read tokens, $359.98 → **$13.85 and ~294k non-cache tokens per checkpoint** ($12.86 per session). `docs/plan-config.md` stated a "~30-50%" saving that nothing had measured; SF0.4 corrected that line rather than leaving a number the project cannot stand behind. |
| FU-B10-3 | HookConfig.TimeoutMinutes=0 is not validated | `TimeSpan.FromMinutes(0)` = `TimeSpan.Zero` causes immediate timeout; plan validation should reject `< 1`. Low risk (default is 3). | B10 fix-lane | CLOSED (C4) — PlanConfig.Validate now rejects TimeoutMinutes < 1 for setup, teardown, pre-hook, and post-hook. |
| FU-B10-4 | ComputeDepth allocates per call (HashSet + O(n·d) scan) | Negligible for the self-plan but could be a hot path for large plans. Pre-compute depth in SnapshotBuilder.BuildStages() once. | post-B11 | CLOSED (C4) — PreComputeDepths() builds a dictionary once; Build() now reads from it. |

## Opened by B11 (audit session, 2026-07-09, session #64)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-B11-1 | Completion script exhaustiveness test | The `Completion_Powershell_ContainsAllVerbs` test checks presence of specific verbs but doesn't assert exhaustive parity with Program.cs command registrations. Adding a command without updating completion silently breaks tab-complete. | B12 fix-lane | CLOSED (C4) — `Completion_ContainsAllRegisteredVerbs_Exhaustive` asserts all 20 verbs present in both PS and bash output; no stale verbs allowed. |
| FU-B11-2 | Cross-platform clean-clone battery on Linux | B11.3 clean-clone proof ran on same-machine Windows only. A Linux-hosted clone+build+test would prove true cross-platform portability. | `HUMAN:` (needs a Linux host; re-homed by SF0.4) | **PARTIAL, and that is the standing answer (SF0.4).** Unchanged since W6.2: CI's `ubuntu-latest` leg clean-clones, restores, **builds** the whole tree and runs the Go suite on every push, so "accidentally Windows-only at compile time" is gated; it deliberately does not run `dotnet test`, whose tests spawn PowerShell gates and `.exe` children. Running the ENGINE on Linux stays unproven and `README.md`'s **Platforms** section says so in the shipped docs — the row is not a hidden gap. Closing it means someone runs a real plan on a Linux host, which needs a host this project does not have. **Owner: `HUMAN:`.** |
| FU-B11-3 | Real-credential cTrader owner-gated path | B11.4 acceptance used a fake-agent; the credentialed cTrader login + live compare-both path is unproven. Needs a live Shamshir run with real credentials. | `HUMAN:` (Shamshir run) | **OPEN, owner-gated, and stated as such (SF0.4)** — not carried as if a session could clear it. Real cTrader credentials and real money put this outside what any agent in this era is allowed to do; no amount of scheduling turns it into a checkpoint. It stays open until the owner runs a credentialed Shamshir plan, and no future triage should re-home it. |

**Fixed in-phase by the B10 audit** (no followup, recorded for the trail): (1) critical bug —
`PreHookRunStages` recorded before checking hook success, causing a failed pre-hook to be silently
skipped on resume; fixed by moving `Add()` into `RunStageHook`'s success branch; (2) hook failure
error log omitted stdout — now includes output (truncated to 500 chars); (3) `RunStageHook` used
hardcoded `CancellationToken.None` — pre-hook now passes `ct` from the orchestrator loop; (4) 3 new
tests added validating failure-path stdout capture and RunState round-trip for PreHookRunStages. See
`.conductor/handovers/B10.md`.

## Opened by F0 (audit session, 2026-07-10)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-F0-1 | `RunStageHook` post-hook uses `CancellationToken.None` | Post-hook in `ConfirmStageAsync` passes `None` — hook is a local PowerShell process with its own timeout; low risk but inconsistent with async CT pattern. Pre-existing, not introduced by F0. | F1 or F2 | CLOSED (be10727) — post-hook now passes `ct` from `ConfirmStageAsync` |
| FU-F0-2 | `StartParallelAudit` launches uncancellable task | `Task.Run` passes CT but no `_parallelAuditCts` to cancel mid-flight. Pre-existing. | SF0.4 | **CLOSED as accepted-by-design (SF0.4).** Verified: `LaneCoordinator.cs:45` is `StartParallelAudit(PendingParallelAudit audit, CancellationToken ct)` and `RunLoop.cs:373` hands it the run's token, so the audit dies with the run — the leak the row was filed about is gone. What remains is that there is no way to cancel *just* the audit while the run continues, and nothing has ever wanted one: a parallel audit is short, read-only, and started by the engine rather than by an operator who might change their mind. Adding a second CTS would be machinery with no caller. |
| FU-F0-3 | Telegram fire-and-forget has no fault continuation | `_ = PushAsync/PushWithKeyboardAsync` — internal methods catch exceptions but the pattern is fragile. Pre-existing. | SF0.4 | **CLOSED as accepted-by-design (SF0.4).** Re-verified at 5 sites, which drifted since the last triage: `RunLoop.cs:361`, `RunLoop.Plumbing.cs:244`, `VerdictEngine.cs:99` and `:475`, `VerdictEngine.Phase.cs:151`. The pattern is deliberate and documented at the callee — `TelegramService.cs:269` says in as many words that every real caller is `_ = Push…(…)`, so the method must not throw, and it does not. A fault continuation would therefore have nothing to catch. The failure mode is a notification that silently does not arrive, and that is a *reporting* gap owned by real rows with living owners: FU-OWNER-11 and FU-OWNER-13 (SF4.2). Nothing here to fix. |
| FU-F0-4 | `.GetAwaiter().GetResult()` at Spectre.Cli boundaries | 3 sites in `Commands.cs` — safe without `SynchronizationContext` but fragile. Convert `Execute` to `async Task<int>`. Pre-existing. | on demand | CLOSED as filed — `Commands.cs` no longer exists (split into one file per command) and the commands that matter, `run` above all, are already `async Task<int>`. Four `.GetAwaiter().GetResult()` sites remain in `Commands/`, each an intentional Spectre.Cli sync boundary carrying a justified pragma; they are the documented pattern, not a leftover. |
| FU-F0-5 | `EventLog.Dispose()` blocks on `_drain.GetAwaiter().GetResult()` | Drain task blocks synchronously on dispose — could hang on slow filesystems. Pre-existing, out of F0 scope. | on demand | CLOSED as accepted-by-design — `EventLog.cs:166` and `TranscriptLog.cs:152` now carry the reasoning explicitly: `IDisposable.Dispose` is sync by contract, and the drain blocks only at the run boundary, where blocking is the point (the alternative is losing the tail of the log). |
| FU-F0-6 | `HostLoggingTests.DryRunWritesJsonLogWithCorrelationProperties` flaky | File-share race between parallel test runs. Pre-existing (possibly worsened by F0.2 async File.ReadAllText→ReadAllLinesAsync timing change). Passes on retry. | F1 fix-lane | CLOSED (W2) — root cause was not a file-share race: Serilog's file sink is process-global, so a parallel host interleaves lines, and the test asserted the FIRST runId-bearing line was its own instead of scanning for it. **Do not read this as covering the current intermittent** — `DryRunWritesStructuredLogWithRunIdCorrelation` (a different test) times out under load ~2 in 12 local full runs and is unresolved; see the W6 handoff. |

**Fixed in-phase by the F0 audit (session #4, prior)**: (1) 7 `CancellationToken.None` delay sites in async flow → `ct` with OCE handling for immediate Ctrl+C responsiveness; (2) CT threaded through `RunFollowupFixLanesAsync`, `CollectLaneArtifactsAsync`, and 3 `_progress.Read` calls; (3) `PushWithKeyboardAsync` at new CT scope given explicit `CancellationToken.None`. See `.conductor/handovers/F0.md`.  
**Fixed in-phase by the F0 re-audit (session #5, this session)**: (4) `ApproveAwaitingOwnerAsync` → `ConfirmStageAsync` passed `CancellationToken.None` — now accepts and threads `ct` through the chain (`HandleControlAsync` → `ApproveAwaitingOwnerAsync` → `ConfirmStageAsync`); (5) `RunStageHook` post-hook now passes `ct` instead of `CancellationToken.None`; (6) removed redundant `Task.Run()` wrapping in `RunFollowupFixLanesAsync` (pre-F0 leftover — `MutatingLaneRunner.RunAsync` is already async).

## Opened by F1 (audit session, 2026-07-10)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-F1-01 | TrackerGenerator no test/framework baseline | Removed misleading hardcoded placeholders (framework version, test count). If needed, derive from gate battery results (scores table, F4). | — | CLOSED as won't-do — the misleading placeholders were removed, which was the actual defect. Nothing since has wanted a framework/test-count line in a generated tracker, and inventing one would re-create the thing that made it misleading. |
| FU-F1-02 | McpTaskServer.HandleNote uses NoteAdded as journal container | Notes persist as `NoteAdded` events — these are NOT tasks and the `TaskGraph` projection ignores them. Fixed in 10th F1 audit: added dedicated `NoteAdded` event type. | F2 | CLOSED (10th F1 audit) |
| FU-F1-03 | EmitSessionFinished commit SHA extraction | `rec.NewCommits[^1].Split(' ')[0]` assumes git log --oneline format. Advisory-only evidence field. | SF0.4 | **CLOSED as accepted-by-design (SF0.4).** Verified unchanged at `RunLoop.Plumbing.cs:200`. The "assumption" is not one: `Git.CommitsSince` produces the `git log --oneline` lines this expression parses, so the producer and the consumer are the same codebase and a format change breaks both together. The field is advisory evidence on `SessionFinished`, not a decision input. (SF0.2 fixed the genuinely broken thing two lines away — the `rec.GateSummary ?? completed` evidence fallback that stamped an empty string over the agent's.) |
| FU-F1-04 | RunDb.Query lacked disposed-connection guard | Safe today but would fail loudly with confusing errors if F2.1 adds concurrent access. **CLOSED this session** — guard added at RunDb.cs:517. | F2 | CLOSED (sixth F1 audit) |
| FU-F1-05 | SeedCheckpoints not transactionally atomic | Each UPSERT is a separate implicit transaction. Power failure mid-loop could leave partial state. Next SeedCheckpointsFromTracker reads intact tracker file and re-seeds, so no permanent data loss. Wrap in a single transaction for atomicity if needed. | F2 fix-lane | CLOSED (superseded by W1.1/W1.2) — the `checkpoints` table this row is about was DROPPED in migration v8. `SeedCheckpoints` now emits `TaskAdded`/`TaskStatusChanged` into the append-only event log, whose fold already tolerates a truncated tail, and W1.2's `WorkGraphSync` re-runs upsert-never-clobber at every boundary — so a partial seed self-heals instead of persisting. |
| FU-F1-06 | run.db runs.status not updated on non-completion non-terminal states | NeedsHuman, Paused, AwaitingOwner, VerifyingGates, Backoff leave runs.status='running' in run.db. RecordRunEnd sets ended_utc which isn't warranted for resumable states. Add an UpdateRunStatus method (status-only, no ended_utc) and call from NeedsHuman + other state transitions. Low severity: state.json is authoritative; run.db is additive/best-effort; InitializeRun (INSERT OR REPLACE) fixes on resume. | **KS0.2** (re-homed by SF0.4 to SF2.1, which never touched it) | **CLOSED (`15627b9`, KS0.2).** `IRunStore.UpdateRunStatus` writes the status and nothing else — no `ended_utc`, because a run that can still be resumed has not ended — and `RunContext.Save` calls it, so every park already routes through the writer and one invented in a later era does too. `RunRecord.StatusText` owns the vocabulary: only the parks that outlive the engine (`Paused`, `NeedsHuman`, `AwaitingOwner`) get their own word, while `Idle`, `Waiting`, `Backoff` and `VerifyingGates` stay `running` deliberately — a row that stops saying `running` under a live engine is a row `StateRepair` would believe it may write, so its liveness query was widened to terminal-vs-not in the same commit. The rows already frozen at `running` by engines that exited are closed through `conductor run close`, shipped alongside. Read-side reconciliation of a killed engine's row is KS1.3. |
| FU-F1-07 | Completion test uses hardcoded verb list | The `Completion_ContainsAllRegisteredVerbs_Exhaustive` test hardcodes `expectedVerbs`, which allowed `task` and `note` to be missing from both the completion scripts AND the test for 9 audit sessions. Replace with a runtime reflection test that enumerates Program.cs registrations dynamically. | SF0.4 | **CLOSED (`2fea703`, SC8.3) — it had been fixed for a while and nobody came back to say so.** Verified in the tree, not read off the handoff: `Completion_ContainsAllRegisteredVerbs_Exhaustive` now opens with `var expectedVerbs = RegisteredVerbs()`, and `RegisteredVerbs()` reads `src/Conductor/Program.cs` line by line, matching `AddCommand<T>("verb")` and skipping any line carrying `.IsHidden()`. It also asserts the scan itself is not broken (`> 30` verbs parsed), rejects stale verbs the scripts declare but Program.cs does not register, and asserts exact count parity for **both** the PowerShell and bash scripts. The failure mode the row describes — add a verb, forget the hand-typed list, stay green — cannot happen: the list is no longer hand-typed. |

## Opened by owner (manual dogfooding observation, 2026-07-12)

Screenshots of a live `conductor run` session (Ink TUI, `face/`), not a synthetic run. All 7 are
about the Face specifically — M5's remit. Read this section at M5's opening, not only as a
post-confirmation fix-lane: these are acceptance items for that stage, not cleanup after it.

**Triaged 2026-07-28 — FU-OWNER-1..8 are CLOSED as obsolete.** They describe the Ink TUI in
`face/`, which M7 deleted; face-go is a different program (Bubble Tea, golden-frame tested, the
v3 sidebar-always/tabs-not-modals layout). Keeping them open would mean carrying UI defects
against a UI that no longer exists. Where a concern survived the rewrite, the row says where it
went. FU-OWNER-9 is not a Face row and stays OPEN.

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-OWNER-1 | Screen flickers during a live run | Visible full/partial redraw flicker while a session is active. Likely a full-frame repaint on every tick instead of a diffed render. | — | CLOSED (obsolete) — Ink's render loop is gone; Bubble Tea diffs frames itself. |
| FU-OWNER-2 | Notes/toasts disappear before they can be read | A transient notification (e.g. a `conductor note` add) flashes and vanishes too fast to read. Needs a longer minimum display time or a way to review recently-dismissed notices. | — | CLOSED (obsolete) — different toast implementation entirely. If it recurs in face-go, file it fresh against `face-go/internal/tui`. |
| FU-OWNER-3 | Command palette modal is unreadable — renders merged with the panel behind it | Screenshot: opening the command palette overlays it on the AGENT log pane with no opaque backdrop; both texts render interleaved/overlapping, illegible. Needs a solid background fill (or a real overlay layer) behind any modal. | — | CLOSED (obsolete, and fixed by design) — the v3 redesign is explicitly *tabs, not modals*; the command bar is a real layer, and the overlay rules live in `face-go/STYLE.md`. |
| FU-OWNER-4 | Agent's actual thinking/reasoning is not visible, only tool-call one-liners | The AGENT pane shows terse lines like "3 tool calls (last: read X)" — there is no way to see what the agent is actually reasoning about or deciding. This is the M5.3 "native console" gap (stream raw stdout via `/console/current`) — confirms it's needed, not optional polish. | — | CLOSED — the concern was real and was delivered, not dropped: the live transcript wire (`877ff57`) plus U3.2/U3.3 transcript readability give face-go the agent's actual output, and W2.3 put the card's own context into the prompt. |
| FU-OWNER-5 | Panels read as empty/sparse with little info density | PLAN and PROCESSES panes show large blank areas relative to the information in them; screenshots look unfinished even mid-run. | — | CLOSED — the concern drove the v3 redesign (sidebar-always, denser panes) and the U-series; the panes it names no longer exist in that form. |
| FU-OWNER-6 | Top status bar is cramped | session cost / run cost / timer / stage are crammed into one thin strip, hard to scan at a glance. | — | CLOSED (obsolete) — face-go's status strip is a different widget with its own golden-frame tests. |
| FU-OWNER-7 | Footer hotkey bar doesn't read as interactive | `Tab 1 2 3 : or Ctrl+K i e h r ? q or Ctrl+C` renders as a dense unlabeled string, not obviously a set of buttons/actions. | — | CLOSED (obsolete) — those keybindings are gone; face-go's are in `cmdbar.go` and documented in `face-go/STYLE.md` (quickstart's table, which still described THIS Ink footer, was fixed in W6.3). |
| FU-OWNER-8 | Face TUI crash when clicking a work item | Clicking on a work item in the Face TUI killed both `conductor.exe` and `node.exe` with no crash dump (`crash-*.log` absent). No `pendingResume` set, consistent with terminal-window close (known `CTRL_CLOSE_EVENT` gap) rather than an app-domain crash. In-flight agent work survived on disk. Reproduce by clicking any checkpoint row in the PLAN or TASK pane mid-run. | — | CLOSED — obsolete as filed (`node.exe` is the retired Ink face), and its own diagnosis was closed for real by W3.3: it reads as a `CTRL_CLOSE_EVENT` window-close, which now stops the run gracefully and leaves a resumable run.db. Proven end to end by `tools/w3/window-close.ps1`. |
| FU-OWNER-9 | Agent kills its own parent conductor process | A fix session's prompt showed a build error `"locked by: conductor (15300)"`. The agent inferred PID 15300 was a stale orphan, set a todo to kill it, ran `Stop-Process -Id 15300` — but PID 15300 was the CURRENT conductor (handled both sessions 3 and 4). No crash dump (external process kill). **Suggested fix:** (1) add a self-PID guard to the agent tool contract so Stop-Process rejects the conductor's own PID; (2) gate battery should skip rebuilding Conductor.csproj when the running binary IS the one being built (detect via PID matching); (3) add a warning in the fix prompt: "locked by conductor (PID)" usually means the current run, not a leftover. | SF0.3 (was: next era, safety) | **OPEN — the most consequential row left in this file.** Not a Face bug and not obsolete: the agent runs unsandboxed by design (see `SECURITY.md`), so nothing today stops a session from killing the process supervising it. W3.3's `PidLiveness` fixed the mirror-image defect — the *engine* tree-killing a pid it no longer owns — but added no guard on the agent's side of the tool contract. Suggested fixes (1)–(3) still stand as written. **Owner: SF0.3** (2026-07-31) — and the hazard grew: this machine now runs two conductors at once, so a pid an agent writes off as stale can belong to another repo's live run. **CLOSED by SF0.3 (`c84ccfc`)** — suggested fixes (1) and (3) delivered, and the guard is knowledge rather than a permission, because the agent runs unsandboxed by design and always will: `SessionRunner` puts `CONDUCTOR_PID` in every agent's environment, `ToolContract` names that pid in the prompt with the story of what it cost, and `fix.md` carries the `locked by: conductor (PID)` warning where gate output actually shows it. Proven by a live orchestrator run whose fake agent echoes `%CONDUCTOR_PID%` (`LiveSession_CarriesConductorPidInTheAgentsEnvironment_AndNamesItInThePrompt`). Suggestion **(2)** — the gate battery skipping a rebuild when the running binary IS the one being built — is **NOT** delivered: it was outside the checkpoint's title and that session was forbidden from touching the published engine. It is the only part of this row still open; re-home it at SF0.4 rather than closing it silently. **SF0.4 re-homed it into the bug ledger as `#16`**, which is the right home now that a bug survives the run that filed it — and it is not theoretical: SF0.4 itself drove a live proof from `src/Conductor/bin/Debug/net10.0/conductor.exe`, which is precisely the image the next `dotnet build Conductor.slnx` has to overwrite. **This row is now fully CLOSED**; `#16` carries the remainder. |

## Opened by owner (dogfooding the v0.2.0 install against a live run, 2026-07-31)

Filed while driving the **NINE STREETS** plan in `C:/Code/sk-studio` (run `7951c3ca…`) with the
freshly installed `0.2.0+f638ba6f7f14`. All three are the same shape: **the system knows the answer
and never volunteers it**, so a correct install looks identical to a stale one.

Context that is *not* a defect and should not be re-filed: `conductor face` still renders the
pre-Sarban face because the face era is unbuilt — every checkpoint in `SARBAN-FACE-TRACKER.md`
(SF1.1–SF7.2) is TODO. v0.2.0 shipped the **core** era (SC1–SC8). The installed
`conductor-face.exe` is stamped `vcs.revision=f638ba6…`, `vcs.modified=false`, i.e. already the
newest face code that exists.

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
|FU-OWNER-10|Nothing on the wire says which build you are attached to|`GET /state` carries plan, run id, repo, model, cost — and no engine version, commit or face build. The face therefore cannot show it either, so "did my reinstall take?" is unanswerable from inside the tool. Proving this run was on the new engine took `Get-CimInstance Win32_Process` for the image path, the file's mtime against the run's start, `conductor version`, and `go version -m` on the face binary — four out-of-band checks for a fact the engine already holds (`version` prints it: commit, built, runtime, os, binary). **Suggested fix:** add `engineVersion` / `engineCommit` / `faceBuild` to the `/state` payload and put the short form in the face's status strip; SF3.3 already opens that payload and that strip for branch/dirty/ahead-behind/HEAD sha, so this is the same edit, not a new one.|SF3.3| **CLOSED (SF3.3, `d500f00` engine + `234dd01`/`83b432e` face)** — `GET /state` carries `EngineVersion` and `FaceBuild` (`Core/Http/ControlPlaneDto.cs:86-88`), stamped from `Core/Face/FaceBuildStamp.cs`; the Face decodes them into the top-bar build chip and the Home card, so "did my reinstall take?" is answerable from the surface. |
|FU-OWNER-11|Telegram pushes carry no identity — repo, plan, run id or build|A run notification reads `s2 NoProgress — P0` + gates + result + cost, with nothing naming the repo, the plan or the run. One chat receiving two machines' runs cannot attribute a line, and a message read hours later cannot be dated to a build. The corollary bit today: a hand-sent operator message ("Sarban core complete… New engine `0.1.1-alpha.0.57+2fea7032749d` installed") is indistinguishable in the chat from an engine push, and quoted a version the engine had already superseded — the engine's own pushes would have been right by construction. **Suggested fix:** prefix each push with `<planName> · s<N>` and carry repo + engine version in the run-start/run-end message; if FU-OWNER-10 lands, take the version from the same field.|SF4.2 (owns the push path)| **CLOSED (SF4.2, `bc7ff3f`) — with a stated remainder.** `TelegramService.IdentityLine` (`Core/Integrations/TelegramService.cs:390`) is prefixed in `SendAsync`, the one point every push, digest, command reply and test message passes through, so no call site can forget it and none added later can either. **Measured remainder:** the line carries the plan name and `s<session>` — *not* the repo or the build, which this row also asked for (`grep EngineVersion\|BuildStamp\|repo` across `TelegramService*.cs` finds none on a push path). The commit subject claims all four; two shipped. The in-code comment argues plan+session are the two facts a message cannot recover on its own, which is a fair call — but it is a narrower closure than the row requested, and it is recorded as such rather than rounded up. |
|FU-OWNER-13|Between a saved plan edit and the next session boundary, Telegram status contradicts the plan on disk (owner: **SF4.2**)|Wiring Telegram into the live NINE STREETS run: `POST /plan/edit` returned `ok:true, planVersion:3` and the block was on disk, then `POST /telegram/token` answered *"saved, but this run still will not deliver: not configured — **add a telegram block to the plan**"* — advising the edit that had just been made and accepted seconds earlier. `GET /telegram/status` says the same, because both read the live in-memory `PlanConfig`, which by design is not mutated on the HTTP path (`ControlPlaneServer.Plan.cs:11`); the reload is queued (`control: ReloadPlan (during session)`) and applied at the next boundary. The behaviour is right; the sentence is not — it names a cause that no longer exists and gives an instruction that would be a no-op. **Suggested fix:** when a reload is pending, both replies should say so instead — "a plan reload is queued; Telegram starts at the next session boundary" — and `/telegram/status` should carry a `reloadPending` bool so the Face's Telegram tab can show *waiting*, not *unconfigured*. This is the same failure SC1.3 was written to kill (a saved thing reporting as if nothing were saved), one layer out.|SF4.2 / SC1 fix-lane| **CLOSED (`f0d12bb` engine + `8580cca`/`017c8a9` face)** — a queued reload now says so instead of advising the edit you just made. `TelegramStatusDto.ReloadPending` (`Core/Http/ControlPlaneDto.Telegram.cs:27`) is set from the queued plan at `ControlPlaneServer.Telegram.cs:37,47,67`, so `/telegram/status` and the token reply both distinguish *waiting for the session boundary* from *unconfigured*, and the Face's Telegram tab renders the waiting state. |
| FU-OWNER-12 | A run never says its notification path is dead (owner: **SF0.1**) | With no `telegram` block, `grep -ci telegram .conductor/conductor.log` on a live run returns **0** — startup logs nothing, so the operator watches a silent chat and cannot tell "nothing happened" from "nothing can be delivered". The verdict *is* computed and it is good: `conductor doctor` warns `⚠ telegram not configured — optional; add a telegram block to the plan, or set it up from the Face's Telegram tab`, and `GET /telegram/status` answers `willDeliver:false` with the identical sentence (SC1.2's same-words requirement, working). Both only answer when asked. **Suggested fix:** log that one sentence once at run start, at the same level as the control-plane URL. | SF0.1 | **CLOSED (SF0.1, `5217986`)** — `RunLoop.cs:77` calls `LogNotificationReadiness()`, which logs either `notifications: telegram will NOT deliver — <blocker>` or `notifications: telegram will deliver this run's pushes` (`RunLoop.Control.cs:182-186`). Seen live in SF0.4's proof run, one line under `conductor start`: *"notifications: telegram will NOT deliver — not configured — optional; add a telegram block to the plan…"* — the same sentence `doctor` and `/telegram/status` give, now volunteered instead of only answered. |

## Carried forward from the core run's bug ledger — 2026-07-31

**Why this section exists.** `conductor bug` is **run-scoped**, not repo-scoped. Measured while
setting up the face run:

```
> conductor bug list -p plans\conductor-sarban-face.plan.json
No run found in run.db. Initialize the run first.
```

The Sarban **core** run filed 14 bugs and closed 3 in flight (#1, #7, #14). The remaining **11 are
open**, and the moment the face plan starts a new run they become invisible to every session working
in this repo — no error, no warning, an empty ledger that looks like a clean one. They are
transcribed here because this file is tracked, survives eras, and is the one place the project has
agreed never to drop a row silently. Full repro text stays in `run.db`'s `bugs` table (`detail`
column) — read it with
`sqlite3 "file:.conductor/run.db?mode=ro&immutable=1" "select id, detail from bugs where status='open'"`.

**SF0.4 owns making this stop happening**, not just cleaning up after it: open bugs must survive the
run that found them.

### SF0.4 closed this section — 2026-07-31

**The disappearance is fixed at the source.** `conductor bug list` now shows the open rows earlier
runs in the same repo left behind, attributed to the plan that filed them, and `conductor bug fix`
closes a carried row from the run that actually fixed it. Measured against this repo's own `run.db`:
before the change the face run's `bug list` printed **1** row while **12** were open; after it,
**12** with the 11 carried ones marked `filed by: Sarban core - the engin…`. The same rows now reach
the next session's *prompt* (`BugsBattery`), a finished run's console epilogue, and `RUN-SUMMARY.md`.
Live proof over two real completed runs sharing one `.conductor`: `tools/sf0/sf0-4-live-proof.ps1`.

**And this section is now closed.** SF0.1–SF0.3 fixed all eleven, so SF0.4 closed all eleven in the
ledger itself — the `status` column below records the commit, and `conductor bug list` in this repo
is down to the two genuinely-open rows (#15 prompt length, #16 the FU-OWNER-9 remainder). The table
stays as the historical record; it is no longer the source, because the ledger no longer forgets.

| bug | filed under | item | owner | status |
|-----|-------------|------|-------|--------|
| #2 | SC1 | `Run services started: TelegramService` prints even when that service early-returned and started nothing. | SF0.1 | **CLOSED (SF0.1, `5217986`)** |
| #6 | SC3 | `workflowStep.model` and `stage.overrides.model` are read by nothing — a model pinned there is inert. Same class as the trap SC3 was written to kill. | SF0.1 | **CLOSED (SF0.1, `5217986`)** |
| #11 | SC4 | `plan.verifyEachDelivery` is read by nothing: its one reader `VerdictEngine.ShouldVerify` (`VerdictEngine.Advisor.cs:114`) is called from nowhere since M3.1 made the workflow pick the next step; the live decision is `Qa.EffectiveSkipVerification`. A plan setting it `false` still runs a verify after every delivery, silently. Fix by folding it in as lowest-precedence input **or** deleting the key and failing plan load on it. | SF0.1 | **CLOSED (SF0.1, `5217986`)** |
| #3 | SC2 | A confirmed LAST stage with a queued verify session spins the run loop forever instead of completing. The only outright hang on the list. | SF0.2 | **CLOSED (SF0.2, `fdd78ae`)** |
| #4 | SC2 | A phase-gate RED logs `queuing fix session` and the next line is `session #N start — Verify`. `RunPhaseGateAsync` writes `PendingFix` and announces a fix while the workflow engine may hand back a Verify; the attempt number agrees (SC2.2), the kind does not. Name the kind the workflow will actually select, or stop asserting one. | SF0.2 | **CLOSED (SF0.2, `fdd78ae`)** |
| #10 | SC4 | A checkpoint claimed during a Verify or Audit session is counted in **no** session's `newlyDone` — `ComputeVerdict` returns at `VerdictEngine.cs:264` before `GraphClaimsDuringSession` runs, and the next delivery session's pre-set already contains the claim. History, report, timeline and StatusAgent show it belonging to nobody; `PendingConfirmation` never gets it; the engine-side commit+evidence stamp never runs. Real trigger: the owner runs `conductor task --done` from another shell mid-verify. **Trap:** `RunLoop.Plumbing.cs:199` uses `rec.GateSummary ?? completed` and `GateSummary` is the empty **string** on a verify session — fix the evidence fallback in the same change or it stamps empty evidence over the agent's. | SF0.2 | **CLOSED (SF0.2, `fdd78ae`)** |
| #8 | SC4 | `HarnessTests.GitRun(string args)` splits on spaces and hands the pieces to `ArgumentList`, so the initial commit reaches git with quotes as data and message words as pathspecs. It fails, nothing checks the exit code, the harness repo has **zero commits**, and `Git.CommitsSince(repo, "")` short-circuits — every harness assertion about `NewCommits` is vacuously true. `SC42NoProgressTests`' params-array `GitRun` that asserts the exit code is the pattern. | SF0.2 | **CLOSED (SF0.2, `fdd78ae`)** |
| #5 | SC2 (tagged SC5) | `conductor bg status` crashes with a Win32 access-denied when a tracked pid cannot be opened. | SF0.3 | **CLOSED (SF0.3, `c84ccfc`)** |
| #9 | SC4 | `McpTaskServer.IsProcessAliveMcp` (`McpTaskServer.Handlers.cs:304`) answers `false` for a pid it cannot inspect — the exact inversion of the policy SC4.1 set in `PidLiveness.LooksAlive` (cannot-inspect means ALIVE), so MCP `bg_status` can mark a live tracked child dead. Route it through `PidLiveness.LooksAlive` as `BgStatusHandler` already does. | SF0.3 | **CLOSED (SF0.3, `c84ccfc`)** |
| #12 | SC5 | `conductor bg start` leaks the caller's stdout handle to the detached grandchild, so piping `bg start` blocks until that child exits. | SF0.3 | **CLOSED (SF0.3, `c84ccfc`)** |
| #13 | SC7 | `conductor bg logs` cannot read a **live** background log — it opens without `FileShare.ReadWrite` and fails with a sharing violation, i.e. it fails at the one case the verb exists for. | SF0.3 | **CLOSED (SF0.3, `c84ccfc`)** |

## Re-homed to SF0.4 — 2026-07-31

The 2026-07-28 triage left 12 rows open and gave four of them owners that do not exist (`next era`,
`on demand`). SF0.4 is a real stage with real sessions, so it owns the triage — every row below ends
that stage either fixed, closed with the evidence that closed it, or re-homed to a living owner.
No row is deleted.

- **FU-B2-2**, **FU-B2-3** (was `next era` — concurrency / recovery): the double-hard-crash orphan is
  still the only recovery path with no live gate on it. Test it or write down why not.
- **FU-B4-1**, **FU-F0-2**, **FU-F0-3**, **FU-F1-03**, **FU-F1-06** (was `on demand`): cosmetic or
  advisory. Expected disposition is a documented accept-by-design, not silent carry.
- **FU-F1-07** — likely **already closed** by SC8 without anyone saying so: that stage's handoff
  records *"the verb-parity test now SCANS Program.cs instead of a hand-typed list, so a new verb is
  two places, not three"*, which is exactly what this row asked for. Verify against
  `tests/Conductor.Tests/B11_2Tests.cs` and close it with the commit, or say why it does not count.
- **FU-B10-2** (was `HUMAN:` riding W5.2): deferred for want of a real model. The core run produced
  28 real sessions in `run.db` with `batteryCollapse` known per plan — the before/after
  token-per-checkpoint number is now computable. Measure it or retire the row as unanswerable.
- **FU-B11-2** (PARTIAL): running the engine on Linux is still unproven and README says so. Leave
  partial with that sentence, or close it as won't-do.
- **FU-B11-3** stays **`HUMAN:`** — real cTrader credentials and real money, outside this era. State
  it as owner-gated rather than carrying it as if a session could clear it.
- **FU-OWNER-9** moves to **SF0.3** (self-PID guard) — see the row for why it is now sharper.

## SF0.4 disposition — 2026-07-31 (the reconciliation this stage owed)

Every row above that was still open on 2026-07-31 now ends with a disposition and the evidence for
it. **No row was deleted, and none was closed by reading a doc comment** — each was checked against
the tree as it stands, and two of the premises the re-home note handed down turned out to be wrong
(see FU-B10-2 and FU-F1-07). The scoreboard, so nobody re-derives it:

| row | disposition | why |
|---|---|---|
| FU-F1-07 | **CLOSED** (`2fea703`) | SC8.3 replaced the hand-typed verb list with a `Program.cs` scan; verified in `B11_2Tests.cs`, not taken from the handoff |
| FU-OWNER-12 | **CLOSED** (SF0.1, `5217986`) | the run-start notification sentence is logged; seen live in SF0.4's proof run |
| FU-B2-2 | **CLOSED** | the invariant is documented where it lives; the behaviour was never wrong |
| FU-B4-1 · FU-F0-2 · FU-F0-3 · FU-F1-03 | **CLOSED, accept-by-design** | each re-verified in the tree, each with the reason stated on its row — declined, not deferred |
| FU-B10-2 | **RETIRED as unanswerable by observation** | run.db holds no `batteryCollapse:true` run to compare against; the baseline is recorded for a real A/B, and `docs/plan-config.md`'s unmeasured "~30-50%" claim was corrected |
| FU-F1-06 | **re-homed to SF2.1** | a stale `runs.status` would make SF2.1's own last-run card lie, so it is that stage's prerequisite |
| FU-OWNER-9 | **CLOSED**; remainder filed as bug **#16** | suggestions (1) and (3) landed in SF0.3; (2) is real and current, and now lives in the ledger this checkpoint made durable |
| FU-B2-3 | **half closed, half `HUMAN:`** | the decision is implemented (`RunLoop.Control.cs:125-131`); the live gate needs a recovery lane |
| FU-B11-2 | **PARTIAL is the answer** | `HUMAN:` — needs a Linux host; README already says so in the shipped docs |
| FU-B11-3 | **`HUMAN:`, stated** | real credentials and real money; no session can clear it |
| FU-OWNER-10 · 11 · 13 | unchanged | already owned by SF3.3 / SF4.2, stages that will open |
| the 11 carried bug rows | **CLOSED** in the ledger itself | SF0.1–SF0.3 fixed them; SF0.4 closed them with `conductor bug fix`, which it first taught to reach across runs |

What is left open in this file after this pass: **FU-B2-3** (`HUMAN:`), **FU-B11-2** (`HUMAN:`),
**FU-B11-3** (`HUMAN:`), **FU-F1-06** (SF2.1), **FU-OWNER-10** (SF3.3), **FU-OWNER-11** and
**FU-OWNER-13** (SF4.2). Three need the owner; four have a stage. Nothing is homeless.

**And the reason this file had to carry a bug ledger at all is gone.** A future era should file bugs
with `conductor bug new` and let them ride `run.db` — they now survive the run that found them, reach
the next run's prompts, and are counted at run end. Transcribing them into markdown by hand was the
workaround for a defect, not a convention worth keeping.

## SF7.1 closure ledger — 2026-08-01 (the Sarban face era's final reconciliation)

The era spec asked for one thing of this file at the end: **no row whose state is unstated**. Both
ledgers are covered here — the followup rows above, and the bugs the two Sarban runs filed. Every
line was checked against the tree or against `run.db` on 2026-08-01, and the check is pinned by
`SF7_1DocsMatchRealityTests.Ledgers` so a row added without a disposition is a red test.

**Three rows were closed by a token that named nothing.** `FU-OWNER-10`, `FU-OWNER-11` and
`FU-OWNER-13` each carried the status `CLOSED (bFU-OWNER-NN)`. That is not a commit, not a stage and
not a bug id — it entered at `ea6edc7` when a generated write mangled the cell. To a human skimming
the table they read as closed; to anyone trying to verify one, they read as nothing. All three are
genuinely closed, and now say by which stage, with which commit, and where in the tree to look.

One of the three is narrower than its commit subject claims, and is written down that way rather than
rounded up: `bc7ff3f` says every push names *plan, session, repo and build*; what ships is plan and
session. See the row.

### Followup rows — the state of every one at era end

| row | state | owner from here |
|---|---|---|
| the 43 rows closed by the 2026-07-28 triage and the SF0.4 disposition | **CLOSED**, each with its evidence on the row | — |
| FU-OWNER-10 | **CLOSED** — SF3.3, `d500f00` | — |
| FU-OWNER-11 | **CLOSED with a stated remainder** — SF4.2, `bc7ff3f`; repo and build never reached the push | reopen only if a second machine's runs land in one chat |
| FU-OWNER-13 | **CLOSED** — `f0d12bb` + `8580cca`/`017c8a9` | — |
| FU-F1-06 | **CLOSED at KS0.2** (`15627b9`) — the status-only writer exists and is called | — |
| FU-B2-3 | half implemented (`RunLoop.Control.cs:125-131`), half **`HUMAN:`** — the live gate wants a recovery lane | the owner |
| FU-B11-2 | **PARTIAL is the final answer** — `HUMAN:`, needs a Linux host; the shipped README says so | the owner |
| FU-B11-3 | **`HUMAN:`** — real cTrader credentials and real money | the owner |

**FU-F1-06 closed at KS0.2 (`15627b9`), three eras after it was filed.** The history is worth keeping
because the row survived a whole stage by being re-homed rather than read: SF0.4 sent it to SF2.1 on
the premise that a stale `runs.status` would make SF2.1's own last-run card lie, so fixing it was that
stage's prerequisite. SF2.1 made its Home card honest by a different route (the connection line plus
`RunSummary`), the premise stopped being load-bearing, and the row rode the stage untouched. What
finally closed it was not the card but the catalogue: four rows on the operator's machine claiming to
be live runs of engines that exited weeks ago, and no verb able to correct one.

The fix is the one the row always named — `IRunStore.UpdateRunStatus`, status and nothing else, no
`ended_utc`, because a run that can still be resumed has not ended
(`src/Conductor.Core/Store/SqliteRunStore.Sessions.cs`). It is called from `RunContext.Save`, which
every park already goes through, so a park invented in a later era is covered without anyone
remembering this row exists. Vocabulary lives in `RunRecord.StatusText`, and only the parks that
outlive the engine get their own word — `Idle`, `Waiting`, `Backoff` and `VerifyingGates` stay
`running` on purpose, because a row that stops saying `running` under a live engine is a row
`StateRepair` would believe it may write. The read half is KS1.3; the four existing phantoms are
closed through `conductor run close`, which KS0.2 also ships.

### Bugs — the seven this run leaves open

The core run's eleven were transcribed into this file by hand because `conductor bug` was run-scoped
and they would have vanished. **That is fixed** (SF0.4): open bugs now survive the run that filed
them, reach the next run's prompts through `BugsBattery`, and are counted at run end. So these seven
are *not* transcribed as rows — they live in `run.db` and the next run will see them. They are listed
here only so this ledger is complete, with the owner each one needs.

| bug | what | owner from here |
|---|---|---|
| #15 | a composed prompt over ~8191 chars silently stops a cmd.exe-based agent, and the run reports success | next era, engine lane — the guard exists only as a test on the shipped templates |
| #16 | the gate battery can try to rebuild a `conductor.exe` that is running, and fails with a lock error an agent misreads as a stale orphan | next era — **no stage has ever owned `tools/gates`**, which is why this survived two eras |
| #17 | the CLI silently accepts and ignores any unknown option — `conductor status --bogus` exits 0 | next era, CLI lane |
| #18 | the bottom bar hard-clips a pane's contextual help with no ellipsis | next era, face lane — SF1.3 shortened the strings, which is a workaround; 80x24 is still the documented floor |
| #19 | the session digest never records a claim: it counts MCP `task_update`, and every session claims through the CLI | next era — the digest has under-reported claims for the whole of both runs |
| #20 | `run` resolves `CONDUCTOR_PLAN` over the CWD, so a scratch rig launched inside a session can target the driving run's plan | next era, engine lane — **the sharpest of the seven**; every live-proof rig in this era had to work around it |
| #21 | nothing warns when a plan's packs push the composed prompt past the argv ceiling | next era — same root as #15; fix them together |

**Nothing here is homeless and nothing is silently dropped.** Four rows need the owner; one row and
seven bugs ride into the next era, and unlike last time the bugs ride in the database rather than in
markdown, so nobody has to remember to copy them.

## SF7.2 — the reinstall clause, re-homed to the owner — 2026-08-01

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-OWNER-14 | The installed `conductor` on this machine has not been reinstalled against the v0.3.0 release | SF7.2's spec asks for "a reinstall whose conductor version matches the releases page." The other two clauses closed this session: the merge (`8286d63` on `master`) and the tag (`v0.3.0`, published, binary self-reports `0.3.0+e897c2c7e1b0`). The reinstall did not run, on the owner's own instruction (session-39 handoff, 2026-08-01): a second conductor run is live in `C:/Code/sk-studio` (NINE STREETS, mid-session) at the time of this write-up, and `tools/install.ps1` overwrites the one published binary both runs execute — running it now would swap the ground out from under the other run's live session, not just this one's. | SF7.2 | **HUMAN** — the owner runs `tools/install.ps1` once no other conductor run is live on the machine, then confirms `conductor version --short` reads `0.3.0` (or the matching commit-height prerelease) against https://github.com/shaahink/conductor/releases/tag/v0.3.0. |

## K7.1 closure ledger — 2026-08-05 (the Karvan core era's final reconciliation)

Same contract as SF7.1's: **no row whose state is unstated, and nothing silently dropped.** Every
line below was checked against the tree or against `run.db` on 2026-08-05; the check is pinned by
`K7_1ClosureLedgerTests` so a bug listed without an owner is a red test rather than a re-read.

**This era added no rows to this file, and that is a finding rather than a tidy result.** `git log`
over `.conductor/followups.md` since 2026-08-04 is empty across twenty-four sessions. `FollowupParser`
still reads the handover's weak/deferred bullets, and `LaneCoordinator` still resolves the file at
`Path.Combine(_plan.StateDir, "followups.md")` — but nothing wrote. The era's own loose ends went to
`conductor bug` instead, which is the better channel (SF0.4 made bugs outlive the run that filed them
and reach the next run's prompts through `BugsBattery`), so nothing was lost. It does mean the
followup ledger and the bug ledger have quietly become one ledger with two file formats.

### Followup rows carried in from the Sarban eras — state at Karvan's end

| row | state at 2026-08-05 | owner from here |
|---|---|---|
| the 43 rows closed by the 2026-07-28 triage and the SF0.4 disposition | **CLOSED**, unchanged | — |
| FU-OWNER-10, FU-OWNER-11, FU-OWNER-13 | **CLOSED** by SF7.1, unchanged | — |
| FU-F1-06 | **STILL OPEN.** Re-verified today with the scan widened to the whole of `src/`: `UpdateRunStatus` exists nowhere, so a run that ends `NeedsHuman`, `Paused` or `AwaitingOwner` still reads `status='running'`. K2.1's extraction had silently narrowed the old check to `src/Conductor`, which no longer contains the store — the pin was passing on half a tree. Widened in this checkpoint. | next era, engine lane |
| FU-B2-3 | **PARTIAL**, untouched by K1–K7 (`RunLoop.Control.cs` last changed at `e45fa11` for K3.3 provenance, not for this row) — the live gate still wants a recovery lane | the owner |
| FU-B11-2 | **PARTIAL is the final answer**, unchanged — needs a Linux host | the owner |
| FU-B11-3 | **HUMAN**, unchanged — real cTrader credentials and real money | the owner |
| FU-OWNER-14 | **re-homed to K7.2**, and that is a real move rather than a restatement: SF7.2's reinstall clause was deferred because a second conductor run was live on this machine. K7.2's spec absorbs it verbatim — first install of this run, owner confirms no other run is live, then `conductor version --short` must match the releases page. | K7.2 |

### Bugs — the eleven this era leaves open

They live in `run.db`, not in this table; the table exists so the ledger is complete and so every one
of them has a name against it. Seven ride in from Sarban unchanged and four were filed by this run.

**Re-measured at K7.2, session 29 (2026-08-05).** K7.1 wrote fourteen rows here. Three of them
(#28, #29, #32) were closed by sessions 26 and 27 in the hours after, and two more (#33, #34) were
filed and closed after this ledger existed at all. All five are restated in the closed table below
rather than deleted from this one: a row whose state changes is restated, never dropped — that is
the contract at the top of this section, and it applies to good news too.

| bug | what | owner from here |
|---|---|---|
| #15 | a composed prompt over ~8191 chars silently stops a cmd.exe-based agent, and the run reports success | next era, engine lane — fix with #21, same root |
| #16 | the gate battery can try to rebuild a `conductor.exe` that is running | next era — no stage has ever owned `tools/gates`, which is why this has survived three eras |
| #17 | the CLI silently accepts and ignores any unknown option — `conductor status --bogus` exits 0 | next era, CLI lane |
| #18 | the bottom bar hard-clips a pane's contextual help with no ellipsis | next era, face lane — K6.2's viewport work did not reach the bottom bar |
| #19 | the session digest never records a claim: it counts MCP `task_update`, and every session claims through the CLI | next era — under-reporting claims for the whole of three runs now |
| #20 | `run` resolves `CONDUCTOR_PLAN` over the CWD, so a scratch rig launched inside a session can target the driving run's plan | next era, engine lane — still the sharpest of the carried-in seven; every live-proof rig in this era worked around it too |
| #21 | nothing warns when a plan's packs push the composed prompt past the argv ceiling | next era — fix with #15 |
| #23 | CI Windows gate battery flakes on `SF0_3PidsAndBackgroundWorkTests.McpBgStatus_CallsAnUninspectablePidRunning_NotDead` | next era, CI lane — a GH runner answers `Ours/Recycled` where the test expects `Unverifiable` |
| #24 | `AgentConfig.Merge` silently drops `Env`: a stage-level agent override wipes the plan-level `agent.env` | next era, engine lane — `src/Conductor/Models/AgentConfig.cs:36-48`; bites any plan that sets `OPENCODE_CONFIG` and then overrides an agent per stage |
| #27 | a brand-new `run.db` logs `FOREIGN KEY constraint failed` on the first `run_state` write | next era, store lane — cosmetic today, but it is the first line a new user sees |
| #31 | `bubbles/textarea` cannot replace `widgets.TextArea` until the face's key dispatch stops being a string | next era, face lane — named deliberately in K6.4 rather than left implicit |

### Bugs closed after this ledger was first written — sessions 26 to 28

Every row below was open (or not yet filed) when the table above was written, and `run.db` calls all
five `fixed` today. `fixed session` is the store's own `fixed_session` column, queried 2026-08-05 —
not a recollection.

| bug | fixed session | what closed it |
|---|---|---|
| #28 | 26 | `MigrationRunner` crashing on this repo's own half-migrated `run.db` (`duplicate column name: soft_break`). Re-measured at session 29: `dotnet run --project src/Conductor -- doctor` now reads the store through to `✓ state` instead of dying at `MigrationRunner.cs:85`. |
| #29 | 26 | the same half-migration seen from the upgrade side. Closed with #28 by the same guard — which also retires the reason `K7_1ClosureLedgerTests` gave for refusing to open `run.db`. |
| #32 | 27 | the seven face-showcase rows (`F0.1`, `F1.1`, `F1.2`, `F2.1`, `F3.1`, `R0.1`, `R0.2`) belonging to no stage of this plan. `TrackerGenerator` no longer emits them as table rows — a row was a re-declaration, which is what made them immortal — and lists them under **Not in the plan** instead (`TrackerGenerator.cs:109-130`, pinned by `K7OrphanBoardTests`). **It has not taken effect on this repo yet, and that is expected**: the tracker is regenerated by the *running* engine, which is the published one, so the rows and `doctor`'s `✗ work … (G13)` survive until the K7.2 reinstall. First regeneration by the new engine clears both. |
| #33 | 27 | the state-home import was one-time, so a reinstall resumed from a stale snapshot of `.conductor/run.db`. The reinstall at K7.2 is exactly the event that would have fired it. |
| #34 | 28 | `conductor budget` chose its verdict on the cap alone, so a moved `softBreakRatio` was reported as "no change needed". The verb this era leads its release notes with, contradicting the ledger it reads. |

**Nothing here is homeless.** Four followup rows need the owner, one is re-homed to K7.2, one rides
into the next era; five bugs closed between K7.1 and the ship, and the eleven still open ride into
the next era in the database rather than in markdown, so nobody has to remember to copy them.

---

## KS10.1 closure ledger — 2026-08-15 (the Karvansara era's final reconciliation)

Same contract as SF7.1's and K7.1's: **no row whose state is unstated, and nothing silently dropped.**
Every line below was checked on 2026-08-15 against the tree or against the live store, and the check
is named beside the claim rather than left as "verified".

### The finding this ledger exists to catch — and it caught one

K7.1 closed with a promise: the eleven bugs it left open *"ride into the next era in the database
rather than in markdown, so nobody has to remember to copy them."* **Four of them did not arrive.**

Measured with a read-only open of all three stores on this machine:

| store | runs it holds | bug ids | schema |
|---|---|---|---|
| `%LOCALAPPDATA%/conductor/runs/conductor-karvansara-core---the-open-door-308cfb9b/run.db` — **live** | sarban-core, sarban-face, **karvansara** | 1–23, 36–46 | v14 |
| `%LOCALAPPDATA%/conductor/runs/conductor-karvan-core-…-b4640aef/run.db` | karvan-core | 1–35 | v12 |
| `C:/code/conductor/.conductor/run.db` — pre-K3.1 leftover, last written 2026-08-07 | sarban ×2, karvan-core | 1–35 | v9 |

The live store imported the two **sarban** runs and their bugs, but not karvan-core's. So ids **24–35
are absent from the store every karvansara session read**, the sequence jumps 23 → 36 with a hole in
it, and `conductor bug list`'s "N open bug(s) carried forward from an earlier run in this repo" was
only ever as complete as the one store it opens. Four of the twelve are still open, and one of those
— **#35** — was never named in *any* ledger, K7.1's included. That is the silent drop the contract
exists to prevent, and it is filed as **#46** rather than fixed here.

### Followup rows — state at karvansara's end

| row | state at 2026-08-15 | owner from here |
|---|---|---|
| the 43 rows closed by the 2026-07-28 triage and the SF0.4 disposition | **CLOSED**, unchanged | — |
| FU-OWNER-10, FU-OWNER-11, FU-OWNER-13 | **CLOSED** by SF7.1, unchanged | — |
| FU-F1-06 | **CLOSED at KS0.2** (`15627b9`) — K7.1 re-verified it open and this era closed it; the SF7.1 pin turned over to assert the writer exists and the engine calls it | — |
| FU-B2-3 | **PARTIAL**, unchanged by KS0–KS10: the decision is implemented, the live gate still wants a recovery lane | the owner |
| FU-B11-2 | **PARTIAL is the final answer**, unchanged — running the engine on Linux needs a Linux host | the owner |
| FU-B11-3 | **OPEN, owner-gated**, unchanged — real cTrader credentials and real money | the owner |
| FU-OWNER-14 | **re-homed again, K7.2 → KS10.3.** K7.2 could not take the reinstall because a second conductor run was live; KS10.3's spec carries it verbatim and it is the owner's first install of this run | KS10.3 |

**This era wrote to this file once** — KS0.2's `ed6aab9`, closing FU-F1-06 — and filed everything else
as a bug. That is the same shape K7.1 reported, and the same conclusion holds: the followup ledger and
the bug ledger are one ledger in two file formats, and the bug half is the one with a store behind it.

### Bugs closed by this era

`fixed_session` is the store's own column. Two rows carry no session number because `bug fix` does not
record one when it is not run from inside a session — noted here rather than guessed at.

| bug | closed by | what closed it |
|---|---|---|
| #16 | KS0.3, 2026-08-13 | the gate battery could try to rebuild a running `conductor.exe`. Survived three eras because no stage had ever owned `tools/gates`; KS0.3 gave it one. |
| #20 | KS0.3, 2026-08-13 | `run` resolved `CONDUCTOR_PLAN` over the CWD, so a scratch rig launched inside a session could target the driving run's plan. The sharpest of the seven carried in — the phantom `F0`–`R0` stages in `plans/karvan/CORE-TRACKER.md` are its scar. |
| #36 | KS0.1, session 6 | the karvansara store held a truncated copy of `df9c4af8` that only an idle-moment `conductor catalogue repair --apply` could remove. Closed by that command, against a backup, on the real store. |
| #17 | karvan, 2026-08-05 | the CLI silently accepted and ignored any unknown option. Listed as open by K7.1 and already fixed when that ledger was written — restated here rather than dropped, because a row whose state changes is restated. |

### Bugs open at the close — every one, with a name against it

They live in the stores, not in this table; the table exists so the ledger is complete. **Nine are in
the live store and reach the next run's prompts. Four are in karvan's store and do not** — that is the
`#46` defect above, and until it is fixed the only thing carrying them is this row.

| bug | what | owner from here |
|---|---|---|
| #15 | a composed prompt over ~8191 chars silently stops a cmd.exe-based agent, and the run reports success | next era, engine lane — fix with #21, same root |
| #18 | the bottom bar hard-clips a pane's contextual help with no ellipsis | next era, face lane |
| #19 | the session digest never records a claim: it counts MCP `task_update`, and every session claims through the CLI | next era — under-reporting claims for four runs now |
| #21 | nothing warns when a plan's packs push the composed prompt past the argv ceiling | next era — fix with #15 |
| #23 | CI Windows gate battery flakes on `SF0_3PidsAndBackgroundWorkTests.McpBgStatus_CallsAnUninspectablePidRunning_NotDead` | next era, CI lane |
| #37 | `history --json` does not list every catalogued run: three non-terminal baton rows were invisible to it while a direct read found them | next era, store lane |
| #38 | Telegram `getUpdates` 409 conflict loop — two engines share one bot token, inbound control is dead for the live run | the owner (one token per engine) |
| #39 | an interrupted session leaves a non-terminal `running` session row at $0.00 that no verb can close | next era, store lane — `conductor run close` covers the run row, not the session row |
| #40 | Verdict counts satellite-repo commits made by anyone as the session's own work | next era, verdict lane |
| #41 | payesh's anonymity gate fails closed on the generic word "website" | KS10.2 works around it; the fix is the site's own repo |
| #42 | `catalogue repair` can never collapse a duplicate that lands in the LIVE store | next era, store lane — refusing a live store is correct, so this needs an idle-moment path |
| #43 | import bridges: a 4-digit phase/task count mints ids that pass the progress provider but fail plan validation | KS3.5's own follow-on — next era, planning lane |
| #44 | ratchet gate red before the era began: 43 analyzer suppressions against a ceiling of 38 | **the owner** — raising the ceiling is the one move a session may not make, so this is a decision, not a task |
| #45 | any verb from a newer build silently migrates the live `run.db` and locks the RUNNING engine out of its own store | **the owner, before anything else** — it happened to this session (see below) and it is the highest-severity row here |
| #46 | bugs do not survive a state-home split: karvan's #24/#27/#31/#35 never reached this era's store | next era, store lane — and it is why the four rows below are in markdown at all |
| #24 | `AgentConfig.Merge` silently drops `Env`: a stage-level agent override wipes the plan-level `agent.env` | next era, engine lane — **karvan's store only**, invisible to `bug list` here |
| #27 | a brand-new `run.db` logs `FOREIGN KEY constraint failed` on the first `run_state` write | next era, store lane — **karvan's store only** |
| #31 | `bubbles/textarea` cannot replace `widgets.TextArea` until the face's key dispatch stops being a string | next era, face lane — **karvan's store only** |
| #35 | `tools/w3/window-close.ps1` and `tools/sf1/sf1-2-live-proof.ps1` read `run.db` from the pre-K3.1 path and write scratch runs into the operator's real state home | next era, tooling lane — **karvan's store only, and named in no ledger before this one** |

### The three gaps the KS5 lane close handed forward — verified today, not restated

| gap | measured | owner from here |
|---|---|---|
| the face's `tokens cap` row quotes the plan-file ceiling | **CONFIRMED.** `face-go/internal/tui/tab_home.go:661-666` reads `m.plan.doc.Limits.MaxRunTokens` — the plan **file** — while the money row four lines above reads `b.cap`, the ceiling **in force**. So after `approve --tokens N` the two rows disagree, and the one that disagrees is the one KS5.4 raised. | next era, face lane — the wire needs to carry the effective token ceiling the way it already carries `costCap` |
| `approve` lost `CtlCommand`'s `--yes` and `--force` | **CONFIRMED live.** `ApproveCommand.Settings` (`src/Conductor/Commands/ApproveCommand.cs:24-33`) declares only `--amount` and `--tokens`; `CtlCommand.cs:15,19` still declares both flags. Probed from a scratch cwd against a nonexistent plan so nothing could be written: `approve --yes` exits 1 with `error: Unknown option 'yes'.` Since bug #17 made unknown options fatal, every script that piped `approve --yes` now fails instead of being ignored. | next era, CLI lane — decide whether `approve` needs a confirmation flag at all, then restore or document its absence |
| one owner-gate-plus-lowered-cap path spends a session before parking | **CONFIRMED as designed, with the cost named.** `RunLoop.Budget.cs:46-49` makes any other awaiting-owner reason outrank the cap check, deliberately — "that park was somebody's decision and this check must not rewrite it into a request for money". The consequence is the reported one: release the gate while the cap sits below current spend and the loop spends a session before the cap check next fires. | next era, engine lane — the precedence is right; what is missing is a check at release time |

### What this session did to the run, stated plainly

KS10.1's own acceptance required `budget` and `money` re-run **through the fresh build** against the
live store. `MigrationRunner.Run` (`src/Conductor.Core/Store/MigrationRunner.cs:21-45`) migrates on
**every** `RunDb` construction, including a read-only reporting verb, so that took the live store from
v13 to v14. The engine driving this run is the published snapshot `0.4.1-alpha.0.49+9bf2742`, whose
`CurrentVersion` is 13 — so from that moment every **new** invocation of the PATH binary refuses at
`MigrationRunner.cs:29`, and `conductor note`, `conductor task` and `conductor bug` all stopped
working mid-session. The claim went through `dotnet run --project src/Conductor -- task …` instead.

The run survived because `ConductorHost.cs:160` registers `IRunStore` as a **singleton**: the
supervisor holds the connection it opened at v13 and never re-migrates. **If that engine is restarted
before the owner installs a newer build, it cannot reopen its own store.** That is bug #45, it is
filed `high`, and KS10.3's reinstall is what clears it.

## KS12.1 closure ledger — 2026-08-19 (the Karvansara EDGE era's final reconciliation)

Same contract as SF7.1's, K7.1's and KS10.1's: **no row whose state is unstated, and nothing silently
dropped.** Every line below was checked on 2026-08-19 against the tree, against a read-only open of
both stores, or by running the gate — and the check is named beside the claim rather than left as
"verified". Mechanical proof of completeness: `.conductor/evidence/KS12/ks12-1-closure-ledger.md`.

### Where the bugs live — two stores, and the split KS10.1 found is unchanged

| store | bug ids | open | schema |
|---|---|---|---|
| `%LOCALAPPDATA%/conductor/runs/conductor-karvansara-core---the-open-door-308cfb9b/run.db` — **live**, holds sarban ×2, karvansara-core and karvansara-edge | 1–23, 36–60 (48 rows) | **27** | v14 |
| `%LOCALAPPDATA%/conductor/runs/conductor-karvan-core-…-b4640aef/run.db` | 24–35 (12 rows) | **4** — #24, #27, #31, #35 | v12 |

**Bug #46 is unchanged and still open**: karvan's four open rows are in a store no karvansara session
opens, so `conductor bug list` in this repo has never seen them and no edge prompt carried them. The
only thing carrying them forward is this table, exactly as KS10.1 said. The pre-K3.1 leftover at
`C:/code/conductor/.conductor/run.db` still exists on disk and is still written by nothing.

### Bugs closed by the edge era — two, and one of them is not what it looks like

| bug | closed | what closed it |
|---|---|---|
| **#44** — *ratchet gate red before the era began: 43 analyzer suppressions against a ceiling of 38* | **FIXED 2026-08-19 02:04 by KS6.2** (`0cb514d`, "the analyzer-debt ratchet, and 14 pragmas that were guarding nothing") | KS10.1 handed this to **the owner** on the grounds that raising the ceiling is the one move a session may not make. KS6.2 did not raise it: it retired fourteen suppressions that were guarding nothing, which took the count under the bar and then **tightened the bar to 31**. The right resolution of an "owner decides" row is to make the decision unnecessary. |
| **#50** — *ND-5's premise is false: an allowlist profile cannot replace `--dangerously-skip-permissions`* | **CLOSED 2026-08-18 21:34, session 6** — *as superseded, not as fixed* | KS7.1 re-ran the probe properly and found #50's premise was an artefact of a probe that only ever ran read-only Bash. It was replaced by **#51**, which is the real finding and is open. Recorded here because "fixed" in the store means "this row is done", and a reader deserves to know which kind of done. |

### Bugs open at the close — all 31, every one with a name against it

**The 27 in the live store reach the next run's prompts. The 4 in karvan's store do not.**

*Carried in from earlier eras, re-checked today and unchanged by edge:*

| bug | what | owner from here |
|---|---|---|
| #15 | a composed prompt over ~8191 chars silently stops a cmd.exe-based agent, and the run reports success | next era, engine lane — fix with #21 and #55, one root |
| #18 | the bottom bar hard-clips a pane's contextual help with no ellipsis | next era, face lane |
| #19 | the session digest never records a claim: it counts MCP `task_update`, every session claims through the CLI | next era — and **#52 is its other half**: the digest also counts claims that *failed* |
| #21 | nothing warns when a plan's packs push the composed prompt past the argv ceiling | next era — fix with #15 and #55 |
| #23 | CI Windows gate battery flakes on `SF0_3PidsAndBackgroundWorkTests` | next era, CI lane — **#49 is the same class**, a different test |
| #37 | `history --json` does not list every catalogued run | next era, store lane |
| #38 | Telegram `getUpdates` 409 conflict loop — two engines share one bot token | **the owner** (one token per engine). KS11 built the whole courier around this constraint and did not remove it |
| #39 | an interrupted session leaves a non-terminal `running` session row at $0.00 that no verb can close | next era, store lane |
| #40 | Verdict counts satellite-repo commits made by anyone as the session's own work | next era, verdict lane — **live for KS12.2**, whose payesh PR is a satellite commit |
| #41 | payesh's anonymity gate fails closed on the generic word "website" | **KS12.2 hits this**; the fix is the site's own repo |
| #42 | `catalogue repair` can never collapse a duplicate that lands in the LIVE store | next era, store lane |
| #43 | import bridges: a 4-digit phase/task count mints ids that pass the progress provider but fail plan validation | next era, planning lane |
| #45 | **any verb from a newer build silently migrates the live `run.db` and locks the RUNNING engine out of its own store** | **the owner, at KS12.3.** It is still live and it bit this session too — see the section below |
| #46 | bugs do not survive a state-home split | next era, store lane — and it is why the four rows below are in markdown at all |
| #47 | payesh anonymity: a private repo whose whole name is an ordinary noun makes the check unfalsifiable | **KS12.2 hits this**, same lane as #41 |
| #48 | `conductor face` with no live run in this directory silently attaches to another repo's run | next era, face lane |
| #24 | `AgentConfig.Merge` silently drops `Env` | next era, engine lane — **karvan's store only** |
| #27 | a brand-new `run.db` logs `FOREIGN KEY constraint failed` on the first `run_state` write | next era, store lane — **karvan's store only** |
| #31 | `bubbles/textarea` cannot replace `widgets.TextArea` until key dispatch stops being a string | next era, face lane — **karvan's store only** |
| #35 | `tools/w3/window-close.ps1` and `tools/sf1/sf1-2-live-proof.ps1` read `run.db` from the pre-K3.1 path and write scratch runs into the operator's real state home | next era, tooling lane — **karvan's store only** |

*Filed by the edge era itself — eleven, and they are the shape of what this era touched:*

| bug | what | owner from here |
|---|---|---|
| #49 | `KS1_2StagesFromFoldTests.DerivedStatusMatchesTheStatusSurface_ForEverySeededRun` flakes under full-suite parallel load (1 of 2763 at `cb84b1e`; 5/5 green run alone) | next era, test-infra lane — with #23 |
| #51 | **high** — a restricted permission posture silently breaks the run's own claim path unless the allow list names it. Supersedes #50 | **the owner**, before any unattended run adopts a restricted profile. KS7.1 shipped this run's allow-list entry (`efe1e69`); the hazard is general |
| #52 | digest `Claims` counts a claim attempt that FAILED — KS7.2's per-call outcome data now makes filtering possible | next era, telemetry lane — with #19 |
| #53 | `cache_creation` TTL split (5m vs 1h) is dropped; a rate-based cost model would misprice the write half | next era, accounting lane. **Not urgent**: conductor takes `total_cost_usd` from the CLI and models no rates |
| #54 | MSBuild node reuse serves a stale analyzer config: `Conductor.Planning` fails with MA00xx errors that contradict `.editorconfig` | next era, build lane — with #57 and #59, all one root |
| #55 | `doctor`'s argv lint under-measures the real spawn by 350–500 chars | next era, engine lane — with #15 and #21 |
| #56 | `ControlPlaneServer` coupling 240 is the largest single tightening available (CA1506 240 → 134 in one split) | next era, quality lane — the named next move after KS6.3 |
| #57 | `dotnet build` flaps red on reused MSBuild nodes; `-nr:false` fixes it | next era, build lane — with #54, #59 |
| #58 | `FailureCircuitBreaker.ParseFailingGates` matches glyphs the summary never emits, so the same-failure comparison degrades to comparing empty sets | next era, engine lane. **Worth prioritising**: a silently inert circuit breaker is a rail that reports armed |
| #59 | `dotnet run --project src/Conductor` inside a bg child fails with MA00xx that `dotnet build` never produces | next era, build lane — with #54, #57 |
| #60 | **the analyzer-debt bar is red on this branch** — see the next section, which measures it rather than restating it | **stated, not fixed, and deliberately so** |
| #61 | `CONDUCTOR_RUN_DB` does not redirect the measuring verbs — `budget` resolves by repo path first and answers *"no runs to measure"*, so the documented highest-precedence override does not hold. **Filed by this checkpoint**, because working around it is what made a safe re-measure possible at all | next era, CLI/store lane — or correct `StateHome.cs:27-29`'s doc comment, which currently promises something the verbs do not honour |

### The one gate this era leaves red, measured today rather than described

Run at 2026-08-19 on `feat/karvansara-edge`. Neither script is a gate in `edge.plan.json` — the plan's
battery is build/test for engine and face — so this is a **repo bar the PR template names**, not a
failing run. Both are reproduced verbatim in the evidence file.

    analyzer-debt: pragma-src           bar=31   now=33   unjustified=0
    analyzer-debt: severity-downgrade   bar=15   now=17   unjustified=0  (count not ratcheted)
    analyzer-debt: TOTAL                bar=46   now=50   unjustified=0
    analyzer-debt: rules-enforced       bar=50   now=50   un-enforced=0
    ratchet.ps1:   ANALYZER SUPPRESSIONS ABOVE CEILING (33 > 31)

The two suppressions above the bar are **both `MA0045`**, both added by KS4.4 (`05696d4`), and both
carry a written justification — `unjustified=0` on every kind:

    #pragma warning disable MA0045 // one small sidecar written at attempt creation; async buys
                                   // nothing and the caller is sync
    #pragma warning disable MA0045 // teardown is a synchronous finally-block concern; the sleep is
                                   // injectable and bounded

`complexity-budget.ps1` is **green** — all three rules enforced, every project budgeted, nothing
loosened. The bar of 31 was set by `9707af7a1` and by the tool's own rule no single commit moves it.
**The only legitimate close is to make the two helpers genuinely async**, which is KS4's code and not
this checkpoint's; raising the bar is the one move a session may not make. Whoever takes it starts
with the two lines above. Note that the era shipped this way **on purpose**: bug #44's predecessor
sat at 43 against 38, and this branch is at 33 against 31 — the debt fell by ten while the bar
tightened by seven.

### The three KS5 gaps KS10.1 handed forward — re-measured today, not restated

| gap | measured 2026-08-19 | owner from here |
|---|---|---|
| the Face's `tokens cap` row quotes the plan **file** rather than the run's effective ceiling | **STILL OPEN, unchanged by edge.** `face-go/internal/tui/tab_home.go:662-664` still reads `m.plan.doc.Limits.MaxRunTokens`; the cost row at `:609` reads `MaxRunCostUsd` from the same document | next era, face lane |
| `approve` lost `CtlCommand`'s `--yes` and `--force` | **STILL OPEN, unchanged by edge.** `ApproveCommand.Settings` declares exactly two options, `--amount <USD>` and `--tokens <N>` | next era, CLI lane |
| one owner-gate-plus-lowered-cap path spends a session before parking | **STILL AS DESIGNED, and the design is written down.** `RunLoop.Budget.cs:49` — `if (_ctx.State.Status == RunStatus.AwaitingOwner) return true;` with the comment above it stating that any other awaiting-owner reason outranks the cap deliberately, so the check cannot rewrite somebody's decision into a request for money | closed as a decision, not a defect |

### Followup rows — state at the edge era's end

| row | state at 2026-08-19 | owner from here |
|---|---|---|
| the 43 rows closed by the 2026-07-28 triage and the SF0.4 disposition | **CLOSED**, unchanged | — |
| FU-OWNER-10, FU-OWNER-11, FU-OWNER-13 | **CLOSED** by SF7.1, unchanged | — |
| FU-F1-06 | **CLOSED at KS0.2** (`15627b9`), unchanged | — |
| FU-B2-3 | **PARTIAL**, unchanged by KS4–KS11: the decision is implemented, the live gate still wants a recovery lane | the owner |
| FU-B11-2 | **PARTIAL is the final answer**, unchanged — running the engine on Linux needs a Linux host | the owner |
| FU-B11-3 | **OPEN, owner-gated**, unchanged — real cTrader credentials and real money | the owner |
| FU-OWNER-14 (the reinstall) | **re-homed once more, KS10.3 → KS12.3.** KS10.3 performed it for v0.4.1; the edge era's engine is not installed, so the clause lives again and KS12.3 carries it | KS12.3 |

**The edge era wrote to this file exactly once — this ledger.** Everything else was filed as a bug,
which is the third era running that the same conclusion holds: the followup ledger and the bug ledger
are one ledger in two file formats, and the bug half is the one with a store behind it.

### What this session did NOT do to the run, and why that is the point

KS10.1's own closing section records that it broke the driving engine: running `budget --json` through
the fresh build migrated the live store v13 → v14, the PATH engine's `CurrentVersion` was 13, and
`conductor note`, `task` and `bug` all died mid-session. That is bug **#45** and it is still open.

**It would have happened again today.** `MigrationRunner.CurrentVersion` is **15** on this branch
(`src/Conductor.Core/Store/MigrationRunner.cs:11`, raised by KS4's `8d649ea`) and **14** on master,
which is what the installed `0.4.1` engine driving this run supports. KS12.1's acceptance requires the
same two verbs through the same fresh build.

The workaround, and it costs nothing: measure against a **`sqlite3.backup` copy** of the store and
pass it as `budget`'s positional db argument. The live file is never opened for write, the copy is
migrated to v15 and thrown away, and `conductor task`/`note`/`bug` keep working for the rest of the
session. One trap for whoever repeats it: **`CONDUCTOR_RUN_DB` does not redirect `budget`** — that
verb resolves the run by repo path first and answers *"no runs to measure for C:\Code\conductor"*.
The positional path is the only seam that works.

## DV7.1 closure ledger — 2026-08-26 (the Divan era's final reconciliation)

Same contract as SF7.1's, K7.1's, KS10.1's and KS12.1's: **no row whose state is unstated, and nothing
silently dropped.** Every number below was measured today against a `sqlite3 .backup` copy of the live
store or against the tree, and the check is named beside the claim. Mechanical proof:
`.conductor/evidence/DV7/dv7-1-closure-ledger.md`.

**This section deliberately contains no `| FU-…` table rows.** Bug **#81** measured the cost of the
convention every prior closure ledger followed: 91 rows for 55 distinct ids, so the ledger mirror
reports phantom updates and, unguarded, oscillates a project card between two columns forever.
Restating 55 rows here would have added a seventh copy of the worst offenders. The state of every id
is below in prose, which the mirror's row regex does not index.

### The store, and the split KS10.1 found — closed by this era

KS12.1 recorded two stores and karvan's four rows (#24, #27, #31, #35) stranded in one no session
opens. **DV2.1 recovered all four into the live store**: karvan #24 → **#70**, #27 → **#71** (fixed
during this era), #31 → **#72**, #35 → **#73**, each carrying a `RECOVERED … where it is karvan bug
#NN` back-reference in its detail. The live store now holds ids 1–23 and 36–82 (70 rows; ids 24–35 are
permanently absent by design — they were re-minted, not moved). Bug **#46** stays open as the *class*
of defect; its four instances no longer carry forward in a table.

Schema is **v15** on both sides today — `MigrationRunner.CurrentVersion` in this tree and in the
installed `0.4.2-alpha.0.79+870786f5` engine driving this run — so the trap-18 hazard did not arm.

### Bugs — 70 rows, 37 fixed, 33 open

**Closed by the Divan era: sixteen.** Twelve filed and fixed here — #62, #63 (DV2.2, batteries), #64,
#65, #66 (DV2.3, telegram), #67, #68, #69 (DV2.4, the three high-severity engine defects), #71 (the
recovered karvan #27), #74 (DV3), #77, #78 (DV4) — plus **four carried in from earlier eras and closed
here**: #15 and #21 (Sarban face, prompt length), #38 (Karvansara core, the getUpdates 409 conflict
loop — closed by the courier owning the token, which is the whole point of DV4), and #55 (Karvansara
edge, doctor's argv lint).

**Open at the close: 33, and every one has a name against it.**

- **Filed by this era and still open — nine, all with the next era or the owner:** **#75** (high —
  `conductor note` stores only the first line; three DV3 acceptance records survive as their header
  alone, and it cost this session three separate notes to work around), **#76** (the courier does not
  upload files), **#79** (high — `github sync --backfill` duplicates the whole board inside the API's
  replica lag), **#80** (Projects v2 built but unproven live; the owner's one-command unblock is
  `gh auth refresh -s project`), **#81** (this file's duplicate rows), **#82** (the SARIF upload has
  never had a 202 — every repo a proof may touch is private and this account has no Advanced Security;
  the public leg is one command at DV7.3), and the three DV2.1 recovered from karvan: **#70**, **#72**,
  **#73**.
- **Carried in and still open — 24**, unchanged in disposition from KS12.1's ledger: #18, #19, #23
  (Sarban face); #37, #39–#43, #45–#49, #51–#54, #56, #57–#61 (Karvansara core and edge). Of these,
  **#45** (a newer build silently migrates the live store and locks the running engine out) and **#61**
  (`CONDUCTOR_RUN_DB` does not redirect the measuring verbs) are the two every future session pays
  for, and both were routed around rather than fixed today — see the last section.
### Reconciled against the DV2 triage, row by row — Ledger 1 is exact

`docs/dev/DIVAN-BUG-SWEEP-2026-08-25.md:23` enumerates the open set as
**15, 18, 19, 21, 23, 37–43, 45–49, 51–61** — 28 ids. Recomputed from the store today, the set that
was open on 2026-08-25 is the 24 still open (#18, #19, #23, #37, #39–#43, #45–#49, #51–#54, #56,
#57–#61) plus the four this era closed (#15, #21, #38, #55): **28, and the same 28.** No id appears in
one ledger and not the other, in either direction. The four ids that look missing — #24, #27, #31,
#35 — are the karvan rows the sweep itself flagged as living only in the imported copy, and DV2.1
re-minted them as #70–#73.

One caution for whoever greps this next: the sweep writes its ranges without a `#` on each member, so
a naive `#\d+` scan of that document returns 27 ids and reports **#56** (`ControlPlaneServer` coupling
240, KS6) as unnamed. It is named. The ranges are the enumeration.

### The finding this ledger exists to catch — and it caught one

**The DV2 sweep's Ledger 2 re-opened a row KS0.2 had closed.** It lists **FU-F1-06** as "the oldest
standing row a session can actually clear", re-verified with the scan widened to all of `src/` — but
`.conductor/followups.md:527` records it **CLOSED at KS0.2 (`15627b9`)** six days earlier, and the
tree agrees: `UpdateRunStatus` is declared at `Store/IRunStore.cs:38`, implemented at
`Store/SqliteRunStore.Sessions.cs:74`, called from `Orchestration/RunContext.cs:391`, and pinned by
`KS0_2RunRecordTests` and `KS0_2NoRunsUpdateOutsideTheStoreTests`. **The followup ledger was right and
the sweep was wrong.** Corrected in place. Nothing was lost — no session acted on it.

### Followup rows — the state of all 55 ids, in prose

**Fifty-one are closed or retired.** Taking each id's *last* row as its verdict: 48 read CLOSED, and
three resolve on reading — `FU-B3-2` CLOSED at W3.3 twice over, `FU-OWNER-11` CLOSED with a stated
remainder, `FU-B10-2` RETIRED as unanswerable by observation.

**Four carry forward, and all four are the owner's:**

- **FU-B11-3** — the real-credential cTrader path. Owner-gated since SF0.4, states so in its own row,
  and that row says no future triage should re-home it. This ledger does not.
- **FU-B2-3** — PARTIAL: the decision is implemented, the live gate still wants a recovery lane.
- **FU-B11-2** — PARTIAL is the final answer: running the engine on Linux needs a Linux host.
- **FU-OWNER-14** — the reinstall clause, re-homed KS10.3 → KS12.3 and still unperformed. **DV7.3
  inherits it**: this branch stacks on `feat/karvansara-edge`, so one reinstall closes both — and DV4.2
  added a clause to it, because a running courier holds the published exe open, so `tools/install.ps1`
  now stops it at step 0 and restarts it on the new engine.

**The Divan era wrote to this file exactly zero times before this ledger** —
`git log feat/karvansara-edge..HEAD -- .conductor/followups.md` is empty. Every defect went to the bug
ledger instead. That is the fourth era running that the same conclusion holds, and Divan is the first
to act on it: `FollowupWriter` exists in Core now, but the promotion path it serves has never been
used in this repo.

**One number in the sweep is not reproducible, and the file is byte-unchanged since it was written.**
Ledger 2 opens "262 rows, 11 OPEN as of today". 262 is exact — that is every table row in this file,
of which 91 are followup rows for 55 distinct ids. **11 is not**: today the file yields 7 rows with a
standalone `OPEN`, 10 lines with an uppercase `OPEN` anywhere, 4 rows with `**OPEN`, and 23 rows
case-insensitively. Since no commit touched this file in the whole era, the discrepancy is in the
counting method, not in the file — which is exactly bug #81's point about counting rows in a ledger
that restates them.

### What this session did to the run, stated plainly

`conductor budget` was re-measured through the fresh build. KS12.1's ledger prescribes the
positional-db workaround for trap 18 and warns that `CONDUCTOR_RUN_DB` does not redirect the verb
(bug #61). **A cleaner seam exists and was used today:** `budget --home <dir> --repo <repo>` reads an
entire alternate state home, so a `sqlite3 .backup` copy plus a `catalogue.json` with one `runDb` path
rewritten measures the live run without opening the live file at all. Bug #61 stays open — the
environment variable still does not work — but no future session needs to fight it. The figures are in
`docs/dev/TOKEN-BUDGET-TUNING.md` §13 and the raw output in `.conductor/evidence/DV7/`.

## Charkh closure ledger — 2026-08-27 (CH5.1)

**How this was derived, so it can be re-derived.** Every bug row below comes from `run.db` — the
consolidated store under `%LOCALAPPDATA%/conductor/runs/`, read through the MCP `run_query` surface,
not from any document. Every followup row comes from `.conductor/followups.md` itself, resolved by
**the last row each id carries** rather than the first: this file is append-per-triage-pass, so 91
rows describe 57 distinct ids and only the newest row for an id is that id's current verdict. That
duplication is bug **#81** and it is still open; reading the file by last-row is the workaround, not
the fix.

### Bugs — the nine this era filed, and the forty it leaves open

Charkh filed nine (#84–#92) and fixed three of them. Bugs outlive the run that filed them (SF0.4), so
this table is a **completeness record, not the source** — `conductor bug list` is. A bug listed with
an empty owner is the homeless row this ledger exists to prevent.

| # | what it is | owner |
|---|---|---|
| #84 | the repo-wide retire sweep that closed another run's checkpoints on every era transition | **FIXED** at CH4.3 (`f4022f6`) — scoped to `GithubIdentity.OwnerMarker` and this run's `GithubMap`; the residue it already caused is #90 |
| #85 | `SqliteRunStore.FlushEvents` could return before the drain loop's in-flight batch committed | **FIXED** at CH1.3 (`349a3a5`) — with a per-instance perturbation seam and a negative control |
| #89 | `DV4_3CourierSeamTests` secret mutation was a 1-in-16 flake | **FIXED** at CH4 — appending `0` is not a mutation when the secret already ends in `0` |
| #86 | a gate that passes on retry discards the failed attempt output, so a recurring flake is undiagnosable | the gates lane, next era. Observed twice one era apart on `engine-full`; the fix is to spill the failed attempt, not to retry less |
| #87 | `courier status` says "ready — `conductor courier run` starts polling" after printing "running: yes pid N" | the courier lane, next era. One verdict line, one branch |
| #88 | `CHANGELOG [Unreleased]` still says "Nothing yet" after CH1–CH4 shipped | **CH5.2, and it is the first act.** `release perform`'s changelog act *refuses over a placeholder body*, so this bug blocks the tag by design — the era cannot close until the notes exist |
| #90 | the residue on `shaahink/conductor`: 23 Divan and every Karvansara checkpoint issue wear `conductor:retired` | **CH5.2, after the reinstall.** The fixed engine repairs it; the installed `0.5.0` would re-cause it |
| #91 | the corpus act never hands over the backfill for a PARKED run — `RunLiveness.IsStillGoing` counts `needs_human` as in flight | **CH5.2 / the owner.** It is why the karvansara-edge run reads as "not something owed yet" and its record is still unwritten |
| #92 | a `TimedOut` session records **no cost rows at all** — session 6 ran 5h36m over 140 turns and is invisible to `conductor budget` | the budget lane, next era. It caveats section 14 of `TOKEN-BUDGET-TUNING.md`, which is computed from 6 of this era's 7 sessions |
| #18 | the bottom bar hard-clips a pane's contextual help with no ellipsis | the face lane, next era |
| #19 | SC7.2 session digest never records a claim | the digest lane, next era — the same subject as #52 |
| #23 | CI windows gate battery flakes on `SF0_3PidsAndBackgroundWorkTests` | the CI lane. **Re-measure with `github ci` before re-filing**: CH1.3 shipped the verb that reads each active workflow's own latest run, which is what this bug lacked |
| #37 | `history --json` does not list every catalogued run — three non-terminal runs are dropped | the history lane, next era |
| #39 | an interrupted session leaves a non-terminal `running` session row | the engine lane, next era — session 6 of this era is a live specimen, and #92 is its cost-side twin |
| #40 | verdict counts satellite-repo commits made by anyone as the session's own | the verdict lane, next era |
| #41 | payesh anonymity fails closed on the generic word "website" | **the payesh repo** (`shaahink/payesh`), not this one. No stage here touches it |
| #42 | catalogue repair can never collapse a duplicate that lands in the LIVE catalogue | the history lane, next era |
| #43 | import bridges: a 4-digit phase/task count mints ids that pass the probe and collide | the import lane, next era |
| #45 | any verb from a newer build silently migrates the live `run.db` and locks the installed engine out | the engine lane — **and it already has a partial answer**: `release preflight`'s `migration` probe names the skew before a reinstall, which is what turned this from a surprise into a checked precondition |
| #46 | bugs do not survive a state-home split — karvan's #24/#27/#31/#35 reach no prompt | the store lane, next era; the four orphans are pinned by `TheKarvansaraLedgerAccountsForTheBugsThatLiveInAnotherStore` |
| #47 | payesh anonymity: a private repo whose whole name is an ordinary noun | **the payesh repo**, not this one |
| #48 | `conductor face` with no live run in this directory silently attaches to another run | the face lane, next era |
| #49 | `KS1_2StagesFromFoldTests` flake | the test lane, next era |
| #51 | a restricted permission posture silently breaks the run's own claim channel | the engine lane, next era — the highest-severity carry-forward this ledger holds |
| #52 | digest `Claims` counts a claim attempt that FAILED | the digest lane, next era — pair with #19 |
| #53 | `cache_creation` TTL split (5m vs 1h) is dropped, so a rate-based cost model is wrong | the cost lane, next era. With #92 it is the second reason section 14's dollars are a floor, not a total |
| #54 | MSBuild node reuse serves a stale analyzer config | the build lane, next era — same subject as #57 and #59 |
| #56 | `ControlPlaneServer` coupling 240 is the largest single tightening available | the architecture lane, next era |
| #57 | `dotnet build` flaps red on reused MSBuild nodes — case-sensitive `.editorconfig` | the build lane, next era |
| #58 | `FailureCircuitBreaker.ParseFailingGates` matches glyphs the summary never emits | the gates lane, next era — pair with #86 |
| #59 | `dotnet run --project src/Conductor` inside a bg child fails with MA0016 | the build lane, next era |
| #60 | "analyzer-debt ratchet is RED: pragma-src 33 against a bar of 31" | **measured green at CH5.1 and left open on purpose.** `tools/gates/ratchet-baseline.json` reads `maxPragmas: 31` and the live count under `src/` is **31** — at the ceiling, not above it. The row stays until a session closes it with the commit that took 33 to 31, because closing it from a measurement alone names no fix |
| #61 | `CONDUCTOR_RUN_DB` does not redirect the measuring verbs | the measuring-verbs lane, next era. **CH5.1 depended on the workaround**: `conductor budget <path-to-db>` positionally does what the env var is documented to do |
| #70 | `AgentConfig.Merge` silently drops `Env` | the config lane, next era |
| #72 | face: `bubbles/textarea` cannot replace `widgets.TextArea` until key dispatch moves | the face lane, next era |
| #73 | `tools/w3/window-close.ps1` and `tools/sf1/sf1-2-live-proof.ps1` read `run.db` directly | the tooling lane, next era |
| #75 | `conductor note` stores only the FIRST LINE of a multi-line note | the engine lane, next era. Every note in this era's ledger is written as one long line *because of* this bug — it shapes the record it is filed in |
| #76 | the courier does not upload files, so a push with an evidence artifact arrives without it | the courier lane, next era |
| #79 | `github sync --backfill` duplicates the whole board when run twice inside the API's replica lag | **CH5.2 / the owner** — it is the hazard sitting on top of #91's owed backfill, and it is a DIFFERENT bug from #84 |
| #80 | the Projects v2 board is built but unproven live: the machine token carries no `project` scope | **the owner** — `gh auth refresh -s project` is the one-command unblock |
| #81 | `.conductor/followups.md` carries 91 rows for 57 distinct ids | **this file's next triage pass.** CH5.1 read it by last-row rather than repairing it, and says so above |
| #82 | the SARIF upload has never had a 202: every repo a proof may touch is private and the account has no Advanced Security | **the owner** — it needs a public repo or GHAS, not a code change |
| #83 | payesh anonymity: a run's PLAN TITLE is matched as wording | **the payesh repo**, not this one |

Three payesh rows (#41, #47, #83) name a repository this plan does not touch: `C:/code/conductor-site`
is `shaahink/payesh`, its `main` auto-deploys to the world, and its era-close landed before Charkh
launched. They are listed so they are not lost, and owned there.

### Followups — the census by last row, and the one that stopped being re-homed

Fifty-seven distinct ids, of which **fifty-three read closed or retired** on their newest row. Four
were living when this era opened. Nothing in this file was touched between DV7.3 and CH5.1.

| id | disposition | owner |
|---|---|---|
| FU-B2-3 | **PARTIAL**, unchanged by CH1–CH5: the decision is implemented (`RunLoop.Control.cs:125-131`), the live gate still wants a recovery lane | the owner |
| FU-B11-2 | **PARTIAL is the final answer**, unchanged — running the engine on Linux needs a Linux host | the owner |
| FU-B11-3 | **OPEN, owner-gated**, unchanged — real cTrader credentials and real money | the owner |
| FU-OWNER-14 (the reinstall) | **CLOSED as a ledger row at CH5.1 (`c0dcad5`), with a stated remainder — and re-homed to the machinery rather than to another stage.** The row was moved four times (SF7.2, then K7.2, then KS10.3, then KS12.3) and its owner is now a closed stage, which is precisely the failure this file's 2026-07-28 triage described: a row pointing at a stage that will never open again is a row nobody will ever clear. CH4.2 made `reinstall` one of five named owner acts in `ReleasePerform.OwnerOrder` (`:40`), printed with its command on **every** `release perform` and rendered by `release runbook`, so the engine now asks each era instead of a document remembering to. **The remainder:** the reinstall itself is owed at CH5.2 and is the owner's act, exactly as before | the owner, prompted by the verb |

**What this row is the proof of.** Charkh's thesis is that what the owner still does by hand becomes
machinery. FU-OWNER-14 is the ledger's own instance of it: the answer was not to re-home the row a
fifth time, it was to make the act unforgettable by a machine and let the row close.
