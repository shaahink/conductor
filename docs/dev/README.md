# Contributor documentation

For **using** Conductor, see [`docs/README.md`](../README.md). This directory is for changing it.

Start with [`CONTRIBUTING.md`](../../CONTRIBUTING.md) — it covers the gate battery, the ratchet, and
what a good change looks like. The material here is what sits behind that page.

## Current work

**Charkh is open**, on `feat/charkh` since 2026-08-26, and
[`CHARKH-PLAN-2026-08-26.md`](CHARKH-PLAN-2026-08-26.md) **is** the design authority for current
work until it tags. It moves to [`../history/`](../history/) at the close, per the convention below
— and at CH4.2 that move stopped being a thing a person remembers: `conductor release perform`
carries it as one of its four mechanical acts, **with** the plan's `tracker`, `planDoc` and
`readOrder` repointed in the same act, because a move without the repoint means the next session
reads nothing.

Divan closed on 2026-08-26 and shipped as `v0.5.0` together with karvansara-edge, which had been
sitting on `master` untagged; both eras' briefs and trackers moved to `../history/` at DV7.3.
`plans/karvan/lanes.plan.json` is authored but not launch-ready and its own tracker says why.

| Doc | What it is |
|---|---|
| [`../../ARCHITECTURE.md`](../../ARCHITECTURE.md) | The map: the three assemblies and which way they point, one session's lifecycle end to end, the seams (thirteen since Divan), the surfaces (four since DV4), the courier - the one process that outlives a run - and where to add a new thing. Start here. Reconciled against the engine at CH5.1, 2026-08-27 (Charkh's three new areas, the seam count re-counted, the ratchet numbers re-measured); before that at DV7.1, 2026-08-26 and KS12.1, 2026-08-19. |
| [`CHARKH-PLAN-2026-08-26.md`](CHARKH-PLAN-2026-08-26.md) | **The open era's brief.** The wheel: what the owner still does by hand becomes machinery — the two batteries that differed in silence for an era, the demo that stopped matching the product, the docs read-and-agreed-with instead of diffed against a binary, and the era-close itself as three verbs (`release preflight`, `perform`, `runbook`) instead of a runbook a person carries out. Its per-stage decisions are the section a session reads, not the whole document. |
| [`DIVAN-BUG-SWEEP-2026-08-25.md`](DIVAN-BUG-SWEEP-2026-08-25.md) | The strand doc for Divan's DV2 sweep: the three defect ledgers (run.db bugs, followups OPEN rows, and field-observed engine defects that were in no repo ledger until this doc) with triage rules. |
| [`OBSERVABILITY-AND-MARKET-2026-08-22.md`](OBSERVABILITY-AND-MARKET-2026-08-22.md) | Where conductor sits in the 2026 market and why observability hurts: the orchestrator lane is commoditised, the referee is not; the edge run's GitHub mirror died to two log lines; the owner queue is the best agent inbox here and cannot leave the machine. Its ranked backlog feeds the document above. |
| [`CHAPAR-REMOTE-SURFACE-2026-08-18.md`](CHAPAR-REMOTE-SURFACE-2026-08-18.md) | The messenger/remote-surface spec KS11 was built from - the channel seam, chat profiles, the push grammar, evidence and metrics on demand. |
| [`GITHUB-SYNC-DESIGN-2026-08-13.md`](GITHUB-SYNC-DESIGN-2026-08-13.md) | The committed design KS9 implemented: one-way push, off by default, nothing inbound. Settled by [ADR-0005](adr/0005-push-only-remote-observability.md). |
| [`../history/NEXT-ERA-FINDINGS-2026-08-23.md`](../history/NEXT-ERA-FINDINGS-2026-08-23.md) | The **closed** Divan brief — the findings-turned-spec behind `plans/divan/core.plan.json`: the feedback inbox, the cloud measurement, and the merged ranking of its own asks and the observability backlog. Moved here at DV7.3 when Divan tagged; it is the design authority for nothing current. |
| [`../history/archive/trackers/DIVAN-TRACKER.md`](../history/archive/trackers/DIVAN-TRACKER.md) | The **closed** Divan tracker, 23/23, shipped as `v0.5.0` on 2026-08-26. A generated view: the checkpoint rows are overwritten from the database, and the handoff block is the part a session writes. |
| [`../history/KARVANSARA-PLAN-2026-08-13.md`](../history/KARVANSARA-PLAN-2026-08-13.md) | The **closed** KS-series brief covering both karvansara plans — the open door, then the gates that can't be gamed. Its per-checkpoint contracts are in [`../../plans/karvansara/contracts/`](../../plans/karvansara/contracts/). |
| [`../history/archive/trackers/KARVANSARA-CORE-TRACKER.md`](../history/archive/trackers/KARVANSARA-CORE-TRACKER.md) | The **closed** core tracker, 30/30, shipped as `v0.4.1` on 2026-08-15. |
| [`../history/archive/trackers/KARVANSARA-EDGE-TRACKER.md`](../history/archive/trackers/KARVANSARA-EDGE-TRACKER.md) | The **closed** edge tracker — gates that can't be gamed, and the courier. Shipped inside `v0.5.0`: `master` carried it untagged from 2026-08-19 until Divan's release covered both eras. |
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

