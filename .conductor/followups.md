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
| FU-B2-2 | `RunStateProjection.FindInterruptedSession` assumes single-session | Tracks one "most recent unmatched start"; cannot represent two concurrently-interrupted sessions. Matches today's one-session-at-a-time model but is an undocumented invariant parallel-lane stages must revisit. | next era (concurrency) | OPEN — still exactly as described; the model is still one session at a time, so nothing has forced the issue. |
| FU-B2-3 | Orphaned-`SessionStarted` recovery may queue a non-resume | Event-log recovery of a `SessionStarted` with no matching `state.json` record synthesises a `SessionRecord` and queues a resume with the event's `AgentSessionId` � possibly empty ? starts a FRESH agent, not a true resume (safe-ish re-deliver, but silent). Double-hard-crash-only path; untested against an empty-id orphaned stream. Add a test + decide skip vs re-deliver vs needs-human. | next era (recovery) | OPEN — untouched, and now the *only* recovery path with no live gate on it: W3.3 proved the graceful close and today's `tools/w3/window-close.ps1` proved the hard-kill contrast, but the double-hard-crash orphan is still reasoned about rather than tested. |

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
| FU-B4-1 | Orchestrator central log emits no severity | The severity model (B4.4) is rendered and now exercised by the dashboard's own control feedback (abort/skip/kill=Warn, inject=Success/Error), but `Orchestrator` still logs via `sink.Log(stamped)` (plain string → `Info`) at `Orchestrator.cs:1241`. The lines an unattended operator most needs colour-coded — gate-failed, backoff, needs-human, stage-confirmed — stay grey. Map those message→severity and emit `LogEntry`. Touches the Orchestrator broadly + needs a mapping decision, so deferred out of the UI-scoped audit budget. | on demand | OPEN — unchanged, and lower stakes than when filed: the Face no longer renders that central log as the primary surface, and Serilog's structured file log carries real levels. Cosmetic. |
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
| FU-B10-2 | Battery-collapse token savings not empirically measured | B10.4 spec requires "token-per-checkpoint measured before/after on a self-run; documented drop." The prompt note is emitted correctly, but no automated metric compares pre- and post-collapse session tokens. Conduct a real measurement run. | `HUMAN:` rides W5.2 | OPEN — deliberately: token-per-checkpoint is only meaningful against a real model, and W5.2 is the run that will have one. Measure there rather than inventing a fake-agent number. |
| FU-B10-3 | HookConfig.TimeoutMinutes=0 is not validated | `TimeSpan.FromMinutes(0)` = `TimeSpan.Zero` causes immediate timeout; plan validation should reject `< 1`. Low risk (default is 3). | B10 fix-lane | CLOSED (C4) — PlanConfig.Validate now rejects TimeoutMinutes < 1 for setup, teardown, pre-hook, and post-hook. |
| FU-B10-4 | ComputeDepth allocates per call (HashSet + O(n·d) scan) | Negligible for the self-plan but could be a hot path for large plans. Pre-compute depth in SnapshotBuilder.BuildStages() once. | post-B11 | CLOSED (C4) — PreComputeDepths() builds a dictionary once; Build() now reads from it. |

