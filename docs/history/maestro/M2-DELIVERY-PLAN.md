# M2 — "One Truth" Delivery Plan

**Design authority:** `docs/history/MAESTRO-PLAN.md` §M2 + AD-3 \
**Tracker:** `MAESTRO-TRACKER.md` \
**Branch:** `feat/foreman` | **Predecessor:** M1 (4/4 DONE)

---

## 0. Executive summary

M2 eliminates the three-way split brain (`state.json` + `events.jsonl` + `run.db`) and makes
`run.db` the single authoritative store. Per AD-3, an `events` table inside `run.db` is the
append-only spine; every other table is a projection folded from it.

**Current state:**
```
state.json   → orchestrator truth (RunState serialized to disk)
events.jsonl → append-only event spine (EventLog, NDJSON file)
run.db       → SHADOW store (best-effort writes, schema defined TWICE in create+migrate)
```

**Target state:**
```
run.db/events        → append-only event spine (replaces events.jsonl)
run.db/sessions,     → projections (session lifecycle, queried by TUI/MCP/Telegram/reporter)
       costs,
       gates,
       scores,
       checkpoints,
       handovers,
       ledger,
       injections,
       pids
run.db/run_state     → orchestrator mutable state as JSON blob (replaces state.json)
.conductor/sessions/ → per-session artifact directory (replaces flat logs/ files)
  <NNN>-<stage>/
    prompt.md        → exact compiled prompt (byte-matched against what the agent received)
    transcript.md    → agent stdout/thinking formatted
    verdict.md       → VerdictEngine's verdict
    handover.md      → the handoff block
    cost.json        → cost breakdown
INDEX.md             → links all sessions
```

**Key decisions (from owner):**
1. Delete `attempts` table + `RecordAttempt()` — dead code, zero callers
2. Transcript consolidated into session directories; remove `transcript.jsonl`
3. Session directory naming: `<NNN>-<stage-id>` (number + stage id from plan)
4. Single `IRunStore` interface — no CQRS split at this scale
5. M2.2 + M2.3 delivered as an integrated whole — the interface IS the authoritative mechanism

---

## 1. Checkpoint breakdown

### M2.1 — Single-source schema (versioned `.sql` migrations)

**Problem:** `checkpoints` and `pids` DDL exists in both `CreateSchema()` (lines 271-299) and
`MigrateFrom()` (lines 309-337). The `ledger` table's `created_at` column went missing because
the create path and migrate path diverged.

**Fix:** Replace the 200+ line `CreateSchema()` + `MigrateFrom()` with a migration runner that
reads versioned `.sql` files as embedded resources and applies them sequentially.

**Migration files (embedded in `Conductor` project):**
```
src/Conductor/Core/Store/Migrations/
  v1_init.sql               — runs, stages, sessions, attempts, gates, scores,
                               ledger, handovers, injections, costs
  v2_checkpoints.sql        — checkpoints table
  v3_pids.sql               — pids table
  v4_ledger_created_at.sql  — ALTER TABLE ledger ADD COLUMN created_at
  v5_events_and_state.sql   — events table + run_state table (for M2.3)
```

**Migration runner rules:**
- On open: read `schema_version` → apply migrations from `stored_version + 1` to `current`
- Each migration runs inside the EnsureSchema transaction
- If a migration SQL file is missing (embedded resource not found), throw immediately
- Empty schema_version → apply all migrations from v1
- Version > current → throw `InvalidOperationException` (existing behavior)
- Version == current → no-op (existing behavior)
- `CREATE TABLE IF NOT EXISTS` for idempotent re-runs in each migration

**Truth gate:** A test builds a fresh DB and a DB migrated v1→v5 and asserts the schemas match
via `SELECT sql FROM sqlite_master WHERE type='table' ORDER BY name`.

