# Conductor — Baton v2 Phase Tracker (resume here)

**Read order for a fresh session:** this file → `conductor-DEBT.md` (audit followups —
unresolved bugs + deferred work) → `docs/workflows/conductor-post-b12-workflow.md`
(the workflow for this phase) → `docs/baton/BATON-BRIEF.md` (design authority, MANDATORY) →
your stage in `.conductor/plans/conductor-debt.plan.json`.
Branch: `feat/baton`. Driver: `C:\Code\conductor\bin\conductor.exe` (stable from master).

## Handoff  (overwrite this block, ≤12 lines, no history)
last: session #70 (C3) — events + metrics + budget + recovery: RollbackExecuted event emitted from Orchestrator, budget (PerRunCostUsd/PerRunTokens) persisted in RunState and restored on restart, mid-session control feedback for rejected verbs, orphaned resume with empty AgentSessionId marks NeedsHuman. 493 tests pass (13 new C3EventsAndRecoveryTests).
stage: C3 DONE. Build 0w/0e.
dirty: none.
next: C4 — Small debt sweep (12 items: fake-agent, smokes, persona, Telegram, etc.)
followups: FU-B2-1 (LiveMetrics dashboard wiring) partially deferred — dashboard still reads via agent.Tokens* (event-log fold proven in tests, full cutover deferred to C4/C5).
evidence: docs/baton/evidence/C3-gate.txt (493 tests, 13 C3). Test file: tests/Conductor.Tests/C3EventsAndRecoveryTests.cs.
C2 QA: verified DONE — MA0045/MA0002 at error, CT in IProgressProvider, stdout/stderr split, 10 C2Async tests pass.

## Baseline numbers (2026-07-08, before B0 — re-measure, drift >5% without explanation blocks)

