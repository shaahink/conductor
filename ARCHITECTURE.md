# Architecture

Conductor drives coding agents through a plan: it picks the next checkpoint, composes a prompt, spawns
an agent CLI, judges what came back, runs the gate battery, and records all of it as events. This
document is the map — where each of those lives, what the seams are, and where a new thing goes.

Every `file.cs:NN` below was read at the commit that added it. If one drifts, fix the line number
rather than deleting the citation: a map with no coordinates is the thing this document replaced.

## The projects, and which way they point

```
tools/plan-lint          consumes Conductor.Planning ALONE - the standalone proof
        |
src/Conductor            CLI (Program.cs, Commands/**) + hosting (Hosting/, Http/ControlPlaneServer*)
        |  ProjectReference
src/Conductor.Core       domain, orchestration, store, events, providers, integrations  <- the engine
        |  ProjectReference
src/Conductor.Planning   pure decision logic: data in, decisions out. No IO, no clocks, no processes.
```

Each arrow points one way and only one way. `Conductor.Core` does not reference `Conductor`, so a
command cannot be called from the run loop and the store cannot format console output - not by
convention, but because it does not link. `tests/Conductor.Tests/ArchitectureBoundaryTests.cs` states
the rest of the rules as tests that name the offending type, and they run in the `engine-full` gate.

## One session, end to end

There is **no `IHostedService` running the loop.** Hosted services exist (`TelegramService` is the only
one today), but the run is a plain awaited call: `RunCommand` builds the composition root, starts the
hosted services, then awaits the orchestrator at `src/Conductor/Commands/RunCommand.cs:153`. If you go
looking for a `BackgroundService` that owns the run, you will not find it.

### 1. Dispatch — which stage, which checkpoint, which session kind

`Orchestrator` is a wiring hub whose `RunAsync` is one line — `src/Conductor.Core/Orchestrator.cs:101`
delegates to `RunLoop`. The loop is the `while` at `Orchestration/RunLoop.cs:104`, and one turn of it is
one session:

| Step | Where |
|---|---|
| Plan hot-swap at the session boundary (reload pending, or the plan file changed on disk) | `RunLoop.cs:112` |
| Read the work — declared tracker rows overlaid with graph status | `RunLoop.cs:199` → `Planning/WorkSnapshot.cs` |
| A pending phase gate pre-empts everything | `RunLoop.cs:206` |
| Pick the stage — first incomplete stage whose `dependsOn` is satisfied | `RunLoop.Plumbing.cs:25` (`SelectStage`) |
| Dispatch the session | `RunLoop.cs:399` → `SessionRunner.RunAsync` (`Orchestration/SessionRunner.cs:58`) |

`SessionRunner` first decides what *kind* of session this is — deliver, fix, resume, audit, verify,
review — at `SessionRunner.Kinds.cs:37`. The kind picks the template and the prompt shape.

### 2. Prompt composition

`PromptBuilder` resolves a template by name, preferring the plan's own directory and falling back to a
built-in string:

- `PromptBuilder.cs:150` `ResolveTemplatePath` — `<planDir>/<templatesDir>/<name>.md`, then
  `<planDir>/<name>.md`, else the built-in in `PromptBuilder.BuiltIns.cs`.
- `PromptBuilder.cs:203` `Render` — reads the template, substitutes `{key}` from the variable table
  (readOrder, stage notes, lessons, the tools contract, packs, the verifier threshold), then calls
  `PromptValidator.ThrowIfUnresolved` at `PromptValidator.cs:29`. **An unresolved `{token}` throws**, the
  loop parks on it, and the refusal is why a stray brace in a template file kills a run.
- `PromptBuilder.cs:269` `BatterySection` — the knowledge ledger and open bugs, contributed by
  `IPromptBattery` implementations, appended after the template body.
- Work items and per-card context are composed in `SessionRunner.cs` around `:167`–`:199`.

**The handoff block is not substituted into a deliver prompt.** The template tells the agent to read
`{tracker}` itself. The parsed handoff is injected verbatim only into advisor and analysis-lane prompts.
The final rendered prompt is written to `logs/session-NNN.prompt.md` (`SessionRunner.cs:212`) — that file
is the ground truth for "what did the agent actually receive".

### 3. The agent

