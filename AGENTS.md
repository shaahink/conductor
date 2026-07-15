# Conductor (Baton worktree) — session handoff

## What this is
Conductor is an autonomous multi-session engineering orchestrator (C# / .NET, Spectre.Console). It
spawns headless agent sessions, verifies work independently (gate battery + git commits + tracker
diff), is fully resumable, and reports to `.conductor/REPORT.md`. This worktree (`feat/baton`) hosts
**Baton — Conductor v2**: Conductor improving itself. It also now hosts **Conductor Face v2** — a
Go + Bubble Tea TUI, merged in from `feat/go-face`, living alongside the existing TypeScript + Ink
TUI at `face/`.

## This worktree
- **Path:** `C:\Code\conductor-baton`  **Branch:** `feat/foreman`
- **Do NOT touch:** `C:\Code\conductor` (master, the stable DRIVER) or the live `C:\Code\DevContext2-ui`
  Loom run (separate repo + lock).
- **Faces:** `face/` — TypeScript + Ink TUI. `face-go/` — Go + Bubble Tea TUI, wired to the real
  control plane (control verbs, inject, report query, template editor file I/O, live SSE
  transcript/events, session history, a Processes modal, a TTY guard, spring-animated toasts,
  markdown-rendered session result summaries) — no longer a visual-only prototype. Verified against
  a real `ControlPlaneServer` (not just `--demo`), not only build/vet/test — see "How to verify a
  face-go change" below before claiming a change works.
  **Retirement policy (explicit, from the user):** keep both until `face-go` is usable, then
  retire `face/`. Not a judgment call to make unilaterally — ask before deleting `face/`, but do
  ask again once `face-go` has seen real day-to-day use, since the user's stated intent is to
  retire it, not to keep both indefinitely.
- **Driver:** the STABLE `C:\Code\conductor\bin\conductor.exe` (built from master). The tool improving
  Conductor is never the tool under edit.

## Resume here (Maestro M6 complete + face-go mission-control pass, 2026-07-15)
**Latest session (M6 close-out):** a full parity + polish + refactor pass on `face-go` before M7.
What changed (commit `4d15c2f`):
- **Parity gaps closed** — run status/attention reason, session kind/attempt, `/tasks` (MCP task
  list) and the splash empty-state were fetched-but-never-rendered (or lost in the v3 redesign);
  all visible now. Agent tab = mission control: status strip (session · checkpoint · gate chips ·
  task progress · elapsed + attention banner) over the transcript.
- **Real bugs fixed** — transcript scroll-up was offset-from-top (one ↑ teleported to the top of
  the buffer; now offset-from-bottom, unit-tested in `widgets/transcript_test.go`); sidebar rows
  word-wrapped at 80 cols (lipgloss v2 counts the border inside `.Width()` — content is width−3);
  demo/goldens had `/sessions` oldest-first while the real wire is `ORDER BY number DESC`.
- **Refactor** — `update.go`/`view.go` split per concern: each tab's handler+renderer in
  `tab_<name>.go`, palette/inject/search/help in `cmdbar.go`; dead sidebar-selection machinery and
  ad-hoc colour helpers deleted; `STYLE.md` updated (read it before any face-go change).
- **Alive** — braille spinner + live cost/elapsed in the top bar only while the agent is active;
  Timeline auto-refreshes on spine events while open; search matches highlighted in place.

## Previous resume point (Maestro M6 complete, 2026-07-15)
**M6 (Plan authoring) is DONE, 3/3** — plan import/edit landed on the C# control plane AND in `face-go`,
and the truth gate is met with **zero LLM spend**. What's new this era:
- **M6.1 deterministic import** (`src/Conductor/Core/Planning/MarkdownPlanParser.cs`): a *structured*
  plan/tracker doc (`### M6 — …` headers + `**M6.1**` bullets or `| M6.1 |` rows) parses into a stage
  graph with **no model call**. Freeform prose still falls back to the advisor (`--model` fills a
  `{model}` placeholder in advisor args); `--yes` skips the confirm. `conductor plan import <file>`.
  **Truth gate met**: `plan import docs/MAESTRO-PLAN.md` → exactly M1…M9 (a `(DONE …)`-marked bootstrap
  header like M0 is excluded). Unit test reads the *real* doc; also CLI-verified.
- **M6.2 re-import diff** (`PlanDiff.cs`): `Compute` + `Apply` — a re-import shows added/changed
  stages+gates and applies only those; hand-tuned entries are never clobbered. Idempotent (a second
  import of the same doc = "Nothing to change").
- **M6.3 edit from the TUI**: backend `GET /plan` / `POST /plan/edit` / `POST /plan/import`
  (`ControlPlaneServer.Plan.cs`, `ControlPlaneDto.Plan*.cs`) — reads/writes are served from a *fresh
  load of the plan file* and validated via `CollectErrors`; the live `_plan` instance is never mutated
  on an HTTP thread, so edits take effect on the next run (like `plan reload`). Face: the **`g` Plan
  Editor** modal (tabs Stages·Gates·Settings·Import; a `‹ value ›` carousel picker for enum fields —
  model/workflow/kind/tier/gatePolicy; the Import tab drives `/plan/import` path→diff→apply). Fully
  interactive in `--demo` (edits mutate the in-memory plan), so it's reviewable with no engine/no spend.
- **Crush polish pass on face-go**: persistent `◆ conductor` brand mark in the ticker; unified rounded
  modal chrome (accent `◆ Title` + full-width rule + dim contextual help — note lipgloss v2 counts the
  border inside `.Width()`, so inner width is `modalW-6`); an empty-state **splash** in the transcript
  pane (wordmark + how-to-start) shown whenever no run is attached.

**Next: Maestro M7 (knowledge that compounds).** M7.1 ledger injected into the next prompt, surfaced in
the Face, queryable; M7.2 `conductor bug new|list|fix` + MCP (a found bug outlives its session). M7
truth gate: a 2-session toy run where session 1 writes a note + files a bug and session 2's compiled
`prompt.md` on disk contains both. See `MAESTRO-TRACKER.md` for the live handoff.

**No-spend demo recipe (see the whole face, incl. the M6 plan editor):**
```powershell
cd face-go; go build -o bin/conductor-face.exe ./cmd/conductor-face/
.\bin\conductor-face.exe --demo    # everything interactive: g=plan editor, t=timeline, c=console, :=palette, …
```
Or drive the REAL control plane with a local fake agent (no LLM) — see the "Dogfood recipe" below.

Dogfood recipe (real orchestrator, no LLM): a throwaway git repo + a minimal plan whose `agent.command`
is a local script emitting Claude stream-json (`{"type":"result","total_cost_usd":…}`) + a trivial
`cmd /c exit 0` gate; `conductor run --control-plane --no-face --headless`, then curl the control plane
and run `conductor status`. This exercises RunLoop → SessionRunner → AgentSession raw log → control
plane end to end without agent spend.

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

## Architecture
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
  — seeding only the SQL tables (for `/report/query`, `/sessions`, `/processes`) without matching
  events will correctly leave `/state` showing zero cost / no gates. That's expected, not a bug.
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
**Tabs** (jump straight there — also `1`–`8`, or `tab`/`shift+tab` to cycle; `esc` returns to Agent):
| Key | Tab |
|-----|-----|
| `a` | Agent (mission control: status strip + transcript; `f` fold, `↑↓` scroll, `end`/`l` live-tail) |
| `h` | Sessions (history + inline detail) |
| `t` | Timeline (`r` refresh) |
| `s` | Procs (supervised processes) |
| `c` | Console (raw agent stdout) |
| `e` | Templates (list + editor + `v` compiled-prompt preview, all on one page) |
| `g` | Plan editor (M6.3) — `←→` sections Stages·Gates·Settings·Import; edit fields inline; Import → diff → apply |
| `r` | Report / query console |

**Actions** (bottom command bar / overlays):
| Key | Action |
|-----|--------|
| `:` | Command palette (11 verbs, filterable, destructive ones confirm, `goto` asks for a stage id) |
| `i` | Inject context (bottom bar: `tab` field, `ctrl+s` send) |
| `/` | Inline transcript search (enter: lock, n/N: next/prev, esc: clear) |
| `p` | Collapse / expand the plan sidebar |
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

## Current state (2026-07-11, post-F7 first pass)
- **Baton v2 COMPLETE** — 77 sessions, 67/67 checkpoints DONE, status=completed.
- **Foreman v3 ACTIVE** — 31/40 checkpoints DONE, F0-F6 confirmed, F7 (Gate caching + truth gates + speed program) IN PROGRESS (3/5 DONE, 1 cancelled, 1 TODO pending F7.1).
- **Branch:** `feat/foreman` is the active branch; `feat/baton` is the worktree.
- **Driver:** `C:\Code\conductor\bin\conductor.exe run -p plans\conductor-foreman.plan.json`
- **Read order:** `CONDUCTOR-VNEXT-PLAN.md` (tracker) → `docs/CONDUCTOR-VNEXT-PLAN.md` (design doc) → this section.

### F6 COMPLETE (verified 2026-07-11)

**Engine side (47c7ecb):** `Core/Events/TranscriptLog.cs` (new), `GET /transcript/current` SSE,
`GET /processes`, `GET /sessions`, `GET /report/query` (SELECT-only), `POST /inject` (records to
run.db — NOT yet consumed into a prompt, that's F8), `StateDto` extended with session-level ticker
fields + runId/repo/planDir. **647/647 dotnet tests pass, 0w/0e** (the 1 pre-existing Serilog-flush
flake is gone — either fixed by a dependency update or no longer reproducible).

**Face TUI (f3dde7c):** Full TypeScript + Ink TUI ("conductor-face") — plan tree (F6.2), agent pane
with thinking-stream + tool-fold + search (F6.3), process pane + 11-verb command palette + tiered
ticker (F6.4), PLUS D11 extras: inject editor, prompt/persona template editor (direct filesystem),
session-history browser, report/query console. Mouse support via raw SGR (1000+1006) escape parsing.
`--demo` flag runs fully offline. **23/23 tests pass** (4 test files), typecheck clean, build ~135ms.
1 bug found and fixed this session: golden snapshot non-determinism — `fixtures.ts` used live
timestamps (`new Date().toISOString()`); pinned to `FIXED_TS = "2026-07-11T02:04:43.000Z"`.

**Verified this session:** dotnet build 0w/0e, full suite 647/647 pass (including 17 control-plane
tests), face/ 23/23 pass, control plane HTTP endpoints structurally correct. Control plane confirmed
opt-in (`--control-plane` flag); headless mode unchanged. Live TTY integration was not exercisable
in this environment — user should do a 2-minute smoke test: `conductor run --control-plane -p ...`
+ `node face/dist/cli.js` in a real terminal.

**How to run the TUI:**
```powershell
cd face
npm install   # first time only
npm run build
node dist/cli.js --demo          # offline synthetic data
node dist/cli.js                  # live against conductor --control-plane (http://127.0.0.1:4317)
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
- **HTTP wire types are separate DTOs** (`Core/Http/ControlPlaneDto.cs`), not `DashboardSnapshot`
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
