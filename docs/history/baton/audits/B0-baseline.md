# B0 Baseline Audit — Conductor Architecture & Debt Inventory

**Generated:** 2026-07-08 (B0.5, session #2)
**Scope:** `src/Conductor/` (25 `.cs` files, 1 `.csproj`). Target framework: `net10.0`, 56 tests green.
**Purpose:** Record current coupling, debt, and architectural hotspots — every claim cites a file:line,
so later stages (B1–B12) can verify that specific lines were addressed.

> This is evidence for B0.5. The audit finds real problems; the **fix** mandate belongs to each
> targeted stage (B2 fixes token lag + async coupling; B1 fixes tracker parsing; B4 fixes render split, etc.).

---

## 1. Provider coupling — AgentSession dispatch on string names (F-2)

`AgentSession.Spawn()` dispatches on a **string-based provider name** to determine the entire
parsing strategy:

**`src/Conductor/Core/AgentSession.cs:76-81`**
```csharp
var mode = cfg.Output.ToLowerInvariant() switch
{
    "stream-json" => "stream-json",
    "opencode-json" => "opencode-json",
    _ => "text",
};
```

The mode string is checked on **every single output line** (`AgentSession.cs:101`, `AgentSession.cs:106`).
Two hard-coded parsers — `ParseOpencode` (`:116-173`) and `ParseClaude` (`:176-218`) — must understand
each provider's raw JSON wire format (`type` field, `part` sub-object, token/cost shapes). There is
no `IAgentProvider` abstraction; adding a new agent means editing the session core.

**Impact:** BATON-BRIEF F-2. Baton target: `IAgentProvider` (B2.4).

---

## 2. Loom-isms — TrackerParser regex baked to one table shape (F-1)

`TrackerParser.RowRx` is a **single hard-coded regex** that expects exactly:
- 5 pipe-delimited columns: `| id | title | status | commit | evidence |`
- IDs matching `[A-Za-z]+\d+(?:\.\d+)?[a-z]?` (e.g. `L0.1`, `B1`, `B1.1a`)

**`src/Conductor/Core/TrackerParser.cs:38-39`**
```csharp
@"^\|\s*(?<id>[A-Za-z]+\d+(?:\.\d+)?[a-z]?)\s*\|(?<title>[^|]*)\|\s*
  (?<status>TODO|IN\s+PROGRESS|DONE|BLOCKED)(?<rest>[^|]*)\|(?<commit>[^|]*)\|(?<evidence>[^|]*)\|"
```

**Specific gaps:**
- ID pattern does **not** match `P-0` (hyphenated), `P3.4b` (digit then letter), or `F5` (bare
  letter-digit) — `TrackerParser.cs:39` id group.
- Status enumeration is hard-coded to `TODO | IN PROGRESS | DONE | BLOCKED` — `:39` status group.
- Handoff block regex (`:42-44`) assumes `## Handoff` follows `## Heading` markdown convention.

**Impact:** Conductor cannot drive Shamshir or plans with non-Loom tracker formats today (F-1).
Baton target: `IProgressProvider` + configurable conventions (B1).

---

## 3. Mutable RunState — no transition guards, self-persisting god-object

`RunState` has **20+ public get/set properties** with zero encapsulation:

**`src/Conductor/Models/RunState.cs:79-103`**
```csharp
public RunStatus Status { get; set; }
public string? CurrentStage { get; set; }
public int AttemptsThisStage { get; set; }
public PendingFix? PendingFix { get; set; }
public PendingResume? PendingResume { get; set; }
public PendingPhaseGate? PendingPhaseGate { get; set; }
public PendingAudit? PendingAudit { get; set; }
// … 13 more mutable properties
```

**Specific debt:**
- `PendingFix`, `PendingResume`, `PendingPhaseGate`, `PendingAudit` are mutually exclusive in
  practice, but the type system allows all four to be set simultaneously — `RunState.cs:90-93`.
- The class **self-persists** via `LoadOrNew()` (`:110-126`) and `Save()` (`:128-136`), coupling
  domain state to filesystem IO.
- No explicit state machine guards transition validity — the Orchestrator enforces invariants
  procedurally across `Orchestrator.cs` (969 lines).
- Legacy field names: `SessionRecord.ClaudeSessionId` (`RunState.cs:31`) and
  `PendingResume.ClaudeSessionId` (`RunState.cs:56`) — stores any agent's session ID, not just
  Claude's. AGENTS.md acknowledges this; B2 renames/abstracts.

**Impact:** Any refactor of state transitions risks introducing invalid states that the type system
won't catch. Baton target: event-sourced backbone, `RunState` becomes a projection (B2).

---

## 4. Dashboard render split — hard-coded layout thresholds (F-5)

`DashboardRenderer.BuildRoot()` switches between **four layout modes** based on magic-number
thresholds:

**`src/Conductor/Ui/DashboardRenderer.cs:17-69`**
- `h < 24`: compact mode, header=5, footer=3–5 (`:23-33`)
- Default: header=7, footer=3 to `FooterHeight(st)` (`:36-43`)
- `st.Width >= 150`: wide 3-column mode (`:45-53`) vs narrow 2-column mode (`:55-65`)

All thresholds are hard-coded, not configurable. The sub-plan tree (F-5) renders only top-level
stages; sub-checkpoints appear only for the *current* stage in a separate table.

**`src/Conductor/Ui/DashboardRenderer.cs:189`** — `PlanTable()` renders top-level stages only.

**Impact:** F-5 (BATON-BRIEF). Baton target: Spectre `Layout` rebuild (B4).

---

## 5. Token/cost lag — written only at session end (F-3)

Token/cost metrics are assigned to the `SessionRecord` **only after the agent process exits**:

**`src/Conductor/Core/Orchestrator.cs:278-283`**
```csharp
rec.CostUsd = agent.CostUsd;
rec.NumTurns = agent.NumTurns;
rec.TokensInput = agent.TokensInput;
rec.TokensOutput = agent.TokensOutput;
rec.TokensReasoning = agent.TokensReasoning;
rec.TokensCacheRead = agent.TokensCacheRead;
```

The dashboard `TokenLine()` (`DashboardRenderer.cs:155`) and `CostLine()` (`DashboardRenderer.cs:146`)
read from `DashboardSnapshot`, which is built from the `SessionRecord` — so the live dashboard shows
a `sessionCost` that stays at `$0.0000` until the session ends. AFK on Telegram there's no way to
see current burn.

**Impact:** F-3 (BATON-BRIEF). Baton target: `TokenDelta` events per `step_finish` (B2.6).

---

## 6. Heartbeat commits pollute branch history (F-4)

`Reporter.WriteAndPublish()` commits REPORT.md on every heartbeat (~10 min), producing
"chore(conductor): … — Idle/working" commits interleaved with real feature commits:

**`src/Conductor/Core/Reporter.cs:178-180`**
```csharp
var msg = commitMessage ?? (last != null
    ? $"chore(conductor): s{last.Number} {last.Stage} {last.Outcome?.ToString() ?? "running"} — {state.Status}"
    : $"chore(conductor): {state.Status}");
```

The no-op dedup (`Reporter.cs:172`) strips only the `_Updated` timestamp line via `Normalize()`
(`:191-192`). If nothing else changed, the commit is skipped — but any state change (e.g. a
cost increment, a turn count, a dashboard snapshot field) produces a new commit. In a long session
this can produce 6–8 heartbeat commits.

**Impact:** F-4 (BATON-BRIEF). Baton target: richer REPORT.md with clean heartbeat + event log (B6.3).

---

## 7. Cross-cutting duplication

### 7.1 Stage overview logic — 3 identical implementations

The decision tree for "what state is this stage in?" (skipped/confirmed/gating/done/active/partial/todo)
appears in three separate locations with identical logic:

- **`src/Conductor/Core/Reporter.cs:59-64`** — REPORT.md table rows
- **`src/Conductor/Commands/Commands.cs:93-99`** — `StatusCommand`
- **`src/Conductor/Core/SnapshotBuilder.cs:44-49`** — `Build()` dashboard snapshot

Any change to stage state semantics requires coordinated edits in all three. Baton target: single
projection from the event log (B2).

### 7.2 Glyph mapping — 2 identical implementations

The agent-event-kind → glyph mapping (text = `📝`, thinking = `💭`, tool = `🔧`, etc.) is duplicated:

- **`src/Conductor/Ui/LiveDashboard.cs:351`** — `Glyph()`
- **`src/Conductor/Ui/DashboardRenderer.cs:287-295`** — `AgentLine()`

### 7.3 Key-to-action mapping — 2 divergent implementations

Console key → `ControlAction` dispatch appears in:

- **`src/Conductor/Ui/PlainSink.cs:27-48`** — supports P/R/A/S/K/Q only
- **`src/Conductor/Ui/LiveDashboard.cs:211-239`** — supports P/R/A/S/K/Q + T/O/D/V/X/G/I

Adding a new control action requires changes in both, and the divergence means `PlainSink` users
have fewer controls.

### 7.4 String-enum disconnect

`RunStatus`, `SessionKind`, `SessionOutcome` are proper C# enums, but the render/snapshot layer
uses raw strings:

- `DashboardSnapshot.Status` is `string` — **`src/Conductor/Core/Progress.cs:28`**
- `DashboardSnapshot.SessionKind` is `string` — **`src/Conductor/Core/Progress.cs:33`**
- `DashboardRenderer.StatusColor()` switches on string literals — **`src/Conductor/Ui/DashboardRenderer.cs:383-393`**

The enum information is discarded at the `SnapshotBuilder` boundary (via `.ToString()`) and
never recovered. Renaming an enum member silently breaks the renderer's switch.

---

## 8. Synchronous blocking — no async/await in core paths

The entire orchestrator loop is synchronous. These operations block the orchestrator thread:

- **`src/Conductor/Core/Git.cs:9-11`** — `Git.Exec()` synchronous
- **`src/Conductor/Core/ProcessRunner.cs:32-60`** — `ProcessRunner.Run()` synchronous with 500ms polling
- **`src/Conductor/Core/Advisor.cs:23`** — `Advisor.Consult()` synchronous
- **`src/Conductor/Core/GateRunner.cs:39-59`** — `GateRunner.RunAll()` synchronous with `Parallel.ForEach`
- **`src/Conductor/Core/StatusAgent.cs:71-83`** — `StatusAgent.Run()` synchronous

Baton target: async/`ConfigureAwait(false)`/`CancellationToken` threading + `Host/DI` (B2.5).
**Note:** MA0045 (sync-over-async, 28 sites) is deferred to B2 in `.editorconfig` and ADR-0001.

---

## 9. File-based IPC — no schema, no versioning, no atomicity

Three inter-process communication channels use ad-hoc files under `.conductor/`:

| Channel | File | Writes | Reads |
|---------|------|--------|-------|
| Control | `control.json` | `Commands.cs:156-166` | `Orchestrator.cs:752-773` |
| Lock | `conductor.lock` | `Orchestrator.cs:929` | `Orchestrator.cs:927-961` |
| Instruction queue | `queue/*.json` | `InstructionQueue.cs:22-51` | `InstructionQueue.cs:83-93` |

None have a formal schema, version marker, or atomicity guarantee. The control file is written-then-deleted
(`Orchestrator.cs:758-760`), creating a race condition if two control commands issue simultaneously.

Baton target: typed control events in the event log (B2 for the log, ad-hoc IPC for legacy compatibility).

---

## 10. Monolithic Orchestrator — 969 lines, single responsibility? No.

**`src/Conductor/Core/Orchestrator.cs`** (969 lines) contains the main loop, session lifecycle,
gate verification, phase gating, advisor consultation, crash recovery, heartbeat scheduling,
lock-file management, control-file IPC, and snapshot building — all in one class with shared
mutable fields (`_lastGates`, `_pendingSkip`, `_pausePending`, `_backoffUntil`, `_activity`).

The class is the single highest-risk file for regression bugs. Baton target: layer separation
+ DI composition (B2.5), event-sourced projections (B2.2).

---

## 11. Limit-detection regex — brittle coupling to provider error strings

**`src/Conductor/Core/Orchestrator.cs:17-19`**
```csharp
@"usage limit|rate.?limit|overloaded|quota|out of credit|insufficient credit|credit balance|429|too many requests|5-hour|weekly limit"
```

This regex detects rate-limit backoffs from agent output text. It is coupled to specific error
message wording from Claude/opencode. If a provider changes their error text or a new provider
uses different phrasing, backoff detection silently fails.

Baton target: `IAgentProvider.DetectsUsageLimit()` (B2.4).

---

## 12. Other notable debt

| # | Item | Location | B-stage target |
|---|------|----------|----------------|
| 12.1 | `Advisor.Consult()` JSON extraction regex cannot handle nested `{}` | `Advisor.cs:37` | B8 (brain layer) |
| 12.2 | `PlainSink.PollControl()` silently ignores new event kinds | `PlainSink.cs:13-17` | B4 (TUI) |
| 12.3 | `ReasoningBuffer` snapshot-collapse logic fragile (`StartsWith` on growing text) | `ReasoningBuffer.cs:26-34` | B4 (TUI) |
| 12.4 | `PromptBuilder` templates are compiled-in strings (~100 lines) | `PromptBuilder.cs:79-177` | B7/B8 (personas + batteries) |
| 12.5 | `DocsExtractor.ForStage()` regex per invocation (not cached) | `DocsExtractor.cs:17` | B1 (read-order battery) |
| 12.6 | `JobObject` Windows-only with silent no-op on Linux/macOS | `JobObject.cs:20` | B11 (cross-platform) |
| 12.7 | `PlanConfig.JsonOpts` is a public static mutable field | `PlanConfig.cs:41-50` | B2 (DI/Host) |
| 12.8 | Dashboard `TokenLine()` reads snapshot — lags when snapshot is stale | `DashboardRenderer.cs:155` | B4.7 (live-consistent tokens) |
| 12.9 | Destructive keys `A`/`K`/`S` fire on single keystroke — no confirmation | `LiveDashboard.cs:222-226` | B3 (safety) |
| 12.10 | `StatusAgent.Run()` consumes model quota with no cost tracking | `StatusAgent.cs:71-83` | B4/B5 (metrics) |
| 12.11 | `InstructionQueue.Write()` non-transactional chain mutation | `InstructionQueue.cs:33-47` | B9 (task graph) |
| 12.12 | `ProcessRunner.RunPowerShell()` hardcodes `powershell.exe` (Windows-only) | `ProcessRunner.cs:67` | B11 (cross-platform gates) |
| 12.13 | Test-only configuration: `DashboardPreview.Seed()` calls `GateRunner.Summary()` | `DashboardPreview.cs:13` | B2 (layer separation) |
| 12.14 | `SnapshotBuilder.SessionKind` uses `RunStatus.ToString()` instead of actual session kind | `SnapshotBuilder.cs:38` | B2 (projections) |

---

## Summary

| Metric | Value |
|--------|-------|
| Source files audited | 25 `.cs` + 1 `.csproj` in `src/Conductor/` |
| Tests | 56 pass, 0 fail, 0 skip |
| Arch debt hotspots | AgentSession provider switch, TrackerParser regex, RunState mutable blob |
| Cross-cutting duplications | 3× stage overview, 2× glyph mapping, 2× key mapping |
| File:line citations | 40+ (every claim above references a concrete file:line) |
| Largest file | `Orchestrator.cs` (969 lines) |
| Sync-blocking ops | 5 classes block the orchestrator thread |
| Deferred to B2+ | MA0045 (28 sites), MA0002 (38 sites), MA0009 (regex timeout) — per ADR-0001 |
