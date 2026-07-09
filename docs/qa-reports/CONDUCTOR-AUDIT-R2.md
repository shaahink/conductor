# Conductor R2 Audit — Report + Prompts + Agent Context

**Session:** #75 (tracker C6 / plan C7)
**Date:** 2026-07-09
**Method:** Every surface traced to BATON-BRIEF.md design authority + source code. Report generated live against `.conductor/plans/conductor-debt.plan.json`. PromptBuilder traced through Orchestrator.cs dry-run path. Personality + lessons + instruction-injection verified via source audit + tests.

---

## Verdict

**PASS** — 19/19 features ✅. 2 ⚠️ findings (1 data quality, 1 doc mismatch). 0 ❌.

---

## Part 1: Report Generation (Reporter.cs — 10 features)

All features traced to `src/Conductor/Core/Reporter.cs` (450 lines). Report generated via `conductor report -p .conductor/plans/conductor-debt.plan.json` and inspected at `.conductor/REPORT.md`.

| # | Feature | Design § | Source | Tests | Verdict | Notes |
|---|---------|----------|--------|-------|---------|-------|
| 1 | Progress bars (Unicode) | B6.3 | `Reporter.cs:394-401` | `ReporterTests.cs` (6) | ✅ | `ProgressBar()` renders `█`/`░` at 10-char width. |
| 2 | Collapsible per-stage sections | B6.3 | `Reporter.cs:77-95` | Indirect | ✅ | `<details><summary>` with per-checkpoint table. Stage depth-level indent (B10.2). |
| 3 | Commit links (GitHub) | B6.3 | `Reporter.cs:405-449` | Indirect | ✅ | `FormatCommitLink()` resolves remote URL, converts `git@` → HTTPS, caches result. |
| 4 | Stage progress table | B6.3 | `Reporter.cs:55-73` | Indirect | ✅ | Depth-indented rows, progress bars, status (active/confirmed/skipped/done/todo). |
| 5 | Sessions table | B6.3 | `Reporter.cs:97-107` | Indirect | ✅ | Last 30 sessions with stage/kind/attempt/duration/outcome/commit-count/gates/cost/tokens. |
| 6 | Timeline section (B5.1) | B5.1 | `Reporter.cs:112-123` | `ReporterTests.cs` | ✅ | Folded from `events.jsonl`. Last 40 transitions. |
| 7 | Health section (B5.3) | B5.3 | `Reporter.cs:128-138` | `ReporterTests.cs` | ✅ | Retry rate, same-failure-loop, gate repetition, oscillation, context saturation. |
| 8 | Confidence section (B5.4) | B5.4 | `Reporter.cs:143-153` | Indirect | ✅ | Evidence count per checkpoint. 70/70 confirmed checkpoints have evidence. |
| 9 | MCP metrics (B5.4) | B5.4 | `Reporter.cs:158-169` | Indirect | ✅ | Tool-call counts + success rate folded from `McpCallFinished` events. |
| 10 | Repo strip (B5.4) | B5.4 | `Reporter.cs:173-183` | Indirect | ✅ | Live git snapshot: branch, working tree, upstream sync. |
| 11 | Commits by session | B6.3 | `Reporter.cs:185-201` | Indirect | ✅ | Last 8 sessions with commit count + clickable SHAs. |
| 12 | Phase handovers | B6.3 | `Reporter.cs:203-216` | Indirect | ✅ | Lists all `.conductor/handovers/*.md` files. |
| 13 | Last gate run | B6.3 | `Reporter.cs:218-235` | Indirect | ✅ | Gate summary + collapsible failure details with output tail. |
| 14 | Last session result | B6.3 | `Reporter.cs:237-244` | Indirect | ✅ | Extracted `SESSION-RESULT:` paragraph from session output. |
| 15 | Tracker handoff block | B6.3 | `Reporter.cs:246-253` | Indirect | ✅ | Fenced code block rendering of tracker's `## Handoff` section. |
| 16 | Heartbeat no-op (F-4) | B6.3, F-4 | `Reporter.cs:258-302` | `ReporterTests.cs` | ✅ | `Normalize()` strips `_Updated` line; timestamp-only rewrite → no commit. `chore(conductor):` messages → disk-only, never commit. |

### Report ⚠️ findings

| # | Finding | Severity |
|---|---------|----------|
| R2-1 | `report` CLI has no `--dry-run` flag. The workflow doc references `conductor report --dry-run` but the command always writes to disk. The command is simple (read state + write file); adding a `--dry-run` (print to stdout) is low-priority. | ⚠️ doc mismatch |
| R2-2 | C5 shown as "SKIPPED ⚠" in stage table. `state.json` still has `"skippedStages":["C5"]` even though C5 is DONE in the tracker (commit `479df5e`). Root cause: running orchestrator (PID 31880) loaded state.json before session #74's fix; the fix was overwritten by the orchestrator's `Save()` cycle. The report correctly reflects state.json — the data is stale, not the renderer. **Fix:** kill orchestrator, fix state.json, restart. | ⚠️ data quality |

