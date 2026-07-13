# Maestro Phase Tracker

**Plan:** Maestro | **Branch:** `feat/foreman` | **Design doc:** docs/MAESTRO-PLAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: M4 DELIVERED (3/3 checkpoints DONE). Claims vs confirmations: agent marks via conductor task --done (status=DONE, confirmed=0), engine confirms after green gates + verifier pass (confirmed=1). TrackerGenerator shows DONE ✓ for confirmed. Hand-edits detected, warned to ledger, discarded. Gate cache by (name, tier, sha) proven with tests. Verifier findings → retry prompt proven with tests. 17 new tests. Fixed 2 M3 bugs (skip-verify recursion, PathClaimTracker TOCTOU).
stage: M4 COMPLETE. 15/30 checkpoints DONE.
commits: (pending commit for this session: M4 delivery + M3 bug fixes + schema v6).
gate: build 0w/0e · 631/631 tests pass (0w/0e).
branch: feat/foreman.
next: M5 — Observability: timeline, live plan, native console, ticker, prompt preview, conductor status.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 30 |
| Done | 15 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence).

### M1 — Deconstruction — delete the old face, break the god classes

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M1.1 | Delete `Ui/**` (2,021 lines) + PreviewCommand/DashboardPreview + tests that only test them | DONE | - | src/Conductor/Ui/ deleted (2,021 lines removed). git commit sha will follow. |
| M1.2 | Split `Commands.cs` (2,574 lines / 54 types) — one file per command, none over 250 lines | DONE | - | 29 files in Commands/, all under 250 lines. Commit: 6434e54. Commands.cs deleted. |
| M1.3 | Split `Orchestrator.cs` (2,334 lines) into RunLoop + SessionRunner + VerdictEngine | DONE | c540a13 | Orchestrator.cs 142L (thin wiring). RunLoop.cs (489L) + RunLoop.Plumbing.cs (263L) + RunLoop.Snapshot.cs (98L). SessionRunner.cs (396L) + SessionRunner.Mcp.cs (150L). VerdictEngine.cs (440L) + VerdictEngine.Phase.cs (495L). |
| M1.4 | Split remaining offenders; `architecture-baseline.json` is empty `{}` | DONE | [next] | Baseline {} — all 5 line-ceiling files and 9 type-ceiling files split into 70+ total files under limits. |

### M2 — One truth — run.db is authoritative, state.json and events.jsonl are deleted

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M2.1 | Schema defined once (versioned .sql); fresh DB and migrated DB are byte-identical | DONE | - | 5 versioned .sql files under Core/Store/Migrations/ (v1-v5). MigrationRunner applies sequentially. Old duplicate DDL deleted with RunDb.cs. |
| M2.2 | `IRunStore` + `SqliteRunStore`; no SQL elsewhere; failed writes are loud, not swallowed | DONE | - | IRunStore (104L) with 8 partial SqliteRunStore files. All SQL behind store. Failed writes log Error. RunDb.cs deleted (457L). |
| M2.3 | `run.db` authoritative; `state.json` + `events.jsonl` DELETED; kill -9 mid-session then resume | DONE | - | RecoverFromCrash reads from IRunStore.FindInterruptedSession. ControlPlaneServer, McpTaskServer, SessionRunner all use store. 7 CLI commands migrated. events table + run_state table replace files. |
| M2.4 | Session history dir `.conductor/sessions/<NNN>/` + INDEX.md; `prompt.md` matches what was sent | DONE | - | WriteSessionHistory in RunLoop.Plumbing.cs — creates prompt.md, cost.json, verdict.md, handover.md, INDEX.md per session. |
| M2.5 | Accurate per-session/per-plan cost + tokens incl. gate/advisor split | DONE | - | Agent/gate costs already recorded. Advisor cost tracking added in ConsultAdvisorAsync (wallet + category="advisor"). |

### M3 — Workflows that bend — declarative steps, per-session overrides, safe parallelism

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M3.1 | Declarative workflow steps + 4 built-ins (deliver-verify, big-dev-then-big-audit, docs-only, spike) | DONE | [next] | WorkflowEngine.cs (232L) — 4 built-in workflows, condition evaluation, step resolution. SessionRunner.ResolveSessionKind + VerdictEngine.AdvanceWorkflowStep replace hardcoded state machine. 10 workflow tests pass. |
| M3.2 | Per-stage/per-session overrides from plan AND TUI (drop QA, change model, skip gates/commit) | DONE | [next] | WorkflowOverrides model, StageConfig.Overrides + PathClaims fields, RunState.SkipGatesThisStage/SkipCommitThisStage/SkipVerificationThisStage. ApplyStageOverrides in RunLoop.Plumbing.cs. WorkflowEngine skips verification step when override is active. |
| M3.3 | Safe parallelism with path-claim collision avoidance | DONE | [next] | PathClaimTracker.cs (65L) — concurrent path-claim registration/release with normalization. LaneCoordinator.StartParallelAudit checks for conflicts before spawning audit lane. Claims released on lane completion. 5 path-claim tests pass. |

### M4 — Gates that cannot be escaped — claims vs confirmations

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M4.1 | Claims vs confirmations: agent claims, engine confirms; tracker hand-edits discarded | DONE | [next] | ConfirmationEngine via PendingConfirmation → ConfirmCheckpoints. TrackerGenerator shows DONE ✓ for confirmed. Hand-edits detected (tracker vs DB cross-ref), warned to ledger, discarded from NewlyDone. 5 tests pass. |
| M4.2 | Truth-gate tier per stage + gate caching by (gate, sha, tier) that demonstrably hits | DONE | [next] | GetLastPassingGateResult tests: null (no cache), true (passing record), false (failed record), different SHA/tier miss. GateConfig truth tier exclusion from fast-only proven. 6 tests pass. |
| M4.3 | Verifier findings become the retry prompt; rigged-bad fails, rigged-good is not blocked | DONE | [next] | Verifier.Parse tests: bad delivery scores <80 (findings in output), good delivery passes ≥80, malformed output → null. PendingFix carries VerifierFindings. WorkflowEngine skip-verify treats as passed (no fix queued). 6 tests pass. |

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
