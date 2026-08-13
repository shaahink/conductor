# AGENTS.md — what a session needs to know before it edits anything

Current state only. The append-only stack of nine superseded `## Resume here` sections that used to
live here is archived verbatim at
[`docs/history/handoffs/AGENTS-resume-log-2026-07.md`](docs/history/handoffs/AGENTS-resume-log-2026-07.md)
— nothing was deleted, it was moved so that this file is short enough to actually be read.

## What this is

Conductor is an autonomous multi-session engineering orchestrator (C# / .NET 10). It spawns headless
agent sessions, verifies their work independently (gate battery + git commits + tracker diff + a
Verifier pass), is fully resumable, and reports to `.conductor/REPORT.md`. It drives itself: every era
of this repo since Baton has been built by Conductor running a plan against Conductor.

The Face is `face-go/` — a Go + Bubble Tea TUI over the engine's HTTP+SSE control plane. It is a
disposable client: the run outlives it. (The TypeScript + Ink face was retired in Maestro M7; its
history is in git, do not re-add it.)

**The architecture map is [`ARCHITECTURE.md`](ARCHITECTURE.md)** — the assemblies and the one allowed
dependency direction, a session's lifecycle end to end, the seams, the two surfaces, the
file-organisation convention, and a "where do I add X" table. Read it before your first edit.

## The live era: Karvan

The active plan is **Karvan core — the engine knows what it did and what it cost**.

| | |
|---|---|
| Spec | `docs/history/CONDUCTOR-KARVAN.md` (mission, per-stage acceptance) |
| Plan | `plans/karvan/core.plan.json` |
| Tracker | `plans/karvan/CORE-TRACKER.md` — the `## Handoff` block is the whole handover between sessions |
| Branch | `feat/karvan` |

Era artefacts live one folder per era, as `plans/karvan/` does. Closed-era trackers are under
`docs/history/trackers/`; closed-era specs under `docs/history/`.

## Read order for a fresh session

1. `ARCHITECTURE.md` — the map, and the file-organisation convention reviewers cite.
2. `plans/karvan/CORE-TRACKER.md`, `## Handoff` block — what the last session left you.
3. Your stage's section of `docs/history/CONDUCTOR-KARVAN.md` — that section, not the whole document.
4. `docs/operating.md` — driving a live run: commands, steering, the control plane, escalation.
5. This file's standards sections below, before you write C#; `face-go/STYLE.md` before you touch the Face.

## Traps this repo has already paid for

- **`.conductor/` in this repo is the live state of the run driving you.** The claim and note verbs
  (`conductor task`, `note`, `bug`, `bg`) and read-only verbs (`status`, `task --list`) are yours.
  Never aim a run-control verb (`run`, `pause`, `resume`, `abort`, `approve`, `plan set/reload`, `goto`,
  `skip`, `retry-stage`, `rollback`, `kill`) at this repo. Live proofs go in a scratch repo with its own
  plan, its own state dir and its own port.
- **`CONDUCTOR_PLAN` is set in a session's environment and points at THIS repo's plan.** A scratch
  working directory is not isolation: `conductor doctor` run from one still opened this repo's `run.db`
  and migrated it, after which every claim verb from the installed engine refused with
  `schema version is newer`. Clear it and pass `--plan` explicitly. KS0.3 put an unambiguous cwd ahead
  of the variable and made the override warn, but the engine on PATH is the published one until the
  owner reinstalls, so keep passing `--plan` in rigs.
- **`conductor` on PATH is the published engine driving you, not your working tree.** Exercise your
  changes through `dotnet run --project src/Conductor -- <verb>`. Never run `tools/install.ps1`
  mid-era — the owner reinstalls between plans.
- **Another repo's conductor run may share this machine.** Never kill a `conductor.exe`,
  `conductor-face.exe` or stray `dotnet` process by name or by a pid you have not identified with
  `Get-CimInstance Win32_Process`. `locked by: conductor (PID)` in a build error is almost always the
  run that spawned you holding its own binary.
- **A literal brace token in a plan template kills the engine.** Template text is validated for
  unresolved placeholders and the refusal goes to stderr only. After any template edit, sweep it: only
  real placeholder names may appear, and a doubled brace emits a literal one.
- **Anything over ~3 minutes runs under `conductor bg`.** A silent foreground command reads as a stall
  to the watchdog; a session was killed at 15 minutes of silence doing perfectly good work.
- **PowerShell tooling targets Windows PowerShell 5.1 and stays ASCII-only** — em-dashes have torn
  string literals here. Go sources are checked out LF; keep them that way.
- **Never weaken a measurement to get green.** No deleted or skipped tests, no relaxed expectations, no
  raised ratchet ceiling, no golden re-declared to match broken output. If a bar is genuinely wrong,
  say so in the handoff with evidence and stop. The ratchet gate is `tools/gates/ratchet.ps1` — the
  wrong path prints an error and exits 0, a silent false green.

## How to verify a face-go change

`go build ./... && go vet ./... && go test ./internal/<pkg>/...` is necessary and **not sufficient** —
a face change is not verified until it has been driven. Run the real control plane (or `--demo`) and
capture what a reviewer would see. Goldens pin UTC and live in `face-go/internal/tui/testdata/golden`;
regenerate them in a separate rebaseline commit. Pad plain text then style — never width-format an
ANSI string — and clamp with `MaxWidth`/`MaxHeight`.

## Go Face v2 — quick start
```powershell
cd face-go
go build -o bin/conductor-face.exe ./cmd/conductor-face/
.\bin\conductor-face.exe --demo          # offline synthetic data
.\bin\conductor-face.exe                  # live against conductor --control-plane (http://127.0.0.1:4317)
```
Needs a real interactive terminal (exits with a clear message otherwise) — if you're an agent
running this from a sandboxed shell tool with no real TTY (the common case), set
`FACE_FORCE_TTY=1` to bypass the check when you just need to confirm it doesn't crash; that
doesn't make output actually render anywhere you can see it, though — see "How to verify a
face-go change" for how to actually check a change without a TTY.

### Dependency gotcha: `go.mod` needs `x/cellbuf` pinned above what MVS picks by default
Adding `github.com/charmbracelet/glamour` pulls in `charmbracelet/x/cellbuf` (via glamour's
`lipgloss v1` dependency) at a version whose compiled API (`ansi.Style` methods) doesn't match
the newer `charmbracelet/x/ansi` that `bubbletea v2`/`ultraviolet` already require elsewhere in
the graph — a **real build failure**, not a hypothetical one (`b.Italic()` "not enough
arguments", `b.SlowBlink undefined`, etc.). Fixed by explicitly bumping `x/cellbuf` to `v0.0.15`
(`go get github.com/charmbracelet/x/cellbuf@v0.0.15`), which does implement the newer API. If a
future `go get -u` / `go mod tidy` ever re-triggers this, the fix is the same: bump `x/cellbuf`
to its latest, don't downgrade `x/ansi`.

## Face architecture (`face-go`)
- **Language:** Go 1.26
- **Framework:** Bubble Tea v2 (Elm Architecture) + Lip Gloss v2 (styling)
- **Layout (v3 "dashboard", 2026-07-15 redesign):** top bar · tab strip · **[always-on sidebar | content pane]** · bottom bar. Everything that used to be a modal is now a **tab** in the content pane (Agent · Sessions · Timeline · Procs · Console · Templates · Plan · Report), one keypress away, with the plan sidebar always beside it (collapse with `p`). The only floating things are the command palette, the help card, and toasts — composited **transparently** over the live dashboard via lipgloss v2's `Compositor`/`Layer` (never opaque `lipgloss.Place`). Transient input (palette, inject, goto, search, confirm) is a **bottom command bar**, not a boxed modal. **The design language is authoritative in `face-go/STYLE.md` — read it before any face-go change and keep new work consistent with it (owner directive: future plans follow the new Go style).** Palette Catppuccin Mocha, defined once in `widgets/style.go`.
- **Data:** Same HTTP+SSE API as the Ink TUI (9 endpoints on localhost:4317), including `?since=` resume-on-reconnect for both SSE streams (server-supported, `ControlPlaneServer.Endpoints.cs` `ParseSince`)
- **Tests:** `go test ./...` — all packages pass; `internal/tui/update_test.go` covers the control-plane wiring (palette send/confirm/goto, inject guard, report query, template read/write round-trip, processes nav, transcript search); `internal/tui/anim_test.go` covers the toast spring animation (starts at 0, arms/re-arms/stops the ticker correctly, settles within a bounded tick count); `internal/tui/markdown_test.go` covers Glamour rendering (empty passthrough, markdown syntax stripped, never errors on plain text); `internal/tui/golden_test.go` renders `View()` headlessly (no real TTY needed) against fixed demo state and diffs it against `testdata/golden/*.golden` — `go test ./internal/tui/ -run TestGolden -v` prints every frame as plain text, `-update` refreshes the goldens after an intentional layout change. Mirrors the Ink side's `face/tests/golden.test.tsx`.

### How to verify a face-go change (no real TTY needed — read this before claiming something works)
A Bubble Tea program renders via ANSI escapes to a real terminal; running the binary in a headless
agent session and grepping its stdout tells you nothing (you'll just see raw escape codes). Two
techniques close that gap, both added this session because build/vet/test alone missed real bugs:

**1. Golden rendering (`internal/tui/golden_test.go`) — layout/rendering correctness, in-memory, ~instant.**
Drives `Update()` directly with synthetic `tea.KeyPressMsg`/`tea.WindowSizeMsg` against a fixed,
deterministic `fakeSource`, captures `View().Content`, strips ANSI, and diffs against
`testdata/golden/*.golden`.
- `go test ./internal/tui/ -run TestGolden -v` — prints every frame as plain text under `-v`,
  regardless of pass/fail. This is how you actually *see* a frame without a TTY.
- If you changed `view.go` / `widgets/*`, goldens **will** fail — that's expected, not a red flag.
  Read the printed frame and confirm the new output is correct, not just different.
- `go test ./internal/tui/ -run TestGolden -update` refreshes the goldens once you've confirmed
  the new output is right, then re-run without `-update` to confirm it now passes.
- Add a new scenario by appending a `{name, do}` case to `golden_test.go`'s `cases` slice, driving
  it through real exported `Msg` types and `keyMsg`/`specialKey`/`ctrlKey` — never by poking
  unexported `Model` fields directly, so the test exercises the real interaction path.
- Fixtures must be fully deterministic — no bare `time.Now()`-derived values (see the `ExitedUtc`
  comment in `golden_test.go`: process runtimes are relative-to-now for genuinely alive processes,
  so the fixture pins exact start+exit timestamps instead).
- This caught two real bugs this session: `RenderTicker`/`RenderFooter`/`RenderGateBar`/
  `renderTranscriptLine` were truncating already-ANSI-styled strings with a raw `s[:width]` byte
  slice — cuts mid-escape-sequence and corrupts everything after the cut point. Fixed by using each
  style's existing `.MaxWidth(width)` (lipgloss already truncates ANSI-safely via `ansi.Truncate`
  internally). And: **spaces were silently dropped from every text field app-wide** (inject content,
  template editor, custom SQL, transcript search, palette filter, goto stage id) — Bubble Tea v2's
  `Key.String()` deliberately returns `"space"` (a keybinding name) for the spacebar, not a literal
  `" "`, so every `len(key) == 1` guard excluded it. Fixed via a `typedChar()` helper at all six
  text-accumulation sites in `update.go`. Mirrors the Ink side's `face/tests/golden.test.tsx`.

**2. Live smoke test — real wire round-trip against a real `ControlPlaneServer`, no LLM spend.**
Golden tests only prove rendering is correct against data you made up; they can't catch a DTO field
name mismatch or a report-query SQL string that's wrong against the *real* SQLite schema (this
session's third bug: the "cost per stage" quick query referenced `costs.stage_id`, which doesn't
exist — `costs` only has `session_number`; fixed by joining `sessions` on `run_id`+`number`). To
verify against the real thing without spending on a real LLM session:

- `ControlPlaneServer` only exists inside `conductor run` (`RunCommand.cs` constructs it) — there is
  no standalone `--control-plane` CLI command. The fastest way to get a real one running is to copy
  the pattern from `tests/Conductor.Tests/ControlPlaneServerTests.cs`'s `StartServer()`: construct a
  minimal `PlanConfig` (Name/Repo/Tracker/Stages, `Repo` pointed at a scratch temp dir — never this
  worktree, since a real session spawn would `git commit` into it), a `RunState`, a
  `SqliteRunStore(tempDbPath, ...)`, an empty `ConcurrentQueue<ControlCommand>`, and
  `new ControlPlaneServer(plan, state, store, inbox, NullLogger.Instance, port).Start()`. That's a
  real `HttpListener` on a real loopback port — curl it or point `face-go --url` at it directly.
- Seed realistic data with the store's own write methods — `InitializeRun`/`InitializeStage`/
  `ConfirmStage`, `RecordSession`, `RecordCost`, `RecordGate`, `WriteScore`, `TrackPid`, plus
  `store.Emit(new RunStarted{...})` / `new StageEntered{...}` / `new GateFinished{...}` for the
  event-log-derived parts of `/state`, and a `TranscriptLog` for `/transcript/current`. Note `/state`'s
  `Gates`/`TotalCostUsd` are folded from the **event log**, not the `gates`/`costs` SQL tables directly
  — seeding only the SQL tables (for `/sessions`, `/scores`, `/processes`) without matching events
  will correctly leave `/state` showing zero cost / no gates. That's expected, not a bug.
- Write this as a throwaway xUnit `[Fact]` in `tests/Conductor.Tests/` (reuses the project's
  references — no new csproj needed) that starts the server, writes its port to a temp file, then
  `await Task.Delay(...)` for long enough to drive the Go side against it. Run it with
  `dotnet test ... --filter "FullyQualifiedName~YourTestName"` via a **background** shell command so
  it keeps running while you build/run the Go side.
- On the Go side, write a throwaway `_test.go` in `internal/tui/` that calls `api.NewLiveSource(url)`
  directly (not `fakeSource`) and exercises `FetchState`/`FetchTasks`/`FetchProcesses`/
  `FetchSessions`/`QueryReport`/`PostControl`/`PostInject` for real, then builds a real `Model`,
  calls `Init()`, drains its returned `tea.Cmd`/`tea.BatchMsg` tree by hand for a few seconds
  (`Init()`'s SSE subscriptions need real time to replay+deliver), and prints `stripANSI(m.View().Content)`
  — same technique as golden rendering, just against a live source instead of `fakeSource`.
- **Delete both scratch test files when done** — they're verification tooling, not permanent
  coverage (unlike `golden_test.go`/`update_test.go`, which are committed).

### Key files
| Path | Purpose |
|------|---------|
| `cmd/conductor-face/main.go` | CLI entry: --demo, --url, --host, --port, TTY guard, --help |
| `internal/api/` | HTTP client, SSE client (with since-resume), DTO types, demo data source |
| `internal/tui/update.go` | Message loop + global key routing only |
| `internal/tui/view.go` | Frame assembly (top bar, tab strip, sidebar, bottom bar, overlays) |
| `internal/tui/tab_*.go` | One file per tab: its key handler + its renderer (agent, sessions, timeline, processes, console, templates, report) |
| `internal/tui/plan.go` | The Plan editor tab (M6.3) |
| `internal/tui/cmdbar.go` | Palette / inject / search / help — the transient command layer |
| `internal/tui/anim.go` | Harmonica spring animation: toast entrance reveal; spinner tick lives in messages.go |
| `internal/tui/markdown.go` | Glamour markdown rendering for prose detail panes (session result summary) |
| `internal/widgets/` | Transcript (scroll/fold/search), sidebar (plan+gates+tasks), top bar, toasts, one palette (style.go) |
| `internal/templates/` | Direct filesystem read/write for the template editor (planDir on disk) |

### Keybindings (v3 dashboard)
**Tabs** (jump straight there — also `1`–`9`/`0`, or `tab`/`shift+tab` to cycle; `esc` returns to Agent).
`tabKey` in `model.go` is the single source of truth for this table:
| Key | Tab |
|-----|-----|
| `h` | **Home** (U1.1 landing + the startup tab: Server · Run · Workspace · Next steps, all from `/state`+`/plan`) |
| `a` | Agent (mission control: status strip + transcript; `f` fold, `↑↓` scroll, `end`/`l` live-tail) |
| `s` | Sessions (history + inline detail) |
| `t` | Timeline (`r` refresh) |
| `o` | Procs (supervised processes; `x` kills a live one after a confirm) |
| `c` | Console (raw agent stdout) |
| `e` | Templates (list + editor + `v` compiled-prompt preview, all on one page) |
| `p` | Plan editor (M6.3) — `←→` sections Stages·Gates·Settings·Import·Prompt; `n` add, `d` delete |
| `r` | Report / query console |
| `k` | Knowledge (M7: ledger + tracked bugs; `n` note, `b` bug, `x` resolve) |
| `g` | Telegram (M8.2 guided setup/status/test) |
| `b` | Kanban (G2.2 board; `←→` move, `n` add, `enter` card detail) |

Telegram and Kanban are past the digit row (`1`–`9` reach Home…Report, `0` reaches Knowledge) — they are
mnemonic/tab-cycle only.

**Actions** (bottom command bar / overlays):
| Key | Action |
|-----|--------|
| `:` | Command palette (13 verbs, filterable, destructive ones confirm, `goto` asks for a stage id) |
| `i` | Inject context (bottom bar: `tab` field, `ctrl+s` send) |
| `/` | Inline transcript search, Agent tab (enter: lock, n/N: next/prev, esc: clear) |
| `\` | Collapse / expand the plan sidebar (moved off `p` so Plan could take its natural mnemonic) |
| `?` | Help card (transparent overlay) |
| `q / ^C` | Quit |

### Development
```powershell
cd face-go
go fmt ./...           # format
go vet ./...           # lint
go test ./...          # test
go build -o bin/conductor-face.exe ./cmd/conductor-face/   # build
```


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
- **HTTP wire types are separate DTOs** (`Core/Http/Contracts/<feature>/`), not `DashboardSnapshot`
  directly — the TUI-rendering types carry `ValueTuple` fields System.Text.Json's source generator
  can't serialise. Mapping is a thin field copy; don't duplicate the actual computation.
- **Control plane is opt-in** (`RunOptions.ControlPlane` / `--control-plane` CLI flag, off by
  default) and a bind failure is caught + logged, never fatal — headless/no-flag runs must stay
  byte-identical whether or not it's enabled. Don't add a code path that assumes it's running.
- Still explicitly deferred (documented, not forgotten): `GET /transcript/current` (thinking-stream
  SSE — build with F6's agent pane, the first real consumer).
- **Lane coordination is cut out too** (chore/debt, pre-F6): `StartParallelAudit`/
  `RunFollowupFixLanesAsync`/`StartAnalysisLanes`/etc. now live in `Core/Lanes/LaneCoordinator.cs`,
  not Orchestrator. Same shape as ControlDispatcher — Orchestrator holds a lazily-constructed
  `Lanes` property and only decides *when* to call in; `LaneCoordinator` owns the parallel-audit
  worktree lane, fix-lanes, and the analysis-lane pool. New lane-shaped work goes there.

## Foreground-blocking anti-patterns (codebase-specific)

### Test filtering — use Category traits, not substring guessing
- Every test that spawns a real process/git repo (ProcessSupervisor*, MutatingLane* in
  `B12_3Tests.cs`/`B12_4Tests.cs`, `HarnessTests`, `GateRunnerTests`, `B11_1CrossPlatformShellTests`)
  carries `[Trait("Category", "Integration")]`, at class or method level. The one known-flaky test
  (`EventLogTests.ReadAllSucceedsWhileLiveWriterHoldsTheFile`, a live-file-handle race) carries
  `[Trait("Category", "Flaky")]`. Do not hand-maintain a substring filter list again — a prior
  version of this doc did (`FullyQualifiedName~FailureCircuitBreaker|...`) and it silently missed
  real integration tests whose names didn't match the listed substrings.
- **Fast dev loop** (measured ~8s for 583 tests): `dotnet test Conductor.slnx --filter "Category!=Integration&Category!=Flaky"`
- **Full suite** (measured ~21s for 639 tests as of the pre-F6 debt sweep — much faster than the
  "5+ min" this doc used to claim; re-measure if it regresses): `dotnet test Conductor.slnx`
- New tests that spawn a real process, real git repo, or sleep >500ms for a real OS event: add the
  `Integration` trait when you write them, not after the fact.
- Kill orphan dotnet processes from failed runs: `Get-Process dotnet -ea 0 | Stop-Process -Force`

### ProcessRunner has both sync and async entry points — use the right one
- **Closed (chore/debt, pre-F6):** `ProcessRunner.RunAsync`/`RunShellAsync`/`RunPowerShellAsync`
  now exist (`Process.WaitForExitAsync` instead of the old `WaitForExit(500)` polling loop).
  `GateRunner`/`Advisor` are fully async (`RunAllAsync`/`RunOneAsync`/`RunHookAsync`/
  `ConsultAsync`), and the whole Orchestrator call chain that reaches them (`RunGateBatteryAsync`,
  `ConsultAdvisorAsync`, `RunStageHookAsync`, `RunRemediationAsync`, `EvaluateSessionAsync`,
  `ApplyVerdictAsync`, `EscalateExhaustedStageAsync`, `ConfirmCompletionAsync`) awaits them —
  a multi-minute gate battery or advisor spawn no longer ties up the async run loop's thread-pool
  thread for its whole duration.
- **Use `RunAsync`/`RunAllAsync`/`ConsultAsync`** from any `async Task` engine method (the
  orchestrator loop, lanes, mutating-lane merge gates). **Use the sync `Run`/`RunAll`** only at a
  genuine CLI sync boundary with no concurrent async work to protect (`GateCommand.Execute`,
  `RecentCommits`, `RunAgent` in `Commands.cs`) — same category as the existing
  `#pragma warning disable MA0045 // sync-over-async boundary: Spectre.Cli Execute must return int`
  pattern already used by `RunCommand.Execute`. The analyzer (MA0045/CA1849/MA0042) flags every
  sync call once an async twin exists — pragma-suppress at the boundary rather than threading
  async into a Spectre.Cli `Execute` that must return `int`.

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
