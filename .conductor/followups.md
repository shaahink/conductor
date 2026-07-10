# Tracked followups

Living list of debt/ratchets deferred out of a phase, each with an owning stage. A later
fix/harden session (or the owning stage's opening) must clear these. Never silently drop a row —
close it with a commit ref or move it, don't delete it.

## Opened by B0 (audit session, 2026-07-08)

### Analyzer ratchets (recorded in ADR-0001 §"Deliberately relaxed") — ratchet severity to `error`
| id | rule | sites | why deferred | owning stage | status |
|----|------|-------|--------------|--------------|--------|
| FU-B0-1 | MA0045 (sync-over-async I/O) | ~28 | Async-ifying the Orchestrator is signature-changing and *is* the B2 async/Host/DI rework. The deadlock-class twin MA0042 stays `error`. | B2 | OPEN |
| FU-B0-2 | MA0002 (explicit StringComparer) | ~38 | Cross-cutting, mechanical; low correctness risk under `InvariantGlobalization`. | post-B2 | OPEN |
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
| FU-B1-1 | `ScriptProvider` can't split stdout from stderr | `ProcessRunner.Run` interleaves both streams, so a normaliser that writes any progress/warning to stderr corrupts the JSON parse (surfaces as "not a JSON checkpoint array"). Provider stays resilient (clear error, no crash) and the "print ONLY JSON to stdout" contract is documented, but it's brittle. Splitting streams is a `ProcessRunner` signature change → land with structured logging. | B2 | OPEN |
| FU-B1-2 | No `CancellationToken` through `IProgressProvider.Read` | Progress read is synchronous; `ScriptProvider` spawns a process with only a timeout, not the run's CT, so Ctrl+C won't interrupt a long normaliser mid-read. Consistent with FU-B0-1 (sync-over-async deferred); thread the CT with the B2 async/Host/DI pass. | B2 | OPEN |
| FU-B1-3 | `ScriptProvider` trusts checkpoint content shape | Accepts a JSON array with empty ids / unknown status without validation (garbage-in → empty-id rows). Fine for a plan-owned script, but a stricter contract would fail louder. Low priority. | post-B2 | CLOSED (C4) — id and title now validated; empty/unknown status tolerated (conventions parser has fallback). |

Fixed in-phase by the B1 audit (no followup needed, recorded for the trail): shamshir `new-plan`
scaffold was undrivable (declared `P-0/P0/P1` stages but scaffolded `S1` rows) → now stage-coherent
+ `NewPlanScaffoldTests`; double-space/tab `IN PROGRESS` silently misclassified by the new status
vocabulary → whitespace-tolerant `StartsWithAny` + regression test. See `.conductor/handovers/B1.md`.

## Opened by B2 (audit session, 2026-07-08, session #17)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-B2-1 | `LiveMetrics` has no production consumer | `ForSession`/`RunWide` are called only from tests; the dashboard reads `agent.Tokens*` directly. The B2 audit FIXED the persisted-data bug (TokenDelta now carries `sessionId`), so the log is now correct � but the end-to-end "consumer folds live tokens from the log" loop is unproven by a real run. Wire it and prove against a recorded log. | B5 | OPEN |
| FU-B2-2 | `RunStateProjection.FindInterruptedSession` assumes single-session | Tracks one "most recent unmatched start"; cannot represent two concurrently-interrupted sessions. Matches today's one-session-at-a-time model but is an undocumented invariant parallel-lane stages must revisit. | parallel-lane stage | OPEN |
| FU-B2-3 | Orphaned-`SessionStarted` recovery may queue a non-resume | Event-log recovery of a `SessionStarted` with no matching `state.json` record synthesises a `SessionRecord` and queues a resume with the event's `AgentSessionId` � possibly empty ? starts a FRESH agent, not a true resume (safe-ish re-deliver, but silent). Double-hard-crash-only path; untested against an empty-id orphaned stream. Add a test + decide skip vs re-deliver vs needs-human. | B3 (process control) | OPEN |

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
| FU-B3-1 | No Orchestrator integration harness for the process-control loop | The B3 "gate" tests simulate `RunState` by hand; none drive `HandleControl`/`Run`. Build a harness (fake agent + temp git repo) and cover budget park->approve->run->re-park, approval mode, `goto`, `rollback` (clean/dirty/`--force`), `retry-stage`, graceful-cancel. The B3 audit fixed the real branch logic and locked the PURE slices (`OwnerApproval`, `ControlFile`) but the loop itself is proven only by reasoning. | B4/B5 (whoever adds the run harness) | OPEN |
| FU-B3-2 | B3.5 graceful Ctrl+C is unproven | No cancellation test and no `B3.5-gate.txt` (evidence exists only for B3.1-B3.4). Add a test asserting state saved + resume queued + log flushed + exit code 130, and capture the evidence. | B3 fix-lane / B4 | OPEN |
| FU-B3-3 | Budget accumulators are per-process, not persisted | `_runCostUsd`/`_runTokens` reset at each `Run()` start. The park survives restart, but a run killed mid-accrual BEFORE parking restarts its count from 0 -> a run split across restarts can exceed `maxRunCostUsd`/`maxRunTokens` without parking. Decide per-process vs per-logical-run; if the latter, persist a cumulative baseline. | B3 fix-lane | OPEN |
| FU-B3-4 | Rollback is not recorded as an event | `git reset --hard` is written to conductor.log + Serilog but has no `ConductorEvent`, so the timeline/report/(B6) Telegram do not show a rollback. Trap B3.3 says "log the reset". Emit a destructive-action event. | B6 (or B3 fix-lane) | OPEN |
| FU-B3-5 | `!inSession` control verbs issued mid-session are silently dropped | `retry-stage`/`rollback`/`pause-after-stage`/`goto` consume (delete) `control.json` but the `when !inSession` guard fails with no operator feedback. Queue for after-session or reject with a message. | B3 fix-lane / B4 | OPEN |

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
| FU-B4-1 | Orchestrator central log emits no severity | The severity model (B4.4) is rendered and now exercised by the dashboard's own control feedback (abort/skip/kill=Warn, inject=Success/Error), but `Orchestrator` still logs via `sink.Log(stamped)` (plain string → `Info`) at `Orchestrator.cs:1241`. The lines an unattended operator most needs colour-coded — gate-failed, backoff, needs-human, stage-confirmed — stay grey. Map those message→severity and emit `LogEntry`. Touches the Orchestrator broadly + needs a mapping decision, so deferred out of the UI-scoped audit budget. | B4 fix-lane / B6 | OPEN |
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
| FU-B10-1 | No orchestrator integration harness for SelectStage + DepSatisfied | The readiness-ordering logic is tested only via model validation (B10_1Tests); no test drives a live orchestrator with real tracker + RunState. Extends FU-B3-1 (the base harness gap). | B12 fix-lane | OPEN |
| FU-B10-2 | Battery-collapse token savings not empirically measured | B10.4 spec requires "token-per-checkpoint measured before/after on a self-run; documented drop." The prompt note is emitted correctly, but no automated metric compares pre- and post-collapse session tokens. Conduct a real measurement run. | B12 | OPEN |
| FU-B10-3 | HookConfig.TimeoutMinutes=0 is not validated | `TimeSpan.FromMinutes(0)` = `TimeSpan.Zero` causes immediate timeout; plan validation should reject `< 1`. Low risk (default is 3). | B10 fix-lane | CLOSED (C4) — PlanConfig.Validate now rejects TimeoutMinutes < 1 for setup, teardown, pre-hook, and post-hook. |
| FU-B10-4 | ComputeDepth allocates per call (HashSet + O(n·d) scan) | Negligible for the self-plan but could be a hot path for large plans. Pre-compute depth in SnapshotBuilder.BuildStages() once. | post-B11 | CLOSED (C4) — PreComputeDepths() builds a dictionary once; Build() now reads from it. |

## Opened by B11 (audit session, 2026-07-09, session #64)

| id | item | detail | owning stage | status |
|----|------|--------|--------------|--------|
| FU-B11-1 | Completion script exhaustiveness test | The `Completion_Powershell_ContainsAllVerbs` test checks presence of specific verbs but doesn't assert exhaustive parity with Program.cs command registrations. Adding a command without updating completion silently breaks tab-complete. | B12 fix-lane | CLOSED (C4) — `Completion_ContainsAllRegisteredVerbs_Exhaustive` asserts all 20 verbs present in both PS and bash output; no stale verbs allowed. |
| FU-B11-2 | Cross-platform clean-clone battery on Linux | B11.3 clean-clone proof ran on same-machine Windows only. A Linux-hosted clone+build+test would prove true cross-platform portability. | post-B12 | OPEN |
| FU-B11-3 | Real-credential cTrader owner-gated path | B11.4 acceptance used a fake-agent; the credentialed cTrader login + live compare-both path is unproven. Needs a live Shamshir run with real credentials. | post-B12 (Shamshir run) | OPEN |

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
| FU-F0-1 | `RunStageHook` post-hook uses `CancellationToken.None` | Post-hook in `ConfirmStageAsync` passes `None` — hook is a local PowerShell process with its own timeout; low risk but inconsistent with async CT pattern. Pre-existing, not introduced by F0. | F1 or F2 | OPEN |
| FU-F0-2 | `StartParallelAudit` launches uncancellable task | `Task.Run` passes CT but no `_parallelAuditCts` to cancel mid-flight. Pre-existing. | F3 (process control) | OPEN |
| FU-F0-3 | Telegram fire-and-forget has no fault continuation | `_ = PushAsync/PushWithKeyboardAsync` — internal methods catch exceptions but the pattern is fragile. Pre-existing. | F8 | OPEN |
| FU-F0-4 | `.GetAwaiter().GetResult()` at Spectre.Cli boundaries | 3 sites in `Commands.cs` — safe without `SynchronizationContext` but fragile. Convert `Execute` to `async Task<int>`. Pre-existing. | F1 (or next Commands.cs touch) | OPEN |
| FU-F0-5 | `EventLog.Dispose()` blocks on `_drain.GetAwaiter().GetResult()` | Drain task blocks synchronously on dispose — could hang on slow filesystems. Pre-existing, out of F0 scope. | F1 | OPEN |
| FU-F0-6 | `HostLoggingTests.DryRunWritesJsonLogWithCorrelationProperties` flaky | File-share race between parallel test runs. Pre-existing (possibly worsened by F0.2 async File.ReadAllText→ReadAllLinesAsync timing change). Passes on retry. | F1 fix-lane | OPEN |

**Fixed in-phase by the F0 audit** (no followup): (1) 7 `CancellationToken.None` delay sites in async flow → `ct` with OCE handling for immediate Ctrl+C responsiveness; (2) CT threaded through `RunFollowupFixLanesAsync`, `CollectLaneArtifactsAsync`, and 3 `_progress.Read` calls; (3) `PushWithKeyboardAsync` at new CT scope given explicit `CancellationToken.None`. See `.conductor/handovers/F0.md`.