`AgentSession.Start` (`src/Conductor.Core/AgentSession.cs:111`) spawns whatever `agent.command` names —
typically `claude -p --output-format stream-json`. Arguments are templated (`{prompt}`, `{sessionId}`,
`{model}`, `{claudeSessionId}`), MCP config args are appended, and the child gets `CONDUCTOR_PLAN` and
`CONDUCTOR_PID` in its environment so in-worker `conductor` verbs address the right run.

Every stdout line is teed to `logs/session-NNN.jsonl` and then handed to the provider
(`AgentSession.cs:166`), inside a catch-all so one malformed line cannot kill the run. The provider is
chosen by `IAgentProvider.Create` (`Providers/IAgentProvider.cs:35`). For Claude, live token deltas are
deduplicated by message id and folded onto session state at `Providers/ClaudeProvider.cs:130`; the
authoritative totals come off the terminal `result` envelope.

While the agent runs, the poll loop watches three rails: the **soft break**
(`SessionRunner.Mcp.cs:20` — at `softBreakRatio × ceiling`, writes the signal file and emits
`SoftBreakRequested`), the **hard ceiling** (`SessionRunner.Mcp.cs:93`, kills the agent), and the
watchdog thread. Hitting the ceiling takes the rollover branch at `SessionRunner.cs:420`: commits and
claims are recorded, **no attempt is burned and no gate battery runs**, and the next turn of the loop
starts a fresh session.

### 3b. Blast radius — what actually bounds a session (KS7.1)

An unattended session runs a general-purpose coding agent with a shell on the operator's machine. It
is worth being exact about what limits that, because the honest answer is narrower than the config
vocabulary suggests.

**There is no OS sandbox here.** Conductor spawns the agent CLI as an ordinary child process under a
job object (`JobObject`, so strays die with the run) with the repo as its working directory. That is a
lifetime boundary, not a security boundary: the child runs as the operator, with the operator's token,
and can reach anything the operator can. Codex CLI shipped a native Windows sandbox on restricted
tokens/AppContainer/ACLs, which proves stock Windows primitives suffice — building one is an era of
its own and is explicitly parked (KARVANSARA plan §8).

**What does bound it is the agent CLI's own permission engine**, driven from `agent.permissions` and
written into the settings file conductor already owns per session (`SessionRunner.Mcp.cs` →
`WriteSessionSettings`). Measured against the installed CLI, and against a live rig run:

- `deny` is enforced whatever else is set — **including under `--dangerously-skip-permissions`**. A
  bare tool name removes the tool from the set the model is told about; a specifier (`Bash(curl:*)`)
  refuses the call and emits a `permission_denied` envelope.
- Taking the bypass flag off turns the CLI's own gate back on, and it **does** gate: read-only shell
  commands auto-approve, but a mutating command and any un-allowlisted MCP tool are refused.
- `allow` is a pre-approval list, not a whitelist — it re-opens what the gate would otherwise refuse.

So ND-5's recommendation stands, with a correction to its reasoning: an allow/deny profile **can**
replace the flag, but not because the allow list narrows anything. It works because dropping the flag
restores the CLI's gate, and the allow list is then what lets the run's own work through.
`PermissionPosture.StripBypass` takes the flag off the resolved args when a non-bypass mode is set, in
`AgentSession.Start` — the one seam every session kind passes through.

**Which is the sharp edge, and conductor closes it.** The first rig run under a restricted posture had
`conductor task --done` refused ("This command requires approval") and every `conductor-tasks` MCP
tool refused ("you haven't granted it yet") — both channels a checkpoint can be claimed on. The work
landed, the gates went green, and the verdict read `newly DONE []` and filed NoProgress against a
stage that had in fact been delivered. `PermissionPosture.AllowRulesFor` therefore folds
`mcp__conductor-tasks` and `Bash(conductor:*)` into the profile whenever a non-bypass mode is set:
conductor's own plumbing is not something an operator should have to know to allow. Everything the
*stage* needs — build, tests, `git` — is still the plan's to allow, and getting that wrong reads as a
stalled stage rather than a permissions error, which is why the posture line and the refusal count are
both in the run log.