---

## Part 2: Prompt Generation (PromptBuilder.cs — 6 features)

All features traced to `src/Conductor/Core/PromptBuilder.cs` (275 lines). Dry-run prompt path traced through `Orchestrator.cs:190-201` → `BuildPrompt()` → `PromptBuilder.Render()`.

| # | Feature | Design § | Source | Tests | Verdict | Notes |
|---|---------|----------|--------|-------|---------|-------|
| 1 | Built-in templates | B0.3, §6 | `PromptBuilder.cs:156-273` | `PromptBuilderTests.cs` (13) | ✅ | 6 kinds: session, fix, resume, audit, advisor, review. `session.md` spells out full pre-/post-/mid- ritual (BATON-BRIEF.md §6). |
| 2 | Template override from disk | B7 | `PromptBuilder.cs:107-128` | `PromptBuilderTests.cs` | ✅ | Plans can override templates via `<planDir>/<templatesDir>/<kind>.md`. |
| 3 | readOrder injection | B1.5 | `PromptBuilder.cs:68-76` | `PromptBuilderTests.cs` | ✅ | Numbered "Required reading (in order):" list from plan's `readOrder`. Current plan has 3 docs. |
| 4 | Persona injection (B7.3) | B7.3, D-7 | `PromptBuilder.cs:79-80,116-121` | `PromptBuilderTests.cs` (5) | ✅ | Persona system prompt prepended before conductor contract rules. Legacy `Persona:` hint in Notes scraped. PersonaDivergence test proves distinct outputs. |
| 5 | Battery collapse note (B10.4) | B10.4 | `PromptBuilder.cs:101-103` | Indirect | ✅ | When `batteryCollapse:true`, adds token-saving instruction to skip build/test. |
| 6 | InstructionQueue injection | B3 | `PromptBuilder.cs:125-126` | Indirect | ✅ | Queued instructions appended after persona + template; consumed via `InstructionQueue.ConsumeAll()`. |

### Context variables verified in template rendering (`Vars()` at `PromptBuilder.cs:67-105`):

| Placeholder | Source | Value in current plan |
|-------------|--------|----------------------|
| `{planName}` | `plan.Name` | `"Conductor-Debt"` |
| `{repo}` | `plan.Repo` | `C:/Code/conductor-baton` |
| `{tracker}` | `plan.Tracker` | `CONDUCTOR-START.md` |
| `{planDoc}` | `plan.PlanDoc` | `docs/workflows/conductor-post-b12-workflow.md` |
| `{stage}` | `stage.Id` | (dynamic per stage) |
| `{stageTitle}` | `stage.Title` | (dynamic per stage) |
| `{stageNotes}` | `stage.Notes` | (dynamic per stage) |
| `{extra}` | `plan.PromptExtra` | Long autonomous-session context (144 chars) |
| `{readOrder}` | `plan.ReadOrder` | 3-item numbered list |
| `{persona}` | `plan.ResolvePersona()` | e.g. "reviewer" |
| `{personaSystemPrompt}` | `PersonaRegistry` | Full persona prompt from file/built-in |
| `{lessons}` | `LessonsManager.ReadRecent(5)` | Auto-rotating lesson entries |
| `{batteryCollapseNote}` | `plan.BatteryCollapse` | Token-saving instruction when true |

---

## Part 3: Agent Context — Batteries (PromptBattery.cs — 3 features)

| # | Feature | Design § | Source | Tests | Verdict | Notes |
|---|---------|----------|--------|-------|---------|-------|
| 1 | Lessons battery (B8.2) | B8.2, B8.5 | `PromptBattery.cs:27-41` | `PromptBuilderTests.cs` (3) | ✅ | `LessonsBattery` reads from `.conductor/lessons.md`, capped at `maxEntries` (3). F-7 fixed. |
| 2 | Recent failure battery (B8.5) | B8.5 | `PromptBattery.cs:47-76` | Indirect | ✅ | `RecentFailureBattery` injects last `GatesRed`/`AgentError`/`NoProgress` session summary. |
| 3 | Lane artifact battery (B12.1) | B12.1 | `PromptBattery.cs:83-138` | Indirect | ✅ | `LaneArtifactBattery` injects analysis-lane output artifacts (`.conductor/lanes/*.md`). |
| 4 | BatteryGroup compose + bound | B8.5 | `PromptBattery.cs:146-183` | `PromptBuilderTests.cs` | ✅ | Groups batteries, caps at `maxBytes` (2048), truncates at paragraph boundary. |

---

## Part 4: Persona Registry (PersonaRegistry.cs — 4 features)

