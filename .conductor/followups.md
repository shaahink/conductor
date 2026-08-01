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
| FU-F1-06 | run.db runs.status not updated on non-completion non-terminal states | NeedsHuman, Paused, AwaitingOwner, VerifyingGates, Backoff leave runs.status='running' in run.db. RecordRunEnd sets ended_utc which isn't warranted for resumable states. Add an UpdateRunStatus method (status-only, no ended_utc) and call from NeedsHuman + other state transitions. Low severity: state.json is authoritative; run.db is additive/best-effort; InitializeRun (INSERT OR REPLACE) fixes on resume. | **SF2.1** (re-homed by SF0.4) | **OPEN — re-homed to a stage that will open.** Verified: no `UpdateRunStatus` exists anywhere in `src/`, so the row stands exactly as written. It is no longer "low severity, run.db is best-effort": M2 moved run state INTO run.db, and **SF2.1 puts a last-run summary card on Home, read from run.db** — a run that ended `NeedsHuman` still says `status='running'` in the `runs` row, so that card would report a finished run as live. The fix (a status-only `UpdateRunStatus`, no `ended_utc`) is a prerequisite of SF2.1's own acceptance, which is why it goes there rather than to a fix-lane. |
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
|FU-OWNER-10|Nothing on the wire says which build you are attached to|`GET /state` carries plan, run id, repo, model, cost — and no engine version, commit or face build. The face therefore cannot show it either, so "did my reinstall take?" is unanswerable from inside the tool. Proving this run was on the new engine took `Get-CimInstance Win32_Process` for the image path, the file's mtime against the run's start, `conductor version`, and `go version -m` on the face binary — four out-of-band checks for a fact the engine already holds (`version` prints it: commit, built, runtime, os, binary). **Suggested fix:** add `engineVersion` / `engineCommit` / `faceBuild` to the `/state` payload and put the short form in the face's status strip; SF3.3 already opens that payload and that strip for branch/dirty/ahead-behind/HEAD sha, so this is the same edit, not a new one.|SF3.3| CLOSED (bFU-OWNER-10) |
|FU-OWNER-11|Telegram pushes carry no identity — repo, plan, run id or build|A run notification reads `s2 NoProgress — P0` + gates + result + cost, with nothing naming the repo, the plan or the run. One chat receiving two machines' runs cannot attribute a line, and a message read hours later cannot be dated to a build. The corollary bit today: a hand-sent operator message ("Sarban core complete… New engine `0.1.1-alpha.0.57+2fea7032749d` installed") is indistinguishable in the chat from an engine push, and quoted a version the engine had already superseded — the engine's own pushes would have been right by construction. **Suggested fix:** prefix each push with `<planName> · s<N>` and carry repo + engine version in the run-start/run-end message; if FU-OWNER-10 lands, take the version from the same field.|SF4.2 (owns the push path)| CLOSED (bFU-OWNER-11) |
|FU-OWNER-13|Between a saved plan edit and the next session boundary, Telegram status contradicts the plan on disk (owner: **SF4.2**)|Wiring Telegram into the live NINE STREETS run: `POST /plan/edit` returned `ok:true, planVersion:3` and the block was on disk, then `POST /telegram/token` answered *"saved, but this run still will not deliver: not configured — **add a telegram block to the plan**"* — advising the edit that had just been made and accepted seconds earlier. `GET /telegram/status` says the same, because both read the live in-memory `PlanConfig`, which by design is not mutated on the HTTP path (`ControlPlaneServer.Plan.cs:11`); the reload is queued (`control: ReloadPlan (during session)`) and applied at the next boundary. The behaviour is right; the sentence is not — it names a cause that no longer exists and gives an instruction that would be a no-op. **Suggested fix:** when a reload is pending, both replies should say so instead — "a plan reload is queued; Telegram starts at the next session boundary" — and `/telegram/status` should carry a `reloadPending` bool so the Face's Telegram tab can show *waiting*, not *unconfigured*. This is the same failure SC1.3 was written to kill (a saved thing reporting as if nothing were saved), one layer out.|SF4.2 / SC1 fix-lane| CLOSED (bFU-OWNER-13) |
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