**Files to create:**
| File | Purpose |
|---|---|
| `Core/Store/Migrations/v1_init.sql` | All initial tables |
| `Core/Store/Migrations/v2_checkpoints.sql` | checkpoints table |
| `Core/Store/Migrations/v3_pids.sql` | pids table |
| `Core/Store/Migrations/v4_ledger_created_at.sql` | ALTER TABLE ledger |
| `Core/Store/Migrations/v5_events_and_state.sql` | events + run_state tables |
| `Core/Store/MigrationRunner.cs` | Applies migrations; called by SqliteRunStore |

**Files to modify:**
| File | Change |
|---|---|
| `Core/RunDb.cs` | Replace CreateSchema+MigrateFrom with call to MigrationRunner |
| `Conductor.csproj` | Add `<EmbeddedResource>` for migration `.sql` files |
| `tests/.../RunDbTests.cs` | Add byte-identical schema comparison test |

**Risk: Low.** Mechanical extraction of existing DDL into files.

---

### M2.2 + M2.3 — `IRunStore` + `SqliteRunStore` + authoritative `run.db`

**This is the core delivery.** M2.2 and M2.3 are delivered together because the IRunStore
interface IS the mechanism that makes run.db authoritative.

#### Phase A: Create IRunStore + SqliteRunStore

**IRunStore interface** (single interface, no split):
- Run lifecycle: `InitializeRun`, `RecordRunEnd`
- Stage lifecycle: `InitializeStage`, `ConfirmStage`
- Session lifecycle: `RecordSession`
- Costs: `RecordCost`
- Gates: `RecordGate`, `GetLastPassingGateResult`
- Scores: `WriteScore`
- Ledger: `WriteLedger`, `QueryLedger`
- Handovers: `WriteHandover`, `GetLatestHandover`
- Checkpoints: `SeedCheckpoints`, `UpdateCheckpoint`, `MarkCheckpointInProgress`, `GetCheckpoints`
- PIDs: `TrackPid`, `MarkPidExited`, `GetOrphanPids`, `GetAllPids`
- Events: `AppendEvent` (implements IEventSink.Emit), `ReadAllEvents`, `ReadEventsAfter`
- State: `LoadRunState`, `SaveRunState`
- Queries: `QuerySessions`, `QuerySessionOutcomesByStage`, `QueryRecentGateFailures`, `QuerySingleSession`
- Raw query: `Query` (SELECT-only, parametrized — for report/REPL)

**SqliteRunStore** implements both `IRunStore` and `IEventSink`:
- Single instance, registered as both interfaces in DI
- `Emit(ConductorEvent)` → enqueues to channel → drain thread batch-INSERTs into events table
- Same channel+batching pattern as current EventLog (proven, thread-safe)
- WAL mode (already enabled)
- **Failed writes:** emit `DatabaseWriteFailed` event + log at Error level (not silently swallowed)
  - Critical writes (session lifecycle, run state) MUST throw
  - Additive writes (costs, ledger, handovers) emit event + log, don't crash run

**Implementation as partial class (same pattern as current RunDb):**
```
Core/Store/
  IRunStore.cs                     — interface
  SqliteRunStore.cs                — connection, schema, migration runner, Query
  SqliteRunStore.Sessions.cs       — session/cost/gate/score/handover/checkpoint writes
  SqliteRunStore.Pids.cs           — PID tracking
  SqliteRunStore.Events.cs         — events table append + read (channel+batching)
  SqliteRunStore.State.cs          — run_state JSON blob load/save
  SqliteRunStore.Queries.cs        — typed read queries (ledger, sessions, gate failures)
  StoreRowTypes.cs                 — DTO records (LedgerRow, SessionSummaryRow, etc.)
  Migrations/                      — embedded .sql files (from M2.1)
  MigrationRunner.cs               — migration runner (from M2.1)
```

**delete**: `Core/RunDb.cs`, `Core/RunDb.Sessions.cs`, `Core/RunDb.Pids.cs` — replaced by SqliteRunStore \
**delete**: `Core/Events/EventLog.cs` — replaced by events table \
**delete**: `Core/Events/StateProjectionParity.cs` — no longer needed (single truth)

#### Phase B: Switch DI registration

