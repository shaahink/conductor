# CONDUCTOR-UX — the U-series: landing, controls, insight, themes

Owner-directed UX era for the Face (face-go) and the engine's day-one experience. Source: owner
feedback from the 2026-07-16 playground dogfood session (the first real `conductor run` with the
token-free fake agent), verbatim asks paraphrased into specs below. Companion tracker:
`CONDUCTOR-UX-START.md`. Style contract: `face-go/STYLE.md` (sidebar-always, tabs not modals,
transparent overlays, one Catppuccin-coherent scheme).

The owner's summary: *"the demo looks great; the product around it must explain itself."* A person
who has never read the README should be able to run `conductor`, understand where they are, what
is running, in which directory, what it costs, and what will happen next — before spending a token.

## Ground rules (all stages)

- **Reuse before net-new.** The control plane already serves nearly everything these screens need
  (`/state` has repo, planDir, runId, budgets, tokens, gates; `/sessions`, `/tasks`, `/timeline`,
  `/plan`). Do not add engine endpoints unless a spec below says so.
- **Goldens are the eyes.** Every visual change lands with golden frames (`go test ./internal/tui/
  -run TestGolden -update` after review). Frames render in `widgets.ClockLocation = time.UTC`
  (pinned in TestMain) and the default theme.
- **Demo parity.** `--demo` (api/demo.go) must feed every new screen with plausible synthetic data —
  the demo is the product tour.
- **ASCII in PowerShell files**, `.editorconfig` discipline everywhere else; match surrounding idiom.
- Commit per checkpoint with conventional messages (`feat(face): U1.1 …`), update the tracker row
  (Status/Commit/Evidence) via `conductor task --done` semantics where wired, tracker edit otherwise.

## U0 — Engine: start, resume, journey (the pre-token experience)

**U0.1 Plan discovery — stop pointing at JSON paths.**
`PlanSettings.ResolvePlanPath()` today: `-p` → `CONDUCTOR_PLAN` → `./conductor.plan.json` → throw.
Extend the fallback chain: (a) exactly one `*.plan.json` in cwd → use it, print `using <path>`;
(b) else exactly one in `./plans/` → same; (c) several candidates + interactive console → Spectre
`SelectionPrompt` listing name + path (plan `name` field read cheaply); (d) several + redirected
output → error listing the candidates and how to choose; (e) none → today's friendly error, now
also suggesting `conductor init`. Unit-test the resolution order with temp dirs; the prompt path
is manual-only.

**U0.2 `conductor journey` — see the run before you buy it.**
New verb `journey` (PlanSettings): a pre-flight itinerary of the run, no state written, no agent
spawned. Sections: (1) identity — plan name, repo, tracker, state dir, whether saved state exists
(`.conductor/state.json` → "resumes session #N, stage X" vs "fresh run"); (2) stages in order with
sessions, workflow (resolved: deliver→verify etc.), model, and checkpoint counts from the tracker;
(3) gates by tier (fast/full/truth) with commands; (4) **human moments** — every point the run can
stop for a person: `pauseOnBlocked`, stages with `ownerGate`/approval semantics, `HUMAN:` token in
the tracker handoff, budget caps (`maxRunCostUsd`, `maxRunTokens`, `maxSessions`) and what happens
when they trip; (5) footer — exact commands to proceed: `conductor run -p <plan>` (or `--paused`,
`--dry-run`). Spectre tables/tree; must render < 1s; works on a plan whose run is live (read-only).
`run --dry-run` stays what it is (next prompt preview); journey is the map, dry-run is the next step.

**U0.3 Gateless plans + the resume story, documented and proven.**
Plans with `"gates": []` (or absent) must run: verdicts read "gates green (none configured)" rather
than failing or lying. Add a unit/integration test (fake-agent scratch loop with zero gates reaches
the same Progress verdicts) and make `doctor` say `gates: none configured — every session verdict
will trust commits + tracker only` as a warn-level notice, not a failure. Document in README run
section: how resume actually works (`conductor run -p <plan>` re-loads `.conductor/state.json` and
continues; `--paused` to attach the Face first; `conductor resume` is the control verb for a live
paused run — different thing). Fix the README `--no-dashboard` staleness if still present.

