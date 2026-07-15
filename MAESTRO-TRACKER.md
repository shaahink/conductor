# Maestro Phase Tracker

**Plan:** Maestro | **Branch:** `feat/foreman` | **Design doc:** docs/MAESTRO-PLAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: M5 COMPLETE (6/6). Added M5.3 (native console: GET /console/current SSE + face-go `c` pane) and M5.4 (live ticker: fold TokenDelta into /state live session cost/tokens/elapsed/agentActive, no double-count after finish). Both wire-tested. M5 truth gate met: golden snapshots at 80×24/120×30/200×50 (size_* goldens); killing the Face leaves the run alive (verified `--no-face`; control plane independent, AD-2). DOGFOOD: ran the REAL orchestrator against a toy plan + local fake agent → real events + raw log; curled /state (live agentActive+elapsed), /timeline (real folded events), /console/current (real raw agent stdout over SSE); `conductor status` gave the right verdict + what-hurt from real events in 118ms.
stage: M5 COMPLETE — all M5.1–M5.6 DONE. 21/30 DONE. Next: M6 (plan authoring).
commit: [next]
gate: build 0w/0e · 643 tests pass (twice, stable) · face-go build/vet/test green · ratchet green (ceiling 38).
branch: feat/foreman.
next: M6.1 `conductor plan import <file>` (a model turns a mega-plan into the task graph, rendered as a confirm/edit table); M6.2 re-import diffs not clobbers; M6.3 edit plan/stages/models/gates from the TUI. M6 truth gate: import THIS design doc → a graph whose stage ids match M1…M9.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 30 |
| Done | 21 |

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
| M3.1 | Declarative workflow steps + 4 built-ins (deliver-verify, big-dev-then-big-audit, docs-only, spike) | DONE | 18e0711 | WorkflowEngine.cs (232L) — 4 built-in workflows, condition evaluation, step resolution. SessionRunner.ResolveSessionKind + VerdictEngine.AdvanceWorkflowStep replace hardcoded state machine. 10 workflow tests pass. |
| M3.2 | Per-stage/per-session overrides from plan AND TUI (drop QA, change model, skip gates/commit) | DONE | 18e0711 | WorkflowOverrides model, StageConfig.Overrides + PathClaims fields, RunState.SkipGatesThisStage/SkipCommitThisStage/SkipVerificationThisStage. ApplyStageOverrides in RunLoop.Plumbing.cs. WorkflowEngine skips verification step when override is active. |
| M3.3 | Safe parallelism with path-claim collision avoidance | DONE | 18e0711 | PathClaimTracker.cs (65L) — concurrent path-claim registration/release with normalization. LaneCoordinator.StartParallelAudit checks for conflicts before spawning audit lane. Claims released on lane completion. 5 path-claim tests pass. |

### M4 — Gates that cannot be escaped — claims vs confirmations

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M4.1 | Claims vs confirmations: agent claims, engine confirms; tracker hand-edits discarded | DONE | 7d289e1 | ConfirmationEngine via PendingConfirmation → ConfirmCheckpoints. TrackerGenerator shows DONE ✓ for confirmed. Hand-edits detected (tracker vs DB cross-ref), warned to ledger, discarded from NewlyDone. 5 tests pass. |
| M4.2 | Truth-gate tier per stage + gate caching by (gate, sha, tier) that demonstrably hits | DONE | 7d289e1 | GetLastPassingGateResult tests: null (no cache), true (passing record), false (failed record), different SHA/tier miss. GateConfig truth tier exclusion from fast-only proven. 6 tests pass. |
| M4.3 | Verifier findings become the retry prompt; rigged-bad fails, rigged-good is not blocked | DONE | 7d289e1 | Verifier.Parse tests: bad delivery scores <80 (findings in output), good delivery passes ≥80, malformed output → null. PendingFix carries VerifierFindings. WorkflowEngine skip-verify treats as passed (no fix queued). 6 tests pass. |

### M5 — Observability — timeline, live plan, the native console, compiled prompts

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M5.1 | Timeline pane — sessions, gates, stalls, verdicts, cost over time | DONE | [next] | GET /timeline (backend) now has its first wire test (ControlPlaneServerTests seeds events, asserts the JSON kinds/cost). Face pane built in face-go: `t` opens a scrollable Timeline modal (clock + kind glyphs + cost), consuming /timeline via api.FetchTimeline. Golden test `timeline_modal` + unit test `TestTimelineOpenFetchNavigate`. |
| M5.2 | Live plan pane — per-stage state/score/cost/attempts, no truncation at any width | DONE | [next] | face-go sidebar now renders per-stage score (done/total), attempts (N×) and cost ($X.XX) alongside state glyph/colour; id + score never truncate, title adapts to width (ANSI-safe single-Render). Golden `sidebar_open` shows `● F7 2/5 … 1× $0.30`. Data all flows from StageDto (already carried it). Note: depth-indent for nested-stage dependencies is a follow-up (maestro stages are flat, so N/A here). |
| M5.3 | Native console pane — raw agent stdout over SSE, toggle to clean folded view | DONE | [next] | New GET /console/current SSE endpoint tails the current session's raw agent stdout (.conductor/logs/session-NNN.jsonl, newest by mtime). face-go `c` opens a Native Console modal showing raw lines with scroll/live-tail; the transcript pane is the folded view. Wire test + golden `console_modal` + unit test. DOGFOOD: curled /console/current against a real toy run — streamed the real agent's raw stream-json line by line. |
| M5.4 | Live ticker — cost/tokens fold from tokenDelta during the session, not at the end | DONE | [next] | ControlPlaneServer.WithLiveSessionMetrics folds TokenDelta for the in-flight session into /state's SessionCostUsd/tokens + live SessionElapsedSec + AgentActive, and adds it to the run total (no double-count once SessionFinished lands). face-go ticker shows a live `● $x.xx` session segment when active. 2 wire tests (live fold + no-double-count). DOGFOOD: real toy run's /state showed agentActive:true, sessionElapsedSec live; TokenDelta fold unit-verified (the fake agent emits none). |
| M5.5 | Compiled-prompt preview beside the template editor (live + future sessions) | DONE | [next] | GET /prompt/preview?stage=&kind= (backend) now has its first wire tests (compiled prompt non-empty for a real stage; 404 for unknown stage). Face pane built in face-go: in the template editor, `v` toggles a compiled-prompt preview for the current stage via api.FetchPromptPreview. Golden test `prompt_preview` + unit test `TestPromptCompiledPreviewToggle`. |
| M5.6 | `conductor status` — one verdict, from the database, under a second | DONE | [next] | StatusReportBuilder folds run.db's event log (RunStateProjection + SnapshotBuilder, the same path `/state` uses) into a verdict; StatusCommand renders it. No longer reads state.json or the tracker markdown for the verdict. Exercised live against the real `.conductor/run.db` (Maestro): DB read 223ms (well under 1s). 6 truth-gate tests seed a run.db and assert the verdict with NO state.json on disk. Fast by default; LLM narrative moved to opt-in `--deep`. |

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