`ConductorHost.cs` changes:
```csharp
// BEFORE: two separate services
builder.Services.AddSingleton<RunDb>(...);     // SqliteConnection + writes
builder.Services.AddSingleton<IEventSink>(     // events.jsonl file writer
    new EventLog(path, state.RunId));

// AFTER: single store instance, two interfaces
var store = new SqliteRunStore(runDbPath, logger);
builder.Services.AddSingleton<IRunStore>(store);
builder.Services.AddSingleton<IEventSink>(store);  // same instance
```

All consumers that currently accept `RunDb?` → accept `IRunStore?` instead.
All consumers that currently accept `IEventSink` → no change (same interface).

Also remove:
- `TranscriptLog` DI registration → replaced by M2.4 session directories
- `events.jsonl` path construction → no longer needed
- `statePath` parameter → no longer needed (state loaded from run.db)

#### Phase C: Migrate consumers

**Orchestrator-side** (writes — via `IRunStore` instead of `RunDb?`):
- `RunLoop.cs` — `InitializeRun`, `RecordRunEnd`, `InitializeStage`, `SeedCheckpointsFromTracker`
- `RunLoop.Plumbing.cs` — `EmitSessionFinished()`: RecordSession, RecordCost, UpdateCheckpoint, WriteHandover, RegenerateTracker
- `VerdictEngine.cs` — `WriteScore`, `RecordRunEnd`
- `VerdictEngine.Phase.cs` — `ConfirmStage`
- `GateOrchestrator.cs` — `RecordGate`, `GetLastPassingGateResult`
- `SessionRunner.cs` — StallDetector query
- `ProcessSupervisor.cs` — PID tracking (TrackPid, MarkPidExited, GetOrphanPids)

**Control-plane side** (reads — via `IRunStore` instead of `EventLog.ReadAll`):
- `ControlPlaneServer.Endpoints.cs` — 6 GET endpoints switch from `EventLog.ReadAll(path)` to `IRunStore.ReadAllEvents(runId)` / `IRunStore.ReadEventsAfter(runId, seq)`. Session query switches from raw SQL to `IRunStore.QuerySessions(runId)`.
- `ControlPlaneServer` constructor — takes `IRunStore` instead of `eventsPath` + `transcriptPath` + `runDbPath`

**MCP side** (reads — via `IRunStore` instead of raw SQL):
- `McpTaskServer.cs` — journal reads switch from `EventLog.ReadAll` to `IRunStore.ReadAllEvents` / `IRunStore.QueryLedger`
- `McpTaskServer.Handlers.Queries.cs` — ledger query, session query, gate query → typed methods on IRunStore

**Telegram side:**
- `TelegramService.Messages.cs` — session outcomes query, gate failures query → typed methods on IRunStore. Event log reads → `IRunStore.ReadAllEvents`.

**CLI commands** (reads — via `IRunStore` instead of `EventLog.ReadAll` + `RunState.LoadOrNew`):
- `RunCommand.cs` — `RunState.LoadOrNew` → `IRunStore.LoadRunState`. `new EventLog(...)` → removed.
- `ReportCommand.cs` — `EventLog.ReadAll` → `IRunStore.ReadAllEvents`. `RunState.LoadOrNew` → `IRunStore.LoadRunState`.
- `StatusCommand.cs`, `DoctorCommand.cs`, `AuditCommand.cs`, `GateCommand.cs`, `TaskCommand.cs`, `TasksCommand.cs`, `NoteCommand.cs` — all switch from `RunState.LoadOrNew(path, ...)` to `RunState.LoadOrNew(...)` that reads from IRunStore, or directly use IRunStore queries.
- `BgStartHandler.cs`, `BgLogsHandler.cs`, `BgStatusHandler.cs`, `BgStopHandler.cs` — switch from `new RunDb(path)` to `new SqliteRunStore(path, logger)` or accept IRunStore.
- `McpServeCommand.cs` — switch from `new RunDb(path)` to `new SqliteRunStore(path, logger)`.

