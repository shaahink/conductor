# AGENTS.md resume log, 2026-07-09 to 2026-07-31 (archived at K2.4)

The append-only handoff stack that used to sit in `AGENTS.md` at the repo root. Nine superseded
`## Resume here` sections plus the era snapshots that followed them, kept verbatim because they are
the record of how the project got here - and moved out because a fresh session was reading 600 lines
of dead instructions to find the twenty that were live.

Newest first, as they were stacked. Anything here describes a CLOSED era: the live one is in
`AGENTS.md`, and the spec for it is under `docs/history/`.

| Era | Section | Closed |
|---|---|---|
| Sarban | Resume here (SARBAN ERA - field-report-driven evolution) | 2026-07-31 |
| U-series | Resume here (U-SERIES IN FLIGHT - conductor drives itself) | 2026-07-17 |
| P-series | Resume here (P-SERIES CLOSED + PF follow-ups landed) | 2026-07-16 |
| face-go UX | Resume here (face-go UX pass - gaps review closed) | 2026-07-15 |
| Maestro M9 | Resume here (Maestro M9 complete - plan CLOSED, 30/30) | 2026-07-15 |
| Maestro M8 | Resume here (Maestro M8 complete) | 2026-07-15 |
| Maestro M7 | Resume here (Maestro M7 complete + Ink face retired) | 2026-07-15 |
| Maestro M6 | Resume here (Maestro M6 complete + face-go mission-control pass) | 2026-07-15 |
| Maestro M6 | Previous resume point (Maestro M6 complete) | 2026-07-15 |
| Baton / Foreman | Read order, deliverables, QA protocol, current state (post-F7 first pass) | 2026-07-11 |

---

## Resume here (SARBAN ERA — field-report-driven evolution, 2026-07-31)

**Read this first if you're the fresh session.** The active era is **Sarban** — two self-hosted
plans built from three real-run field reports, the owner's screenshot critique, and the driver
skill's field log. The spec is `docs/history/CONDUCTOR-SARBAN.md` (mission, per-stage acceptance,
traceability); the plans are `plans/conductor-sarban-core.plan.json` (SC1–SC8: Telegram,
truthful surfaces, config-trap validation, verdict correctness, wait/detach/board verbs, history
hygiene, structured transcript, versioning + self-update) and `plans/conductor-sarban-face.plan.json`
(SF1–SF7: kill the SQL console + tab consolidation, honest Home/time/money, digest layer + kanban
clarity + git awareness, the owner queue, `conductor watch` supervision, prompt bank, era close).
Trackers: `SARBAN-CORE-TRACKER.md`, `SARBAN-FACE-TRACKER.md`. Branch: `feat/sarban`; everything
below this section describes EARLIER eras — historical record, not current instructions.

Era rules: the core plan runs first, driven by the engine published from this branch at launch;
**never run `tools/install.ps1` mid-run** (the owner reinstalls between plans); keep tracker
handoffs and plan prose brace-free until SC3.3's fix is the engine driving you; the field-notes
files under `docs/dev/FIELD-NOTES-*.md` stay **untracked** (they carry private client context —
same scrub rule as commit 5cf77f1).

## Resume here (U-SERIES IN FLIGHT — conductor drives itself, 2026-07-17)

**Read this first if you're the fresh session.** The active era is the **U-series** — the owner's
UX era (landing page, workspace identity, organized/promptable controls, visual report, dev stats,
themes, Claude-Code/opencode vibe, glitch pass) — and it is the first plan **conductor drives
itself** with real claude-native sessions. This SUPERSEDES, for this plan only, the older "do NOT
run `conductor run`" directive below: the owner explicitly asked for conductor-driven delivery.
The Claude Code session's role is **supervisor** (author/monitor/intervene); never edit the working
tree while an agent session is live.

- **Session #4 (FIX, 2026-07-17) — corrected the record + fixed the verifier crash.** Session #3
  (Verify) ended AgentError ("verifier session produced no valid score JSON"). Root cause:
  `SessionRunner.ExtractSessionResult` ran the verifier's full output through the same 700-char
  SESSION-RESULT: crop used for Deliver/Fix narrative summaries — session #3's real output was a
  valid 2682-char JSON verdict (score 66, WARN) but got chopped mid-string, so `Verifier.Parse`
  never saw the closing brace. Separately, `Verifier.Parse`'s regex forbade ANY brace character
  anywhere in the match, so a finding quoting a `{model}`/`{planDoc}`-style placeholder (this repo's
  docs are full of them) could break it even without truncation. Both fixed: `ExtractSessionResult`
  skips the 700-char convention for `SessionKind.Verify` (capped at 16,000 instead), and
  `Verifier.Parse` now scans for balanced top-level `{...}` spans (string/escape-aware) instead of a
  single-level regex. Proved live: a new test drives a real Deliver-then-Verify pair through the
  orchestrator with a >700-char verdict containing a quoted `{model}` — reaches `Progress`, not
  `AgentError`. Below, "U0 is CLOSED, 3/3" was true in substance (session #3's own analysis, before
  it crashed, independently confirmed the code for all three checkpoints) but false in the system of
  record — `CONDUCTOR-UX-START.md`/run.db still showed all three TODO, since session #2 never called
  `conductor task --done`. Claimed now (commits `199f2c8`/`66e6f57`/`84fe84f`), matching the tracker;
  still **unconfirmed** until a live verify session (now working) confirms them. Gate battery
  reproduced green: dotnet build 0w/0e, dotnet test 889/889, ratchet OK (38≤38), face-go green.