**Refusals are telemetered, and that is what makes the posture falsifiable.** A profile that failed to
load and a profile whose rules never matched are indistinguishable from outside the process — and the
CLI silently ignores a settings file that fails validation in print mode, so a broken profile does not
announce itself. `ClaudeProvider` parses each `permission_denied` into a `refusal` transcript line, a
`toolRefused` event stamped with session and stage, and a counted line in the run log. A `toolRefused`
row can only exist if the rules reached the session.


### 4. Verdict

`VerdictEngine.EvaluateSessionAsync` (`Orchestration/VerdictEngine.cs:116`) judges what happened. It
handles control outcomes first (killed, stalled, blocked-until), then branches by session kind — an
audit queues the phase gate, a verify parses the agent's JSON score, a delivery runs the gate battery.

For a delivery it reads: the tracker after the session, the commits the session made, the **claims**
(`VerdictEngine.Claims.cs:71` — resolved from the work graph, with a tracker diff only as a flagged
fallback), newly-blocked items, whether gates are green, and whether the tree is dirty. That is what
"evidence or it did not happen" is made of. Green emits `Advanced`/`Progress` and a pending-confirmation
set; red queues a fix session carrying the gate failure tails.

### 5. Gates

`GateRunner` is a static class (`src/Conductor.Core/GateRunner.cs:25`); `RunAllAsync` at `:35` is the
whole battery. Gates are declared in `plan.gates`, filtered by stage and tier, cached per commit SHA, run
in parallel batches with non-parallel gates as barriers, and **every failed required gate is retried once
unconditionally** before the battery is called red.

The **phase gate** — the one that confirms a stage rather than a session — is
`VerdictEngine.Phase.cs:29`. Green runs the audit and then `ConfirmStageAsync` (`Phase.cs:140`), which is
the only path that turns `DONE` into `DONE ✓`. Red increments the stage attempt and queues a fix.

`rollback` is not bookkeeping — it is **`git reset --hard`** onto `state.CurrentStageStartHead`, the
commit the repo sat on when the stage began
(`src/Conductor.Core/Commands/ControlDispatcher.cs:197`). It **destroys uncommitted work and drops
every commit made since that head**. It is refused when no stage-start head has been recorded
(`:183-188`), and refused on a dirty working tree unless `--force` — which does not stash the tree, it
**discards** it (`:189-194`). It applies only outside a session — the case itself is guarded
`when !inSession` (`:181`); arriving mid-session it is logged as taking effect after the session ends
(`:239`). A rollback that ran emits `RollbackExecuted { StageId, FromSha, ToSha, Forced }` (`:198`)
and leaves the run `Idle` (`:199`). *(Citations re-measured at KS10.1, 2026-08-15; they had drifted
by 6-8 lines. The semantics had not moved at all.)*

### 6. The claim, and the tracker

`conductor task --done <id>` is the only claim path, and it is one function deep:

```
TaskCommand → TaskBoard.Move (src/Conductor/Commands/TaskBoard.cs:19)
            → SqliteRunStore.ApplyTaskStatus (Store/SqliteRunStore.Sessions.cs:249)
            → TaskWrites.BuildStatusChange (Events/TaskWrites.cs:26)   ← validates the transition
            → event appended
```

`TaskBoard.Move` reports the **post-fold** status and exits non-zero on a refused transition, which is why
the CLI's output is trustworthy and intent is not.

The tracker markdown is a **generated view**: `RunLoop.RegenerateTracker` (`RunLoop.Plumbing.cs:301`) →
`TrackerGenerator.Write` (`TrackerGenerator.cs:151`), rows from the database, handoff from the latest
recorded handover. Editing a checkpoint row by hand changes nothing; editing the handoff block does,
because that block is parsed back out and stored.

### 7. Events and projection

Events are the durable spine. `SqliteRunStore.Emit` queues and a drain loop persists in batches, and
`seq` is **re-assigned from the database inside the transaction** (`Store/SqliteRunStore.Events.cs:150`)
because two processes share one `run.db`. `EventLog` writes the same facts as NDJSON to
`.conductor/events.jsonl`, tolerating a torn tail on read.

`RunStateProjection.Fold` (`Events/RunStateProjection.cs:23`) folds events into a `RunState`. Note which
side uses it: **the read side** — the control plane, `conductor status`, the report builder. The engine's
own live state is an in-memory `RunState` saved as JSON. Crash recovery is the one place the engine folds
events itself, to find an interrupted session.

