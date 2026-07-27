# W-series — One Work Graph (design brief)

**Tracker:** `CONDUCTOR-WORKGRAPH.md` (root) · **Plan:** `plans/conductor-workgraph.plan.json` ·
**Evidence base:** `docs/GAP-ANALYSIS.md` (2026-07-27, commit `875169c`) — read it first; this
brief does not re-derive its findings.

## The owner's acceptance criteria (the whole point — verbatim intent)

1. **Plan in → Kanban out.** Put a plan (one of the existing conductor plans, or a similar doc)
   into the system and the Kanban board is built from it. No hand-authoring a second copy.
2. **Everything stays in sync.** Tweak the plan, the tasks, or the cards — tasks, database, TUI,
   and the pipeline all reflect it. One truth, many views.
3. **Prompt building blocks are visible and real.** The user can see the blocks a session prompt
   is built from, and tweak them — and what the card detail shows IS what the agent receives.
4. **Add work in flight.** Mid-run, "we realize there's a requirement for another task" — add it
   from the TUI (or CLI/MCP); it lands in the graph, the board, and the engine's schedule without
   a restart.
5. **Pipeline control per task.** Sometimes QA a specific task; sometimes just deliver tasks
   one-by-one with no verify step. The dial exists at plan/stage level (P2); it must reach the
   individual work item.

Criteria 1–2 land in W1, 3 in W2, 4 in W1+W2, 5 in W4. **W5 proves all five in one unattended
real-model run.** Nothing is "met" until W5's evidence exists.

## Design principles

- **The event-sourced work graph in run.db is the single runtime truth for work items.** This is
  the deferred FU-F1-03 decision (`.conductor/handovers/F1.md:49`), now taken. Plans *declare*
  work; the graph *is* the work; the tracker markdown and every Face surface are generated views.
- **Sync is upsert-with-provenance, never clobber.** Declared work syncs into the graph by id
  (add / update title-notes / retire-as-archived). Runtime status (in_progress, done, confirmed)
  is never overwritten by a re-sync. Removing a declared item archives it, never deletes history.
- **One claim path.** An agent reports progress through exactly one instructed mechanism, and the
  verdict engine reads exactly that mechanism. No surface that "achieves nothing".
- **Views never write around the graph.** A card move, a tracker regen, a sidebar chip — all read
  the same projection; writes go through `TaskWrites`-style validated events only.
- **Determinism by default** (unchanged from the P-series): the advisor is consulted only where
  the owner explicitly invokes it — import, refine, split. Never inside scheduling.
- **Reuse, don't fork:** `TaskGraph`, `TaskWrites`, `QaPolicy`/`QaProjection`, `PromptComposer`,
  `PlanDiff`, the G3 reload machinery, and the Face Kanban/Plan tabs all exist. The W-series
  rewires them; it should add few new surfaces.
- **Anti-cheat stays.** The M4.1 idea (independent verification of claims) survives: claims flip
  status; *confirmation* still requires the verdict engine's gates+commits+verify evidence.

## W1 — One work graph (keystone)

The single biggest change; everything else lands on it. Migration note: `run.db` is per-run
state — a schema/event change needs a migration (`Migrations/`) but no long-lived data care
beyond the current run resuming cleanly.

- **W1.1 — Unify checkpoints and tasks into one graph.** Work items get a `kind`
  (`checkpoint` | `subtask`) and `provenance` (`plan` | `tracker` | `import` | `human` | `agent`)
  on the existing `TaskAdded`/`TaskStatusChanged`/`TaskDetailEdited` event family
  (`Core/Events/TaskGraph.cs`). The mutable `checkpoints` table (`Migrations/v2_checkpoints.sql`,
  written in place by `SeedCheckpoints`/`UpdateCheckpoint` — the ADR-0002 violation) becomes a
  projection of the graph (or is dropped in favor of folding; keep whichever is cheaper for
  `TrackerGenerator`/`status`). `CheckpointConfirmed` joins the same fold. Amend ADR-0002.
  Truth gate: replaying the event log reproduces checkpoint state byte-for-byte; the two seeds
  that disagreed on restart (gap G4) are gone because there is only one.
