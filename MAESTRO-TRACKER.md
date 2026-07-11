# Maestro Phase Tracker

**Plan:** Maestro (Conductor v4) | **Branch:** `feat/foreman` | **Design doc:** `docs/MAESTRO-PLAN.md`

> This file is a **generated view** from `run.db` (from M4 onward it is regenerated on every write, and
> hand-edits to the checkpoint rows are discarded). Report progress with `conductor task`, not by editing
> this table. The handoff block below is yours to overwrite.

## Handoff (overwrite this block, <= 12 lines, no history)

last: M0 (bootstrap) landed by hand — Claude session, 2026-07-11. Not an agent session.
stage: M1 is next. 0/24 checkpoints DONE.
commits: e93a0be (agent-line crash fix) · b19bb08 (one-command run, port scan, prompt contract, templatesDir) · e2a24aa + fix (interlocking gates).
gate: dotnet 682/682 pass, 0w/0e, three consecutive clean runs. face 23/23. ratchet gate green.
branch: feat/foreman.
next: M1.1 — delete `src/Conductor/Ui/**` (2,021 lines) and everything that only exists to test it. The Face is the only UI now; `conductor run` already launches it.
qa: n/a — M0 was verified by running a real toy plan end to end (fake agent, real engine, control plane probed live), not by unit tests alone.

## Baseline numbers

| Metric | Value |
|---|---|
| Total checkpoints | 24 |
| Done | 0 |
| Tests (floor) | 621 attributes / 682 cases |
| Architecture debt | 8 files over the 500-line ceiling (9,131 lines total) |

## Checkpoints

Status: TODO | IN PROGRESS | DONE | BLOCKED.
**Evidence** = an artifact produced by a run *this phase*. A code path is not evidence. A test you wrote
is weak evidence. A truth gate Conductor ran itself is evidence.

### M1 — Deconstruction

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M1.1 | Delete `Ui/**` (2,021 lines) + PreviewCommand/DashboardPreview + tests that only test them | TODO | - | - |
| M1.2 | Split `Commands.cs` (2,574 lines / 54 types) — one file per command, none over 250 lines | TODO | - | - |
| M1.3 | Split `Orchestrator.cs` (2,334 lines) into RunLoop + SessionRunner + VerdictEngine | TODO | - | - |
| M1.4 | Split remaining offenders; `architecture-baseline.json` is empty `{}` | TODO | - | - |

### M2 — One truth: the database

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M2.1 | Schema defined once (versioned .sql); fresh DB and migrated DB are byte-identical | TODO | - | - |
| M2.2 | `IRunStore` + `SqliteRunStore`; no SQL elsewhere; failed writes are loud, not swallowed | TODO | - | - |
| M2.3 | `run.db` authoritative; `state.json` + `events.jsonl` DELETED; kill -9 mid-session then resume | TODO | - | - |
| M2.4 | Session history dir `.conductor/sessions/<NNN>/` + INDEX.md; `prompt.md` matches what was sent | TODO | - | - |
| M2.5 | Accurate per-session/per-plan cost + tokens incl. gate/advisor split | TODO | - | - |

### M3 — Workflows that bend

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M3.1 | Declarative workflow steps + 4 built-ins (deliver-verify, big-dev-then-big-audit, docs-only, spike) | TODO | - | - |
| M3.2 | Per-stage/per-session overrides (drop QA, change model) from plan AND TUI | TODO | - | - |
| M3.3 | Safe parallelism with path-claim collision avoidance | TODO | - | - |

### M4 — Gates that cannot be escaped

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M4.1 | Claims vs confirmations: agent claims, engine confirms; tracker hand-edits discarded | TODO | - | - |
| M4.2 | Truth-gate tier per stage + gate caching by (gate, sha, tier) that demonstrably hits | TODO | - | - |
| M4.3 | Verifier findings become the retry prompt; rigged-bad fails, rigged-good is not blocked | TODO | - | - |

### M5 — Observability and the Face

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M5.1 | Timeline pane — sessions, gates, stalls, verdicts, cost over time | TODO | - | - |
| M5.2 | Live plan pane — per-stage state/score/cost/attempts, no truncation at any width | TODO | - | - |
| M5.3 | Native console pane — raw agent stdout over SSE, toggle to clean folded view | TODO | - | - |
| M5.4 | Live ticker — cost/tokens fold from tokenDelta during the session, not at the end | TODO | - | - |
| M5.5 | Compiled-prompt preview beside the template editor (live + future sessions) | TODO | - | - |
| M5.6 | `conductor status` — one verdict, from the database, under a second | TODO | - | - |

### M6 — Plan authoring

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M6.1 | `conductor plan import` with model choice + confirm/edit table | TODO | - | - |
| M6.2 | Re-import diffs instead of clobbering | TODO | - | - |
| M6.3 | Edit plan/stages/models/workflows/gates from the TUI | TODO | - | - |

### M7 — Knowledge that compounds

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M7.1 | Ledger injected into the next prompt, surfaced in the Face, queryable | TODO | - | - |
| M7.2 | `conductor bug new/list/fix` + MCP; bugs outlive the session that found them | TODO | - | - |

### M8 — AFK

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M8.1 | `conductor doctor` < 2s, says exactly what is missing | TODO | - | - |
| M8.2 | Telegram v2 driven end to end from a phone | TODO | - | - |

### M9 — Dogfood close

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M9.1 | Real plan run end to end under Maestro; what bled is fixed | TODO | - | - |
| M9.2 | Final audit: every design-doc checkpoint rated CONFORMS/DEVIATES with evidence | TODO | - | - |

## Dependencies

```
M1 -> M2 -> M3 -> M4 -> M5 -> M6 -> M7 -> M8 -> M9
(linear on purpose: this is a deconstruction, and parallel lanes over a moving
 foundation is exactly how the last three eras produced code nobody ever ran)
```
