# Contributor documentation

For **using** Conductor, see [`docs/README.md`](../README.md). This directory is for changing it.

Start with [`CONTRIBUTING.md`](../../CONTRIBUTING.md) — it covers the gate battery, the ratchet, and
what a good change looks like. The material here is what sits behind that page.

## Current work

| Doc | What it is |
|---|---|
| [`../../ARCHITECTURE.md`](../../ARCHITECTURE.md) | The map: the three assemblies and which way they point, one session's lifecycle end to end, the seams, the two surfaces, and where to add a new thing. Start here. |
| [`KARVANSARA-PLAN-2026-08-13.md`](KARVANSARA-PLAN-2026-08-13.md) | The KS-series plan — the open door: one command over every run the machine ever ran, plan authoring without hand-written config, budget truth, GitHub sync. **The design authority for current work.** Its per-checkpoint contracts are in [`../../plans/karvansara/contracts/`](../../plans/karvansara/contracts/). |
| [`../../plans/karvansara/CORE-TRACKER.md`](../../plans/karvansara/CORE-TRACKER.md) | The live **tracker** — checkpoint table + handoff block, beside its plan in `plans/karvansara/`. Conductor drives itself with it. A generated view: the checkpoint rows are overwritten from the database, and the handoff block is the part a session writes. |
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
| [`adr/`](adr/) | Architecture decision records — **[0001](adr/0001-tooling-and-ruleset.md)** tooling/ruleset, **[0002](adr/0002-event-sourcing.md)** event sourcing (amended by W1.1 for the unified work graph), **[0003](adr/0003-cross-platform-packaging-closeout.md)** packaging, **[0004](adr/0004-face-tab-consolidation.md)** Face tab consolidation, **[0005](adr/0005-push-only-remote-observability.md)** push-only remote observability, **[0006](adr/0006-tui-conventions.md)** TUI conventions (scroll idiom, key namespace, markdown). If you change something an ADR settled, amend the ADR in the same PR. |
| [`../../face-go/STYLE.md`](../../face-go/STYLE.md) | The Face's live keybinding + layout reference. Read it before touching the UI. |
| [`RESEARCH.md`](RESEARCH.md) | Survey of comparable orchestrators and terminal-UX patterns. |
| [`NEXT-FEATURES.md`](NEXT-FEATURES.md) | Backlog of captured-but-unbuilt ideas. |
| [`templates/start-new-iteration.md`](templates/start-new-iteration.md) | Copy-and-fill template for starting a new plan iteration. |

## History

Closed eras, their briefs, and their raw gate transcripts are in [`../history/`](../history/). That
tree is receipts, not documentation — it exists so a past "gates green" can be audited rather than
believed.