**Reporter:**
- `Reporter.cs` — `EventLog.ReadAll(path)` → `IRunStore.ReadAllEvents(runId)` for timeline, health metrics, MCP metrics.

**Other consumers:**
- `RunStateProjection.cs` — `Fold(IEnumerable<ConductorEvent>)` stays; caller provides events from IRunStore instead of file
- `RunLoop.cs` crash recovery — `EventLog.ReadAll(eventsPath)` → `IRunStore.ReadAllEvents(runId)`
- `SessionRunner.Mcp.cs` — `EventLog.ReadAll(journalPath)` → `IRunStore.ReadAllEvents(runId)`
- `TasksCommand.cs` — `EventLog.ReadAll(eventsPath)` → `IRunStore.ReadAllEvents(runId)`
- `TrackerGenerator.cs` — already uses RunDb (just switch to IRunStore)
- `SnapshotBuilder.cs`, `StatusAgent.cs` — `RunState state` stays; state now loaded from IRunStore

#### Phase D: Delete old files

- `Core/RunDb.cs`, `Core/RunDb.Sessions.cs`, `Core/RunDb.Pids.cs`
- `Core/Events/EventLog.cs` (174 lines)
- `Core/Events/StateProjectionParity.cs` (100 lines)
- All `events.jsonl` path string construction
- All `state.json` path string construction (except xmldoc comments)
- `TranscriptLog.cs` — replaced by session-directory transcript in M2.4

#### Phase E: Crash recovery from run.db alone

```
RecoverFromCrash():
  1. Load run_state from run.db → get RunStatus, CurrentStage, PendingResume, etc.
  2. Query sessions WHERE ended_utc IS NULL → find crashed sessions
  3. Query events WHERE type='SessionStarted' without matching SessionFinished → secondary check
  4. Rebuild DecomposedCheckpoints from TaskAdded events in events table
  5. Queue resume if interrupted session found
```

No `state.json`, no `events.jsonl`. Everything from run.db.

**Truth gate:** `conductor run` a toy plan, `kill -9` mid-session, restart → resumes correctly.
No `state.json` or `events.jsonl` on disk.

**Files to create:**
| File | Purpose |
|---|---|
| `Core/Store/IRunStore.cs` | Single interface for all DB operations |
| `Core/Store/SqliteRunStore.cs` | Connection, schema, migration, Query |
| `Core/Store/SqliteRunStore.Sessions.cs` | Lifecycle writes |
| `Core/Store/SqliteRunStore.Pids.cs` | PID writes + queries |
| `Core/Store/SqliteRunStore.Events.cs` | Events table append + read |
| `Core/Store/SqliteRunStore.State.cs` | run_state blob load/save |
| `Core/Store/SqliteRunStore.Queries.cs` | Typed read queries |
| `Core/Store/StoreRowTypes.cs` | DTO records |
| `Core/Store/MigrationRunner.cs` | Sequential migration runner |

**Files to delete:**
| File | Replaced by |
|---|---|
| `Core/RunDb.cs` | `SqliteRunStore.cs` + `MigrationRunner.cs` |
| `Core/RunDb.Sessions.cs` | `SqliteRunStore.Sessions.cs` |
| `Core/RunDb.Pids.cs` | `SqliteRunStore.Pids.cs` |
| `Core/Events/EventLog.cs` | `SqliteRunStore.Events.cs` |
| `Core/Events/StateProjectionParity.cs` | Not needed (single truth) |
| `Core/Events/TranscriptLog.cs` | Session directory transcription (M2.4) |