## The seams

`src/Conductor.Core` declares exactly **nine** `public interface I*`. That is the whole list — the
abstraction count is small on purpose.

| Seam | Job | Implementations |
|---|---|---|
| `IRunStore` `Store/IRunStore.cs:10` | The run's durable write + query surface | `SqliteRunStore` (7 partials). Tests point it at a temp sqlite file rather than faking it. |
| `IAgentProvider` `Providers/IAgentProvider.cs:6` | Adapt one agent CLI's argv and stdout to the session loop | `ClaudeProvider`, `OpencodeProvider`, `GenericTextProvider`; factory at `:35` |
| `IProgressProvider` `Planning/IProgressProvider.cs:12` | Answer "how far along is this stage?" from an external source | `MarkdownTableProvider`, `ScriptProvider`, `PlanCheckpointProvider` |
| `IPromptBattery` `PromptBattery.cs:10` | Contribute one optional block of context to the next prompt | `LedgerBattery`, `BugsBattery`, `LessonsBattery`, `RecentFailureBattery`, `LaneArtifactBattery` |
| `IEventSink` `Events/EventLog.cs:8` | Append one `ConductorEvent` | `EventLog`, `SqliteRunStore`, `NullEventSink` (dry run) |
| `IProgressSink` `Progress.cs:67` | Push snapshots/logs out to an operator, poll control commands back in | `PlainSink`; test doubles record |
| `ITelegramService` `Integrations/TelegramService.cs:22` | The notify + remote-control channel | `TelegramService`, `NoOpTelegramService` (null object when the plan has no telegram block) |
| `IPlanner` `IPlanner.cs:7` | Decide the next checkpoint | `CheckpointPlanner` |
| `IReportsStartOutcome` `IReportsStartOutcome.cs:17` | Let a hosted service say it declined to start on purpose | `TelegramService` |

**KS9's GitHub sync did not add a tenth.** Counted again at KS10.1 (2026-08-15) after the era that
added `Integrations/Github/` — thirteen files, a client, a mirror, a board sync and a v14 migration —
and the list above is still the whole list. That is deliberate and it is the same trick `ReleaseClient`
uses: `GithubClient` takes an `HttpMessageHandler`, so the test double is
`tests/Conductor.Tests/FakeGithub.cs` (a handler that records requests and can park one mid-flight),
not an `IGithubClient` nobody else would ever implement. **The rule this era learned:** if the only
other implementation would be a fake, the seam belongs at the transport, not at the type.

**Five things you would expect to be seams are not**, and knowing this saves an afternoon:

- **Clock** — the BCL `TimeProvider`, passed as an optional constructor argument, not a custom interface.
  Two hot paths (`SessionWatchdog`, `StallDetector`) take a bare `Func<DateTime>` instead.
- **Process launch** — `public static class ProcessRunner` (`ProcessRunner.cs:8`). Not injectable; the
  escape hatches are `ProcessSupervisor` and a per-call environment override.
- **Git** — `public static class Git` (`Git.cs:3`), shelling straight to `git -C <repo>`. Not mockable.
- **Gate execution** — `public static class GateRunner` (`GateRunner.cs:25`). Its seams are *parameters*
  (`onProgress`, `onGates`, an optional `IRunStore` for the per-SHA cache), not types.
- **GitHub sync** — `GithubClient` + `GithubMirror` (`Integrations/Github/`). Push-only by design
  (ADR-0005): nothing is ever read back from GitHub into the run, so there is nothing for a seam to
  invert. The mirror is attached to the run rather than registered on the event path, and
  `ArchitectureBoundaryTests.TheGithubMirrorIsNeverRegisteredOnTheEventPath` holds that line by
  **file name** — only `RunContext.cs`, `RunContext.Mirror.cs` and `RunLoop.Plumbing.cs` may name it
  under `Orchestration/`. Splitting a file that names it will go red; that is the point.

## The two surfaces

### The control plane — HTTP, loopback, one file to find it

`ControlPlaneServer` (`src/Conductor/Http/ControlPlaneServer.cs:34`) is a `sealed partial class` across
11 files / ~2,000 lines. It lives in the **CLI assembly, not Core** — Core owns only the discovery path
convention (`Conductor.Core/Http/ControlPlaneDiscovery.cs:17`) and the wire contracts under
`Core/Http/Contracts/`.

