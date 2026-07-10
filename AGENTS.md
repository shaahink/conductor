# Conductor (Baton worktree) — session handoff

## What this is
Conductor is an autonomous multi-session engineering orchestrator (C# / .NET, Spectre.Console). It
spawns headless agent sessions, verifies work independently (gate battery + git commits + tracker
diff), is fully resumable, and reports to `.conductor/REPORT.md`. This worktree (`feat/baton`) hosts
**Baton — Conductor v2**: Conductor improving itself.

## This worktree
- **Path:** `C:\Code\conductor-baton`  **Branch:** `feat/baton`
- **Do NOT touch:** `C:\Code\conductor` (master, the stable DRIVER) or the live `C:\Code\DevContext2-ui`
  Loom run (separate repo + lock).
- **Driver:** the STABLE `C:\Code\conductor\bin\conductor.exe` (built from master). The tool improving
  Conductor is never the tool under edit.

## Read order
1. `C:\Code\conductor\NEXT-ERA.md` — strategic roadmap for Era v3 (post-Baton)
2. `CONDUCTOR-START.md` — tracker, all 67/67 checkpoints DONE
3. `docs/qa-reports/CONDUCTOR-FINAL.md` — final audit + Needs Human checklist
4. `docs/baton/BATON-BRIEF.md` — v2 design authority (reference)

## Deliverables authored on this branch (plan, not yet executed)
- `docs/baton/BATON-BRIEF.md` + `docs/baton/stages/B0.md`…`B12.md`
- `docs/baton/tooling/` (B0 drafts: editorconfig, Directory.Build.props, Directory.Packages.props,
  Meziantou ruleset rationale) + `docs/baton/adr/` (created in B0.6)
- `CONDUCTOR-START.md` (tracker — verified parses: 65 checkpoints, 13 stages)
- `plans/conductor.self.plan.json` + `plans/baton-templates/` (session/fix/resume/audit/advisor,
  tuned: value-only gates, audit-fixes-leftovers, fix-session leftover sweep)
- `examples/README.md` + `examples/shamshir/parity-pipeline.TRACKER.md` (drivability proof)

## How to run (with the STABLE driver)
```powershell
C:\Code\conductor\bin\conductor.exe run --dry-run -p .conductor\plans\conductor-debt.plan.json
C:\Code\conductor\bin\conductor.exe run         -p .conductor\plans\conductor-debt.plan.json
```

## QA protocol (added 2026-07-09)
- Skip previous-session QA when last session ended `advanced` or `progress` with all gates green.
- Run QA only when last session was `gatesRed`, `stalled`, `noProgress`, or `interrupted`.
- **Tracker rule:** always update BOTH handoff block AND checkpoint row (DONE + commit + evidence). If row stays TODO, conductor re-launches the same stage.

## Current state (2026-07-11)
- **Baton v2 COMPLETE** — 77 sessions, 67/67 checkpoints DONE, status=completed.
- **Foreman v3 ACTIVE** — 23/40 checkpoints DONE, F0/F1/F2/F3/F4/F5 confirmed, next: F6 (Ink TUI, TypeScript).
- **Branch:** `feat/foreman` is the active branch; `feat/baton` is the worktree.
- **Driver:** `C:\Code\conductor\bin\conductor.exe run -p plans\conductor-foreman.plan.json`
- **Read order:** `CONDUCTOR-VNEXT-PLAN.md` (tracker) → `docs/CONDUCTOR-VNEXT-PLAN.md` (design doc)
- **Last session:** s29 (manual, Claude Code direct) — F5 delivered: ControlDispatcher command/query
  extraction (the seam the god-class Orchestrator lacked) + HTTP+SSE control plane on top of it.
  Commits 5370cec, 4c3aa00. 634/635 tests pass (1 pre-existing unrelated flake), 0w/0e.

## C# coding standards (this codebase)

### Language level: C# 13 / .NET 10
- **Primary constructors** for classes that take dependencies and immediately assign them
- **`record`** for data-only types (VerifierVerdict, AdvisorVerdict, PendingFix) — value semantics, with-expressions
- **`sealed`** by default on all classes that aren't designed for inheritance
- **Collection expressions** `[1, 2, 3]` instead of `new[] { 1, 2, 3 }`
- **Raw string literals** `"""..."""` for SQL and multi-line templates
- **`using var`** for IDisposable resources (SqliteCommand, SqliteDataReader, Process handles)
- **File-scoped namespaces** — `namespace Foo.Bar;` (no braces)

### Async patterns
- **`ConfigureAwait(false)`** on EVERY await in library/engine code (not in test projects)
- **`CancellationToken` threaded everywhere** — no `CancellationToken.None` in async methods
- **`await Task.Delay()`** not `Thread.Sleep()` in async paths
- **No `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`** — sync-over-async is forbidden
- **`async Task` not `async void`** except for event handlers (but there are none)

### Null safety
- **Nullable reference types ON** — `string?`, `int?`, etc. explicit
- **`??` and `?.`** operators for safe navigation
- **Pattern matching** `is { } x` for non-null checks, `is not null` for guard clauses
- **`ArgumentNullException.ThrowIfNull()`** in public API entry points

### Collections and strings
- **`StringComparer.Ordinal`** / `OrdinalIgnoreCase` for all dictionary lookups and comparisons
- **`StringBuilder`** for multi-append string construction
- **`IReadOnlyList<T>`** for read-only return types; `List<T>` for mutable state
- **`HashSet<string>`** with explicit comparer for lookup sets

### Security and correctness
- **Regex: always use timeout** — `RegexOptions` with `RegexTimeout` from `ProgressConventions`
- **SQL: always parameterised** — no string concatenation in SQL (RunDb uses `@param` syntax)
- **JSON: `System.Text.Json`** not Newtonsoft
- **No secrets in code** — tokens read from env vars

### Analyzer strictness
- **TreatWarningsAsErrors** on the whole solution
- **Meziantou.Analyzer** full ruleset — never lower severity to pass
- **#pragma warning disable** only with inline justification comments, scoped to the minimum block

## Delivery flow (F4+)
- **Deliver session** delivers checkpoints → gates green → **Verifier session** independently checks claims
- Verifier outputs `{score, findings[], verdict}` JSON — score ≥ 80 → DONE; < 80 → findings feed retry
- VerifierThreshold configurable in plan's `LimitsConfig`; per-stage override pending F7
- ShouldVerify gates only `SessionKind.Deliver` — Fix/Audit/Resume sessions skip verification

## Command/Query/Event layering (F5+)
Orchestrator was a 2763-line god-class (design doc C10) mixing the run-loop state machine with
control-verb execution, snapshot building, and lane glue. F5 cut the first seam; keep cutting along
it rather than adding new responsibilities back into Orchestrator:
- **New control verb?** Goes in `Core/Commands/ControlDispatcher.cs` (one `case` in `DispatchAsync`),
  not inline in `Orchestrator.HandleControlAsync`. All three ingresses (TUI queue, control.json,
  `POST /control`) already converge on `ControlCommand` → `ControlDispatcher.DispatchAsync` — a new
  verb written there is automatically available from all three, no per-ingress wiring.
- **New read/query surface?** Build it from the event log (`RunStateProjection.Fold`,
  `Core/Events/TaskGraph.cs`, `Core/SnapshotBuilder.cs`) or extend those, never by reaching into
  `Orchestrator`'s private fields. This is what keeps `Core/Http/ControlPlaneServer.cs`'s GET
  endpoints decoupled from Orchestrator internals — they only ever read `events.jsonl`.
- **HTTP wire types are separate DTOs** (`Core/Http/ControlPlaneDto.cs`), not `DashboardSnapshot`
  directly — the TUI-rendering types carry `ValueTuple` fields System.Text.Json's source generator
  can't serialise. Mapping is a thin field copy; don't duplicate the actual computation.
- **Control plane is opt-in** (`RunOptions.ControlPlane` / `--control-plane` CLI flag, off by
  default) and a bind failure is caught + logged, never fatal — headless/no-flag runs must stay
  byte-identical whether or not it's enabled. Don't add a code path that assumes it's running.
- Still explicitly deferred (documented, not forgotten): `GET /transcript/current` (thinking-stream
  SSE — build with F6's agent pane, the first real consumer) and extracting the ~350-line lane
  coordination glue (`StartParallelAudit`/`RunFollowupFixLanesAsync`/etc.) out of Orchestrator — no
  gate has needed it yet; revisit if a `/lanes` endpoint or further god-class shrinkage is wanted.

## Foreground-blocking anti-patterns (codebase-specific)

### Test filtering — use Category traits, not substring guessing
- Every test that spawns a real process/git repo (ProcessSupervisor*, MutatingLane* in
  `B12_3Tests.cs`/`B12_4Tests.cs`, `HarnessTests`, `GateRunnerTests`, `B11_1CrossPlatformShellTests`)
  carries `[Trait("Category", "Integration")]`, at class or method level. The one known-flaky test
  (`EventLogTests.ReadAllSucceedsWhileLiveWriterHoldsTheFile`, a live-file-handle race) carries
  `[Trait("Category", "Flaky")]`. Do not hand-maintain a substring filter list again — a prior
  version of this doc did (`FullyQualifiedName~FailureCircuitBreaker|...`) and it silently missed
  real integration tests whose names didn't match the listed substrings.
- **Fast dev loop** (measured ~8s for 573 tests): `dotnet test Conductor.slnx --filter "Category!=Integration&Category!=Flaky"`
- **Full suite** (measured ~21s for 626 tests as of F5 prep — much faster than the "5+ min" this
  doc used to claim; re-measure if it regresses): `dotnet test Conductor.slnx`
- New tests that spawn a real process, real git repo, or sleep >500ms for a real OS event: add the
  `Integration` trait when you write them, not after the fact.
- Kill orphan dotnet processes from failed runs: `Get-Process dotnet -ea 0 | Stop-Process -Force`

### ProcessRunner.Run() is synchronous — blocks the async orchestrator
- `src/Conductor/Core/ProcessRunner.cs:68` — sync `WaitForExit(500)` polling loop
- Every gate run (`dotnet build`, `dotnet test`) and advisor spawn blocks the calling thread
- The F0.2 async refactor removed `.Result`/`.Wait()` in the orchestrator but the process layer
  is still sync — gate battery and advisor consultation both are foreground-blocking
- **Correction:** an earlier version of this doc predicted F5 would fix this. It didn't — F5 added
  an orthogonal HTTP+SSE *read/control* surface (`Core/Http/ControlPlaneServer.cs`) on its own
  background thread; it never touches `ProcessRunner`. This debt is still open and unowned by any
  current stage — flag it if you pick it up.

### Agent sessions: use `conductor bg start|status|logs|stop` for long ops
- F2.3 delivered sanctioned background-run primitives for agent prompts
- Prompts must mandate `conductor bg start` for anything >3 min
- StallDetector v2 (F3.1) uses bg liveness as a keepalive signal — "quiet but its backtest
  is running" is NOT a stall

### MCP task server is in-process, not a background process
- `McpTaskServer` runs in the same process as the agent session
- It reads/writes files synchronously — keep operations fast

### PreflightHealth checks can block (DNS timeout, HTTP timeout)
- DNS and HTTP checks have 10s timeouts via CancellationTokenSource
- When preflight is disabled or unconfigured (DnsHealthCheckConfig absent), `RunAllAsync`
  returns empty list and `AnyFailed` returns false — orchestrator proceeds
- Git check spawns `git status --porcelain` synchronously — <1s normally

## Gotchas
- **`claudeSessionId`** is a legacy field name storing ANY agent's session id (B2 renames/abstracts).
- Templates for the self-plan live in `plans/baton-templates/` (NOT `plans/templates/`, which are the
  Loom templates B1 relocates to `examples/loom/`).
- `Conductor.slnx` already exists on master — B0 verifies, doesn't recreate.
- Stage-id convention: the current `TrackerParser` regex does NOT match `P-0` (hyphen) — proven
  against `examples/shamshir/parity-pipeline.TRACKER.md` (16/17 rows). B1.4 makes it configurable.
- Value-only gates/tests (BRIEF §5.1): don't add ceremony; audit fixes leftovers; followups feed the
  next phase / B12 fix-lanes.