**Files to modify (~35 files):**
| File | Change |
|---|---|
| `Core/Hosting/ConductorHost.cs` | DI: IRunStore + IEventSink from single SqliteRunStore instance |
| `Core/Orchestration/RunContext.cs` | `RunDb? RunDb` → `IRunStore? Store`; remove TranscriptLog |
| `Core/Orchestration/RunLoop.cs` | Crash recovery from IRunStore; InitializeRun/InitializeStage via IRunStore |
| `Core/Orchestration/RunLoop.Plumbing.cs` | EmitSessionFinished via IRunStore |
| `Core/Orchestration/VerdictEngine.cs` | WriteScore + RecordRunEnd via IRunStore |
| `Core/Orchestration/VerdictEngine.Phase.cs` | ConfirmStage via IRunStore |
| `Core/Orchestration/GateOrchestrator.cs` | RecordGate via IRunStore |
| `Core/Orchestration/SessionRunner.cs` | Remove transcript logging (→ session dirs) |
| `Core/Orchestration/SessionRunner.Mcp.cs` | EventLog.ReadAll → IRunStore |
| `Core/GateRunner.cs` | RunDb → IRunStore |
| `Core/ProcessSupervisor.cs` | RunDb → IRunStore |
| `Core/StallDetector.cs` | RunDb → IRunStore |
| `Core/TrackerGenerator.cs` | RunDb → IRunStore |
| `Core/Reporter.cs` | EventLog.ReadAll → IRunStore |
| `Core/Events/RunStateProjection.cs` | Events from IRunStore, not file |
| `Core/Http/ControlPlaneServer.cs` | Constructor: take IRunStore instead of paths |
| `Core/Http/ControlPlaneServer.Endpoints.cs` | All GET → IRunStore |
| `Core/Integrations/McpTaskServer.cs` | EventLog.ReadAll + RunDb → IRunStore |
| `Core/Integrations/McpTaskServer.Handlers.Queries.cs` | Raw SQL → IRunStore typed methods |
| `Core/Integrations/TelegramService.cs` | RunDb → IRunStore |
| `Core/Integrations/TelegramService.Messages.cs` | Raw SQL + EventLog.ReadAll → IRunStore |
| `Core/Orchestrator.cs` | RunDb → IRunStore; TranscriptLog → remove |
| `Commands/RunCommand.cs` | LoadOrNew → IRunStore; EventLog → remove |
| `Commands/ReportCommand.cs` | LoadOrNew + EventLog.ReadAll → IRunStore |
| `Commands/StatusCommand.cs` | LoadOrNew → IRunStore |
| `Commands/DoctorCommand.cs` | LoadOrNew → IRunStore |
| `Commands/AuditCommand.cs` | LoadOrNew → IRunStore |
| `Commands/GateCommand.cs` | LoadOrNew → IRunStore |
| `Commands/TaskCommand.cs` | LoadOrNew → IRunStore |
| `Commands/TasksCommand.cs` | LoadOrNew + EventLog.ReadAll → IRunStore |
| `Commands/NoteCommand.cs` | LoadOrNew + OpenRunDb → IRunStore |
| `Commands/BgStartHandler.cs` | LoadOrNew + new RunDb → IRunStore |
| `Commands/BgLogsHandler.cs` | LoadOrNew + new RunDb → IRunStore |
| `Commands/BgStatusHandler.cs` | LoadOrNew + new RunDb → IRunStore |
| `Commands/BgStopHandler.cs` | new RunDb → new SqliteRunStore |
| `Commands/McpServeCommand.cs` | new RunDb + EventLog → IRunStore |

**Risk: HIGH.** This touches ~35+ files and changes the persistence layer. Mitigations:
- Incremental cut-over: build SqliteRunStore alongside existing RunDb, switch consumers one at a time
- Keep `RunDb` and `EventLog` in place until all consumers have switched, then delete
- Run full test suite (`dotnet test Conductor.slnx`) after each consumer migration
- The kill -9 truth gate is the ultimate verification

---

### M2.4 — Session history directories

**What changes:**
- Create `.conductor/sessions/<NNN>-<stage>/` per session:
  - `prompt.md` — exact prompt byte-matched against what the agent received
  - `transcript.md` — agent stdout/thinking formatted as markdown
  - `verdict.md` — VerdictEngine's structured verdict
  - `handover.md` — the handoff block
  - `cost.json` — token + cost breakdown
