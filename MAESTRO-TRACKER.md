# Maestro Phase Tracker

**Plan:** Maestro | **Branch:** `feat/foreman` | **Design doc:** docs/MAESTRO-PLAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: M8 COMPLETE (AFK & smart setup) + the pre-existing workflow-index NRE from the M7 heads-up FIXED. Bug (found + fixed this session): `SessionRunner.ResolveSessionKind`'s workflow-fallback resolved a step but never recorded its index, so `WorkflowStepIndices` lagged one step behind after a stage's first session and `AdvanceWorkflowStep` never populated `PendingVerify`/`PendingAudit`/`PendingFix` for the step it actually picked — `PromptBuilder.Verify` NRE'd on a null pending record. Fixed by extracting `WorkflowEngine.ResolveAndRecordStep`, a single call both `SessionRunner` and `VerdictEngine` now share; filed+fixed via `conductor bug` (bug #1). M8.1 — `conductor doctor` repurposed in place (owner decision) from the pre-M2 resume-preview into a <2s health check: agent CLI/git/face-go binary/DNS/disk/API/budget/Telegram, reusing `PreflightHealth` + `StatusReportBuilder`; verified live against this repo's own Maestro plan (421ms). M8.2 — Telegram v2 **reframed by the owner mid-session**: instead of only "drive a toy run from a phone", the Face itself now guides Telegram setup end-to-end — new `SecretsStore` (`.conductor/secrets.local.json`, gitignored) lets the bot token be typed into the Face instead of requiring an env var; `TelegramService` gained live status tracking + `TestConnectionAsync` (real getMe + test push); new `GET /telegram/status`, `POST /telegram/test`, `POST /telegram/token`, and a `telegram` `/plan/edit` target for chat ids/poll interval/two-way. face-go's new `l` Telegram tab reads as a guided wizard: live status line, numbered guide with checkmarks, in-pane field editor, one-shot test-send.
stage: M8 COMPLETE — 28/30 DONE. Next: M9 (dogfood close).
commit: 50720b0 (workflow-index bug fix), 19a45e1 (M8.1 doctor + M8.2 backend), 9ed1192 (M8.2 face-go Telegram tab).
gate: dotnet build 0w/0e · full C# suite green (669 tests: 614 fast + 55 integration, incl. 8 new Telegram wire tests + 2 new WorkflowEngine regression tests + 24 new DoctorCommand tests) · architecture ratchet green · face-go build/vet/test green (3 new `telegram_*` goldens, full suite regenerated for the 10th tab) · doctor verified live against the real Maestro plan.
branch: feat/foreman.
next: M9.1 real plan run end to end under Maestro, fix what bleeds. M9.2 final audit. HEADS-UP for M9: the credential-gated live verification for M8.2 (paste a real bot token into the Face, add a real chat id, hit Test, confirm a real Telegram message arrives, then drive a toy run watching session-end pushes/NeedsHuman buttons/reply-to-inject/`/status`) has NOT been done — it needs the owner's real bot token (`HUMAN:` item). Do this before M9 close, or explicitly accept the gap in the M9.2 final audit.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 30 |
| Done | 28 |

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
| M6.1 | `conductor plan import` with model choice + confirm/edit table | DONE | abd1b5f | Deterministic markdown→graph parser (`MarkdownPlanParser`): structured plan/tracker doc → stages with NO model call (zero-spend); freeform prose falls back to the advisor model; `--model` fills a `{model}` placeholder; `--yes` skips confirm. Truth gate met: `plan import docs/MAESTRO-PLAN.md` → exactly M1…M9 (unit test reads the real doc; also verified via CLI). |
| M6.2 | Re-import diffs instead of clobbering | DONE | abd1b5f | `PlanDiff.Compute/Apply`: re-import shows added/changed stages+gates and applies only those; hand-tuned entries never clobbered. CLI-verified: import→M1…M9 then re-import = "Nothing to change" (X0 stage preserved). |
| M6.3 | Edit plan/stages/models/workflows/gates from the TUI | DONE | c337cca, 9d6951c | Backend: `GET /plan` (editable plan from disk), `POST /plan/edit` (atomic field edits, validated via `CollectErrors`, live instance never mutated on the HTTP thread), `POST /plan/import` (deterministic parse+diff over the wire, apply flag) — 5 wire tests. Face: `g` Plan Editor modal (Stages·Gates·Settings·Import tabs; enum carousel model/workflow/kind/tier picker; import path→diff→apply), interactive in `--demo`. 6 golden frames + `plan_test.go`. |