- **W1.2 — Plan → graph sync at every boundary.** One `WorkGraphSync` service invoked at run
  start (replacing the seed-once at `RunLoop.cs:80`), inside `ApplyPlanReload`
  (`RunLoop.cs:399-443`), after `/plan/edit` + `/plan/import` apply, and by `plan add-stage`
  (delete the "don't forget" printout, `PlanAddStageCommand.cs:59`). Declared sources: tracker
  markdown (existing plans — criterion 1) and inline `progress.checkpoints`; both normalize to
  the same declared-work list. Upsert semantics per the design principles. Validation:
  `PlanConfig.CollectErrors` + `doctor` gain a stage↔work-item coverage check (gap G13 — a
  stage with zero items is an authoring-time error, not a mid-run NeedsHuman).
  Truth gate: a live fake-agent run where a stage is added via `/plan/edit` mid-run and its
  cards appear on the board without a restart.
- **W1.3 — Claims from the graph; tracker demoted to view.** `conductor task --done`
  (`TaskCommand.cs`) and MCP `task_update` emit graph events (not bare `checkpoints` UPDATEs).
  `VerdictEngine.EvaluateSessionAsync` computes `newlyDone` from graph claims made during the
  session (`VerdictEngine.cs:281-303` — the tracker-markdown diff stops being the signal; keep a
  transition-period fallback that ALSO accepts tracker flips, flagged in the log, so old habits
  degrade gracefully instead of silently). The M4.1 hand-edit veto inverts naturally: a tracker
  hand-edit is now simply not a claim. `TrackerGenerator` remains the only tracker writer.
  **Fix bug #6 here:** verify dispatch consumes `PendingVerify.StageId`
  (`SessionRunner.Kinds.cs:55` currently passes the loop's current stage; the field is written
  at `VerdictEngine.Workflow.cs:55` and read by nothing). Truth gate: re-run the U-series
  incident shape — a fake agent that claims ONLY via `task --done` and never touches the tracker
  gets `newlyDone = [the item]`, and a post-advance verify session's prompt names the delivered
  stage, not the next one.
- **W1.4 — All views, one projection.** `/state`'s sidebar/chips (`SnapshotBuilder.cs:29-69`,
  today re-reading the tracker markdown) and `GET /tasks` (Kanban) serve the same graph
  projection — the "sidebar full / Kanban empty" split (gap G11) becomes impossible. Card status
  moves route through the graph's legal-transition rules; dragging a card to Done records a
  **claim** (subject to verdict confirmation like any other claim), never a silent no-op.
  Engine-side checkpoint transitions emit the same events, so a checkpoint going DONE mid-run
  moves its card (gap G6 both directions). Face: sidebar + Kanban read the unified DTOs; goldens
  updated. Truth gate: golden + a live wire test where a verdict flip is visible on the board
  before the next session starts.

## W2 — The claim path works on the real provider, and prompts are honest

- **W2.1 — Claude-shaped MCP + a working CLI fallback.** Emit a claude-compatible MCP config
  (`--mcp-config`/`--strict-mcp-config` on the spawned CLI) alongside the opencode shape
  (`SessionRunner.Mcp.cs:79-95` currently writes only `OPENCODE_CONFIG`). Inject
  `CONDUCTOR_PLAN=<plan path>` into the child env (`AgentSession.cs:113-116`) and make
  `PlanSettings.ResolvePlanPath` honor it, so `conductor task/bug/note` inside a worker stops
  dying on "multiple plan files found" (`PlanSettings.cs:43-47` — the four U-series
  `crash-*.log`s). Truth gate: a live claude-CLI session (cheap model, one checkpoint) whose
  session log shows non-empty `mcp_servers` AND whose `task --done` claim lands.
- **W2.2 — The board is live during a session.** Agent MCP task writes stop waiting for
  session end (`SessionRunner.Mcp.cs:112` folds `mcp-journal.jsonl` only at close): journal
  entries are folded into served projections on read, or MCP writes emit graph events directly
  with a single id allocator (closing the `TaskWrites` id-collision race, gap G10). Truth gate:
  wire test — `task_add` from a running session is visible on `GET /tasks` within one poll.
- **W2.3 — One prompt composition; one instruction.** `PromptBuilder` renders the task-scoped
  section through `PromptComposer`'s blocks so `GET /prompt/blocks` shows exactly what the
  session receives (today it's a parallel, non-authoritative rendering —
  `ControlPlaneServer.TaskPrompt.cs:39` is its only consumer). Card `Title`/`Context` reach the
  prompt (criterion 3); rewrite `ToolContract.cs:56-63` + `PromptBuilder.cs:235` so the agent is
  told exactly one claim path (the W1.3 one) — the current text instructs a hand-edit in one
  paragraph and calls it pointless in the next (gap G7/G8). Truth gate: byte-compare — the
  composed blocks concatenation equals the prompt.md section on disk for a real session.