- Create `.conductor/sessions/INDEX.md` — links all session directories
- Delete `.conductor/logs/session-{N}.prompt.md` and `.conductor/logs/session-{N}.jsonl`
- Delete `transcript.jsonl` (transcript now per-session in the directory)

**Implementation:**
- New class `Core/SessionArtifacts.cs` — static method `WriteSessionDirectory(session, prompt, transcript, verdict, handover, cost)`
- Called from `EmitSessionFinished()` in `RunLoop.Plumbing.cs`
- Prompt captured at `SessionRunner.cs` line 131 (before `BuildPrompt()`) — this already exists, just redirect to new path
- Transcript captured from raw log NDJSON (currently `session-{N}.jsonl`) — read and format as markdown, then delete the flat file
- Verdict from `VerdictEngine.EvaluateSessionAsync()` output
- Handover from tracker's `HandoffBlock`
- Cost from `SessionRecord`'s `CostUsd`, `TokensInput/Output/Reasoning/CacheRead`

**Directory naming:** `sessions/<NNN>-<stage-id>/` where NNN is zero-padded 3-digit session number and `stage-id` is the stage identifier from the plan (e.g., `sessions/001-M1/`, `sessions/015-M2/`).

**INDEX.md format:**
```markdown
# Session Index
| # | Stage | Kind | Started | Outcome | Cost | Dir |
|---|---|---|---|---|---|---|
| 001 | M1 | Deliver | 2026-07-12T... | Advanced | $0.42 | [001-M1/](001-M1/) |
```

Appended to on each session end.

**Truth gate:** After a 2-session toy run, both directories exist, `INDEX.md` links them, and
`prompt.md` byte-matches what the agent actually received.

**Files to create:**
| File | Purpose |
|---|---|
| `Core/SessionArtifacts.cs` | Write per-session directory + INDEX.md |

**Files to modify:**
| File | Change |
|---|---|
| `Core/Orchestration/SessionRunner.cs` | Write prompt to sessions/ dir; remove transcript.jsonl logging |
| `Core/Orchestration/RunLoop.Plumbing.cs` | Call SessionArtifacts.Write after EmitSessionFinished |
| `Core/Orchestration/VerdictEngine.cs` | Return/pass verdict data for artifact writing |
| `Core/Hosting/ConductorHost.cs` | Remove TranscriptLog registration |

**Files to delete:**
| File | Replaced by |
|---|---|
| `Core/Events/TranscriptLog.cs` | Session directory per-session transcript |

**Edge cases:**
- **Directory already exists** (crash recovery): Overwrite
- **Prompt capture timing**: Captured BEFORE spawning agent process — must not be deferred
- **Transcript empty** (agent crashed before output): Write empty transcript.md with note
- **Very long transcripts**: Cap formatted version at 1MB; always keep raw NDJSON in the directory
- **INDEX.md race**: Single writer (orchestrator), no race possible

**Risk: Low.** Additive feature. New files, minimal changes to existing code.

---

### M2.5 — Accurate cost and token accounting

**What changes:**
- Add "advisor" category to costs table (currently only "agent" and "gate")
- Ensure advisor invocations record their cost
- Gate overhead wall_ms accuracy: currently `OverheadCostPerSecond * wallTime` — verify this is correct
- Make `conductor report --query "SELECT stage_id, SUM(cost_usd) FROM costs GROUP BY stage_id"` work correctly

**costs table schema (updated):**
```sql
CREATE TABLE costs (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id          TEXT NOT NULL,
    session_number  INTEGER NOT NULL,
    stage_id        TEXT,           -- NEW: stage for rollup queries
    category        TEXT NOT NULL,  -- "agent", "gate", "advisor"
    tokens_in       INTEGER NOT NULL DEFAULT 0,
    tokens_out      INTEGER NOT NULL DEFAULT 0,
    tokens_think    INTEGER NOT NULL DEFAULT 0,
    tokens_cache    INTEGER NOT NULL DEFAULT 0,
    cost_usd        REAL NOT NULL DEFAULT 0,
    wall_ms         INTEGER NOT NULL DEFAULT 0
);
```

