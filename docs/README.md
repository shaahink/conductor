# Documentation index

The `docs/` tree is layered by **era**. Conductor has been built in named series — B (Baton),
M (Maestro), G/P (AI-native + Planner), U (UX), W (Work graph) — and each era left behind a design
brief, per-stage notes, and gate evidence. That history is deliberately kept: the briefs are the
authority the engine was built against, and the evidence files are the transcripts that let a
checkpoint be re-checked years later.

**If you are new, read in this order:**

1. [`quickstart.md`](quickstart.md) — plan → tracker → dry run → first supervised session.
2. [`OPERATING-CONDUCTOR.md`](OPERATING-CONDUCTOR.md) — the control guide, written for an AI agent
   driving conductor (and just as usable by a human).
3. [`DOGFOOD-RUNBOOK.md`](DOGFOOD-RUNBOOK.md) — read this first when a run looks stuck, dead, or wrong.
4. [`../README.md`](../README.md) — the full plan-config schema reference lives there, not here.

## Current era — W (Work graph)

| Doc | What it is |
|---|---|
| [`CONDUCTOR-WORKGRAPH.md`](CONDUCTOR-WORKGRAPH.md) | The W-series design brief — one event-sourced work graph, the real-provider claim path, autonomy rails, AI-native bootstrap, the proof runs, and GitHub-readiness. **The design authority for current work.** |
| [`GAP-ANALYSIS.md`](GAP-ANALYSIS.md) | The owner-commissioned analysis that produced the W-series: why the loop broke and the road back. |
| [`workgraph/W5-REHEARSAL.md`](workgraph/W5-REHEARSAL.md) | The credential-free dress rehearsal write-up: one real binary driven from a markdown document to the first `RunFinished`, and the three engine defects it found. |
| [`workgraph/W3-WINDOW-CLOSE.md`](workgraph/W3-WINDOW-CLOSE.md) | The window-close rail proven by really closing a window: how `WM_CLOSE` reaches `CTRL_CLOSE_EVENT` from outside the process, and the hard-kill control that makes the evidence falsifiable. |
| [`workgraph/W5.2-RUNBOOK.md`](workgraph/W5.2-RUNBOOK.md) | **Read this before starting W5.2.** The one command, what it guards against before spending anything, which rails are armed, and what each outcome means. |
| [`workgraph/W5.2-TRACKER.md`](workgraph/W5.2-TRACKER.md) | The tracker the W5.2 proof run drives — four followup rows, three stages. Generated view; claims come from `conductor task --done`. |
| [`../CONDUCTOR-WORKGRAPH.md`](../CONDUCTOR-WORKGRAPH.md) | The live **tracker** (repo root) — checkpoint table + handoff block. |

## Reference — applies to every era

| Doc | What it is |
|---|---|
| [`quickstart.md`](quickstart.md) | Ten-minute path from nothing to an autonomous run. |
| [`OPERATING-CONDUCTOR.md`](OPERATING-CONDUCTOR.md) | Every control verb and when to reach for it. |
| [`DOGFOOD-RUNBOOK.md`](DOGFOOD-RUNBOOK.md) | Triage for a run that is stuck, dead, or lying. |
| [`RESEARCH.md`](RESEARCH.md) | Survey of comparable orchestrators and terminal-UX patterns. |
| [`NEXT-FEATURES.md`](NEXT-FEATURES.md) | Backlog of captured-but-unbuilt ideas. |
| [`templates/start-new-iteration.md`](templates/start-new-iteration.md) | Copy-and-fill template for starting a new plan iteration. |
| [`workflows/`](workflows/) | Session workflows used to drive specific eras. |
| [`baton/adr/`](baton/adr/) | Architecture decision records — **[0001](baton/adr/0001-tooling-and-ruleset.md)** tooling/ruleset, **[0002](baton/adr/0002-event-sourcing.md)** event sourcing (amended by W1.1 for the unified work graph), **[0003](baton/adr/0003-cross-platform-packaging-closeout.md)** packaging. |

## Earlier eras

Each row is a design brief; its tracker is named in the brief's header, and its stage notes and
evidence sit alongside it.

| Era | Brief | Also |
|---|---|---|
| **B — Baton** (v2: the engine's foundations) | [`baton/BATON-BRIEF.md`](baton/BATON-BRIEF.md) | [`baton/stages/`](baton/stages/) per-stage briefs · [`baton/evidence/`](baton/evidence/) gate transcripts · [`baton/audits/`](baton/audits/) · [`baton/tooling/`](baton/tooling/) B0 reference drafts (not active build files) · [`baton/CONDUCTOR-NEXT.md`](baton/CONDUCTOR-NEXT.md) |
| **M — Maestro** (v4) | [`MAESTRO-PLAN.md`](MAESTRO-PLAN.md) | [`maestro/`](maestro/) delivery plan, pre-session notes, final audit |
| **Era 3** | — | [`era3/evidence/`](era3/evidence/) gate transcripts |
| **G — AI-native** | [`CONDUCTOR-AI-NATIVE.md`](CONDUCTOR-AI-NATIVE.md) | — |
| **P — Planner** | [`CONDUCTOR-PLANNER.md`](CONDUCTOR-PLANNER.md) | — |
| **U — UX** (the Go face) | [`CONDUCTOR-UX.md`](CONDUCTOR-UX.md) | [`../face-go/STYLE.md`](../face-go/STYLE.md) is the live keybinding + layout reference |
| **F — v-next / Foreman** | [`CONDUCTOR-VNEXT-PLAN.md`](CONDUCTOR-VNEXT-PLAN.md) | — |
| QA sweeps | — | [`qa-reports/`](qa-reports/) |

Historical **trackers** (the checkpoint tables those eras were driven against) are archived under
[`archive/trackers/`](archive/trackers/). Only the live tracker stays at the repo root.

## A note on the evidence directories

`baton/evidence/` and `era3/evidence/` hold raw gate transcripts — `dotnet build` / `dotnet test`
output captured at the commit a checkpoint claimed. They are verbose, they quote absolute paths
from the machine that produced them, and that is the point: they are receipts, not documentation.
Nothing reads them automatically; they exist so a past "gates green" can be audited rather than
believed.