## U1 — Face: landing page + workspace identity

**U1.1 Home tab — the landing page.**
New first tab `Home` (key `h`, digit `1` if digits map to tabs), and the tab the Face opens on.
Panels (stacked, sidebar stays): **Server** — mode (live/demo), control-plane URL, connected
(events/transcript SSE), last connection error; **Run** — plan name, status badge, current stage +
title, session # / kind / attempt, run cost + overhead, token totals, budget caps with headroom
when set; **Workspace** — `repo` (the working dir every session edits), plan file (PlanDto.PlanFile),
tracker name, state dir (`<planDir>/.conductor`); **Next steps** — 3-4 contextual hints ("press a
for the live agent", "run is paused — : resume", "no run detected — conductor run -p …"). All data
already in `/state` + `/plan`; demo source fills it. When disconnected, Home replaces the splash as
the natural landing (splash content folds into the Server panel's disconnected state).

**U1.2 Workspace in the frame.**
The owner must always know what folder/repo the run is editing. Top bar (row with the chips): add
the repo's basename (dim, e.g. `…/conductor-baton`) with the full path on Home. Sessions tab rows
and Agent header keep stage context as today. Golden updates for the bar at normal + narrow widths.

## U2 — Face: organized controls, visual report, dev stats

**U2.1 Palette organization + promptable danger.**
`allVerbs` becomes grouped: `Run` (pause, resume, stop-after, approve, heartbeat, reload-plan),
`Stage` (goto, retry-stage, skip, pause-after-stage), `Danger` (kill, abort, rollback). The palette
overlay renders group headers (subtle), danger rows in red with `⚠`; filtering searches across
groups. Confirmation: every non-Safe verb keeps the `y/N` prompt (exists); the prompt line must
name consequences (`abort — kill session + stop conductor. y/N`). Help overlay (`?`) reflects the
groups. No direct destructive hotkey may fire without the same confirm.

**U2.2 Report tab — visual, not SQL.**
Replace the Report tab's SQL console with a rendered run report (the owner said: "report being sql
is stupid — show a good report visually"). Content, from `/state` + `/sessions` (+ existing canned
queries via `/report/query` where a DTO is missing): run header (plan, status, cost, tokens,
elapsed); overall checkpoint progress bar; per-stage table — state glyph, done/total, attempts,
cost, last outcome; sessions digest — last N sessions with kind, outcome, duration, cost, commits;
gates — last battery results with times; verifier scores when present. Layout: full-width sections,
progress bars in the theme's semantic colours, no interaction beyond scroll. Golden frames.

**U2.3 Dev tab — the developer screen.**
New tab `Dev` (key `d`). Top: the SQL console that used to be Report (quick queries + history +
editor + result grid — code moves, behaviour identical). Below/beside: **run internals** — event
seq / transcript seq / console seq counters, SSE connected flags, control-plane URL + token
presence, poll cadence; **per-session stats table** — tokens in/out/reasoning/cache-read and cost
per session (consume what `/sessions` + `/state` already carry; if per-session token columns are
missing from `SessionRowDto`, extend GET /sessions server-side — the run.db sessions table already
stores them). This is where stats live; Report stays owner-level.

## U3 — Face: curated themes, agent-terminal vibe, the glitch pass

**U3.1 Themes.**
3–4 curated schemes, each a complete palette for the existing role set (base/mantle/surface/…/
accent/semantic colours): `mocha` (today's Catppuccin, default), `latte` (light), `nord`, `gruvbox`.
Implementation: palette roles become a `Theme` struct in widgets/style.go; all derived lipgloss
styles rebuild via an explicit `ApplyTheme(name)` (widgets + tui each rebuild their style vars —
keep it one function per package, called at startup and on switch). Selection: `--theme <name>`
flag; runtime switch via palette verb `theme <name>` cycling live; persisted to
`os.UserConfigDir()/conductor-face/config.json` so the choice sticks. Goldens pin `mocha`.
STYLE.md gains a Themes section naming the roles.

**U3.2 The glitch pass — render, look, fix.**
Golden frames are the screenshots: render every tab at 132×40, 100×30, and 80×24 (the goldens'
sizes + a narrow one), read the frames, and fix what a human would flinch at — truncation collisions,
misaligned columns, orphan separators, missing hints, dead space. Every fix gets a one-line note in
the tracker evidence column and an updated golden. Start from the appendix list below — each item is
an observed defect from the owner's dogfood screenshots, not a guess.

**U3.3 Agent-terminal vibe — feel like the CLIs it drives.**
Owner: users arrive from Claude Code and opencode; the Agent tab should feel like those terminals,
not a third dialect. Priority order: Claude Code first, opencode second. Concretely: (1) transcript
presentation modeled on Claude Code's — `●`-bulleted tool calls with the tool name bold and its
one-line argument dim, thinking rendered as the quiet interstitial it is (dim italic, collapsed by
default beyond ~3 lines with a "+N lines (T to expand)" tail), result lines indented under their
tool call; (2) a session footer strip like Claude Code's status line — model, elapsed, token/cost
so far; (3) provider awareness — the plan's `agent.provider` ("claude" | "opencode") reaches the
Face (extend GET /plan or /state with the provider string; server-side it's `AgentConfig.Provider`),
and the transcript adopts that provider's glyph/label conventions; (4) keys users already have:
`ctrl+c` double-tap to quit (single tap = hint toast), `esc` always backs out one layer. Golden
frames for both provider renderings.

## Appendix — dogfood screenshot findings (2026-07-16, owner session)

Observed on live runs; each is a concrete defect for U-sessions to clear (most belong to U3.2's
pass; the starred ones are load-bearing enough to fix in whichever earlier stage touches that pane).

1. ★ **Top bar reads `LIVE · IDLE` with no labels** — connection state and run status side by side
   look contradictory. Label them (`conn LIVE · run IDLE`) or separate them visually (U1.2).
2. ★ **Chip row is cryptic**: `s16 $0.00 1s 0/0 cp 0/4 run $0.00` — the owner could not decode it.
   Every chip needs a label or a legend on Home/help (`session`, `checkpoints`, `run cost`) (U1.1/U2).
3. **`attempt 0/0` rendered pre-first-attempt** — fixed 2026-07-16 (hide when MaxAttempts is 0);
   keep a golden pinning the hidden state.
4. **`no gates` label ambiguous** — distinguish "gates: not run yet" from "no gates configured
   (gateless plan)" (pairs with U0.3).
5. **Kanban empty with no explanation while the sidebar shows a full plan** — a fetch failure or
   pre-seed state must say so in-pane ("cannot reach /tasks: …" / "no cards yet — engine is
   seeding"), never render as silent emptiness (U2/U3.2).
6. **Timeline "poured" history on attach with no visual break** — separate replayed history from
   live tail (a "— live —" rule line) so an attach never looks like an event storm (U3.2).
7. **Which model is working was invisible** — fixed 2026-07-16 (`/state.model` + strip chip);
   Home + Report should surface it too (U1.1/U2.2).
8. **Right-pinned elapsed clips before left segments truncate** at ~100 cols in the agent strip —
   padBetween should sacrifice the left side first (U3.2).
9. **Wall of un-collapsed thinking dominates the transcript** during long reasoning — U3.3's
   collapsed-by-default thinking addresses it; until then `T` folds.
10. **Ghost transcript across runs** — fixed 2026-07-16 engine-side (run-scoped rotation +
    reconnected live feed); U-sessions must not regress the `?since=` resume contract.

## Out of scope (naming so sessions don't drift)

- Web face, multi-run switching, and the plan marketplace: not this era.
- Engine scheduling changes (PathClaims etc.): done in the P-series, leave alone.
- The playground repo itself stays outside the repo; `tools/fake-agent.ps1` +
  `.claude/skills/run-conductor/driver.ps1` are the in-repo internal dogfood tools.