Note: Add `stage_id` column for per-stage rollup. Migration v6.

**Truth gate:** Toy run's `costs` rows sum to the ticker's total; `conductor report --query`
answers "cost of stage X".

**Files to modify:**
| File | Change |
|---|---|
| `Core/Store/Migrations/v6_costs_stage.sql` | ALTER TABLE costs ADD COLUMN stage_id |
| `Core/Store/SqliteRunStore.Sessions.cs` | RecordCost includes stage_id |
| `Core/Orchestration/RunLoop.Plumbing.cs` | Pass stage_id to RecordCost |
| (no other files)

**Risk: Low.** Schema migration + one additional column on existing writes.

---

## 2. Edge cases (cross-cutting)

### Kill -9 during DB write
SQLite WAL mode guarantees atomic commits. Either the transaction commits fully or it doesn't.
No partial rows, no torn state. This is a fundamental SQLite property and why WAL mode is used.

### Kill -9 during event batch
The channel drain thread batch-INSERTs events in a transaction. Kill -9 mid-batch: the
transaction rolls back, events since the last committed batch are lost. This matches current
`events.jsonl` behavior (torn lines after last flush). Acceptable — the orchestrator detects
missing events and re-emits what it needs on restart.

### Resume finds no interrupted session
Load run_state → Status is Idle → proceed normally. Fresh start.

### Resume finds multiple interrupted sessions
Take the highest-numbered session with `ended_utc IS NULL`. This matches current behavior
(FindInterruptedSession picks the highest unmatched SessionStarted).

### Concurrent readers during writes
WAL mode supports concurrent reads. Control plane reads events while orchestrator writes.
This was proven working with `EventLogTests` (concurrent reader test). The events table
in WAL mode provides equivalent guarantees.

### Existing run.db without events/run_state tables
Migration v5 adds them. If the DB is at v4 (current), migrations v5 (and v6 for M2.5) run on
next open. Seamless.

### Existing state.json from old runs
Per MAESTRO-PLAN: "No backward compatibility." On startup, if no `run_state` row exists
but `state.json` is on disk, the migration path is:
1. Load RunState from state.json (one-time)
2. Save to run_state table
3. Rename state.json to state.json.migrated
This is a one-time bridge. After M2, state.json is never read again.

### Large event payloads
`ConductorEvent` → JSON → TEXT column. SQLite TEXT max is 1GB (default). TokenDelta events
are tiny. SessionFinished events with commit lists are <10KB. The largest events are
transcript lines (~few KB). No practical size issue.

### Session directory naming collision
If two sessions in the same stage share the same number (e.g., a resumed session with the
same number), the directory is overwritten. This is correct — the latest artifacts are the
truth for that session number.

### Orphaned .conductor/logs/ files
After M2.4, `session-*.prompt.md` and `session-*.jsonl` in `logs/` are no longer written.
Existing files remain as history but new files go to `sessions/`. The `logs/` directory still
holds `conductor-*.log` (Serilog structured logs) — those are NOT affected.

---

## 3. Test strategy

### Unit tests (fast, no process spawning)
- `MigrationRunnerTests` — applies v1 through v5, verifies schema version
- `SqliteRunStoreTests` — every write method → read-back round-trip
- `SqliteRunStoreEventTests` — AppendEvent → ReadAllEvents round-trip
- `SqliteRunStoreStateTests` — LoadRunState/SaveRunState round-trip
- `SqliteRunStoreQueryTests` — typed query methods return correct data
- Schema byte-identical test (M2.1 truth gate)

### Integration tests (tagged `[Trait("Category", "Integration")]`)
- `HarnessTests` — full orchestrator cycle with SqliteRunStore (already exists, adapt to IRunStore)
- Resume test: kill -9 during toy run, verify restart from run.db alone (M2.3 truth gate)
- Session directory test: 2-session toy run, verify directories + INDEX.md (M2.4 truth gate)
- Cost rollup test: toy run, verify costs sum correctly (M2.5 truth gate)