## Opened by B11 (audit session, 2026-07-09, session #64)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-B11-1 | Completion script exhaustiveness test | The `Completion_Powershell_ContainsAllVerbs` test checks presence of specific verbs but doesn't assert exhaustive parity with Program.cs command registrations. Adding a command without updating completion silently breaks tab-complete. | B12 fix-lane | CLOSED (C4) — `Completion_ContainsAllRegisteredVerbs_Exhaustive` asserts all 20 verbs present in both PS and bash output; no stale verbs allowed. |
| FU-B11-2 | Cross-platform clean-clone battery on Linux | B11.3 clean-clone proof ran on same-machine Windows only. A Linux-hosted clone+build+test would prove true cross-platform portability. | on demand | PARTIAL (W6.2) — CI's `ubuntu-latest` leg does a clean clone, restores, **builds** the whole tree and runs the Go suite on every push, so "accidentally Windows-only at compile time" is now gated. It deliberately does not run `dotnet test`: those tests spawn PowerShell gates and `.exe` children, so a red there would only restate that Linux is not a supported host. Running the ENGINE on Linux remains unproven, and README's Platform section says so. |
| FU-B11-3 | Real-credential cTrader owner-gated path | B11.4 acceptance used a fake-agent; the credentialed cTrader login + live compare-both path is unproven. Needs a live Shamshir run with real credentials. | `HUMAN:` (Shamshir run) | OPEN — owner-gated by definition (real credentials, real money). Unrelated to W5.2, which uses a toy plan. |

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
| FU-F0-2 | `StartParallelAudit` launches uncancellable task | `Task.Run` passes CT but no `_parallelAuditCts` to cancel mid-flight. Pre-existing. | on demand | OPEN — the call moved to `LaneCoordinator.StartParallelAudit(audit, CancellationToken ct)` and takes the run's CT, so it dies with the run; there is still no way to cancel just the audit. Smaller than filed, not gone. |
| FU-F0-3 | Telegram fire-and-forget has no fault continuation | `_ = PushAsync/PushWithKeyboardAsync` — internal methods catch exceptions but the pattern is fragile. Pre-existing. | on demand | OPEN — verified still present at 5 sites (`RunLoop.cs:323`, `RunLoop.Plumbing.cs:234`, `VerdictEngine.cs:79,466`, `VerdictEngine.Phase.cs:125`). The notifier swallows its own faults, so the failure mode is a silently unsent notification, not a crash. |
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
| FU-F1-03 | EmitSessionFinished commit SHA extraction | `rec.NewCommits[^1].Split(' ')[0]` assumes git log --oneline format. Advisory-only evidence field. | on demand | OPEN — unchanged and still advisory-only; the assumption holds because the same code writes the format it parses. |
| FU-F1-04 | RunDb.Query lacked disposed-connection guard | Safe today but would fail loudly with confusing errors if F2.1 adds concurrent access. **CLOSED this session** — guard added at RunDb.cs:517. | F2 | CLOSED (sixth F1 audit) |
| FU-F1-05 | SeedCheckpoints not transactionally atomic | Each UPSERT is a separate implicit transaction. Power failure mid-loop could leave partial state. Next SeedCheckpointsFromTracker reads intact tracker file and re-seeds, so no permanent data loss. Wrap in a single transaction for atomicity if needed. | F2 fix-lane | CLOSED (superseded by W1.1/W1.2) — the `checkpoints` table this row is about was DROPPED in migration v8. `SeedCheckpoints` now emits `TaskAdded`/`TaskStatusChanged` into the append-only event log, whose fold already tolerates a truncated tail, and W1.2's `WorkGraphSync` re-runs upsert-never-clobber at every boundary — so a partial seed self-heals instead of persisting. |
| FU-F1-06 | run.db runs.status not updated on non-completion non-terminal states | NeedsHuman, Paused, AwaitingOwner, VerifyingGates, Backoff leave runs.status='running' in run.db. RecordRunEnd sets ended_utc which isn't warranted for resumable states. Add an UpdateRunStatus method (status-only, no ended_utc) and call from NeedsHuman + other state transitions. Low severity: state.json is authoritative; run.db is additive/best-effort; InitializeRun (INSERT OR REPLACE) fixes on resume. | on demand | OPEN — no `UpdateRunStatus` exists yet, so the row stands as written. Note the premise moved: state.json is no longer authoritative (M2 put run state in run.db's `run_state` table), which makes a stale `runs.status` slightly more visible than when this was filed. |
| FU-F1-07 | Completion test uses hardcoded verb list | The `Completion_ContainsAllRegisteredVerbs_Exhaustive` test hardcodes `expectedVerbs`, which allowed `task` and `note` to be missing from both the completion scripts AND the test for 9 audit sessions. Replace with a runtime reflection test that enumerates Program.cs registrations dynamically. | on demand | OPEN — verified still hardcoded at `tests/Conductor.Tests/B11_2Tests.cs:111`. The failure mode is unchanged: add a verb, forget the list, and both the completion script and its "exhaustive" test stay quietly green. |

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
| FU-OWNER-9 | Agent kills its own parent conductor process | A fix session's prompt showed a build error `"locked by: conductor (15300)"`. The agent inferred PID 15300 was a stale orphan, set a todo to kill it, ran `Stop-Process -Id 15300` — but PID 15300 was the CURRENT conductor (handled both sessions 3 and 4). No crash dump (external process kill). **Suggested fix:** (1) add a self-PID guard to the agent tool contract so Stop-Process rejects the conductor's own PID; (2) gate battery should skip rebuilding Conductor.csproj when the running binary IS the one being built (detect via PID matching); (3) add a warning in the fix prompt: "locked by conductor (PID)" usually means the current run, not a leftover. | next era (safety) | **OPEN — the most consequential row left in this file.** Not a Face bug and not obsolete: the agent runs unsandboxed by design (see `SECURITY.md`), so nothing today stops a session from killing the process supervising it. W3.3's `PidLiveness` fixed the mirror-image defect — the *engine* tree-killing a pid it no longer owns — but added no guard on the agent's side of the tool contract. Suggested fixes (1)–(3) still stand as written. |
