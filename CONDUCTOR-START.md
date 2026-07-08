# Conductor — Baton v2 Phase Tracker (resume here)

**Read order for a fresh session:** this file → `docs/baton/BATON-BRIEF.md` (design authority,
MANDATORY) → your stage file `docs/baton/stages/B<n>.md` → the audit/handover for the previous
stage in `.conductor/handovers/`.
Branch scheme: `feat/baton-b<stage>` off `feat/baton`. Worktree: `C:\Code\conductor-baton`.
Driver: the stable `bin\conductor.exe` built from `master`.

## Handoff  (overwrite this block, ≤12 lines, no history)
last: session #8 (B1, deliver) — landed **B1.4**: per-plan `ProgressConventions` (stageIdPattern,
      handoffMarker, humanToken, statusVocabulary) on PlanConfig; `CheckpointRow.Create` derives stage +
      status via the conventions (P-0→P-0, P0.1→P0, P3.4b→P3, F5→F5); Orchestrator consumes humanToken.
      Ratcheted **MA0009→error** (FU-B0-3 CLOSED), all regexes carry `ProgressConventions.RegexTimeout`.
      Defaults byte-identical to Loom. Build 0w/0e net10, 73 tests (66+7). Diff 12 files, in budget.
stage: **B1 IN PROGRESS** — B1.1…B1.4 DONE; B1.5…B1.7 TODO. Battery GREEN.
gate: GREEN — `dotnet build Conductor.slnx` 0w/0e net10 (MA0009=error); `dotnet test` 73 pass.
qa: session #7 (B1.3) PASS. (1) 9 ProgressProviderTests green; (2) `_progress =
      ProgressProviderFactory.Create(plan)` (Orchestrator.cs:23) load-bearing, read at 5 sites
      (66/352/401/842/904). No findings.
next: **B1.5** — read-order battery: `plan.readOrder: [docs…]` rendered into the session prompt as an
      ordered, bounded list; `PromptBuilder` gains a `{readOrder}` section. Gate: `PromptBuilderTests`
      assert the list appears; empty when unset.
trap: the STABLE driver is master's binary — it parses via master's `TrackerParser`, NOT this build, so
      new conventions only bite once this build ships; defaults are byte-identical (proven) so
      CONDUCTOR-START.md parses under both. Don't dry-run the live self-plan (lock). `DashboardRenderer`
      :219 still hard-codes DONE/BLOCKED for row colour (display-only; convention-wire later if needed).
dirty: none tracked.
evidence: B1.4-gate.txt (+ B1.3, B1.2, B1.1, B0.1…B0.5, audits/B0-baseline.md, adr/000{1,2}-*.md)

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
| B1.5 | Read-order context battery (mandated docs per plan) | TODO | | |
| B1.6 | conductor new-plan --template {minimal,dotnet,node,shamshir}; schema version + fail-fast validation | TODO | | |
| B1.7 | Shamshir iter-parity-pipeline TRACKER.md authored + parsed via default provider (unit test) | TODO | | |
| B2.1 | ConductorEvent schema + append-only events.jsonl writer (additive, alongside state.json) | TODO | | |
| B2.2 | Projections: RunState rebuilt by folding the log; StateCompat parity tests | TODO | | |
| B2.3 | Crash recovery replays the event log (not just state.json) | TODO | | |
| B2.4 | IAgentProvider + Opencode/Claude/GenericText adapters; Orchestrator provider-switch removed | TODO | | |
| B2.5 | Host/DI/Options + Microsoft.Extensions.Logging + Serilog sinks; no silent catch {} | TODO | | |
| B2.6 | TokenDelta events per step_finish (fixes live-token lag F-3) | TODO | | |
| B3.1 | Destructive-action confirm in TUI (A/K/S) + CLI (--yes/interactive) | TODO | | |
| B3.2 | Owner-gate step type + AwaitingOwner status; approve via CLI/TUI | TODO | | |
| B3.3 | Process control: retry-stage, rollback (to checkpoint), pause-after-stage, goto | TODO | | |
| B3.4 | Budget/token caps (limits.maxRunCostUsd/maxRunTokens) + approval mode | TODO | | |
| B3.5 | Graceful Ctrl+C (final heartbeat + queue-resume + flush) | TODO | | |
| B4.1 | Alternate-screen buffer with clean restore on exit/crash | TODO | | |
| B4.2 | Spectre Layout rebuild of DashboardRenderer.BuildRoot | TODO | | |
| B4.3 | Hierarchical plan tree (sub-checkpoints; expand/collapse; per-stage cost/attempts/last-outcome) | TODO | | |
| B4.4 | Severity model (INFO/WARN/ERROR/SUCCESS/WAITING/HUMAN) + clearer header labels | TODO | | |
| B4.5 | Structured thinking pane (Goal/Hypothesis/Evidence/Action) + tool-call folding | TODO | | |
| B4.6 | Command history search + filters (/build /git /test; commands/thoughts/errors) | TODO | | |
| B4.7 | Live-consistent token line + plan-tree filter/search for large plans; doc-on-select | TODO | | |
| B5.1 | Timeline view (transitions with duration) from the event log | TODO | | |
| B5.2 | Replay / time-travel (F8) reconstructs a past run from events.jsonl | TODO | | |
| B5.3 | AI-health metrics (retry rate, command repetition, failure loops, tool oscillation, context saturation) | TODO | | |
| B5.4 | Confidence tracking per checkpoint (evidence count) + MCP call metrics + repo strip | TODO | | |
| B6.1 | Telegram client (long-poll getUpdates) + push (needs-human/owner-gate/complete/backoff) + /status | TODO | | |
| B6.2 | Two-way control (inline-keyboard callback_query → control.json); chat-id allowlist; destructive confirm | TODO | | |
| B6.3 | Richer REPORT.md (progress bars, collapsible per-stage, commit links) + clean heartbeat (no history pollution) | TODO | | |
| B6.4 | Notify hooks (webhook/Discord/Slack) first-class examples | TODO | | |
| B6.5 | **Acceptance: Conductor drives Shamshir P-0 + P0.1 headless, independently verified** | TODO | | |
| B7.1 | Per-stage/per-checkpoint agent override in plan schema (command/systemPrompt/temperature/tokens) | TODO | | |
| B7.2 | Built-in persona registry (planner/reviewer/architect/qa/docs/refactor/test-writer/git-cleanup/security) | TODO | | |
| B7.3 | PromptBuilder merges base + persona; persona shown in dashboard/report/events | TODO | | |
| B8.1 | Reflection step → rolling .conductor/lessons.md (bounded) | TODO | | |
| B8.2 | Lessons injected into next prompt ({lessons} battery) — closes F-7 | TODO | | |
| B8.3 | Self-review stage kind (stronger model reviews last N sessions, proposes adjustments) | TODO | | |
| B8.4 | Handover weak/deferred bullets → tracked .conductor/followups.md (opt. block phase-confirm) | TODO | | |
| B8.5 | Pluggable IPromptBattery (lessons/DoD-recap/repo-map/recent-failure); token rollover (RolledOver, no attempt burned) | TODO | | |
| B9.1 | Task graph model + event-sourced store (TaskAdded/TaskStatusChanged) beneath the checkpoint table | TODO | | |
| B9.2 | Planner persona decomposes active checkpoint → ordered sub-tasks | TODO | | |
| B9.3 | MCP task server (task_list/task_update/task_add) — persists agent todo list across sessions | TODO | | |
| B9.4 | Cooperative soft-break (finish sub-task→handoff→fresh session) + hard token-ceiling fresh-start fallback | TODO | | |
| B9.5 | Task views in CLI/TUI/Telegram | TODO | | |
| B10.1 | stages[].dependsOn graph + smarter ready-stage ordering (sequential exec preserved) | TODO | | |
| B10.2 | First-class hierarchical stages in state + reports | TODO | | |
| B10.3 | Per-stage pre/post hooks beyond gates | TODO | | |
| B10.4 | Collapse double gate battery (agent ritual + conductor) → one source of truth; measured token drop | TODO | | |
| B11.1 | Cross-platform gate runner (bash/sh alongside PowerShell via gates[].shell) | TODO | | |
| B11.2 | dotnet tool packaging + tab completion + conductor doctor | TODO | | |
| B11.3 | ADRs finalised; StateCompat + clean-clone battery | TODO | | |
| B11.4 | **Acceptance: drive a full owner-gated Shamshir phase (parity-pipeline P2.2)** | TODO | | |
| B12.1 | Tier A read-only analysis lanes (arch/design/qa/research, scratch cwd, artifacts feed prompts+handover) | TODO | | |
| B12.2 | Worker pool + concurrency cap + brain scheduling (opt-in per task-type) | TODO | | |
| B12.3 | Tier B isolated-worktree mutating lanes → full-battery MERGE GATE before acceptance | TODO | | |
| B12.4 | fix-lanes consume .conductor/followups.md (blend-in debt fixing) | TODO | | |

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