| Metric | Value |
|---|---|
| Target framework | net9.0 (B0 → net10.0) |
| Tests | 56 pass (0 warn, 0 err) |
| Analyzers | none (B0 adds Meziantou + NetAnalyzers, warnings-as-errors) |
| Source files | ~30 .cs under src/Conductor |
| Providers | opencode-json / stream-json / text (hard-branched — B2 → IAgentProvider) |
| Progress model | Loom markdown-table only (B1 → IProgressProvider) |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Scope changes get a `> scope change:` line under the row —
never silent renumbering.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| B0.1 | net10 migration + Directory.Build.props + Directory.Packages.props (verify existing Conductor.slnx) | DONE | b3f1499 | docs/baton/evidence/B0.1-gate.txt |
| B0.2 | .editorconfig + Meziantou.Analyzer + NetAnalyzers, curated ruleset, warnings-as-errors, 56 tests green | DONE | cf378f0 | docs/baton/evidence/B0.2-gate.txt |
| B0.3 | CONDUCTOR-START.md + plans/conductor.self.plan.json + self-plan gates (temp-dir workaround: self-contained copy with repo path rewritten, --dry-run there) | DONE | 90d2567 | docs/baton/evidence/B0.3-gate.txt |
| B0.4 | fake-agent.ps1 scenarios extended; self-loop token-free smoke via --once (fixed A6 crash: opencode-json must nest under `part`) | DONE | 3032eb9 | docs/baton/evidence/B0.4-gate.txt |
| B0.5 | Baseline audit doc (current coupling/debt) written as B0 evidence | DONE | 62a819e | docs/baton/evidence/B0.5-gate.txt, docs/baton/audits/B0-baseline.md |
| B0.6 | ADR-0001 (tooling/ruleset rationale) + ADR-0002 (event-sourcing decision) | DONE | cf378f0,d416ead | docs/baton/adr/0001-tooling-and-ruleset.md, docs/baton/adr/0002-event-sourcing.md |
| B1.1 | Move plans/loom* + templates → examples/loom/; Loom loads + --dry-run green from new path | DONE | 0aa242d | docs/baton/evidence/B1.1-gate.txt |
| B1.2 | IProgressProvider abstraction + MarkdownTableProvider (today's parser, zero behaviour change) | DONE | ac306f5 | docs/baton/evidence/B1.2-gate.txt |
| B1.3 | ScriptProvider (command→JSON) + PlanCheckpointProvider | DONE | 3e0fdbd | docs/baton/evidence/B1.3-gate.txt |
| B1.4 | Configurable conventions (stage-id regex incl. P-0/P3.4b/F5, handoff marker, HUMAN token, status vocab) | DONE | 2330361 | docs/baton/evidence/B1.4-gate.txt |
| B1.5 | Read-order context battery (mandated docs per plan) | DONE | 01c1732 | docs/baton/evidence/B1.5-gate.txt |
| B1.6 | conductor new-plan --template {minimal,dotnet,node,shamshir}; schema version + fail-fast validation | DONE | c3fa637 | docs/baton/evidence/B1.6-gate.txt |
| B1.7 | Shamshir iter-parity-pipeline TRACKER.md authored + parsed via default provider (unit test) | DONE | 8701aff | docs/baton/evidence/B1.7-gate.txt |
| B2.1 | ConductorEvent schema + append-only events.jsonl writer (additive, alongside state.json) | DONE | d5ebd12 | docs/baton/evidence/B2.1-gate.txt |
| B2.2 | Projections: RunState rebuilt by folding the log; StateCompat parity tests | DONE | e2b6a03 | docs/baton/evidence/B2.2-gate.txt |
| B2.3 | Crash recovery replays the event log (not just state.json) | DONE | a5a6b85 | docs/baton/evidence/B2.3-gate.txt |
| B2.4 | IAgentProvider + Opencode/Claude/GenericText adapters; Orchestrator provider-switch removed | DONE | 8e1ceb4 | docs/baton/evidence/B2.4-gate.txt |
| B2.5 | Host/DI/Options + Microsoft.Extensions.Logging + Serilog sinks; no silent catch {} | DONE | 02da5a0, 7512371 | docs/baton/evidence/B2.5-gate.txt |
| B2.6 | TokenDelta events per step_finish (fixes live-token lag F-3) | DONE | <commit> | docs/baton/evidence/B2.6-gate.txt |
| B3.1 | Destructive-action confirm in TUI (A/K/S) + CLI (--yes/interactive) | DONE | db01755 | docs/baton/evidence/B3.1-gate.txt |
| B3.2 | Owner-gate step type + AwaitingOwner status; approve via CLI/TUI | DONE | a48b3bd | docs/baton/evidence/B3.2-gate.txt |
| B3.3 | Process control: retry-stage, rollback (to checkpoint), pause-after-stage, goto | DONE | 90ce43a | docs/baton/evidence/B3.3-gate.txt |
| B3.4 | Budget/token caps (limits.maxRunCostUsd/maxRunTokens) + approval mode | DONE | 157cdc8 | docs/baton/evidence/B3.4-gate.txt |
| B3.5 | Graceful Ctrl+C (final heartbeat + queue-resume + flush) | DONE | 157cdc8 | docs/baton/evidence/B3.4-gate.txt |
| B4.1 | Alternate-screen buffer with clean restore on exit/crash | DONE | c6d5efb | docs/baton/evidence/B4.1-gate.txt |
| B4.2 | Spectre Layout rebuild of DashboardRenderer.BuildRoot | DONE | d3aa1a5 | docs/baton/evidence/B4.2-gate.txt |
| B4.3 | Hierarchical plan tree (sub-checkpoints; expand/collapse; per-stage cost/attempts/last-outcome) | DONE | 8197bd4 | docs/baton/evidence/B4.3-gate.txt, docs/baton/evidence/B4.3-preview.txt |
| B4.4 | Severity model (INFO/WARN/ERROR/SUCCESS/WAITING/HUMAN) + clearer header labels | DONE | 9b25fe2 | docs/baton/evidence/B4.4-gate.txt, docs/baton/evidence/B4.4-preview.txt |
| B4.5 | Structured thinking pane (Goal/Hypothesis/Evidence/Action) + tool-call folding | DONE | 5b9db37 | docs/baton/evidence/B4.5-gate.txt, docs/baton/evidence/B4.5-preview.txt |
| B4.6 | Command history search + filters (/build /git /test; commands/thoughts/errors) | DONE | f4f2997 | docs/baton/evidence/B4.6-gate.txt, docs/baton/evidence/B4.6-preview.txt |
| B4.7 | Live-consistent token line + plan-tree filter/search for large plans; doc-on-select | DONE | 1f61578, c1edb3b | docs/baton/evidence/B4.7-gate.txt, docs/baton/evidence/B4.7-tokens-preview.txt, docs/baton/evidence/B4.7-docselect-preview.txt |
| B5.1 | Timeline view (transitions with duration) from the event log | DONE | 69d70c2 | docs/baton/evidence/B5.1-gate.txt |
| B5.2 | Replay / time-travel (F8) reconstructs a past run from events.jsonl | DONE | 6c876e5 | docs/baton/evidence/B5.2-gate.txt |
| B5.3 | AI-health metrics (retry rate, command repetition, failure loops, tool oscillation, context saturation) | DONE | 17642cf | docs/baton/evidence/B5.3-gate.txt |
| B5.4 | Confidence tracking per checkpoint (evidence count) + MCP call metrics + repo strip | DONE | 1507870 | docs/baton/evidence/B5.4-gate.txt |
| B6.1 | Telegram client (long-poll getUpdates) + push (needs-human/owner-gate/complete/backoff) + /status | DONE | 762bed5 | docs/baton/evidence/B6.1-gate.txt |
| B6.2 | Two-way control (inline-keyboard callback_query → control.json); chat-id allowlist; destructive confirm | DONE | 762bed5 | docs/baton/evidence/B6.1-gate.txt |
| B6.3 | Richer REPORT.md (progress bars, collapsible per-stage, commit links) + clean heartbeat (no history pollution) | DONE | 762bed5 | docs/baton/evidence/B6.1-gate.txt |
| B6.4 | Notify hooks (webhook/Discord/Slack) first-class examples | DONE | 762bed5 | docs/baton/evidence/B6.1-gate.txt |
| B6.5 | **Acceptance: Conductor drives Shamshir P-0 + P0.1 headless, independently verified** | DONE | 762bed5 | docs/baton/evidence/B6.5-shamshir-acceptance.txt, docs/baton/audits/B6-shamshir-acceptance.md |
| B7.1 | Per-stage/per-checkpoint agent override in plan schema (command/systemPrompt/temperature/tokens) | DONE | 38e14fc | docs/baton/evidence/B7-gate.txt |
| B7.2 | Built-in persona registry (planner/reviewer/architect/qa/docs/refactor/test-writer/git-cleanup/security) | DONE | 38e14fc | docs/baton/evidence/B7-gate.txt |
| B7.3 | PromptBuilder merges base + persona; persona shown in dashboard/report/events | DONE | 38e14fc | docs/baton/evidence/B7-gate.txt |
| B8.1 | Reflection step → rolling .conductor/lessons.md (bounded) | DONE | a50c15f | docs/baton/evidence/B8-gate.txt |
| B8.2 | Lessons injected into next prompt ({lessons} battery) — closes F-7 | DONE | a50c15f | docs/baton/evidence/B8-gate.txt |
| B8.3 | Self-review stage kind (stronger model reviews last N sessions, proposes adjustments) | DONE | a50c15f | docs/baton/evidence/B8-gate.txt |
| B8.4 | Handover weak/deferred bullets → tracked .conductor/followups.md (opt. block phase-confirm) | DONE | a50c15f | docs/baton/evidence/B8-gate.txt |
| B8.5 | Pluggable IPromptBattery (lessons/DoD-recap/repo-map/recent-failure); token rollover (RolledOver, no attempt burned) | DONE | a50c15f | docs/baton/evidence/B8-gate.txt |
| B9.1 | Task graph model + event-sourced store (TaskAdded/TaskStatusChanged) beneath the checkpoint table | DONE | a0eda3c | commit msg (build 0w/0e, 336 tests pass) |
| B9.2 | Planner persona decomposes active checkpoint → ordered sub-tasks | DONE | 87a7c72 | tests/Conductor.Tests/PlannerTests.cs (6 tests) |
| B9.3 | MCP task server (task_list/task_update/task_add) — persists agent todo list across sessions | DONE | 92371d7 | tests/Conductor.Tests/McpTaskServerTests.cs (7 tests) |
| B9.4 | Cooperative soft-break (finish sub-task→handoff→fresh session) + hard token-ceiling fresh-start fallback | DONE | e078820 | docs/baton/evidence/B9.4-gate.txt |
| B9.5 | Task views in CLI/TUI/Telegram | DONE | 1fa665c | docs/baton/evidence/B9.5-gate.txt |
| B10.1 | stages[].dependsOn graph + smarter ready-stage ordering (sequential exec preserved) | DONE | 9750665 | docs/baton/evidence/B10.1-gate.txt |
| B10.2 | First-class hierarchical stages in state + reports | DONE | 0e1c1f7 | docs/baton/evidence/B10.2-gate.txt |
| B10.3 | Per-stage pre/post hooks beyond gates | DONE | 6fa8938 | docs/baton/evidence/B10.3-gate.txt |
| B10.4 | Collapse double gate battery (agent ritual + conductor) → one source of truth; measured token drop | DONE | 5cb82f2 | docs/baton/evidence/B10.4-gate.txt |
| B11.1 | Cross-platform gate runner (bash/sh alongside PowerShell via gates[].shell) | DONE | 3ba9d2b | docs/baton/evidence/B11.1-gate.txt |
| B11.2 | dotnet tool packaging + tab completion + conductor doctor | DONE | 16e8532 | docs/baton/evidence/B11.2-gate.txt |
| B11.3 | ADRs finalised; StateCompat + clean-clone battery | DONE | 746b164 | docs/baton/evidence/B11.3-gate.txt |
| B11.4 | **Acceptance: drive a full owner-gated Shamshir phase (parity-pipeline P2.2)** | DONE | b04a264 | docs/baton/evidence/B11.4-gate.txt, docs/baton/audits/B11-shamshir-p2.2.md |
| B12.1 | Tier A read-only analysis lanes (arch/design/qa/research, scratch cwd, artifacts feed prompts+handover) | DONE | 168920d | docs/baton/evidence/B12.1-gate.txt |
| B12.2 | Worker pool + concurrency cap + brain scheduling (opt-in per task-type) | DONE | d659ec6 | docs/baton/evidence/B12.2-gate.txt |
| B12.3 | Tier B isolated-worktree mutating lanes → full-battery MERGE GATE before acceptance | DONE | ebc8ab8 | docs/baton/evidence/B12.3-gate.txt |
| B12.4 | fix-lanes consume .conductor/followups.md (blend-in debt fixing) | DONE | 1706c45 | `docs/baton/evidence/B12.4-gate.txt` |
| C1 | B12.4 — Fix-lanes consume followups.md | DONE | 1706c45 | Same as B12.4 |
| C2 | Async engine + integration harness (MA0045, MA0002, CT, harness) | DONE | 633be3f | `docs/baton/evidence/C2-gate.txt` |
| C3 | Events + metrics + budget + recovery (LiveMetrics, rollback, McpCallFinished, Ctrl+C) | DONE | e14b88c | `docs/baton/evidence/C3-gate.txt` |
| C4 | Small debt sweep (12 items: fake-agent, smokes, persona, Telegram, etc.) | TODO | — | `docs/baton/evidence/C4-gate.txt` |
| C5 | R1 — TUI + CLI audit (--dry-run preview, every surface traced to code+docs) | TODO | — | `docs/qa-reports/CONDUCTOR-AUDIT-R1.md` |
| C6 | R2 — Report + Prompts + Agent Context audit | TODO | — | `docs/qa-reports/CONDUCTOR-AUDIT-R2.md` |
| C7 | Final handover + Needs Human Verification checklist | TODO | — | `docs/qa-reports/CONDUCTOR-FINAL.md` |

## Quick commands

```powershell
# build + test (from the worktree)
dotnet build Conductor.slnx
dotnet test  Conductor.slnx

# dry-run the self-plan with the STABLE driver (never the binary under edit)
C:\Code\conductor\bin\conductor.exe run --dry-run -p plans\conductor.self.plan.json
# one supervised session
C:\Code\conductor\bin\conductor.exe run --once   -p plans\conductor.self.plan.json
# full run
C:\Code\conductor\bin\conductor.exe run          -p plans\conductor.self.plan.json
```