**Karvansara kept both files until edge closed, and DV7.3 is where they moved** (the paragraph that
stood here promised the move at KS12.3, and KS12.3 never performed it). What closed at `v0.4.1` was
the *core plan*, not the era: `karvansara-edge` was authored on 2026-08-18 as
`plans/karvansara/edge.plan.json`, ran KS4/KS6/KS7/KS8/KS11 to green, and was merged to `master` at
KS12 — but its tag, its CHANGELOG rename and this move were all left undone, so the era stayed open
in the docs for a week while its code was already shipped. Divan's release closed both. All three
karvansara files and both Divan files moved together at DV7.3 (2026-08-26), the trackers to
`history/archive/trackers/` and the briefs to `history/`. The move was safe only because **no run was
live**: `edge.plan.json` and `divan/core.plan.json` name their trackers and briefs by path in
`tracker`, `planDoc` and `readOrder`, so moving either mid-run leaves the next session reading
nothing. `plans/karvan/CORE-TRACKER.md` stays where it is for the reason the paragraph above gives.

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
| [`adr/`](adr/) | Architecture decision records — **[0001](adr/0001-tooling-and-ruleset.md)** tooling/ruleset, **[0002](adr/0002-event-sourcing.md)** event sourcing (amended by W1.1 for the unified work graph), **[0003](adr/0003-cross-platform-packaging-closeout.md)** packaging, **[0004](adr/0004-face-tab-consolidation.md)** Face tab consolidation, **[0005](adr/0005-push-only-remote-observability.md)** push-only remote observability, **[0006](adr/0006-tui-conventions.md)** TUI conventions (scroll idiom, key namespace, markdown), **[0007](adr/0007-read-only-mcp-surface.md)** the read-only MCP surface - why `mcp-observe` publishes resources and exposes no tools, with MCP's 2026 attack record as the reason, **[0008](adr/0008-the-courier-outlives-the-run.md)** the courier outlives the run - the first machine-level process, and the four conditions that keep its loopback port consistent with 0005 rather than a reversal of it. If you change something an ADR settled, amend the ADR in the same PR. |
| [`../../face-go/STYLE.md`](../../face-go/STYLE.md) | The Face's live keybinding + layout reference. Read it before touching the UI. |
| [`TOKEN-BUDGET-TUNING.md`](TOKEN-BUDGET-TUNING.md) | **How the ceiling and the nudge are set, and what every era actually measured.** Section 12 carries the current prescription; a new plan compiles against it. Never guess these two numbers - re-run `conductor budget`. |
| [`RESEARCH.md`](RESEARCH.md) | Survey of comparable orchestrators and terminal-UX patterns. |
| [`NEXT-FEATURES.md`](NEXT-FEATURES.md) | Backlog of captured-but-unbuilt ideas. |
| [`templates/start-new-iteration.md`](templates/start-new-iteration.md) | Copy-and-fill template for starting a new plan iteration. |

## History

Closed eras, their briefs, and their raw gate transcripts are in [`../history/`](../history/). That
tree is receipts, not documentation — it exists so a past "gates green" can be audited rather than
believed.

## When a file moves — which references are rewritten, and which are not (CH3.2)

**A path is rewritten if and only if something still reads it.** DV7.3 moved nine structural paths
and left the plans' `notes` prose citing two briefs where they used to be. That was the right call
and it is also a trap, so here is the rule, decided once:

| | What it covers | What happens when a target moves |
|---|---|---|
| **Live** | the plan in flight with its tracker, contracts and templates; every document that plan's `readOrder` names; the published surface (`README.md`, `docs/*.md`, `ARCHITECTURE.md`, `AGENTS.md`, `CONTRIBUTING.md`); this index and [`NEXT-FEATURES.md`](NEXT-FEATURES.md); a path a test prints in a **failure message** | **Repointed, in the same checkpoint as the move.** A session reads these, and a stale path in a file a session reads costs the next session real time. |
| **Record** | a closed era's plan, contracts and tracker; everything under [`../history/`](../history/), `ci-health/` and `.conductor/`; every ADR, finding, field note, closed-era brief, workgraph runbook and template in this directory | **Never rewritten. Reported.** An ADR states a decision as it was made and a finding states what was measured on a date; bringing either "up to date" falsifies it. |

What makes leaving the record alone safe is that a reader who lands on a stale path can resolve it
in one hop. The classes of move this repo has made:

| Where a path used to be | Where it is now |
|---|---|
| `docs/dev/<ERA>-PLAN-<date>.md`, `docs/dev/NEXT-ERA-FINDINGS-<date>.md` | [`../history/`](../history/) |
| `plans/<era>/*-TRACKER.md`, `docs/<era>/…-TRACKER.md` | [`../history/archive/trackers/`](../history/archive/trackers/) |
| `docs/baton/audits/…`, `docs/baton/stages/…` | [`../history/baton/`](../history/baton/) |
| `docs/baton/adr/…` | [`adr/`](adr/) |
| `src/Conductor/Core/**` | `src/Conductor.Core/**` — the assembly split |
| `Core/Http/…`, `Core/Hosting/…` | `src/Conductor/…` — the shell kept the HTTP surface and the host |
| `plans/loom*.plan.json` | `examples/loom/` |
| `scripts/fake-agent.ps1` | `tools/fake-agent.ps1` |

The sweep behind this is `python tools/ch3/link-sweep.py` — every markdown link, every path in a
plan's or a contract's JSON, every path a test's failure message prints, resolved against the disk
and split into the two zones above. `--redirects` re-derives the table above from the moves
themselves rather than from anyone's memory of them; `--all` prints what resolved too. It exits
non-zero on a broken **live** reference and never on the record, which is the rule in executable
form. A reference that is quoted rather than followed goes in `tools/ch3/sweep-ignore.txt` with a
one-line reason that the sweep prints back.
