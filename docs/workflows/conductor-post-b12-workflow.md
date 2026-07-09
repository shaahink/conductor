# Conductor — Post-B12 Cleanup + Audit Workflow

## Phase State
- B12.3 in progress (session #67) — Tier B worktree lanes + merge gate
- B12.4 TODO — fix-lanes consume followups.md
- B0-B11 all DONE, 67 sessions, ~$2.50 est cost
- 25+ OPEN followups across followups.md + conductor-DEBT.md + handover weaknesses

## Design Principle
Every session is fully autonomous. The agent codes, tests, audits, and writes reports.
The final handover surfaces a "Needs Human Verification" checklist of items the agent
couldn't test alone (Telegram with real token, visual TUI confirmation, etc.).
This plan is designed to run overnight — no human blocking points.

## Universal Pre-Session Ritual (≤3 min)

1. Read this workflow doc (first session only).
2. Read `CONDUCTOR-START.md` handoff block + your stage section.
3. Run **selective** gate:
   - Engine change → `dotnet build Conductor.slnx` + `dotnet test Conductor.slnx --no-build`
   - Review/audit (no code) → skip gates
   - **Never build on red.**

## Universal Post-Session Ritual (≤10 min)

1. Run gate again — confirm nothing regressed.
2. Produce evidence artifact under `docs/baton/evidence/<session>/`.
3. Overwrite CONDUCTOR-START.md handoff block (≤12 lines).
4. Update checkpoint status.
5. Commit (`fix(debt): <item>` or `audit: R<N>`). Push.

## Discipline Invariants

- `dotnet build Conductor.slnx` — 0 errors, 0 warnings (warnings=errors)
- `dotnet test Conductor.slnx --no-build` — all pass
- Evidence files exist before claiming DONE
- One commit per session. Never batch.

---

## Session Plan (8 sessions)

### Phase 1: Complete B12 (sessions 1-2)

| # | Item | Sub-tasks | Effort |
|---|------|-----------|--------|
| 1 | **B12.3** — Tier B isolated-worktree lanes + merge gate | 1a. LaneWorker worktree create/teardown. 1b. MergeGate: full battery on integrated tree. 1c. Reject-and-report on red/conflict. 1d. Test: fake-agent good/bad diff. | ~60 min |
| 2 | **B12.4** — Fix-lanes from followups.md | 2a. FollowupParser: read followups.md → structured entries. 2b. FixLaneDispatcher: OPEN entry → worktree lane. 2c. Merge gate acceptance. 2d. Test: followup → lane → merge → closed. | ~40 min |

### Phase 2: Debt Fix Lane (sessions 3-5)

| # | Item | Sub-tasks | Effort |
|---|------|-----------|--------|
| 3 | **Async engine + integration harness** | 3a. MA0045 ratchet to error, fix ~28 sync-over-async sites. 3b. MA0002 ratchet to error, fix ~38 StringComparer sites. 3c. CancellationToken through IProgressProvider.Read. 3d. ScriptProvider stdout/stderr split. 3e. OrchestratorHarness (fake agent + temp git repo). 3f. Process-control loop test (goto/rollback/retry-stage). | ~90 min |
| 4 | **Events + metrics + budget + recovery** | 4a. Wire LiveMetrics to dashboard (not agent.Tokens*). 4b. Rollback ConductorEvent. 4c. Mid-session control feedback. 4d. McpCallFinished events (from B5 trap). 4e. Persist cumulative budget baseline. 4f. Orphaned SessionStarted test + fix. 4g. Empty catch{} in AgentSession. 4h. Graceful Ctrl+C test. | ~90 min |
| 5 | **Small debt sweep** (10+ items) | 5a. fake-agent gatesred rename. 5b. --once smoke cleanup. 5c. CA1031 review. 5d. Completion exhaustiveness test. 5e. Alt-screen restore test. 5f. Status-agent CT. 5g. HookConfig validation. 5h. ComputeDepth memoize. 5i. Persona prompt divergence test. 5j. Mock Telegram test. 5k. LessonsManager thread-safety. 5l. Battery-collapse measurement. | ~90 min |

### Phase 3: Agent-Led Audit + Final Handover (sessions 6-8)

| # | Item | Sub-tasks | Effort |
|---|------|-----------|--------|
| 6 | **R1 — TUI + CLI audit** | 6a. Run `dotnet run -- --dry-run` — inspect alt-screen, header, plan tree, action bar, footer log, modals, token line. 6b. Run each CLI command with `--help` and `--dry-run`: run, replay, report, new-plan, approve, heartbeat, plan. 6c. For each feature: trace to BATON-BRIEF.md § + source code. 6d. Rate ✅/⚠️/❌. 6e. Fix any ❌ findings immediately. | ~60 min |
| 7 | **R2 — Report + Prompts + Agent Context audit** | 7a. Run `conductor report --dry-run` — inspect progress bars, collapsible sections, commit links. 7b. Read generated agent prompt from `--dry-run` — check readOrder, batteries, persona injection, lessons. 7c. Check followups.md format. 7d. Check persona registry files. 7e. Rate ✅/⚠️/❌. 7f. Fix any ❌ findings immediately. | ~45 min |
| 8 | **Final handover** | 8a. Write `docs/qa-reports/CONDUCTOR-FINAL.md`: full feature table with rates, bugs fixed, remaining findings. 8b. Generate **Needs Human Verification checklist**: items agent couldn't test (Telegram with real token, visual TUI, credential-gated tests). 8c. Update TRACKER.md final handoff. 8d. Commit. | ~30 min |

---

## Rating Taxonomy

| Rating | Meaning |
|--------|---------|
| ✅ WORKS | Feature matches design doc. No gaps. |
| ⚠️ WORKS-WITH-FINDINGS | Minor gaps or cosmetic issues. |
| ❌ BROKEN | Feature does not work as designed. |

Stage verdict: all ✅/⚠️ → PASS | 1 ❌ → PASS-WITH-FINDINGS | ≥2 ❌ → FAIL

## Audit Report Format

For each feature in sessions 6-7, the agent writes:

```markdown
| Feature | Design § | Source | Tests | Agent verdict |
|---------|----------|--------|-------|---------------|
| <name> | B<N>.<section> | `File.cs:line` | ✅/⚠️/❌ coverage | ✅/⚠️/❌ |
```

For each finding: `file:line — description. Estimate: N min.`

## Final Handover Format

```markdown
## Phase Verdicts
| Phase | Verdict | |
|-------|---------|-|
| B12 completion | ✅ PASS | All checkpoints DONE |
| Debt fix lane | ✅ PASS | N items resolved, N carried |
| Audit | ✅/⚠️/❌ | N features checked, N fixed |

## Needs Human Verification
- [ ] Item 1 — what to do, what file to look at
- [ ] Item 2 — ...
```

## Quick Commands

```powershell
# Build + test
dotnet build Conductor.slnx
dotnet test Conductor.slnx --no-build

# Dry-run the plan
C:\Code\conductor\bin\conductor.exe run --dry-run -p .conductor\plans\conductor-debt.plan.json

# Preview TUI directly (for audit sessions 6-7)
dotnet run --project src\Conductor -- --dry-run -p .\plans\conductor.self.plan.json
```