- **Transport**: a raw `HttpListener` on a background thread. No ASP.NET Core.
- **Port**: prefers 4317 and scans forward 20 ports (`ControlPlaneServer.cs:50`), so concurrent runs on
  one machine never collide. Bound loopback-only: `http://127.0.0.1:{port}/` (`:122`).
- **Started** explicitly by `RunCommand` (`src/Conductor/Commands/RunCommand.cs:123`); a bind failure is
  never fatal to the run.
- **Advertised** by `WriteDiscoveryFile` (`:156`) into `<stateDir>/control-plane.json` — port, base URL,
  pid, plan name, and the write token. **The file is deleted on shutdown**, so absence of the file does
  not mean absence of an engine; fleet scan probes ports for exactly that reason.
- **Auth**: a per-run random token. Every **POST** must carry `X-Conductor-Token`, compared in fixed time
  (`:256`). **GETs are unauthenticated on purpose** — the threat model is a hostile page or a prompt
  injection issuing *writes*, and the token's only distribution channel is a file whose permissions are
  the trust boundary.
- **Endpoints**: 32, in one `switch (method, path)` at `:207` — 17 GET, 15 POST. Adding one means adding
  a case there, a handler in the right partial, contract records under `Http/Contracts/<feature>/`, and
  the types registered in `ControlPlaneJsonContext`.

Three of the GETs are SSE streams rather than snapshots: `/events`, `/transcript/current`,
`/console/current`.

### The Face — Go, and it is a hybrid

`face-go/` is a Bubble Tea TUI in its own module, talking to the control plane over HTTP. It is **not**
push-only and **not** poll-only:

- **Discovery**: `discoverControlPlane()` (`face-go/cmd/conductor-face/main.go:187`) walks *up* from the
  cwd looking for `.conductor/control-plane.json` and reads `baseUrl` + `token`. `--port`/`--token` and
  `CONDUCTOR_TOKEN` override it. The state directory is discovered separately, because the engine deletes
  the discovery file on shutdown and the Face must still render a finished run.
- **Polling**, once a second (`internal/tui/messages.go:187`), fanning out to `/state`, `/tasks`,
  `/processes`, `/sessions`, plus knowledge and the owner queue. The polls fail independently.
  **Connectedness is derived from a healthy `/state` poll, not from stream liveness.**
- **Streaming**, over SSE (`internal/api/sse.go:20`) for the three stream endpoints, resuming from a
  last-seen `seq` on reconnect rather than replaying the backlog. There is no websocket anywhere.
- **Writes**: everything the operator can do is a POST with the token. The Face's own verb table is
  `internal/tui/cmdbar.go:66` (run group: pause/resume/stop-after/approve/heartbeat/reload-plan; stage
  group: goto/retry-stage/skip/pause-after-stage; danger group: kill/abort/rollback). The engine-side
  enum is `ControlAction` in `Progress.Control.cs` — **the Go verb strings do not map one-to-one by
  name**; the mapping happens in the `/control` handler, so change both ends together.
- **Demo mode**: `DataSource` (`internal/api/types.go:10`) has two implementations, live and demo, which
  is how `conductor demo` renders a whole run with no engine and no credentials.

## The file-organisation convention

One rule, four cases. It exists because "no file over 500 lines" was already true when
`ControlPlaneDto` was **thirty files** and `ConductorEvent` was eleven: file size said healthy, and
finding anything still needed a grep.

