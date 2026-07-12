# Maestro Phase Tracker

**Plan:** Maestro | **Branch:** `feat/foreman` | **Design doc:** docs/MAESTRO-PLAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

stage: M1 in progress. 2/30 checkpoints DONE (M1.1 + M1.2).
commits: 801c3e1 (M1.1) · 6434e54 (M1.2) · [next] (s6 fix).
gate: build GREEN (0w/0e) · architecture 4/4 GREEN · ratchet FAILS test floor (550 < 623).
branch: feat/foreman.
fixes this session: (a) HarnessTests.cs CS0234 — restored Conductor.Core.Hosting + Conductor.Models imports (M1.1 had collapsed them into non-existent Conductor.Tests.Harness). (b) CtlCommand.cs split from 10 types into 1 base file + 9 command files. (c) Orchestrator.cs partials (Sessions 604L, Verdicts 894L) split into files under 500L: Sessions+Live+SoftBreak+Pipeline+Verdicts+Phase+Advisory+Completion. (d) architecture-baseline.json: removed Orchestrator.cs (now 408L, under 500 ceiling). Archdebt: 5812→3478.
HUMAN: ratchet floor 623 must be lowered to 550. M1.1 (commit 801c3e1) legitimately deleted 73 [Fact]/[Theory] attributes from Spectre TUI test files + inline tests that tested deleted Ui/ code. The floor was set at 623 before M1.1 and never updated. The deletions are correct — there is no code to test. Lower minTests in tools/gates/ratchet-baseline.json from 623 to 550.
next after HUMAN: continue M1.3 (Orchestrator partials committed but not yet RunLoop/SessionRunner/VerdictEngine classes), then M1.4 (remaining files to get baseline to {}).


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 30 |
| Done | 2 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence).

### M1 — Deconstruction — delete the old face, break the god classes

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M1.1 | Delete `Ui/**` (2,021 lines) + PreviewCommand/DashboardPreview + tests that only test them | DONE | - | src/Conductor/Ui/ deleted (2,021 lines removed). git commit sha will follow. |
| M1.2 | Split `Commands.cs` (2,574 lines / 54 types) — one file per command, none over 250 lines | DONE | - | 29 files in Commands/, all under 250 lines. Commit: 6434e54. Commands.cs deleted. |
| M1.3 | Split `Orchestrator.cs` (2,334 lines) into RunLoop + SessionRunner + VerdictEngine | TODO | - | - |
| M1.4 | Split remaining offenders; `architecture-baseline.json` is empty `{}` | TODO | - | - |

### M2 — One truth — run.db is authoritative, state.json and events.jsonl are deleted

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M2.1 | Schema defined once (versioned .sql); fresh DB and migrated DB are byte-identical | TODO | - | - |
| M2.2 | `IRunStore` + `SqliteRunStore`; no SQL elsewhere; failed writes are loud, not swallowed | TODO | - | - |
| M2.3 | `run.db` authoritative; `state.json` + `events.jsonl` DELETED; kill -9 mid-session then resume | TODO | - | - |
| M2.4 | Session history dir `.conductor/sessions/<NNN>/` + INDEX.md; `prompt.md` matches what was sent | TODO | - | - |
| M2.5 | Accurate per-session/per-plan cost + tokens incl. gate/advisor split | TODO | - | - |

### M3 — Workflows that bend — declarative steps, per-session overrides, safe parallelism

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M3.1 | Declarative workflow steps + 4 built-ins (deliver-verify, big-dev-then-big-audit, docs-only, spike) | TODO | - | - |
| M3.2 | Per-stage/per-session overrides (drop QA, change model) from plan AND TUI | TODO | - | - |
| M3.3 | Safe parallelism with path-claim collision avoidance | TODO | - | - |

### M4 — Gates that cannot be escaped — claims vs confirmations

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M4.1 | Claims vs confirmations: agent claims, engine confirms; tracker hand-edits discarded | TODO | - | - |
| M4.2 | Truth-gate tier per stage + gate caching by (gate, sha, tier) that demonstrably hits | TODO | - | - |
| M4.3 | Verifier findings become the retry prompt; rigged-bad fails, rigged-good is not blocked | TODO | - | - |

### M5 — Observability — timeline, live plan, the native console, compiled prompts

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M5.1 | Timeline pane — sessions, gates, stalls, verdicts, cost over time | TODO | - | - |
| M5.2 | Live plan pane — per-stage state/score/cost/attempts, no truncation at any width | TODO | - | - |
| M5.3 | Native console pane — raw agent stdout over SSE, toggle to clean folded view | TODO | - | - |
| M5.4 | Live ticker — cost/tokens fold from tokenDelta during the session, not at the end | TODO | - | - |
| M5.5 | Compiled-prompt preview beside the template editor (live + future sessions) | TODO | - | - |
| M5.6 | `conductor status` — one verdict, from the database, under a second | TODO | - | - |

### M6 — Plan authoring — import, re-import diff, edit from the TUI

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M6.1 | `conductor plan import` with model choice + confirm/edit table | TODO | - | - |
| M6.2 | Re-import diffs instead of clobbering | TODO | - | - |
| M6.3 | Edit plan/stages/models/workflows/gates from the TUI | TODO | - | - |

### M7 — Knowledge that compounds — ledger, tracked bugs, structured handovers

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M7.1 | Ledger injected into the next prompt, surfaced in the Face, queryable | TODO | - | - |
| M7.2 | `conductor bug new/list/fix` + MCP; bugs outlive the session that found them | TODO | - | - |

### M8 — AFK — doctor, init, Telegram driven for real

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M8.1 | `conductor doctor` < 2s, says exactly what is missing | TODO | - | - |
| M8.2 | Telegram v2 driven end to end from a phone | TODO | - | - |

### M9 — Dogfood close — run a real plan, fix what bleeds, final audit

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M9.1 | Real plan run end to end under Maestro; what bled is fixed | TODO | - | - |
| M9.2 | Final audit: every design-doc checkpoint rated CONFORMS/DEVIATES with evidence | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
