# Contributor documentation

For **using** Conductor, see [`docs/README.md`](../README.md). This directory is for changing it.

Start with [`CONTRIBUTING.md`](../../CONTRIBUTING.md) — it covers the gate battery, the ratchet, and
what a good change looks like. The material here is what sits behind that page.

## Current work

| Doc | What it is |
|---|---|
| [`../../ARCHITECTURE.md`](../../ARCHITECTURE.md) | The map: the three assemblies and which way they point, one session's lifecycle end to end, the seams, the surfaces (three since KS8.1), and where to add a new thing. Start here. Reconciled against the engine at KS12.1, 2026-08-19 - 27 drifted `file:line` citations corrected and the edge era's own sections added. |
| [`KARVANSARA-PLAN-2026-08-13.md`](KARVANSARA-PLAN-2026-08-13.md) | The KS-series plan — the open door: one command over every run the machine ever ran, plan authoring without hand-written config, budget truth, GitHub sync. **The design authority for current work.** Its per-checkpoint contracts are in [`../../plans/karvansara/contracts/`](../../plans/karvansara/contracts/). |
| [`../../plans/karvansara/EDGE-TRACKER.md`](../../plans/karvansara/EDGE-TRACKER.md) | The live **tracker** for `edge.plan.json` - gates that can't be gamed, and the courier. Same generated-view rules as the core tracker below. |
| [`NEXT-ERA-FINDINGS-2026-08-23.md`](NEXT-ERA-FINDINGS-2026-08-23.md) | The Divan era's findings-turned-spec: the feedback inbox (voice notes and files into a per-project record), whether sessions can run on Anthropic's cloud (measured against `--cloud`: no, not the session loop), and the merged ranking of both this document's asks and the observability backlog. With its Part 6 amendments, **the design authority for `plans/divan/core.plan.json`** (compiled 2026-08-25). |
| [`../../plans/divan/TRACKER.md`](../../plans/divan/TRACKER.md) | The live **tracker** for the Divan era - the chancellery: inbox, courier, and the record that gets out. Same generated-view rules as the karvansara trackers. |
| [`DIVAN-BUG-SWEEP-2026-08-25.md`](DIVAN-BUG-SWEEP-2026-08-25.md) | The strand doc for Divan's DV2 sweep: the three defect ledgers (run.db bugs, followups OPEN rows, and field-observed engine defects that were in no repo ledger until this doc) with triage rules. |
| [`OBSERVABILITY-AND-MARKET-2026-08-22.md`](OBSERVABILITY-AND-MARKET-2026-08-22.md) | Where conductor sits in the 2026 market and why observability hurts: the orchestrator lane is commoditised, the referee is not; the edge run's GitHub mirror died to two log lines; the owner queue is the best agent inbox here and cannot leave the machine. Its ranked backlog feeds the document above. |
| [`CHAPAR-REMOTE-SURFACE-2026-08-18.md`](CHAPAR-REMOTE-SURFACE-2026-08-18.md) | The messenger/remote-surface spec KS11 was built from - the channel seam, chat profiles, the push grammar, evidence and metrics on demand. |
| [`GITHUB-SYNC-DESIGN-2026-08-13.md`](GITHUB-SYNC-DESIGN-2026-08-13.md) | The committed design KS9 implemented: one-way push, off by default, nothing inbound. Settled by [ADR-0005](adr/0005-push-only-remote-observability.md). |
| [`../../plans/karvansara/CORE-TRACKER.md`](../../plans/karvansara/CORE-TRACKER.md) | The **closed** core tracker, 30/30, shipped as v0.4.1 on 2026-08-15. It stays here rather than in `history/` only until KS12.3 moves both trackers together. A generated view: the checkpoint rows are overwritten from the database, and the handoff block is the part a session writes. |
| [`../history/CONDUCTOR-KARVAN.md`](../history/CONDUCTOR-KARVAN.md) | The **closed** K-series brief. Moved to `history/` when Karvan tagged, per the convention below; it is the design authority for nothing current. |
| [`GAP-ANALYSIS.md`](GAP-ANALYSIS.md) | The owner-commissioned analysis that produced the W-series: why the loop broke and the road back. Still the reference for why the rails exist. |

An era's brief and tracker live together while the era is open, and both move to
[`../history/`](../history/) when it closes — the brief to `history/`, the tracker to
[`../history/archive/trackers/`](../history/archive/trackers/). The W-series is the worked example:
its brief is [`../history/CONDUCTOR-WORKGRAPH.md`](../history/CONDUCTOR-WORKGRAPH.md) and its tracker
is [`../history/archive/trackers/WORKGRAPH-TRACKER.md`](../history/archive/trackers/WORKGRAPH-TRACKER.md).
The W-series write-ups below stay here because `plans/conductor-w52.plan.json` and
`tools/w5/start-w52.ps1` still address them by path.

