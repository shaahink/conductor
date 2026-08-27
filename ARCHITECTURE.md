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
src/Conductor.Core       domain, orchestration, store, events, providers, integrations,  <- the engine
                         and since Divan: Courier/ (the daemon), Inbox/, Publishing/
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

*Still true after Divan, and worth stating because Divan added a daemon.* The **courier** is a separate
**process** (`conductor courier run`), not a hosted service inside this one — the run process registers
exactly one `IHostedService`, still `TelegramService` (`Hosting/ConductorHost.cs:121`). See
[The courier](#the-courier--the-one-process-that-outlives-the-run) below.

### 1. Dispatch — which stage, which checkpoint, which session kind

`Orchestrator` is a wiring hub whose `RunAsync` is one line — `src/Conductor.Core/Orchestrator.cs:103`
delegates to `RunLoop`. The loop is the `while` at `Orchestration/RunLoop.cs:131`, and one turn of it is
one session:

| Step | Where |
|---|---|
| Plan hot-swap at the session boundary (reload pending, or the plan file changed on disk) | `RunLoop.cs:141` (`ReloadThenCheckCap`) |
| Read the work — declared tracker rows overlaid with graph status | `RunLoop.cs:163` (`_ctx.ReadWork()`) → `Planning/WorkSnapshot.cs` |
| A pending phase gate pre-empts everything | `RunLoop.cs:199` |
| Pick the stage — first incomplete stage whose `dependsOn` is satisfied | `Orchestration/StageSelection.cs:56` (`StageSelection.Select`) |
| Dispatch the session | `RunLoop.cs:414` → `SessionRunner.RunAsync` (`Orchestration/SessionRunner.cs:58`) |

`SessionRunner` first decides what *kind* of session this is — deliver, fix, resume, audit, verify,
review — at `SessionRunner.Kinds.cs:37`. The kind picks the template and the prompt shape.

### 2. Prompt composition

`PromptBuilder` resolves a template by name, preferring the plan's own directory and falling back to a
built-in string:

- `PromptBuilder.cs:169` `ResolveTemplatePath` — `<planDir>/<templatesDir>/<name>.md`, then
  `<planDir>/<name>.md`, else the built-in in `PromptBuilder.BuiltIns.cs`.
- `PromptBuilder.cs:222` `Render` — reads the template, substitutes `{key}` from the variable table
  (readOrder, stage notes, lessons, the tools contract, packs, the verifier threshold), then calls
  `PromptValidator.ThrowIfUnresolved` at `PromptValidator.cs:29`. **An unresolved `{token}` throws**, the
  loop parks on it, and the refusal is why a stray brace in a template file kills a run.
- `PromptBuilder.cs:288` `BatterySection` — the knowledge ledger and open bugs, contributed by
  `IPromptBattery` implementations, appended after the template body.
- Work items and per-card context are composed in `SessionRunner.cs` around `:167`–`:199`.

**The handoff block is not substituted into a deliver prompt.** The template tells the agent to read
`{tracker}` itself. The parsed handoff is injected verbatim only into advisor and analysis-lane prompts.
The final rendered prompt is written to `logs/session-NNN.prompt.md` (`SessionRunner.cs:175`) — that file
is the ground truth for "what did the agent actually receive".

### 3. The agent

`AgentSession.Start` (`src/Conductor.Core/AgentSession.cs:137`) spawns whatever `agent.command` names —
typically `claude -p --output-format stream-json`. Arguments are templated (`{prompt}`, `{sessionId}`,
`{model}`, `{claudeSessionId}`), MCP config args are appended, and the child gets `CONDUCTOR_PLAN` and
`CONDUCTOR_PID` in its environment so in-worker `conductor` verbs address the right run.

Every stdout line is teed to `logs/session-NNN.jsonl` and then handed to the provider
(`AgentSession.cs:181`), inside a catch-all so one malformed line cannot kill the run. The provider is
chosen by `IAgentProvider.Create` (`Providers/IAgentProvider.cs:35`). For Claude, live token deltas are
deduplicated by message id and folded onto session state at `Providers/ClaudeProvider.cs:147` (`EmitLiveUsage`); the
authoritative totals come off the terminal `result` envelope.

While the agent runs, the poll loop watches three rails: the **soft break**
(`SessionRunner.Mcp.cs:21` (`CheckSoftBreak`) — at `softBreakRatio × ceiling`, writes the signal file and emits
`SoftBreakRequested`), the **hard ceiling** (`SessionRunner.Mcp.cs:99` (`EndOnBudget`), kills the agent), and the
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

### 3c. What the session did — the hook channel is the record (KS7.2)

The per-session digest — call count, tool mix, files written, claims, background jobs, build commands
— used to be re-derived from the assistant stream. It is now written by the agent CLI's own tool
hooks and the stream is the fallback.

Conductor writes `settings.session.json` into the state dir
(`SessionRunner.Mcp.cs:WriteSessionSettings`) registering the hidden `hook-budget` verb on **both**
`PreToolUse` and `PostToolUse` with matcher `*`. Each invocation appends to
`.conductor/hook-tools/NNN.jsonl`: a PreToolUse writes the CALL (`ToolEventExtractor.Extract` over the
hook's `tool_input`, which is the same object the stream's `tool_use.input` carries), a PostToolUse
writes an OUTCOME line merged back by `tool_use_id`. At session end `PromoteHookDigest`
(`SessionRunner.Activity.cs:44`) rebuilds the digest from that file; the digest records the `source`
it came from, so a reader can check the claim on any given session rather than take the design's word
for it.

**Why both events, and not just the one that already existed.** Measured twice on claude 2.1.235:
**PostToolUse does not fire for a tool call that was refused or failed.** Across a probe run and a rig
run, every call whose `tool_result` came back clean had a PostToolUse and no call whose result carried
`is_error` did — no exceptions in either direction. A PostToolUse-only channel therefore counts
successes while the stream it replaces counts attempts, and the two can never agree: a session whose
forty test commands all failed would have reported none. PreToolUse fires for every call the model
makes, including the ones the posture then refuses, so it is the same population the transcript had.
What PostToolUse adds is the one thing the transcript never knew — `FailedCalls`, how many calls did
not come back.

**Fallback.** No file, or an empty one, means the transcript-derived digest stands untouched. That is
an opencode session, a `--bare` claude, any provider with no hook surface, and a session that made no
calls; absent and empty are deliberately the same answer, because promoting an empty digest over the
transcript would report a session that did nothing.

**Skills vs `promptExtra` — decided here, and it is a decision, not a preference.** A skill's body
loads only when the agent invokes it; only its one-line description (~25 tokens) is resident. Both
delivery paths were verified in print mode: a project `.claude/skills/` with `--setting-sources
project`, and — better — `--plugin-dir <dir>`, which delivers the same skill with `--setting-sources`
empty and writes nothing into the target repo. Against that, this plan's `promptExtra` is ~1.66k
tokens, 37% of the composed prompt. **`promptExtra` stays.** Its content is a trap list whose value is
being read *before* the first edit by a session that does not yet know it needs it, and a body that
loads only if the agent chooses to invoke it is a rail conditional on curiosity. The saving is small
against that risk — the resident tokens sit in the cached prefix. The criterion, written down so it is
not re-argued: **if not reading it causes harm the session cannot detect, it is a rail and stays
resident; if it only helps once the decision is already made, it is a reference and can move to a
skill.** The reference half (golden rebaseline recipe, face style rules, plan-editor caveats) is
KS7.5's to spend, through `--plugin-dir` pointed at the state dir.


### 4. Verdict

`VerdictEngine.EvaluateSessionAsync` (`Orchestration/VerdictEngine.Evaluate.cs:111`) judges what happened. It
handles control outcomes first (killed, stalled, blocked-until), then branches by session kind — an
audit queues the phase gate, a verify parses the agent's JSON score, a delivery runs the gate battery.

For a delivery it reads: the tracker after the session, the commits the session made, the **claims**
(`VerdictEngine.Claims.cs:71` — resolved from the work graph, with a tracker diff only as a flagged
fallback), newly-blocked items, whether gates are green, and whether the tree is dirty. That is what
"evidence or it did not happen" is made of. Green emits `Advanced`/`Progress` and a pending-confirmation
set; red queues a fix session carrying the gate failure tails.

**The taxonomy is a pure function, and it is not in the loop (KS6.4).** `EvaluateSessionAsync` now
*gathers* evidence into a `SessionEvidence` record (`Orchestration/SessionEvidence.cs:20`), hands it to
`SessionVerdict.Decide` (`Orchestration/SessionVerdict.cs:19`) and *applies* the returned
`VerdictDecision` (`Orchestration/VerdictDecision.cs:28`). `Decide` is total, deterministic and
allocation-only: same evidence in, same decision out, on any machine, with no run in progress. Before
this, every branch of the taxonomy could only be reached by standing up a `RunContext`, a store, a git
repo and an agent process — which is why the taxonomy was the least-tested part of the engine and is
now the best-tested. When it needs more evidence than it was given it returns a **continuation**
(`VerdictDisposition.RunGateBattery`, `.ReadWorkEvidence`, `.HonourBlockUntil`) rather than reaching for
it, so the impure half stays in the caller.

**A second model may review the work; it may not score it (KS4.5).** `VerdictEngine.Judge.cs` runs the
configured review command and folds the result in as an `AdvisoryEvidence` row
(`SessionEvidence.cs:12`) tagged `judge:<command>` — one more line of evidence beside the gates and the
claims. `SessionVerdict.Decide` never reads `AdvisoryRows` to reach a disposition; the rows reach the
fix prompt and the record, and nothing else. That is enforced by test, not by convention: no code path
lets a judge's score flip a gate verdict.

### 5. Gates

`GateRunner` is a static **partial** class (`src/Conductor.Core/GateRunner.cs:6`); `RunAllAsync` at `:22` is the
whole battery. Gates are declared in `plan.gates`, filtered by stage and tier, cached per commit SHA, run
in parallel batches with non-parallel gates as barriers, and **every failed required gate is retried once
unconditionally** before the battery is called red.

The **phase gate** — the one that confirms a stage rather than a session — is
`VerdictEngine.Phase.cs:25`. Green runs the audit and then `ConfirmStageAsync` (`Phase.cs:136`), which is
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

### 5b. Gate classes — the three things an exit code cannot tell you (KS4)

A gate that exits 0 has said one thing: *this command succeeded*. Three failure modes hide inside that,
and each is a **class** declared on the gate in the plan rather than a new kind of code.

- **Holdout** (`Models/GateVisibility.cs:28`, `visibility: "holdout"`). A holdout gate is redacted
  **where it is produced**, not where it is shown: `GateRunner.RunAllAsync` takes `includeHoldout`
  (`GateRunner.cs:26`) and filters at `:29`, and everything a session can see — progress lines, the
  fix brief, the tail — carries `GateVisibility.RedactedName` via `GateRunner.Label` (`:162`). The
  session cannot tune to a gate whose name it never learns. `GateOrchestrator.cs:37` is the **only**
  call site in the engine that passes `includeHoldout: true` — the phase gate. Holdouts never run per
  session, so there is no per-session signal to tune against.
- **Regression** (`Models/GateClass.cs:34`, `class: "regression"`). Reads what still *passes* rather
  than what failed: `GateRunner.LostChecks` (`GateRunner.Classes.cs:117`) diffs this run's passing
  check names against the baseline and `ApplyRegressionClass` (`:21`) turns a lost check into a red
  gate **even though the command exited 0**. Deleting a test to get green is therefore a gate failure.
  An empty pass set is reported as `GateClass.EmptyPassSetNotice`, never as a silent pass.
- **Mutation** (`class: "mutation"`). `ApplyMutationClassAsync` (`GateRunner.Classes.cs:73`) reads a
  mutation report the gate produced and fails on a score shortfall — the suite that runs but asserts
  nothing. An unreadable report is a `MutationFinding` carrying `UnreadableMutationNotice`, not a pass.

All three say their verdict **in the class's own words** (`SessionVerdict.cs:242-261`): "a gate failed"
is wrong twice over for a classed failure, because the gate exited 0 and what is broken is the checks
rather than the code under them. A fix session told "a gate failed" goes looking for an assertion that
does not exist.

**The attempt diff (KS4.4)** is the fourth thing an exit code cannot tell you: *what this attempt
actually changed*. Each attempt gets a git worktree, the diff against its start head joins the evidence
set, and the engine's own commits (tracker regeneration, REPORT.md) are excluded so the diff carries
the session's work and nothing else. Orphaned attempt worktrees are swept at startup.

### 6. The claim, and the tracker

`conductor task --done <id>` is the only claim path, and it is one function deep:

```
TaskCommand → TaskBoard.Move (src/Conductor/Commands/TaskBoard.cs:19)
            → SqliteRunStore.ApplyTaskStatus (Store/SqliteRunStore.Sessions.cs:367)
            → TaskWrites.BuildStatusChange (Events/TaskWrites.cs:26)   ← validates the transition
            → event appended
```

`TaskBoard.Move` reports the **post-fold** status and exits non-zero on a refused transition, which is why
the CLI's output is trustworthy and intent is not.

The tracker markdown is a **generated view**: `RunLoop.RegenerateTracker` (`RunLoop.Plumbing.cs:313`) →
`TrackerGenerator.Write` (`TrackerGenerator.cs:158`), rows from the database, handoff from the latest
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

`src/Conductor.Core` declares exactly **thirteen** `public interface I*`. That is the whole list — the
abstraction count is small on purpose. *(Counted again at DV7.1, 2026-08-26: ten until Divan, which
added three at once — `ICourierSource`, `ITranscriber`, `ICloudCli`. Each one is a **process or a wire
we do not own**, which is the only justification this repo accepts for a new seam; see the rule under
the table. Before that it was nine until KS11.1 extracted `IMessageChannel`, and the count in this
sentence was stale for the whole edge era. Counted **again at CH5.1, 2026-08-27: still thirteen** —
Charkh added `Core/Release/` and four `Ci*` files and not one seam, because every type in both takes
its facts as a parameter. `grep -rn "public interface I" src/Conductor.Core` settles it in one
command — do that rather than trusting this number.)*

| Seam | Job | Implementations |
|---|---|---|
| `IRunStore` `Store/IRunStore.cs:10` | The run's durable write + query surface | `SqliteRunStore` (7 partials). Tests point it at a temp sqlite file rather than faking it. |
| `IAgentProvider` `Providers/IAgentProvider.cs:6` | Adapt one agent CLI's argv and stdout to the session loop | `ClaudeProvider`, `OpencodeProvider`, `GenericTextProvider`; factory at `:35` |
| `IProgressProvider` `Planning/IProgressProvider.cs:12` | Answer "how far along is this stage?" from an external source | `MarkdownTableProvider`, `ScriptProvider`, `PlanCheckpointProvider` |
| `IPromptBattery` `PromptBattery.cs:10` | Contribute one optional block of context to the next prompt | `LedgerBattery`, `BugsBattery`, `LessonsBattery`, `RecentFailureBattery`, `LaneArtifactBattery`, and KS7.5's two: `RepoMapBattery` (`PromptBattery.Context.cs:23`), `DefinitionOfDoneBattery` (`:113`) |
| `IEventSink` `Events/EventLog.cs:8` | Append one `ConductorEvent` | `EventLog`, `SqliteRunStore`, `NullEventSink` (dry run) |
| `IProgressSink` `Progress.cs:67` | Push snapshots/logs out to an operator, poll control commands back in | `PlainSink`; test doubles record |
| `IRunNotifier` `Integrations/TelegramService.cs:22` | The notify + remote-control channel | `TelegramService`, `NoOpTelegramService` (null object when the plan has no telegram block) |
| `IMessageChannel` `Integrations/Messaging/IMessageChannel.cs:15` | **KS11.1 — the tenth.** One messenger *transport*: send a message, upload a document, poll for inbound commands | `TelegramService` (the only real one); `FakeChannel` in the tests drives the whole surface with no wire |
| `IPlanner` `IPlanner.cs:7` | Decide the next checkpoint | `CheckpointPlanner` |
| `IReportsStartOutcome` `IReportsStartOutcome.cs:17` | Let a hosted service say it declined to start on purpose | `TelegramService` |
| `ICourierSource` `Courier/ICourierSource.cs:66` | **DV4.1.** One messenger seen from the *courier's* side: poll a batch of deliveries, reply, acknowledge. Deliberately not `IMessageChannel` — that seam is a run pushing outward with a queue to flush at shutdown, and the courier has no run to flush | `TelegramCourierSource`; the tests drive the whole daemon with no wire, incl. `KilledOnReply` for the kill-between-receive-and-ack case |
| `ITranscriber` `Inbox/Transcriber.cs:12` | **DV3.3.** Speech to text | `LocalCommandTranscriber` shells out to a configured command; tests substitute their own rather than requiring a 3 GB model on the machine, which is also what makes the untranscribed and failed paths deterministic |
| `ICloudCli` `Integrations/Cloud/CloudCli.cs:17` | **DV5.1.** The `claude` CLI's cloud subsurface | `ClaudeCloudCli`. There is deliberately **no `CreateAsync`** on it: the create direction is refused before this interface is reached, so the seam cannot be the place someone adds it |

**Divan's three are the exception that states the rule.** `ICourierSource`, `ITranscriber` and
`ICloudCli` each invert *a process or a wire this repo does not own* — Telegram's poll side, a local
speech model, another vendor's CLI — and in each case the alternative was not a fake but a 3 GB
download, a network round trip, or an Anthropic account in CI. Contrast the GitHub work below, which
added a whole subsystem and no seam at all, because there the transport already took an
`HttpMessageHandler`. **If the only other implementation would be a fake, the seam belongs at the
transport, not at the type — and if the only other implementation is "the real thing, absent", it
belongs at the type.**

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
- **Gate execution** — `public static partial class GateRunner` (`GateRunner.cs:6`). Its seams are *parameters*
  (`onProgress`, `onGates`, an optional `IRunStore` for the per-SHA cache), not types.
- **GitHub sync** — `GithubClient` + `GithubMirror` (`Integrations/Github/`). Push-only by design
  (ADR-0005): nothing read from GitHub ever reaches run state, so there is nothing for a seam to
  invert. *Read back at DV7.1 after Divan pushed the mirror onto three more surfaces — ledger issues
  (DV6.1), Projects v2 columns over GraphQL (DV6.2), code-scanning alerts (DV6.4). Two things **are**
  fetched, and both answer "what would this write do", never "what should the run do": issue identity
  against a marker in the body, and `GithubRepoInfo` — the repository read for one fact, private or
  not, so a refused SARIF upload can say why. ADR-0005's second addendum states this.* **CH1.3 added a third read and it is a different kind**
  (`Integrations/CiWorkflows.cs`): `github ci` lists the repository's active workflows and each
  one's newest run on the branch. That is not "what would this write do" — it is an answer about
  the outside world, and it does reach a surface the owner reads. The invariant survives because it
  never reaches **run state**: it lands in `<stateDir>/ci-status.json` as a dated observation
  carrying the sha it was about, and every consumer *derives* from it. Nothing in `run.db` and
  nothing in `RunStateProjection.Fold` is written from a GitHub read; if a change makes one, that is
  an ADR amendment, not a patch. The mirror is
  attached to the run rather than registered on the event path, and
  `ArchitectureBoundaryTests.TheGithubMirrorIsNeverRegisteredOnTheEventPath` holds that line by
  **file name** — only `RunContext.cs`, `RunContext.Mirror.cs` and `RunLoop.Plumbing.cs` may name it
  under `Orchestration/`. Splitting a file that names it will go red; that is the point.

## The surfaces

*Titled "the two surfaces" until DV7.1, when counting them gave four. The heading was already stale
at KS8.1 and nobody renamed it; the count is now in the subheadings instead of the title, where it
cannot rot silently.* Three of the four are **loopback HTTP** — the control plane, the MCP surface and
the courier's handover port — which is not an accident: ADR-0005 fixes the posture and each new one
inherits it rather than arguing it again.

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

- **Discovery**: `discoverControlPlane()` (`face-go/cmd/conductor-face/main.go:280`) walks *up* from the
  cwd looking for `.conductor/control-plane.json` and reads `baseUrl` + `token`. `--port`/`--token` and
  `CONDUCTOR_TOKEN` override it. The state directory is discovered separately, because the engine deletes
  the discovery file on shutdown and the Face must still render a finished run.
- **Polling**, once a second (`internal/tui/messages.go:189`, `CmdTick`), fanning out to `/state`, `/tasks`,
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

### A third, added at KS8.1 — the read-only MCP surface

*(The heading above says "two" because it was written when there were two. `conductor mcp-observe`
makes it three, and it is listed here rather than renamed away because the third is deliberately
unlike the other two.)*

`McpObserveCommand` (`src/Conductor/Commands/McpObserveCommand.cs:22`) serves MCP JSON-RPC over stdio
from `McpObserveServer` (`Integrations/McpObserveServer.cs:24`, resources in `.Resources.cs:15`). It
publishes run history, status and money as MCP **resources**. It declares **no tools capability**,
`tools/list` returns an empty array, and `tools/call` is refused `-32601` for every agent-surface tool.
Read-only is not a matter of discipline: the store is opened through `RunArchive`
(`History/RunArchive.cs:24`) with `Mode=ReadOnly`, so SQLite itself rejects a write — the refusal is at
the connection, not at a policy check. ADR-0007
records why control operations are excluded by design.

`conductor history export --atif` (`Core/Interop/AtifExport.cs:38`) is the other half of the same idea:
a run leaves as an ATIF-v1.7 trajectory, billed costs included, for tooling that was never going to
speak conductor's schema.

### The messenger, and why it is no longer "the telegram service" (KS11)

*Read the word "courier" below as the ordinary English one. Divan gave the name to a real namespace
and a real process (`Core/Courier/`, `conductor courier`), so this paragraph's metaphor now collides
with it: `Integrations/Messaging/` is the **messenger**, in-process, owned by a run. The courier is a
daemon that outlives every run. They meet at `CourierChannel`, and nowhere else.*

`Integrations/Messaging/` is the channel-agnostic half of the messenger: composition
(`MessageComposer`, `:24`), the command router (`CommandRouter.cs:76`), chat profiles
(`ChatProfile.cs:11`), evidence browsing, rate limiting and the push grammar. `TelegramService` is now
one `IMessageChannel` implementation — the transport — and the tests drive the whole surface through a
`FakeChannel` with no wire at all.

**Profiles are per chat, and the observer surface is closed.** `ChatProfiles.TryParse`
(`ChatProfile.cs:46`) refuses an unknown profile string **by name at plan load**, not at first use.
An observer chat may ask for status, tasks, progress, evidence and the daily digest; a control verb
from an observer chat is refused. Plans written against the old `allowedChatIds` shape behave
byte-identically, which is pinned by golden replay rather than asserted in prose.

## The courier — the one process that outlives the run

*Added at DV7.1, 2026-08-26, because Divan put a daemon on the machine and this document had no
section a long-lived process could live in. The decision and its four conditions are
[ADR-0008](docs/dev/adr/0008-the-courier-outlives-the-run.md); this is where it sits in the map.*

Every other process here is born and dies with a run. The courier is not. It owns the bot token, it
polls when no run is live, and it files what the owner said into whichever project the note was about.
`Core/Courier/` is its namespace (15 files, no partial fiction — one type per file); `Commands/Courier
Command.cs` is the CLI; `Http/CourierListener.cs` is its listener and is constructed **only** by
`conductor courier run` (`CourierCommand.cs:276`), never by the run process.

**The lifecycle is a verb, not a flag.**

| | |
|---|---|
| `conductor courier install` | Registers a per-user Scheduled Task from XML — logon trigger, restart-on-failure, no admin rights. XML rather than `schtasks /SC ONLOGON` because the command-line form cannot express restart-on-failure (`CourierTask.cs:37-47`). `CourierTask` takes its shell runner as a constructor argument, so the suite never registers anything on a developer's machine |
| `run` / `status` / `restart` / `stop` / `uninstall` | `status` is the default verb. `restart` is the fix named by every staleness refusal |
| `allow --repo` / `deny --repo` | The project allowlist — a note is only ever filed into a repo the owner allowed |
| `chat --id` / `unchat --id` | Which chats the courier answers at all |

**The state it owns** lives in the state home under `courier/` (`CourierHome.cs:21`), not in any repo:
`courier.json` (settings + allowlist), `offset.json`, `courier.run.json` (presence), `courier.secret`,
and `media/`. A note whose project has moved or vanished is parked in `dead-letter/` rather than
dropped.

**Three invariants a change here must not break:**

1. **The offset is durable.** `TelegramService`'s `_offset` is an `int` field, correct for a poll loop
   that dies with its run. A restarting courier with an in-memory offset replays every update Telegram
   still holds and files each note twice. `CourierOffset` persists it; delivery dedups by id.
2. **The handover port is loopback with a secret.** Fixed at 47137 (`CourierEndpoint.cs:27`, override
   `CONDUCTOR_COURIER_PORT` — a *named* port, never a scan, because two conductor runs may share a
   machine), `127.0.0.1` only, `X-Conductor-Courier` matched by `CourierSecret` or `401`
   (`CourierListener.cs:112`). `/hello` and `/push`; nothing that arrives there writes run state.
3. **One consumer per token.** Where a courier is configured, in-run polling refuses to start and
   names it (`CourierPrecedence.cs:34,43`). A machine with no courier keeps the old behaviour
   byte-identically. The protocol states its version (`CourierProtocol.Version = 2`) and a run
   speaking a newer one refuses a stale courier by name, naming `conductor courier restart`.

**And the limit, stated rather than hidden:** the courier narrows the gap from "no run live" to
"machine on". Telegram holds an undelivered update for 24 hours. A note sent to a sleeping laptop on
Friday is gone by Monday — dropped by Telegram, never handed over.

**The installer owns the restart.** A running courier holds the published exe open, so
`tools/install.ps1` stops it at step 0 and puts it back on the *new* engine afterwards
(`install.ps1:77-99`, `tools/lib/courier-guard.ps1`). Publishing the engine around a live courier by
hand is how you get a file lock, or worse, a daemon quietly running last month's build forever.

### The inbox — what the courier files, and what a session reads

`Core/Inbox/` (11 files). A note lands in the target repo at `.conductor/inbox/` — `notes/<id>.json`,
an append-only `index.jsonl`, `cursor.json`, `media/`. Written with temp-file-plus-rename
(`AtomicFile`) because the courier writes while a session reads.

It reaches a session as a prompt battery and nothing else. `InboxBattery` is assembled **last** in
`PromptBuilder` (`PromptBuilder.cs:366`) on purpose — a reader meets the engine's own knowledge first
and the untrusted text last, framed. Immediately after assembly the battery marks seen only what it
actually **carried** (`:373`): the counted remainder stays unread and reaches the next session rather
than being skipped, and the battery cannot grow without bound on a long-lived project.

Promotion into a followup or a task is an explicit act — `conductor inbox` (`list`, `show`, `add`,
`transcribe`, `parked`, `prune`) plus a human, or DV4.4's single promote button. That is what keeps
ADR-0005's invariant true: **a note is context, never a command.**

### The rest of what Divan added, and where it lives

| Surface | Where | Note |
|---|---|---|
| Transcription | `Core/Inbox/Transcriber.cs` | `ITranscriber`; `LocalCommandTranscriber` shells to `courier.transcribe.command` (env `CONDUCTOR_TRANSCRIBE_COMMAND`). Local, no network |
| The cloud lane and `/cloud` | `Core/Integrations/Cloud/` | Owner-only chat verb + an opt-in per-session review lane. `plan.cloud.enabled` defaults **false** with deliberately no env override. `CloudPreflight` refuses a dirty tree or an unpushed branch **in the chat**, quoting the git state that blocked it — the referee stays local |
| The board snapshot | `Core/Publishing/` | `board.html`, rendered from `Http/Contracts` at each boundary and **pushed** as a Telegram document. It states its own staleness. Nothing inbound |
| Ledger issues, Projects v2 columns, SARIF | `Core/Integrations/Github/` | Three more push surfaces; see the seams table and ADR-0005's second addendum |
| Plan config | `Models/CourierConfig.cs`, `Models/CloudLaneConfig.cs` | `plan.courier` and `plan.cloud`. Both carry a `Refusal()` so a wrong key is named at load, not at first use |

### What Charkh added, and where it lives

Charkh's subject is *the owner's hands*: the acts a person still performed between eras — reading a
runbook and doing seven things by hand, agreeing with a document about what the binary does, asking
whether CI ran — became something the engine measures or performs. Three new areas, **and no new
seam**: recounted at CH5.1 (2026-08-27), `src/Conductor.Core` still declares exactly thirteen
`public interface I*`, for the same reason KS9's GitHub sync added none — every one of these takes
its facts as a parameter, so the test double is a record, not an interface.

| Surface | Where | Note |
|---|---|---|
| The era-close, measured | `Core/Release/` (10 files) | `ReleasePreflight` turns six fact records into six `ReleaseCheck`s — merge, changelog, processes, migration, courier, backfill. **Every file here is pure**: facts in, verdict out, no `Process`, no `HttpClient`, no `Console`. The shelling lives in the CLI partials, which is exactly why three verbs can share it |
| The era-close, performed | `Core/Release/ReleasePerform.cs` | Four mechanical acts (`MechanicalOrder`, `:37`) and five owner acts (`OwnerOrder`, `:40`) as **data**. An owner act is *refused by name*, never skipped — in prose "this one is yours" and "nobody did this one" are the same sentence, and that ambiguity lost six of KS12.3's seven acts for an era |
| One source of truth for all three | `Commands/ReleaseCommand.cs:182` (`MeasureAsync`), `ReleaseCommand.Perform.cs:84` (`RunActsAsync`) | `preflight`, `perform` and `runbook` call **these two and nothing else** — `runbook` (`ReleaseCommand.Runbook.cs:49-50`) calls both with `dryRun: true` and renders. A generated runbook cannot disagree with the preflight that produced it, because there is no second measurement to disagree with |
| What CI actually said | `Core/Integrations/CiStatus.cs`, `CiWorkflows.cs`, `CiAgreement.cs`, `CiBatterySignature.cs` | Every **active** workflow, then its newest run **on this branch** — not the commit's check-runs, which list only the workflows that commit triggered, so a schedule-only workflow is invisible there. `CiAgreement` is the other half: whether CI runs the same commands `plan.gates` does, so the two batteries cannot differ in silence for an era again |
| Whose board is this | `Integrations/Github/GithubIdentity.cs:56` (`OwnerMarker`) | An issue is retirable only if that marker names **this** run, or this run's `GithubMap` points at that number. Everything else is refused by name on `GithubSyncResult.RetireRefused`. Bug #84 was a repo-wide sweep that closed another era's 23 checkpoints on every era transition |
| The docs, diffed against a binary | `tools/ch3/` (`docs-surface-diff.py`, `link-sweep.py`, `dump-help.ps1`), `tests/Conductor.Tests/SF7_1DocsMatchRealityTests.Charkh.cs` | The battery derives its expectations from `Program.cs` and the demo manifest rather than from a hand-typed list, and each pin has a **negative control** — blank one option, remove one artifact, and the failure names that exact verb and file |
| The demo GIF's own staleness | `docs/assets/demo.manifest.json`, `tools/demo/make-demo-gif.ps1` | The recording states what it recorded; a README caption that counts a tour the GIF no longer takes is red. Ported from payesh's `scripts/seo.mjs` — a gate that refuses a merge when a published image no longer renders what it was taken from |

**`ci-status.json` is the one place DV1.1's "derived, never stored" rule is *bent on purpose*, and
the reasoning is worth copying** (`CiStatus.cs:20-31`). What is stored is not health — a stored
health record outlives the condition that raised it — but a **dated measurement carrying the commit
it was about**. Health is derived from it every time a surface asks, so the moment `HEAD` moves past
`CiStatus.HeadSha` the derived answer becomes "CI has not judged this commit". There is no clearing
step and no way for a stale green to be reported as a current one. It is stored at all because the
`REPORT.md` header and the owner queue must render on a machine with no network and no token,
exactly as `conductor report` does after the run is over.

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
`ratchet-baseline.json` carries the other two floors, **re-measured at CH5.1 (2026-08-27) rather
than repeated**: `minTests` **1932** (only ever rises — the suite actually carries **3044**
`[Fact]`/`[Theory]` attributes today, so the floor is 1112 below the real count and is drifting
back toward the formality KS1.6 fixed), `maxPragmas` **31** (only ever falls; the live count under
`src/` is **31** — exactly at the ceiling, green). *The two sentences this file carried until CH5.1
said 38 and "red at 43 today — bug #44". Both were two eras stale: KS6.2 ratcheted the ceiling
38 → 31 by proving 14 of the 45 disables dead, and bug #44 is fixed. A doc that reports a green
gate as red is worse than one that says nothing — it teaches the next session to expect a failure
and to explain it away when it does not come.*

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
| **a CLI verb** | `src/Conductor/Commands/<Verb>Command.cs` | `c.AddCommand<…>("verb")` in `Program.cs:46`ff; `docs/cli.md` |
| **a gate** | `plan.gates` in the plan JSON — gates are configuration, not code (`Models/GateConfig.cs:5`) | nothing in the engine; get the path right, a wrong one can exit 0 |
| **support for another agent CLI** | `Core/Providers/<Name>Provider.cs` implementing `IAgentProvider` | the factory switch at `Providers/IAgentProvider.cs:35` |
| **a block of context in every prompt** | an `IPromptBattery` in `Core/PromptBattery.*.cs` | the assembly list in `PromptBuilder.BatterySection` (`PromptBuilder.cs:273`ff) |
| **a session kind or a template** | template file under the plan's `templatesDir`, with a built-in fallback in `PromptBuilder.BuiltIns.cs` | `ResolveSessionKind` (`SessionRunner.Kinds.cs:37`) and `BuildPrompt`; **sweep the template for stray `{braces}`** — `PromptValidator` throws on an unresolved one and the run parks |
| **a plan config key** | the record in `Core/Models/` (`PlanConfig.cs:9`, `LimitsConfig.cs:5`) | `docs/plan-config.md`; give it a default, or every existing plan stops loading |
| **a tab or panel in the Face** | `face-go/internal/tui/tab_<name>.go` | the `tabKey` mnemonic map at `model.go:59` **and** the hand-maintained help legend in `cmdbar.go` — both, or the help lies. Read `face-go/STYLE.md` first |
| **a control verb** | `ControlAction` in `Core/Progress.Control.cs` and the `/control` handler | the Face's verb table at `cmdbar.go:66` — the strings do not map by name |
| **a GitHub-synced surface** | `Core/Integrations/Github/` — a request shape in `GithubRequests.cs`, its wire record in `GithubDtos.cs`, and the call on `GithubClient` | register the DTO in `GithubJsonContext` or it will not serialise; if a run-loop file has to *name* `GithubMirror`, `TheGithubMirrorIsNeverRegisteredOnTheEventPath` fails — go through `RunContext.MirrorBoard` / `MirrorFinalPass` instead |
| **a store column or table** | a new `Core/Store/Migrations/vNN_<what>.sql`, embedded as a resource | **`MigrationRunner.CurrentVersion`** (`MigrationRunner.cs:11`) — and the tests that pin it by literal (`RunDbTests`, `K3_3ProvenanceTests`, `K4_1ContextWindowTests`). Those literals exist so the bump is *decided*; KS9.2 shipped v14 and left all three behind. Note the store migrates on **every** open (`MigrationRunner.cs:21`), so a newer build run against a live store locks an older engine out of it (bug #45) |
| **an inbox note kind, or anything the courier files** | `Core/Inbox/` for the note, `Core/Courier/` for the daemon side — one type per file, no partials | the note reaches a session **only** through `InboxBattery` (`PromptBuilder.cs:366`); if you add a path that promotes without a human, you have changed ADR-0005 and ADR-0008, so amend them |
| **a courier subverb** | the `switch` in `CourierCommand.cs:97`ff | `docs/cli.md`, and `courier status` — a verb the status line never mentions is a verb the owner never finds |
| **a published artefact** (a page, a report, a document pushed out) | `Core/Publishing/`, rendered from `Http/Contracts` | it must state its own staleness and go out as a **push**; a fetchable surface is a different ADR |
| **an era-close precondition or act** | a fact record in `Core/Release/ReleaseFacts.*.cs` and a pure `ReleaseCheck`/`ReleaseAct` builder beside it; the shelling that gathers the facts goes in `Commands/ReleaseCommand.Probes.cs` / `.Perform.cs` | `ReleasePreflight.CheckNames` (`:27`) or `ReleasePerform.MechanicalOrder`/`OwnerOrder` (`:37`,`:40`) — `CH4_4ReleaseRunbookTests` derives the vocabulary **by reflection**, so an act wired to nothing fails the day it is added, and `release runbook` renders it with no further work |
| **an observation about the outside world** (CI, a remote's state) | a dated record in `Core/Integrations/`, written to `<stateDir>/` and **derived** at every read — `CiStatus.cs:20` is the pattern | store health instead of the observation and it outlives the condition that raised it (DV1.1). Carry the sha or the timestamp it was about, or a stale green reads as a current one |
| **an architecture rule** | `tests/Conductor.Tests/ArchitectureBoundaryTests.cs` (**13** `[Fact]` rules today, counted at DV7.1) | make the failure message name the offending type; a rule that says only "boundary violated" costs the next session an hour |
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