## W3 — Autonomy rails (independent of W1/W2; interleave as convenient)

- **W3.1 — Watchdogs with teeth.** Hard session timeout moves to an independent timer (today a
  wall-clock check inside the poll loop, `SessionRunner.cs:286-291` — bug #8's 337-minute hang),
  with a monotonic-vs-wall-clock jump check for sleep/hibernate. `StallDetector` filters
  liveness to `bg:*` purposes only (`StallDetector.cs:52-56` + `SqliteRunStore.Pids.cs:41-57`
  count the agent's own pid and the face as "activity" — zero stall detections in every log ever
  written). A hung/stalled session fires the notification path (Telegram/webhook), not just
  NeedsHuman parks. Truth gate: a fake agent that goes silent trips the stall rail at ~15m and
  a rigged short timeout kills a hung session on time, both in live tests.
- **W3.2 — Auth is a first-class failure.** Classify `401`/`authentication_failed` from the
  provider stream (`ProviderText.cs:8-13` matches only quota/429; `ClaudeProvider.cs:39-41`
  flattens the envelope): outcome parks immediately with "re-auth: claude setup-token", no gate
  battery, no retry burn, distinct Telegram message. `doctor` + preflight gain an auth smoke
  test (a ~$0.001 one-token ping, opt-out flag) so a run cannot start on a dead token. Truth
  gate: replay session #13's raw log through the classifier → auth-park, not AgentError.
- **W3.3 — Process rails.** `SetConsoleCtrlHandler` wired to the graceful-stop path
  (window close / logoff — §7.5, the accidental-✕ data-loss risk; `RunCommand.cs:92` only hooks
  CancelKeyPress). `ReapOrphans` guards pid reuse (start-time match) before tree-killing
  (`ProcessSupervisor.cs:68-103`; 3 stale unexited pids sit in run.db right now). Unbounded
  spend gets a loud start-of-run warning + `doctor` warn (default caps stay owner policy, not
  hardcoded). Fix `conductor bg start`'s dead log pump (bug #2, `BgStartHandler.cs`). Truth
  gate: close the console window on a live fake-agent run → clean park + resumable run.db.

## W4 — AI-native bootstrap + per-task pipeline control

- **W4.1 — Import carries the work.** `ImportResult` gains checkpoints:
  `MarkdownPlanParser.ToImportResult` (`MarkdownPlanParser.cs:117-135`) stops discarding the
  `ParsedCheckpoint` lists it already built; the advisor import contract
  (`PlanImportService.cs:167-186`) gains the matching `checkpoints` key per stage; apply writes
  declared work through W1.2's sync so an imported plan is **drivable immediately** — no
  hand-authored tracker table. Deterministic path also proposes default gates from repo-type
  detection (reuse `InitCommand`'s marker logic) instead of zero. Truth gate:
  `plan import docs/MAESTRO-PLAN.md` on a scratch repo → `conductor run` reaches session 1 with
  a populated board, no hand edits in between.
- **W4.2 — Idea-first entry.** `conductor init` writes a commented advisor block and gains
  `--from-idea "<prose>"` (or `--from-idea <file>`): init scaffolds, routes the prose through
  the advisor import path, syncs the graph — one command from idea to drivable plan
  (`InitCommand.BuildPlanJson:127-148` today writes no advisor, and both prose ingresses
  hard-refuse without one). Truth gate: on an empty scratch repo,
  `conductor init --from-idea "..."` then `conductor run --paused` shows stages + cards in the
  Face.
- **W4.3 — Split a task with AI.** Card detail gains "split into subtasks": the advisor
  proposes child items (propose→confirm, like `/tasks/refine` —
  `ControlPlaneServer.TaskPrompt.cs`), landing as `subtask`-kind graph items under the card.
  Also: a rough card can be added at *stage* level (today `TaskWrites.BuildAdd` requires an
  existing checkpoint parent; a stage-level add becomes a `checkpoint`-kind item the engine
  will schedule — the second half of criterion 4). Truth gate: add a stage-level card mid-run
  via the Face, split it, and watch a fake-agent session claim it.
- **W4.4 — The QA dial reaches the item (criterion 5).** Work items gain
  `qa: inherit | verify | off` (graph detail event + `/tasks/edit` + card-detail editor, next to
  the P3 context/paths fields). `DefaultQaPolicy`/`QaProjection` consult the claimed item's
  override when projecting the workflow for a session — "deliver these one-by-one, but verify
  THAT one". Plan/stage dials unchanged as the inherited default. Truth gate: live fake-agent
  proof — two items in one stage, one `qa: off`, one `qa: verify`: the first goes
  deliver-only, the second gets a verify session (same shape as `P2QaDialLiveTests`).

## W5 — Proof run (the gate — nothing above counts until this exists)

- **W5.1 — Rehearsal, no spend.** Full credential-free dress rehearsal (fake-agent): a
  3-stage toy plan imported from a markdown doc (W4.1 path), driven end-to-end, exercising every
  in-flight lever mid-run — add a card, split it, flip one item's QA, edit a card's context,
  edit the plan — all picked up without restart. Produces the first-ever `RunFinished` event.
- **W5.2 — The real thing (HUMAN: owner starts it; paid).** A real-model (cheap tier),
  multi-stage plan driven **unattended** start → `RunFinished`: budget cap set, watch-run armed
  but expected idle, zero human interventions. The five acceptance criteria at the top are each
  demonstrated and written up as an audit (`docs/workgraph/W5-AUDIT.md`) with run.db/log
  evidence. Fix what bleeds; re-run until clean.

## W6 — GitHub-ready (after W5, so CI is born green on the fixed engine)

- **W6.1 — Legal + weight (HUMAN: license choice; history-purge decision).** LICENSE (owner
  picks MIT or Apache-2.0); `git rm -r --cached publish/` + ignore it (72 MB, incl. iOS static
  libs, commit `a558d56`); owner decides whether to `git filter-repo` the history; `.gitignore`
  hardening (`publish/`, `*.log`, `.idea/`, `.DS_Store`, `.claude/settings.local.json`) +
  `.gitattributes` (Go goldens: LF).
- **W6.2 — CI.** `.github/workflows/ci.yml`: windows-latest full battery (dotnet build/test,
  face-go build/vet/test, ratchet via pwsh — mind the exact path `tools/gates/ratchet.ps1`) +
  ubuntu-latest dotnet+go legs; `dotnet-version: 10.x`, `go-version: 1.26`; badge born green.
- **W6.3 — README + face.** Prerequisites (.NET 10, Go 1.26, agent CLI), platform statement,
  badges, a VHS demo GIF of `conductor-face --demo` under the H1; fix/fold `docs/quickstart.md`
  (says .NET ≥ 9); `docs/README.md` index for the 135-file docs tree.
- **W6.4 — Hygiene + landing.** Archive historical trackers to `docs/archive/trackers/`
  (keep live ones at root); delete `conductor-CLEANUP.md`; scrub foreign-project refs
  (`plans/shamshir-p0.plan.json`, `FUSION.md` local paths, evidence `*-gate.txt` MSBuild logs);
  CONTRIBUTING.md + SECURITY.md; merge `feat/foreman` → `master` (public tip is 944 commits
  stale) and make `master` the branch CI protects.

## Out of scope (explicitly, so it isn't re-litigated mid-series)

- A serverless Face authoring mode (Face before any run exists) — W4.2's CLI bootstrap + the
  existing `run --paused` authoring flow cover the need; revisit after W5 evidence.
- Cross-checkpoint dependency scheduling beyond the existing stage `dependsOn`.
- Multi-repo / multi-run orchestration; provider plugins beyond claude+opencode.
- Telegram phone dogfood (still credential-gated; can ride W5.2 if the owner wants).

## Driving mode

W1–W4, W6: **Claude Code drives directly** (owner directive 2026-07-16 — the session IS the
delivering agent; tracker + this brief are the worklist; gate battery per checkpoint; commit +
push per checkpoint). W5.1 is conductor driving a toy plan (that's the test); W5.2 is conductor
driving itself for real, owner-started. Gate battery: `dotnet build Conductor.slnx` ·
`dotnet test Conductor.slnx` · `cd face-go && go build ./... && go vet ./... && go test ./...` ·
`powershell -File tools/gates/ratchet.ps1` (exact path — the wrong path exits 0).