**Karvan's tracker is the exception, and this is the reason** (recorded at KS10.1, 2026-08-15): the
brief moved to `history/` as the convention says, but `plans/karvan/CORE-TRACKER.md` stays where it is
because `plans/karvan/core.plan.json:32` names it as its `tracker`, and `plans/karvan/lanes.plan.json`
— authored, 0/23, launching after karvansara-edge — sits in the same directory. Moving the file would
break a plan that has not run yet, which is the same trap the W-series paragraph above describes. It
moves when the lanes plan does.

**Karvansara keeps BOTH files, and this is the reason** (recorded at KS10.3, 2026-08-15, when the
core plan shipped as v0.4.1): what closed is the *core plan*, not the era. `karvansara-edge` — KS4
(verification that can't be gamed), KS6 (quality lane), KS7 (platform catch-up), KS8 — was unauthored
at the time, belongs in `plans/karvansara/` beside the plan that just finished, and its design is the
same brief that would move. **Updated at KS12.1 (2026-08-19):** edge was authored on 2026-08-18 as
`plans/karvansara/edge.plan.json` (24 checkpoints, KS11 Chapar added as the remote surface), it has
run KS11/KS7/KS6/KS4/KS8 to green, and it is closing now at KS12. **KS12.3 is the checkpoint that
performs the move this paragraph promises** — `CORE-TRACKER.md`, `EDGE-TRACKER.md` and
`KARVANSARA-PLAN-2026-08-13.md` go to `history/` together, both trackers to
`history/archive/trackers/`. Nothing may move before it: `edge.plan.json` names `EDGE-TRACKER.md` as
its `tracker` and the plan's `readOrder` names the brief by path, so moving either mid-run leaves the
next session reading nothing. `plans/karvansara/core.plan.json:36` names `CORE-TRACKER.md` as its `tracker`, and
`.conductor/contracts/KS2face.json` and `KS9-10.json` address the brief by path. So the tracker stays
in `plans/karvansara/` and `KARVANSARA-PLAN-2026-08-13.md` stays here; both move when edge closes.
Same trap, one era later.

## Findings and write-ups

| Doc | What it is |
|---|---|
| [`FINDING-oss-readiness.md`](FINDING-oss-readiness.md) | What stood between this repo and a public release: the portability audit (the engine is portable; the tooling is not), the docs-density problem, and the decisions taken about all three. |
| [`workgraph/W5-REHEARSAL.md`](workgraph/W5-REHEARSAL.md) | The credential-free dress rehearsal: one real binary driven from a markdown document to the first `RunFinished`, and the three engine defects it found. |
| [`workgraph/W3-WINDOW-CLOSE.md`](workgraph/W3-WINDOW-CLOSE.md) | The window-close rail proven by really closing a window: how `WM_CLOSE` reaches `CTRL_CLOSE_EVENT` from outside the process, and the hard-kill control that makes the evidence falsifiable. |
| [`workgraph/W5.2-RUNBOOK.md`](workgraph/W5.2-RUNBOOK.md) | **Read before starting W5.2.** The one command, what it guards against before spending anything, which rails are armed, and what each outcome means. |
| [`workgraph/W5.2-TRACKER.md`](workgraph/W5.2-TRACKER.md) | The tracker the W5.2 proof run drives. Generated view; claims come from `conductor task --done`. |

## Reference

| Doc | What it is |
|---|---|
| [`adr/`](adr/) | Architecture decision records — **[0001](adr/0001-tooling-and-ruleset.md)** tooling/ruleset, **[0002](adr/0002-event-sourcing.md)** event sourcing (amended by W1.1 for the unified work graph), **[0003](adr/0003-cross-platform-packaging-closeout.md)** packaging, **[0004](adr/0004-face-tab-consolidation.md)** Face tab consolidation, **[0005](adr/0005-push-only-remote-observability.md)** push-only remote observability, **[0006](adr/0006-tui-conventions.md)** TUI conventions (scroll idiom, key namespace, markdown), **[0007](adr/0007-read-only-mcp-surface.md)** the read-only MCP surface - why `mcp-observe` publishes resources and exposes no tools, with MCP's 2026 attack record as the reason. If you change something an ADR settled, amend the ADR in the same PR. |
| [`../../face-go/STYLE.md`](../../face-go/STYLE.md) | The Face's live keybinding + layout reference. Read it before touching the UI. |
| [`TOKEN-BUDGET-TUNING.md`](TOKEN-BUDGET-TUNING.md) | **How the ceiling and the nudge are set, and what every era actually measured.** Section 12 carries the current prescription; a new plan compiles against it. Never guess these two numbers - re-run `conductor budget`. |
| [`RESEARCH.md`](RESEARCH.md) | Survey of comparable orchestrators and terminal-UX patterns. |
| [`NEXT-FEATURES.md`](NEXT-FEATURES.md) | Backlog of captured-but-unbuilt ideas. |
| [`templates/start-new-iteration.md`](templates/start-new-iteration.md) | Copy-and-fill template for starting a new plan iteration. |

## History

Closed eras, their briefs, and their raw gate transcripts are in [`../history/`](../history/). That
tree is receipts, not documentation — it exists so a past "gates green" can be audited rather than
believed.