**The numbers are enforced, not aspirational, and they are these** (`tests/Conductor.Tests/architecture-baseline.json`,
read at KS10.1): `lineCeiling` **500**, `maxTypesPerFile` **3**, and both debt maps —
`filesOverLineCeiling`, `filesOverTypeCeiling` — are **`{}`**. An empty baseline is the whole point:
it records debt that *exists*, may only shrink, and stage M1's definition of done was driving it to
`{}`. It got there, so a file that crosses 500 lines has **no legal home** in this file — the only
move left is to split it, which is what KS9's `RunContext` (514 lines) did into
`RunContext.Mirror.cs`. `tools/gates/ratchet.ps1:173` fails the gate if either ceiling is raised, and
`ratchet-baseline.json` carries the other two floors: `minTests` **1932** (only ever rises),
`maxPragmas` **38** (only ever falls, and is red at 43 today — bug #44, an owner decision).

### 1. A file is named after what it declares - always

`ControlPlaneDto.Lanes2.cs`, `TelegramService.Dto3.cs`: a number appended to a filename is the sound of
a name running out, and it means the file is a drawer rather than a subject. The types in those files
had nothing to do with `ControlPlaneDto` or `TelegramService` at all. **If the filename prefix names a
type the file does not declare, the prefix is a filing convention pretending to be a type.** Split it.

### 2. A partial is legitimate when it is one type with one identity, split for size

`ControlPlaneServer` (11 files), `VerdictEngine` (8), `SqliteRunStore` (7), `SessionRunner` (6),
`RunLoop` (5), `TelegramService` (5) are all genuinely `partial` declarations of a single type that
holds a single set of fields. Splitting *those* would mean inventing handler objects and threading
state through them - a redesign, not a reshape, and nothing about it would make the code easier to
find. They stay, and each partial is named for the aspect it holds (`.Endpoints`, `.Plan`, `.Tasks`).

**The test:** open the file. If everything in it operates on the parent type's fields, the partial is
real. If the file declares independent types that merely *relate* to the parent, it is a pile.

### 3. Endpoint contracts live in a folder per feature, under one namespace

```
src/Conductor.Core/Http/
  ControlPlaneMapper.cs        engine snapshot -> wire contract, the only place that conversion happens
  ControlPlaneJsonContext.cs   the source-generated serializer registry (every DTO is listed here)
  ControlPlaneDiscovery.cs     where a run advertises itself
  Contracts/
    State/  Sessions/  Plan/  Tasks/  Knowledge/  Telegram/  Processes/  Control/  OwnerQueue/
```

A new endpoint's request and result records go in the folder for its feature, in one file named for
the endpoint (`Tasks/TaskSplitDtos.cs` holds the request, the child and the result - they are one
exchange). Then add the type to `ControlPlaneJsonContext`, or it will not serialise.

The namespace stays flat - `Conductor.Core.Http` for every contract, whatever folder it is in. This is
a deliberate exception to "folders map to namespaces", and the reason is that the wire contract is a
**published protocol**: the Face, `conductor face` and the fleet scan all speak it as one vocabulary,
and ten namespaces would mean ten `using` lines in every server file to describe one payload. Folders
organise the *files*; the namespace describes the *protocol*.

### 4. Events live in `Events/Kinds/`, grouped by what they are about

Every durable fact the run records is a record deriving from `ConductorEvent`, and every one of them
lives in `src/Conductor.Core/Events/Kinds/` - `RunEvents.cs`, `SessionEvents.cs`, `StageEvents.cs`,
`GateEvents.cs`, `TaskEvents.cs`, `LaneEvents.cs`, `PlanEvents.cs`, `OwnerControlEvents.cs`,
`BlockedUntilEvents.cs`. `Events/` itself holds the machinery: the base type, the log, the projection,
the metrics.

A new event goes in the file for its subject, never next to the code that raises it -
`ArchitectureBoundaryTests.EventTypesStayInTheEventNamespace` fails the build if it does. The event log
is the run's only durable truth and every read endpoint folds it; an event declared beside its raiser
is invisible to anyone asking "what can happen here".

## Where do I add X

The second column is where the thing lives. The third is what silently lies if you skip it.

| I want to add… | It goes in | Or this breaks |
|---|---|---|
| **an HTTP endpoint** | request/result records in `Core/Http/Contracts/<feature>/`, handler in the matching `ControlPlaneServer.<Feature>.cs` partial | the `switch` at `ControlPlaneServer.cs:207`; **register every DTO in `ControlPlaneJsonContext`** or it will not serialise; `face-go/internal/api/client.go` if the Face calls it |
| **an event** | `Core/Events/Kinds/<subject>Events.cs` — never beside the code that raises it | `ArchitectureBoundaryTests.EventTypesStayInTheEventNamespace` fails the build; `RunStateProjection.Fold` if it changes run state |
| **a CLI verb** | `src/Conductor/Commands/<Verb>Command.cs` | `c.AddCommand<…>("verb")` in `Program.cs:74`ff; `docs/cli.md` |
| **a gate** | `plan.gates` in the plan JSON — gates are configuration, not code (`Models/GateConfig.cs:5`) | nothing in the engine; get the path right, a wrong one can exit 0 |
| **support for another agent CLI** | `Core/Providers/<Name>Provider.cs` implementing `IAgentProvider` | the factory switch at `Providers/IAgentProvider.cs:35` |
| **a block of context in every prompt** | an `IPromptBattery` in `Core/PromptBattery.*.cs` | the assembly list in `PromptBuilder.BatterySection` (`PromptBuilder.cs:273`ff) |
| **a session kind or a template** | template file under the plan's `templatesDir`, with a built-in fallback in `PromptBuilder.BuiltIns.cs` | `ResolveSessionKind` (`SessionRunner.Kinds.cs:37`) and `BuildPrompt`; **sweep the template for stray `{braces}`** — `PromptValidator` throws on an unresolved one and the run parks |
| **a plan config key** | the record in `Core/Models/` (`PlanConfig.cs:9`, `LimitsConfig.cs:5`) | `docs/plan-config.md`; give it a default, or every existing plan stops loading |
| **a tab or panel in the Face** | `face-go/internal/tui/tab_<name>.go` | the `tabKey` mnemonic map at `model.go:59` **and** the hand-maintained help legend in `cmdbar.go` — both, or the help lies. Read `face-go/STYLE.md` first |
| **a control verb** | `ControlAction` in `Core/Progress.Control.cs` and the `/control` handler | the Face's verb table at `cmdbar.go:66` — the strings do not map by name |
| **a GitHub-synced surface** | `Core/Integrations/Github/` — a request shape in `GithubRequests.cs`, its wire record in `GithubDtos.cs`, and the call on `GithubClient` | register the DTO in `GithubJsonContext` or it will not serialise; if a run-loop file has to *name* `GithubMirror`, `TheGithubMirrorIsNeverRegisteredOnTheEventPath` fails — go through `RunContext.MirrorBoard` / `MirrorFinalPass` instead |
| **a store column or table** | a new `Core/Store/Migrations/vNN_<what>.sql`, embedded as a resource | **`MigrationRunner.CurrentVersion`** (`MigrationRunner.cs:11`) — and the tests that pin it by literal (`RunDbTests`, `K3_3ProvenanceTests`, `K4_1ContextWindowTests`). Those literals exist so the bump is *decided*; KS9.2 shipped v14 and left all three behind. Note the store migrates on **every** open (`MigrationRunner.cs:21`), so a newer build run against a live store locks an older engine out of it (bug #45) |
| **an architecture rule** | `tests/Conductor.Tests/ArchitectureBoundaryTests.cs` (**12** `[Fact]` rules today, counted at KS10.1) | make the failure message name the offending type; a rule that says only "boundary violated" costs the next session an hour |
| **anything in Core needing Spectre, `HttpListener` or `Console`** | it does not go in Core | `CoreDoesNotLinkTheCliOrAnyUiAssembly`, `CoreDoesNotHostHttp`, `CoreSourceNeverNamesTheShell` and `TheStoreDoesNotWriteToTheConsole` will each say so by name |

## What K2.3 split, and what it left

| Pile | Was | Now | Why |
|---|---|---|---|
| `ControlPlaneDto` | 30 files, 67 records, **no such type existed** except a static mapper | `Http/Contracts/<feature>/`, mapper renamed `ControlPlaneMapper` | the prefix named nothing; per-endpoint contracts wearing one type name |
| `ConductorEvent` | 11 files, independent records incl. a `Lanes2` | `Events/Kinds/<subject>.cs` | same fiction; only `ConductorEvent.cs` declared the type |
| `TelegramService.Dto{,2,3}` | 3 files, 7 Telegram Bot API records | `Integrations/TelegramApi/` | an external API's wire types, not parts of the service |
| `ControlPlaneServer` | 11 partials | **left** | one type, one field set, split by endpoint group - case 2 |
| `VerdictEngine`, `SessionRunner`, `RunLoop`, `SqliteRunStore`, `TelegramService` | 5-8 partials each | **left** | same - measured, not assumed: every file declares the parent type |
| `Models/` keeping the `Conductor.Models` namespace inside `Conductor.Core` | - | **left** | renaming it touches every file in the repo that names a config record, for no structural gain |