| # | Feature | Design § | Source | Tests | Verdict | Notes |
|---|---------|----------|--------|-------|---------|-------|
| 1 | 9 built-in personas | B7.2, D-7 | `PersonaRegistry.cs:15-26` | `PromptBuilderTests.cs` (3) | ✅ | planner, reviewer, architect, qa, docs, refactor, test-writer, git-cleanup, security-audit. |
| 2 | 9 persona files on disk | B7.2 | `plans/personas/*.md` | Indirect | ✅ | All 9 files present, non-empty, substantive content (4-8 sentences each). |
| 3 | File precedence + fallback | B7.2 | `PersonaRegistry.cs:61-84` | Indirect | ✅ | Disk file wins; falls back to built-in if file missing/empty/IO-error. |
| 4 | Path traversal guard | B7.2 | `PersonaRegistry.cs:49-58` | Indirect | ✅ | Rejects `..`, `/`, `\`, invalid filename chars. Falls back to built-in for malformed names. |

---

## Part 5: followups.md

| # | Feature | Design § | Source | Verdict | Notes |
|---|---------|----------|--------|---------|-------|
| 1 | Structured IDs | B8.4 | `.conductor/followups.md` | ✅ | IDs follow `FU-B{stage}-{n}` convention. |
| 2 | Status tracking | B8.4 | `.conductor/followups.md` | ✅ | OPEN/CLOSED statuses with commit references on CLOSED items. |
| 3 | Per-stage sections | B8.4 | `.conductor/followups.md` | ✅ | Organized by originating stage (B0 through B11). |
| 4 | In-phase fixes | B8.4 | `.conductor/followups.md` | ✅ | Fixed-in-phase items recorded with commit refs + evidence. |
| 5 | Re-homed items | B8.4 | `.conductor/followups.md` | ✅ | Items like FU-B0-1 (MA0045) tracked across re-homes (B0→B2→C2). |

---

## Part 6: Auxiliary Context Systems

| # | Feature | Design § | Source | Verdict | Notes |
|---|---------|----------|--------|---------|-------|
| 1 | LessonsManager | B8.1, B8.2 | `LessonsManager.cs:1-173` | ✅ | Thread-safe `Append()` (Lock), bounded auto-rotation (8192 bytes), `ReadRecent(N)`, atomic write (tmp+rename). |
| 2 | InstructionQueue | B3 | `InstructionQueue.cs:1-124` | ✅ | Chain-linked JSON instruction files in `.conductor/queue/`. Consume = rename to `.done`, not delete. Prompt injection via `PromptSection()`. Not exercised live (no queued instructions in current state). |

---

## Stage Verdict

| Metric | Value |
|--------|-------|
| Report features | **16/16 ✅** |
| Prompt features | **6/6 ✅** |
| Battery features | **4/4 ✅** |
| Persona features | **4/4 ✅** |
| followups.md | **5/5 ✅** |
| Auxiliary context | **2/2 ✅** |
| **Total features** | **37/37 ✅** |
| ❌ BROKEN | **0** |
| ⚠️ WORKS-WITH-FINDINGS | **2** (1 doc mismatch, 1 data quality) |
| Build | 0w/0e |
| Tests | **497 pass** |

**R2 PASS.** All features match design. No broken surfaces. Two ⚠️ findings: (1) `report` CLI lacks `--dry-run` flag referenced in workflow doc — cosmetic; (2) state.json has stale `skippedStages:["C5"]` causing report to show C5 as SKIPPED despite tracker DONE — root cause is orchestrator overwrite of session #74's fix, needs orchestrator restart.

## Trace to BATON-BRIEF.md

| BATON-BRIEF § | Feature | Audit result |
|---------------|---------|--------------|
| §6 (Session protocol) | Prompt embeds full ritual (pre-read → QA → deliver → post-gates → handoff → commit → push) | ✅ |
| §6 (Evidence rule) | "Evidence or it didn't happen" baked into built-in session.md | ✅ |
| §6 (HUMAN: block) | Blocked-on-human instruction in all built-in templates | ✅ |
| §6 (SESSION-RESULT:) | End-of-session paragraph instruction in all templates | ✅ |
| B6.3, F-4 | Clean heartbeat, progress bars, collapsible sections, commit links in report | ✅ |
| B7.2, D-7 | Persona registry with 9 built-in + file-override | ✅ |
| B7.3 | Persona system prompt merged into session prompt | ✅ |
| B8.2, F-7 | Lessons injected into next session's prompt | ✅ |
| B8.4 | Handover gaps → tracked followups (followups.md) | ✅ |
| B8.5 | Pluggable IPromptBattery; Lessons, RecentFailure, LaneArtifact batteries | ✅ |
| B10.4 | Battery-collapse note in prompt (skip redundant build/test) | ✅ |
| B12.1 | Lane artifact battery injects analysis output into next prompt | ✅ |

---

## Evidence Artifacts

- `docs/qa-reports/CONDUCTOR-AUDIT-R2.md` — this report
- `.conductor/REPORT.md` — live generated report (inspected)
- Build: 0 warnings, 0 errors
- Tests: 497 passed, 0 failed, 0 skipped