### Architecture tests
- Extend `ArchitectureTests.cs`: no file outside `Core/Store/` may import `Microsoft.Data.Sqlite`
- If a file outside `Core/Store/` uses `SqliteConnection`/`SqliteCommand` → test failure

### gates (fast tier)
- `dotnet build Conductor.slnx` — must pass
- `ratchet.ps1` — no test count decrease, no ceiling increases

---

## 4. Implementation order

```
Phase 1: Foundations (M2.1)
  ├── Create migrations/ directory + .sql files (extract from CreateSchema+MigrateFrom)
  ├── Create MigrationRunner.cs
  ├── Wire MigrationRunner into RunDb.EnsureSchema()
  ├── Add schema byte-identity test
  └── Verify: dotnet test --filter "Category!=Integration" → green

Phase 2: IRunStore + SqliteRunStore (M2.2 start)
  ├── Create IRunStore interface
  ├── Create SqliteRunStore partial classes (start with no consumers)
  ├── SqliteRunStore implements IEventSink (events table writes)
  ├── All existing RunDb unit tests → SqliteRunStore tests (parallel, same logic)
  └── Verify: new tests pass alongside existing tests

Phase 3: Cut over consumers (M2.2 continue + M2.3)
  ├── Switch DI in ConductorHost (single SqliteRunStore instance as IRunStore + IEventSink)
  ├── Orchestrator side: RunContext, RunLoop, VerdictEngine, GateOrchestrator, etc.
  ├── Control plane side: ControlPlaneServer + Endpoints
  ├── MCP side: McpTaskServer + Handlers.Queries
  ├── Telegram side: TelegramService + Messages
  ├── Reporter side: Reporter
  ├── CLI commands: RunCommand, ReportCommand, StatusCommand, etc. (12 commands)
  ├── Delete old files: RunDb.cs, EventLog.cs, StateProjectionParity.cs
  ├── Crash recovery: switch to IRunStore queries
  └── Verify: full test suite green

Phase 4: Session directories (M2.4)
  ├── Create SessionArtifacts.cs
  ├── Wire into EmitSessionFinished
  ├── Redirect prompt + transcript writes
  ├── Delete TranscriptLog.cs
  └── Verify: 2-session toy run + directory check

Phase 5: Cost accuracy (M2.5)
  ├── Add stage_id to costs table (migration v6)
  ├── Add "advisor" category to RecordCost + verify
  └── Verify: cost rollup test

Phase 6: Truth gates
  ├── Kill -9 resume test (M2.3)
  ├── Session directory structure test (M2.4)
  ├── Cost rollup accuracy test (M2.5)
  └── Final: dotnet test Conductor.slnx → all green
```

---

## 5. Risk register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Consumer migration breaks resume | Medium | High | Dual-run tests comparing old RunState vs new IRunStore reconstruction |
| Event serialization differs from file to table | Low | Medium | Same JsonSerializerContext used for events table JSON as for events.jsonl |
| Concurrent write (event + session) in same transaction | Low | Medium | Events enqueued via channel; drain thread handles batching separately from session writes |
| Architecture test prevents "SQL outside Store" too early | Medium | Low | Add architecture test LAST, after all consumers switched |
| Migration fails on existing run.db | Low | High | Test migration v4→v5 with real toy-run databases |
| kill -9 on Windows behaves differently than Linux | Medium | High | Use `taskkill /F /PID` on Windows (equivalent to SIGKILL); verify WAL recovery |
| CLI commands break (no DI, manual construction) | High | Medium | CLI commands that create their own RunDb must create SqliteRunStore; keep API compatible |

---

## 6. Non-goals (explicitly out of scope for M2)

- Deleting `state.json` file I/O methods from `RunState` class (cleanup in a follow-up — just stop calling them)
- Query optimization (indexes, query plans)
- Per-session event partitioning
- Concurrent run.db access from multiple Conductor instances (explicitly single-writer)
- Migration rollback (migrations only go forward)