### M7 — Knowledge that compounds — ledger, tracked bugs, structured handovers

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M7.1 | Ledger injected into the next prompt, surfaced in the Face, queryable | DONE | b28087a, cb98420 | `LedgerBattery` (src/Conductor/Core/PromptBattery.Knowledge.cs) injects recent `conductor note` entries into `BatterySection`, added first so the byte cap never drops them — proven on disk by `M7KnowledgeTests.Session2_compiled_promptMd_on_disk_contains_the_note_and_the_bug` and the live 3-session dogfood. Surfaced: `GET /ledger` (ControlPlaneServer.Knowledge.cs) + face-go `k` Knowledge tab. Queryable: MCP `ledger_list` (pre-existing) + `GET /ledger`. |
| M7.2 | `conductor bug new/list/fix` + MCP; bugs outlive the session that found them | DONE | b28087a, cb98420, 470b9ae | v7 `bugs` table + `WriteBug`/`QueryBugs`/`UpdateBugStatus` (SqliteRunStore.Bugs.cs); `conductor bug new\|list\|fix` (BugCommand.cs); MCP `bug_new`/`bug_list`/`bug_fix`; `BugsBattery` injects OPEN bugs into later prompts; `GET /bugs`; face-go Knowledge tab. Tests: store CRUD, battery-excludes-fixed, MCP round-trip, `/bugs` wire (open-by-default + `?status=all`). Truth gate met (note + bug both on the session-2 prompt.md, unit + live). |

### M8 — AFK — doctor, init, Telegram driven for real

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M8.1 | `conductor doctor` < 2s, says exactly what is missing | DONE | 19a45e1 | `DoctorCommand.cs` repurposed in place: agent CLI (PATH/absolute resolution), git (branch+dirty via `Git.cs`), face-go binary (`FaceLauncher.ResolveEntrypoint`), DNS/disk/API (reused `PreflightHealth.RunAllAsync` with sane defaults when unconfigured), budget headroom (`StatusReportBuilder`, run.db), Telegram configured. 24 `DoctorCommandTests.cs` unit tests (deliberately-broken-environment truth gate: missing PATH command, nonexistent repo, dirty tree, over-budget, no-token/no-chat-id Telegram states — exact fail/warn lines asserted). Live truth gate: run against this repo's own `plans/conductor-maestro.plan.json` — 421ms, correct 6 ok/2 warn/0 fail output. |
| M8.2 | Telegram v2 — configured, tested, and status shown **from the Face** (owner-redirected mid-session from "phone-driven" to "guided in-app setup") | DONE | 19a45e1, 9ed1192 | Backend: `SecretsStore` (new `.conductor/secrets.local.json`, gitignored); `TelegramService.ResolveToken` instance-ized with a secrets-file fallback (env var still wins); `TestConnectionAsync` (real `getMe` + a real test push when a chat id is configured); `GET /telegram/status`, `POST /telegram/test`, `POST /telegram/token`, `telegram` `/plan/edit` target (`ApplyTelegramEdit`) for chat ids/poll interval/two-way. `ControlPlaneServer` now takes `ITelegramService` (4 call sites updated). 8 `ControlPlaneServerTelegramTests.cs` wire tests (status/test/token/edit round-trips against a real `HttpListener`, no live Telegram call). Face: new `l` Telegram tab (`tab_telegram.go`) — live status line, numbered guided-setup checklist, in-pane field editor (bot token masked & never prefilled, chat ids, poll interval, two-way), one-shot test-send action. 3 new goldens + full suite regenerated. **Not yet done: the credential-gated live phone dogfood** (real bot token, real message, toy run to completion) — needs the owner's real bot token; see tracker Handoff heads-up. |

### M9 — Dogfood close — run a real plan, fix what bleeds, final audit

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| M9.1 | Real plan run end to end under Maestro; what bled is fixed | TODO | - | - |
| M9.2 | Final audit: every design-doc checkpoint rated CONFORMS/DEVIATES with evidence | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