- **The three files:** `plans/conductor-ux.plan.json` (claude-native; U0 sonnet-5, U1–U3 opus-4-8,
  fable advisor) · `docs/history/CONDUCTOR-UX.md` (THE spec — includes the 13-item dogfood appendix, the
  "delivered engine-side, do not redo" ledger, and the owner's orchestrator-gaps backlog) ·
  `CONDUCTOR-UX-START.md` (tracker; the engine regenerates it, don't hand-groom).
- **Run state:** run `1a7c1714` in `.conductor/run.db`, session #2 (Resume U0, resume #1 of
  `6bd47a4c`) after session #1 was interrupted twice (owner Ctrl+C, then a hard cancel). **U0 is
  CLOSED, 3/3** (U0.1 + U0.2 + U0.3 all DONE this session, see below). **Next: U1** (Face — landing
  page + workspace identity, docs/history/CONDUCTOR-UX.md §U1; opus-4-8 per the plan). U1 is Go/face-go
  work — this session did none of it (U0 was scoped engine-only, no Face changes).
- **To continue:** the owner runs `conductor run -p plans\conductor-ux.plan.json` from the repo
  root (resumes the run — resume actually works now; the Face auto-spawns; the engine console is
  muted while the Face owns the terminal and an exit epilogue always prints).
- **U0 delivered this session (2026-07-17, all pushed on `feat/foreman`):**
  - **QA finding, fixed first (`ebd0eca`'s prerequisite, `a15cce6`):** the ratchet gate was
    genuinely RED at session start (40 pragmas > ceiling 38) — session #1's committed WIP
    (`e8f3f17`, `PlanDiscovery.cs`) and the engine fix `4fcecf7` (`RunStateResume.cs`) each added one
    MA0045 suppression without anyone noticing the ceiling breach (same silent-cheat shape as the
    earlier G3.3 39>38 incident). Fixed for real, not by raising the ceiling: `RunCommand` converted
    `Command<Settings>` → `AsyncCommand<Settings>` (same pattern as `DoctorCommand`, M8.1), so its
    "Execute must return int" pragma is gone; `RunStateResume` became genuinely async
    (`OpenAsync`/`ExecuteScalarAsync`). Net 40→38.
  - **U0.1 DONE** (`199f2c8`) — session #1's discovery core (`PlanDiscovery.Discover` +
    `PlanSettings.ResolvePlanPath`) matched the spec exactly, no changes needed; added the
    resolution-order unit tests the spec asked for (9/9: empty→none, single/multiple-in-cwd
    ordered, empty-cwd-falls-back-to-plans, **cwd wins outright even with >1 candidate**,
    malformed/missing name never throws).
  - **U0.2 DONE** (`66e6f57`) — new `conductor journey` verb: identity (resume-or-fresh, mirroring
    `RunCommand`'s own state.json→run.db detection exactly), stages in resolved-workflow order
    (`WorkflowEngine`+`DefaultQaPolicy`, "Deliver -> Verify -> Fix") with model + checkpoint counts,
    gates by tier, human moments (pauseOnBlocked, owner-gated stages, live `HUMAN:` token, budget
    caps), footer with next commands. Read-only, <1s (measured 0.885s built-binary). The
    human-moments extraction + resume description are internal pure statics, unit-tested directly
    (10 tests) rather than via rendered-output scraping — same split as `PlanDiscovery`. Verified
    live against the actual running U-series plan mid-flight (correctly read "resumes session #N,
    stage U0" without writing anything) and against a scratch gateless+ownerGate+HUMAN-token plan.
  - **U0.3 DONE** (`ebd0eca` engine + `84fe84f` docs) — `GateRunner.Summary([])` used to join to an
    empty string (every consumer — SessionRecord.GateSummary, REPORT.md, phase-gate logs — rendered
    a blank where "gates: ..." belonged); now `"gates green (none configured)"`.
    `AllRequiredPassed` was already vacuously true on empty, so only the TEXT was lying by omission.
    `doctor` gained a `gates` check (warn, never fail, when empty). Proof is a REAL fake-agent
    session through the actual Orchestrator/SessionRunner/VerdictEngine with `gates:[]`
    (`U03GatelessLiveTests`, same scaffolding as `P2QaDialLiveTests`) reaching
    `SessionOutcome.Progress` with the honest `GateSummary`. Docs: README's CLI table / "Dashboard
    TUI" (a defunct 5-zone in-process console mockup, replaced with an accurate face-go section) /
    "Face — companion TUI" sections were badly stale (`--no-dashboard` doesn't exist — renamed
    `--headless` long ago, `.claude/skills/run-conductor/SKILL.md` had already flagged this gotcha
    unfixed; `replay`/`preview` commands don't exist; `doctor`'s description was pre-M8.1) — rewrote
    with a new "How resume actually works" section (the three different "resume"s: `run` re-loading
    state, `--paused`, the `resume` control verb) and fixed "Runtime files"/"Trust model" to say
    run.db is the live source of truth, not state.json (verified by reading `RunContext.Save()` —
    it ONLY calls `SqliteRunStore.SaveRunState`, never touches state.json). Also fixed the same
    staleness in `CompletionCommand.cs` (a real functional bug — tab-completion offered a dead flag
    and was missing a third of the real verbs) and `docs/quickstart.md`.
  - **Gate battery at close: dotnet build 0w/0e, full suite 878/878, ratchet OK (38≤38, nothing
    weakened), go build/vet/test green.** One `B12_3Tests` flake + one crashed test host hit
    mid-session, both traced to ~16 orphaned `dotnet` processes accumulated from earlier
    interrupted sessions on this machine (not this change) — cleared via the AGENTS.md-sanctioned
    `Get-Process dotnet | Stop-Process -Force`, reran clean twice after.
- **Delivered engine-side this era (2026-07-16/17, all pushed — build on, don't redo):**
  `d6f9b87` Face/console mute + local timestamps + fake-agent verifier; `877ff57` live transcript
  wire + run.db access gate + truthful `/state` (+ `model` on the wire and in the agent strip);
  `4fcecf7` stale-control purge + resume-from-run.db (`RunStateResume`, now async — see above) +
  exit epilogue; `3cb0579` spec appendix 11–13; `7f2b88b` kanban seeded cards + frame-height
  invariant + collapsed thinking + overnight limits. Known live bugs already root-caused and
  ASSIGNED in the spec appendix: kanban empty (seeding emits no TaskAdded — U2, though `7f2b88b`
  may have already closed this, check before redoing), transcript readability (U3.3), frame-height
  overflow hiding footer + live tail (U3.2, `7f2b88b` added the invariant test — check before
  redoing).
- The open-edges note's item 1 (pre-usage gate: one real-model run) is being satisfied BY this
  era — the U-series run is that gate.

## Resume here (P-SERIES CLOSED + PF follow-ups landed, 2026-07-16)

**Read this first if you're the fresh session.** The planner tracker (`CONDUCTOR-PLANNER.md`) is
CLOSED: all six checkpoints DONE with evidence, gates green at every commit. ALL THREE of its
follow-up candidates landed the same day (the PF session, below) — PF3 closed the last one after
the owner picked the declared-paths schema (a `paths` field on task cards, NOT git inference).
There is no committed next stage — the open-edges note below is the owner-reviewed pointer to
what's left before personal usage.

### Open-edges note (owner-reviewed 2026-07-16 — do not lose these)

The owner reviewed what's left before personal usage. None of it is tracked in a committed plan;
this note is the pointer so it isn't missed:

1. **Pre-usage gate: one real-model toy run (M9.1).** Everything was live-proven only under the
   credential-free fake agent; the engine has never driven a real paid model end-to-end. Small
   2–3-checkpoint plan, cheap model, exercise one live lever mid-run (QA-dial flip or
   `conductor rollover`). Owner-started (paid). Telegram phone dogfood (M8.3) can ride the same run.
2. ~~**The one edge worth fixing: window-close kills the run.**~~ **DONE (W3.3, `c8f9b56`)** —
   `ConsoleCtrlRails` wires CTRL_CLOSE/LOGOFF/SHUTDOWN into the same graceful path as Ctrl+C and
   blocks inside the OS handler until the save completes. The OS-delivery half that W3.3 left as
   "worth one manual ✕" was closed 2026-07-28 **without a manual ✕**: `tools/w3/window-close.ps1`
   posts `WM_CLOSE` to a real run's real console window and asserts the resumable park, with a
   hard-kill negative control. 18/18. See `docs/dev/workgraph/W3-WINDOW-CLOSE.md`.
3. **PathClaims from real task data** — ~~parked~~ **DONE the same evening (PF3, `12fcc87`)**: the
   owner chose the declared-paths schema and it shipped — see the PF session log below.
4. ~~**`.conductor/followups.md` needs a triage pass, not execution.**~~ **DONE 2026-07-28.** 13 rows
   closed with the evidence that closed them (most had been true for weeks — FU-B0-1/2 were still
   listed as deferred analyzer ratchets that `.editorconfig` has read `error` for since C2); the
   eight Ink-era `FU-OWNER-*` rows closed as obsolete; the rest re-homed onto owners that still
   exist (`on demand`, `next era`, `HUMAN:`). No code changed, which was the ask. The one worth
   knowing about is `FU-OWNER-9`, still OPEN: an agent killed its own parent conductor, and nothing
   on the agent's side of the tool contract stops it.
5. **`docs/operating.md` §7 known-gaps list is disclosure, not a backlog** — and is
   partially stale: the "persona kill-list residue" item is resolved-by-design (F0 trimmed 9→3;
   P1 role→persona assignment now USES the registry). The remaining §7 items (crash-net recovery,
   `plan import` bootstrap, `perPhase` gating render, `status` sessions-0, `init` packs, CI) are
   cosmetic or have documented workarounds — fix on demand, not a series.

~~Suggested shape if the owner asks to act on this: a short hardening mini-series = item 2 + the
item 4 triage; item 1 is the owner-run gate before daily use.~~ **Status 2026-07-28: items 2, 3 and
4 are all closed** (3 as PF3, 2 as W3.3 + the automated ✕, 4 as the post-W6 triage). **Item 1 is
the only one left, and it is the owner's to start** — it is now W5.2 in the W-series tracker, and
the merge to `master` is held behind it. Item 5 was never a backlog.

### What landed in the PF session (2026-07-16, pushed on `feat/foreman`)
- **PF3** (`12fcc87`) — **PathClaims from real task data** (owner decision: declared paths, not git
  inference): `TaskItem.Paths` rides the P3 machinery (`TaskDetailEdited.Paths` — null unchanged,
  empty clears; `TaskWrites` cleans entries; fold applies), `TaskGraph.DeclaredOpenPaths` unions
  OPEN cards only, and SessionRunner folds the graph ONCE pre-assignment so every `ReadyItem`
  carries its checkpoint's declared claims — multi-item co-claims now refused on real task data
  (no cards/paths = classic behavior). Wire: `/tasks/edit` `paths`, `GET /tasks` + MCP `task_list`
  serve them; Face card detail gained a declared-paths section + `p` editor (comma-separated,
  empty save clears), goldens ×3. Live proof `FullCycle_ConflictingDeclaredTaskPaths_BlockTheMultiItemClaim`:
  the P1 multi-item setup that claims BOTH checkpoints claims only one when the two cards declare
  the same file (case-variant, so normalization is exercised) — checked on the real prompt.md.
  Plus: the last four probe-port HTTP fixtures now return `server.Port` (the flake struck twice).
- **PF1** (`6401b6f`) — **the set-rollover override surfaced**: `GET /state` carries
  `maxSessionTokensThisRun` (absent = no override; 0 = forced OFF this run; >0 = the cap), read off
  the LIVE RunState (the override is run-state only, never event-folded). Face "rollover (run)"
  Settings row now renders the ACTIVE override (none/OFF this run/ON at N) instead of a blind hint;
  demo source round-trips the verb like the dispatcher. Wire lifecycle pinned by a contract test
  over a real HttpListener.
- **PF2** (`2360ce0`) — **`conductor rollover <tokens|off|clear>`**: the CLI ingress for the 13th
  verb (GotoCommand pattern; value validated by the dispatcher's own `ParseRolloverValue` BEFORE
  writing, exit 2 on a typo). Proven live against a scratch plan through the built binary.
  Also: `ControlPlaneServerTests.StartServer` now returns `server.Port` (the parallel-run port
  flake P5 fixed in the two newer HTTP fixtures — it hit once here, 841/842, then green).
- **Gotcha fixed in the docs of record:** the ratchet gate script is `tools/gates/ratchet.ps1`
  (NOT `tools/ratchet.ps1` — `powershell -File` on the wrong path prints an error but exits 0,
  a silent false-green).

### What landed in the P3–P5 session (2026-07-16, pushed on `feat/foreman`)
7. **P3** (`d174116`) — **Kanban card detail**: pure `PromptComposer` in the library (labeled
   `PromptBlock` list; only task-scoped blocks editable — pinned: a context edit changes exactly
   that block); `TaskItem.Context` via `TaskDetailEdited` event; `GET /prompt/blocks?task=`,
   `POST /tasks/edit` (the one confirm step), `POST /tasks/refine` (advisor PROPOSES only);
   owner context reaches the real session prompt + MCP task_list; Face detail panel (enter on a
   card; t/c editors, a advisor preview→confirm, h hand-off via /inject); goldens ×3.
8. **P4** (`a06f7b7`) — **decoupling finished + proven standalone**: `IWorkflowResolver.Advance`
   (post-session walk incl. the skip-verification collapse, hop list out) + `ResolveStartKind`
   (consume-recorded-index-without-advancing) — VerdictEngine/SessionRunner now effect-only;
   `tools/plan-lint` consumes ONLY `Conductor.Planning`, prints workflow/QA/assignment decisions
   from a plan file; 2 new arch tests. Bonus: ratchet.ps1's silent pragma breach (39>38 since
   G3.3) fixed properly — Telegram control-file write went async, its bare MA0045 deleted.
9. **P5** — **rollover surfaced**: `limits.maxSessionTokens`/`softBreakRatio` editable
   (Face Settings rows, honest "OFF (default)" label) + the 13th verb `set-rollover`
   (tokens/off/clear, this-run-only, `RunState.MaxSessionTokensThisRun`, never writes the plan) +
   `RunContext.EffectiveMaxSessionTokens` choke point. Live-proven: no cap → normal session;
   `set-rollover 10` → RolledOver, no attempt burned, plan file byte-identical.

### The previous session's log (G3 + P0–P2, same branch)
1. **G3.1** (`6205b9c`) — `conductor run --paused`: engine+control plane+Face come up parked, resume
   starts session 1. `RunLoop.ApplyStartPause` pure + tested; live harness proof.
2. **G3.2** (`b5023f6`) — **live plan reload**: 12th verb `reload-plan` (all 3 ingresses via
   `ControlFile.Parse`); dispatcher always defers; the loop swaps the plan ONLY at its top (= the
   session boundary, incl. paused iterations) via `ApplyPlanReload` → `SwapPlan` on RunContext +
   prompts + gates/lanes/dispatcher; `/plan/edit` + applied `/plan/import` auto-enqueue it;
   `conductor plan reload` queues it; `PlanReloaded` event in timeline/SSE; Face palette entry.
   Live proof: paused run, plan file edited, reload+resume → session 1 ran against the new plan.
3. **G3.3** (`4a3b430`) — **live limits**: `limits.maxSessions` (run-total cap → PARKS at boundary
   with `ParkedBySessionCap`+reason; a reload that raises/clears the cap auto-resumes exactly that
   park); `limits` target on `/plan/edit` (5 fields, empty clears); Face Settings gained the rows +
   golden + round-trip test. Live proof: cap=1 parks after session 1, raise-to-3 + reload resumes.
   **G-series tracker (`CONDUCTOR-AI-NATIVE.md`) is CLOSED — G1+G2+G3 all DONE.**
4. **P0** (`9222274`) — **`Conductor.Planning` library** (the P-series keystone): owns SessionKind +
   Workflow{Definition,Step,Overrides,RuntimeVars} + the (now-agnostic) WorkflowEngine +
   `IWorkflowResolver` + the `pipeline` rules schema (PipelineRules/RoleAgentRule/QaRule/
   MultiItemRule). One-way dependency enforced by
   `ArchitectureTests.PlanningLibraryDoesNotReferenceTheEngine` (assembly refs + source usings).
   Engine adapters: `WorkflowVarsFactory`, `Resolve(plan, stage)` extension; DI-wired. Dead
   `agent.tokenCeiling` DELETED (grep-clean). Behavior unchanged.
5. **P1** (`6ab268b`) — **role→agent assignment + multi-item sessions**: `IAssignmentPolicy` /
   `DefaultAssignmentPolicy` (pure; role map deliver/verify/audit/fix → model/persona/command;
   Resume exempt; multi-item deliver-only opt-in with declared-path conflict refusal). SessionRunner
   asks the policy; personaOverride threads through PromptBuilder; multi-item prompts name every
   claimed item. Live harness proof: the {model} process arg = the role override; prompt.md names
   both claimed checkpoints.
6. **P2** — **QA dial** (off/everySession/phaseGate): `IQaPolicy`/`DefaultQaPolicy`/`QaProjection`
   in the library, a pure projection onto the existing workflows (pinned: projected == hand-picked
   definition). One resolve choke point (`Resolve(plan, stage, qa)`, dial-blind overload deleted);
   effective skip-verification/threshold extensions; per-stage `StageConfig.Qa`; `/plan/edit` `qa`
   target + stage `qamode`/`qathreshold` + `limits.verifierthreshold`; Face Settings + stage rows +
   demo + goldens. Live proof `P2QaDialLiveTests`: off = deliver-only; flip + reload verifies the
   SAME run. Fixed 2 real engine bugs it exposed: session-start workflow double-advance (NRE onto
   a pending-less verify — latent since M3.1) and stale session-scoped stage flags across a live
   reload. Suite 793 green.

**NEXT: P3 (Kanban card prompt building-blocks) → P4 (finish extraction + standalone consumer) →
P5 (rollover surfaced).** Read `CONDUCTOR-PLANNER.md` (tracker handoff) + `docs/history/CONDUCTOR-PLANNER.md`
§P3 before starting. P3's shape: a pure `PromptComposition` (labeled `PromptBlock` list) decomposing
what PromptBuilder already renders, served at `GET /tasks/{id}/prompt`; Face card-detail panel with
an editable task-scoped context block persisted as task data (NOT free-form prompt splicing);
advisor-refine + hand-to-Claude reuse G1's advisor plumbing + `/inject`.

### How to drive (owner directive, 2026-07-16): BE the delivering agent — do NOT run `conductor run`
The owner wants plans driven **directly from Claude Code**: *you* are the delivering agent, not the
conductor orchestrator loop. The plan doc + tracker are your worklist. Per checkpoint:
1. **Pre-session ritual** — read the tracker (`## Handoff` + read order), your stage section in the
   design brief, and the docs it cites. Run the gate battery first; never build on red.
2. **Deliver** the next incomplete checkpoint(s) of the target stage only. One landed-with-proof beats
   three claimed.
3. **Gate battery** — `dotnet build Conductor.slnx` · `dotnet test Conductor.slnx` · (in `face-go/`)
   `go build/vet/test ./...` · ratchet (`dotnet test --filter Category=Architecture`). Green or fix.
4. **Update the tracker** — overwrite the `## Handoff` block, fill the checkpoint row (Status DONE +
   Commit + Evidence). If a row stays TODO the work isn't done.
5. **Commit per checkpoint** and push (owner pre-authorized commit+push at checkpoints). Use the repo's
   commit trailer convention.
This is the same discipline the conductor prompt enforces — just executed in the interactive session so
the owner can watch and redirect. `conductor run` is NOT used for this work.

### What's DONE (this session, 2026-07-16) — verified live, all pushed on `feat/foreman`
**AI-native G-series (G1 + G2) — `plans/conductor-ai-native.plan.json`, tracker `CONDUCTOR-AI-NATIVE.md`:**
- **G1 (prompt→plan):** `POST /plan/import` routes freeform prose through the plan's advisor model
  (`Advisor.AskTextAsync` — fixed a latent bug where the import prompt's plan JSON could never satisfy
  the verdict regex); Face Plan tab gained a **Prompt** section beside Import (both land on the shared
  import-diff view). Commit `5bffd0c`.
- **G2 (kanban):** `POST /tasks/update|add` share one `TaskWrites` service with the MCP task tools;
  Face **Kanban** tab (`b`, the 11th tab) — live board, ←→ move / `n` add. Commit `11d77db`.
- **Hardening** (`4c96bd0`): control plane now requires a **per-run write token** (`X-Conductor-Token`,
  from `control-plane.json`) on every POST — CSRF/prompt-injection guard; freeform apply must be
  previewed first; advisor prompt frames its source as untrusted data. Face + `conductor run/face` pass
  the token via env. See `docs/operating.md` §4.
- Gates at close: **C# 750 green**, Go all packages green.

### What's PLANNED (TODO — the next session's work, in dependency order)
1. **G3 (live & dynamic)** — a TODO stage in `plans/conductor-ai-native.plan.json`; brief in
   `docs/history/CONDUCTOR-AI-NATIVE.md` §G3. **The prerequisite for everything dynamic.** Today
   `RunContext.Plan` is get-only/loaded-once, so Face edits only take effect on a full restart.
   G3.1 `conductor run --paused` (the `Paused` idle path already exists in `RunLoop.cs` ~L87); G3.2
   real `ControlAction.ReloadPlan` swapping the live plan at the **session boundary only**, auto-enqueued
   by `/plan/edit` + applied `/plan/import`; G3.3 live limits + session cap from Plan-tab Settings.
   **Start here** — small, self-contained, unblocks the P-series.
2. **P-series (decoupled dynamic planner)** — `plans/conductor-planner.plan.json`, tracker
   `CONDUCTOR-PLANNER.md`, brief `docs/history/CONDUCTOR-PLANNER.md` (validated: 6 stages; dry-run resolves P0).
   P0 keystone = new **`Conductor.Planning`** library (one-way dependency, arch-test enforced from P0) +
   agnostic `pipeline` rules block + `IWorkflowResolver` seam + **delete the dead `agent.tokenCeiling`**
   (audit finding: defined/merged but enforced nowhere — a no-op trap). P1 role→agent assignment +
   multi-item sessions; P2 QA policy dial (off/every-session/phase-gate) over the existing workflows; P3
   Kanban card-detail prompt building-blocks + advisor-refine; P4 finish the extraction + a standalone
   consumer; P5 rollover/limits surfaced (OFF by default, session-scoped). **Read the "Design principles"
   section of `docs/history/CONDUCTOR-PLANNER.md` before writing — purity + one-way dependency + standalone-usable
   are the code-quality gates that matter most.** Reuse, don't fork: `WorkflowEngine`, the workflow/override
   model, the task graph, the Kanban tab, and G1's advisor plumbing all already exist.

### Key audit findings baked into the plans (don't re-derive)
- **`agent.tokenCeiling` is dead** — enforced nowhere; the real per-session rollover knob is
  `limits.maxSessionTokens` (null = **off by default**; on-cross → `RolledOver`, handoff written, next
  session fresh, **no attempt burned**). `softBreakRatio` (80% nudge) only fires when maxSessionTokens is
  set. P0 deletes tokenCeiling; P5 surfaces the real knob. **B13 made both rails real**: the ceiling is
  enforced live rather than read after the agent exits, the nudge is carried into the running session by
  a `PostToolUse` hook instead of being written to a file nobody read, and a plan edit re-applies itself
  at the session boundary instead of waiting for someone to remember `plan reload`.
- **The pipeline is already a data-driven workflow engine** (deliver-verify / big-dev-then-big-audit /
  docs-only / spike + RunIf/SkipIf + per-stage overrides). The P-series surfaces + decouples it.
- **Agent↔session↔task is stage-sequential** (one session = first not-done checkpoint of the current
  stage); per-task agent/auditor assignment and cross-checkpoint claims are net-new (P1).

## Resume here (face-go UX pass — gaps review closed, 2026-07-15)
A review of the Go face against STYLE.md + the docs found ~20 gaps/glitches; this pass attacked them.
**Face (Go):** finished the sessions outcome→colour map (AgentError/TimedOut/NeedsHuman were grey);
added `widgets.TextArea` — a real cursor editor (insert/delete mid-string, arrows, home/end, pgup/pgdn)
now backing the **template editor** and **report SQL**; sidebar self-scrolls to the active stage on a
tall plan; timeline drills into the selected event; a `● disconnected` banner shows in the Agent pane
when a live poll drops; top bar shows reasoning tokens + overhead cost, agent strip shows the stage
**persona**; plan editor gained a **persona** field, plan **name**, and a **✎ custom** model option
(model is no longer limited to 5 hard-coded ids); template preview has a **kind picker**
(Deliver/Fix/Resume/Audit/Review); `T` folds thinking; console has pgup/pgdn/home; report has query
history + wide-table horizontal scroll; `0` reaches the 10th tab. **Backend (C#):** `POST /note`,
`/bug`, `/bug/resolve` (ControlPlaneServer.Knowledge.cs + KnowledgeWrite/Bugs DTOs) so the Knowledge
tab files a note / files a bug / resolves one (`n`/`b`/`x`) instead of being read-only —
`_store.WriteLedger`/`WriteBug`/`UpdateBugStatus`, verified live against a running control plane.
**Deferred (with reason):** add/delete stage-or-gate (needs a new plan-mutation surface),
process-kill from the Procs tab (needs a supervisor kill API), tab-mnemonic relabel (churns keybindings
+ every golden). **Gates:** face-go build+vet green, all Go tests green (+ new editor + knowledge-write
tests, 2 new goldens); C# suite **708 green** (+4 control-plane contract tests), ratchet green.
Commit: see `feat/foreman`.

**Update (2026-07-15/16):** two of the four deferred face-go items are now shipped —

1. **`heartbeat`** — the 11th control verb. New `ControlAction.Heartbeat` (`Progress.Control.cs`) mapped
   in `ControlFile.Parse` (which single-sources the `POST /control` whitelist, so the HTTP ingress accepts
   it for free); `ControlDispatcher` acks it and the `SessionRunner` run-loop calls the existing
   `RefreshReport` when the action bubbles back — forcing a fresh `.conductor/REPORT.md` on demand
   mid-session. `:` palette gains a `heartbeat` entry; `conductor heartbeat` CLI verb added for parity.
   Verified live (control.json = `{"command":"heartbeat",…}`).
2. **add/delete stage-or-gate** — `POST /plan/edit` edits carry an `op` (set|add|delete); add/delete run
   through the same atomic validate-then-save gate, so an unsafe delete (a depended-on stage, the last
   stage) is rejected whole, and a runtime guard refuses deleting the *running* stage. Face plan editor:
   in the Stages/Gates list, **`n`** opens a two-field add form (id/name + title/command), **`d`** deletes
   the selected row after a `y/N` confirm (`n` dodges the `a`=Agent tab mnemonic; both keys become
   pane-owned via `tabHandlesAllKeys`). Demo source + 3 new goldens; wire type `PlanEditDto.Op` on both
   sides.

3. **process-kill from the Procs tab** — `POST /processes/kill` (ControlPlaneServer.Processes.cs) →
   `ProcessKiller.Kill` (Core/ProcessKiller.cs): kills a supervised child's whole tree and marks it
   exited in run.db, but only a PID this run tracked and still alive — never an untracked/arbitrary
   PID, an already-exited one, or the conductor process itself. Same effect as `conductor bg stop`;
   the control plane validates ownership via `_store`, no supervisor reference threaded in. Face Procs
   tab: **`x`** on a live row opens a `y/N` confirm, then posts the kill and re-fetches so the row flips
   to exited (`x` dodges the `k`=Knowledge / vim-up binding). Endpoints split into their own partial to
   stay under the 500-line ratchet.

**Gates:** Go build+vet+tests green (+3 plan, +3 process unit tests, +4 goldens); C# **720 green** (+4
plan, +4 ProcessKiller, +3 kill-endpoint contract tests, +1 parse case).

4. **tab-mnemonic relabel** — the four deferred items are now all closed. Tab letters went from the
   non-obvious `a h t s c e g r k l` to first-letter-where-free: **`a`Agent `s`Sessions `t`Timeline
   `o`Procs `c`Console `e`Templates `p`Plan `r`Report `k`Knowledge `g`Telegram**. Plan reclaims its
   natural `p` because sidebar-collapse moved off `p` → **`\`** (owner-approved). Procs=`o` and
   Telegram=`g` are the two first-letter collisions (p/t already taken). `tabKey` in model.go is the
   single source; bottom bar + help legend updated; all ~30 goldens regenerated; every test that opened
   a tab by letter remapped (Sessions h→s, Procs s→o, Plan g→p, Telegram l→g, sidebar p→\\).

## Resume here (Maestro M9 complete — plan CLOSED, 30/30, 2026-07-15)
**M9 (dogfood close) is DONE, 2/2 — 30/30 checkpoints. The Maestro plan is feature-complete and
release-clean.** M9 was run by dogfooding the engine on itself: a real `conductor run` of a toy plan
(token-free `tools/fake-agent.ps1`) through the binary built from this branch, exercising the whole
path end-to-end.

- **Four real defects bled out of the dogfood and were fixed** (M9.1 — "fix what bleeds"):
  1. **The ratchet gate was RED and had been reported green.** Analyzer suppressions were at 40
     against the ceiling of 38, so the M8 close-out's "architecture ratchet green" was simply false —
     the pushed tree failed its own anti-cheat gate. Fixed the honest way (the gate forbids raising
     the ceiling): removed a **dead class-level `MA0045`** on `Orchestrator.cs` (leftover from before
     the M1.3 god-class split — zero blocking calls remain) and converted **`DoctorCommand` to a
     Spectre `AsyncCommand`** so it awaits `RunChecksAsync` instead of `GetAwaiter().GetResult()`.
  2. **`tools/fake-agent.ps1` failed to PARSE under Windows PowerShell 5.1.** Two em-dashes made the
     BOM-less UTF-8 script decode as ANSI and tear a string literal mid-line, so the smoke harness
     never ran a single session — every toy session errored. Now ASCII-only, matching the discipline
     `ratchet.ps1`'s own header documents. (Note: the engine handled the broken agent *correctly* —
     gate battery ran, cache hit, circuit breaker fired on the identical ×2 failure, escalated to
     NEEDS HUMAN, honoured `--max-sessions`. Good evidence in itself.)
  3. **M2.4 deviation:** `transcript.md` is listed in the design doc but was never written to
     `.conductor/sessions/<NNN>/`. `RunLoop.RenderTranscript` now folds the raw agent NDJSON
     (`logs/session-NNN.jsonl`) into readable markdown there; unparseable lines are kept verbatim so a
     new provider wire format never silently drops content.
  4. **Prompt glitch:** the session template rendered `exactly as `` prescribes` (empty backticks) for
     any plan without a `planDoc`. `{planDoc}` now falls back to the tracker.
- **Built `conductor init`** — the design-doc **M8.2** scaffolder that was never implemented (M8 shipped
  Telegram guided-setup under M8.2 instead — an owner redirect). It's a superset of `new-plan`: detects
  the repo type from a root marker (dotnet/go/rust/node/python — dotnet wins on ties), wires the matching
  build+test gates, drops editable copies of the built-in `session.md`/`fix.md` templates, and self-checks
  the scaffold loads. This closes the audit's clearest DEVIATE. Also fixed the stale `doctor` `--help`
  text (still described the pre-M8.1 resume preview).
- **M9.2 final audit written: `docs/history/maestro/M9-FINAL-AUDIT.md`** — every design-doc checkpoint rated
  CONFORMS/DEVIATES, truth gates **re-run live this session** where credential-free. Verified live:
  M4.1 (rigged tracker edit discarded → 0 checkpoints), M4.2 (gate cache HIT), M3.1 (workflow step
  0→1), M6.1/6.2 (`plan import` → M1…M9, diff), M8.1 (`doctor` <2s), M5.6 (`status` 514ms), M2.3 (no
  `state.json` written), M2.4 (`prompt.md` byte-identical). **30/31 design-doc checkpoints CONFORM.**
- **Two credential-gated `HUMAN:` items remain** (documented in the audit; neither blocks engine
  release): **M8.3** the live Telegram phone dogfood (needs the owner's real bot token) and **M9.1** the
  full real-DeepSeek-model run (paid). Everything reproducible without credentials was reproduced and
  conforms.

**Commits:** `4b1e2e7` ratchet + fake-agent + transcript.md · `fba0fe2` planDoc fallback ·
`baceb4a` conductor init + doctor help + final audit.

**Gate at close:** build 0w/0e · full C# suite 704 green (+11) · ratchet green (652 tests / 38 pragmas)
· face-go green · toy `conductor run` drives a plan deliver→verify→fix end to end.

**Post-close delivery pass (same day):**
- **One-command install — `powershell -File tools/install.ps1`** (commit `f824ac7`). Publishes the C#
  engine (Release) to `%LOCALAPPDATA%\Programs\conductor`, builds the Go face RIGHT NEXT TO it (where
  `FaceLauncher.ResolveEntrypoint` looks first), and drops a `conductor` shim on PATH (scoop's shim dir
  if present, else user PATH). Turns the long `src/Conductor/bin/.../conductor.exe` into a global
  `conductor`; `conductor run` still auto-spawns the face, so there is never a separate Go binary to
  launch. Re-run to cut a fresh local release. Installed + verified live this session. README quick-start
  rewritten around it. (For the self-referential Maestro plan specifically, still drive with the fresh
  BRANCH build — `dotnet run -- run -p plans/conductor-maestro.plan.json` — so a regression is caught
  immediately; the installed command is for everything else.)
- **New: `docs/operating.md`** — a control guide written for an AGENT driving Conductor on the
  owner's behalf: full command reference (every verb + flags), how to monitor a live run, how to steer it
  (`inject`/`approve`/`pause`), how to respond to NEEDS HUMAN, the HTTP control plane + `control-plane.json`
  discovery, the MCP tools, the safety rules, and a **consolidated "known gaps & missing features" list
  (§7)** — start there for the current gap inventory rather than re-deriving it.

---

## Resume here (Maestro M8 complete, 2026-07-15)
**M8 (AFK & smart setup) is DONE, 2/2 — 28/30 checkpoints.** Plus the M7 heads-up NRE is fixed.

- **Workflow-index bug (found + fixed this session, pre-existing, not M8-specific):**
  `SessionRunner.ResolveSessionKind`'s workflow-fallback branch (used to pick a session's kind when
  no `Pending*` is queued) resolved a step from `WorkflowEngine.GetNextStep` but never recorded
  which one — `RunState.WorkflowStepIndices` lagged one real step behind after a stage's first
  session, so `VerdictEngine.AdvanceWorkflowStep` re-derived the wrong "next" step and never
  populated `PendingVerify`/`PendingAudit`/`PendingFix` for the step `ResolveSessionKind` itself
  later picked (by coincidence of the same lag) — `PromptBuilder.Verify`/`Audit`/`Fix` NRE'd on a
  null pending record. Root-caused and fixed by extracting `WorkflowEngine.ResolveAndRecordStep`
  (resolves AND records the index in one call), which both `SessionRunner` and `VerdictEngine` now
  share instead of mirroring the same bookkeeping independently. Regression coverage:
  `WorkflowEngineTests.ResolveAndRecordStep_KeepsIndexInSyncAcrossCallers` drives it exactly the way
  the two real call sites do. Filed and fixed for real via `conductor bug` (bug #1) — dogfoods M7's
  own bug tracker on itself.
- **M8.1 `conductor doctor`** repurposed in place (owner decision — the old "what happens on
  resume" preview read the deleted `state.json` and showed stale state; `conductor status` already
  covers that from the database). Now a <2s health-check battery: agent CLI on PATH, git
  clean+branch, face-go binary (`FaceLauncher.ResolveEntrypoint`), DNS/disk/API reachability
  (reused `PreflightHealth.RunAllAsync` with sane defaults when the plan hasn't configured
  `DnsHealthCheck`), budget headroom (`StatusReportBuilder`, run.db), Telegram configured. 24 unit
  tests (`DoctorCommandTests.cs`) drive the internal `Check*` methods directly against a
  deliberately-broken environment. Live-verified against this repo's own Maestro plan: 421ms.
- **M8.2 Telegram v2 — reframed mid-session by the owner.** Instead of the original "drive a toy
  run from a phone" truth gate alone, the owner asked for Telegram to be **configurable and
  testable from the Face itself** — a guided in-app setup, not hand-edited plan.json/env vars.
  - **`SecretsStore`** (`src/Conductor/Core/Integrations/SecretsStore.cs`) — a new local
    `.conductor/secrets.local.json`, already excluded by the state dir's own blanket `.gitignore`.
    The Face's "paste your token" flow writes here; `TelegramService.ResolveToken` (now an instance
    method) falls back to it when `CONDUCTOR_TELEGRAM_TOKEN` isn't set — the env var still wins.
  - **`TelegramService`** gained live status tracking (`_lastPollUtc`/`_lastError`) and
    `TestConnectionAsync` (a real `getMe` call, plus a real test push when a chat id is configured
    — proves the whole path, not just the token).
  - **New control-plane surface:** `GET /telegram/status`, `POST /telegram/test`,
    `POST /telegram/token` (`ControlPlaneServer.Telegram.cs`), and a `telegram` target on the
    existing `POST /plan/edit` (`ApplyTelegramEdit` in `ControlPlaneServer.Plan.cs`) for the
    non-secret settings (chat ids, poll interval, two-way toggle) — the bot token itself never
    round-trips through the versioned plan file. `ControlPlaneServer` now takes `ITelegramService`
    (previously not wired to Telegram at all; 4 call sites updated). 8 wire tests
    (`ControlPlaneServerTelegramTests.cs`).
  - **Face: new `l` Telegram tab** (`face-go/internal/tui/tab_telegram.go`) — reads as a guided
    wizard: a live status line (not configured / configured-no-token / token-saved-untested /
    connected as @bot), a numbered setup checklist with checkmarks while incomplete, an in-pane
    field editor (bot token — always blank on entry, masked while typing and at rest, since the
    server never echoes it back; allowed chat ids; poll interval; two-way toggle), and a one-shot
    "send test message" action row. `--demo` starts from a realistic "configured, no token yet"
    state so the guided flow is what a reviewer sees by default. 3 new goldens
    (`telegram_unconfigured`/`telegram_token_edit`/`telegram_connected`); full suite regenerated
    for the 10th tab's strip-width change.
  - **Not yet done:** the credential-gated live phone dogfood (paste a real bot token, add a real
    chat id, hit Test, confirm a real message arrives, then drive a toy run watching session-end
    pushes / NeedsHuman buttons / reply-to-inject / `/status`) needs the owner's real bot token —
    a `HUMAN:` item. Do this before M9 close.

**Commits:** `50720b0` workflow-index bug fix · `19a45e1` M8.1 doctor + M8.2 backend ·
`9ed1192` M8.2 face-go Telegram tab.

**Next: Maestro M9 (dogfood close).** M9.1 a real plan run end to end under Maestro, fix what
bleeds; M9.2 final audit — every design-doc checkpoint rated CONFORMS/DEVIATES with evidence. See
`MAESTRO-TRACKER.md` for the live handoff.

## Resume here (Maestro M7 complete + Ink face retired, 2026-07-15)
**M7 (knowledge that compounds) is DONE, 2/2 — 26/30 checkpoints.** Knowledge now survives the session
that learned it, and the Ink face is gone.
- **M7.1 ledger injected + surfaced + queryable** — `LedgerBattery`
  (`src/Conductor/Core/PromptBattery.Knowledge.cs`) reads recent `conductor note` rows and injects them
  into `PromptBuilder.BatterySection(state, store)`, added **first** so the byte cap never truncates them.
  Injected by default whenever a store is present (no `batteries:` block needed). `GET /ledger` +
  face-go's `k` Knowledge tab surface it; `ledger_list` (MCP) + `/ledger` query it.
- **M7.2 tracked bugs** — v7 `bugs` table; store `WriteBug`/`QueryBugs`/`UpdateBugStatus`
  (`SqliteRunStore.Bugs.cs`); `conductor bug new|list|fix` (`BugCommand.cs`); MCP `bug_new`/`bug_list`/
  `bug_fix`; `BugsBattery` injects the run's OPEN bugs into later prompts; `GET /bugs` (open-by-default,
  `?status=all`). `ToolContract` now tells agents to file/list/fix instead of re-finding. Bugs outlive
  the session because they are run.db rows, not session state.
- **Truth gate MET, twice.** Unit: `M7KnowledgeTests.Session2_compiled_promptMd_on_disk_contains_the_note_and_the_bug`
  writes a real prompt.md and asserts against the file. Live: a 3-session dogfood where session 1's fake
  agent files a note + bug **via the CLI, concurrent with the running run.db** (WAL + the store's default
  busy timeout handle it), and sessions 2-3's `prompt.md` on disk contain both.
- **Face-go Knowledge tab (`k`)** — OPEN bugs (severity-coloured, with detail) on top, the ledger below:
  literally the rows the engine will inject next. New golden `knowledge`; every golden regenerated for the
  +1 tab. `api.FetchLedger`/`FetchBugs` + demo data.
- **Ink face (`face/`) RETIRED** (owner-directed). Deleted from the tree (history in git). `FaceLauncher`
  + `conductor face` spawn the **face-go binary** directly — no node runtime; `ResolveEntrypoint` finds
  `conductor-face(.exe)` next to the engine or under `face-go/bin/`. The Maestro `face` gate now
  `cd face-go && go build ./... && go test ./...` (a deliberate gate-command change — the ratchet flags
  that diff once and clears after push).
- **Real bug found + fixed by the smoke:** `conductor note`/`bug new` crashed printing `[{kind}]`/
  `[{severity}]` as Spectre markup (`[finding]`/`[high]` → "no such style"); `note` had it latently. Fixed
  to `(kind): text`.
- **HEADS-UP for M8/M9 (pre-existing, NOT M7):** `PromptBuilder.Verify` throws NRE when a Verify session
  is queued with a null `PendingVerify` (repro: a toy plan on the default `deliver-verify` workflow;
  `docs-only` sidesteps it). File it with `conductor bug new` and fix before the M9 dogfood.

**Commits:** `7f512a6` retire Ink face · `b28087a` M7 backend+tests · `cb98420` face-go Knowledge tab ·
`470b9ae` note/bug markup fix.

**Next: Maestro M8 (AFK & smart setup).** M8.1 `conductor doctor` (<2s, says exactly what's missing);
M8.2 Telegram v2 driven end-to-end from a phone. See `MAESTRO-TRACKER.md` for the live handoff.

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
  **Truth gate met**: `plan import docs/history/MAESTRO-PLAN.md` → exactly M1…M9 (a `(DONE …)`-marked bootstrap
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


---

## Read order
1. `C:\Code\conductor\NEXT-ERA.md` — strategic roadmap for Era v3 (post-Baton)
2. `CONDUCTOR-START.md` — tracker, all 67/67 checkpoints DONE
3. `docs/history/qa-reports/CONDUCTOR-FINAL.md` — final audit + Needs Human checklist
4. `docs/history/baton/BATON-BRIEF.md` — v2 design authority (reference)

## Deliverables authored on this branch (plan, not yet executed)
- `docs/history/baton/BATON-BRIEF.md` + `docs/history/baton/stages/B0.md`…`B12.md`
- `docs/history/baton/tooling/` (B0 drafts: editorconfig, Directory.Build.props, Directory.Packages.props,
  Meziantou ruleset rationale) + `docs/dev/adr/` (created in B0.6)
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
- **Read order:** `CONDUCTOR-VNEXT-PLAN.md` (tracker) → `docs/history/CONDUCTOR-VNEXT-PLAN.md` (design doc) → this section.

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

