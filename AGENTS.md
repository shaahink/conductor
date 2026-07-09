# Conductor (Baton worktree) — session handoff

## What this is
Conductor is an autonomous multi-session engineering orchestrator (C# / .NET, Spectre.Console). It
spawns headless agent sessions, verifies work independently (gate battery + git commits + tracker
diff), is fully resumable, and reports to `.conductor/REPORT.md`. This worktree (`feat/baton`) hosts
**Baton — Conductor v2**: Conductor improving itself.

## This worktree
- **Path:** `C:\Code\conductor-baton`  **Branch:** `feat/baton`
- **Do NOT touch:** `C:\Code\conductor` (master, the stable DRIVER) or the live `C:\Code\DevContext2-ui`
  Loom run (separate repo + lock).
- **Driver:** the STABLE `C:\Code\conductor\bin\conductor.exe` (built from master). The tool improving
  Conductor is never the tool under edit.

## Read order for this phase
1. `CONDUCTOR-START.md` (tracker — `## Handoff` block + C1-C8 checkpoints)
2. `docs/workflows/conductor-post-b12-workflow.md` (the workflow for this phase)
3. `conductor-DEBT.md` (debt catalog, grouped by C-session)
4. `docs/baton/BATON-BRIEF.md` (design authority — MANDATORY)
5. `.conductor/handovers/B<n>.md` (previous stage handover)
6. `.conductor/plans/conductor-debt.plan.json` (your stage in the plan)

## Deliverables authored on this branch (plan, not yet executed)
- `docs/baton/BATON-BRIEF.md` + `docs/baton/stages/B0.md`…`B12.md`
- `docs/baton/tooling/` (B0 drafts: editorconfig, Directory.Build.props, Directory.Packages.props,
  Meziantou ruleset rationale) + `docs/baton/adr/` (created in B0.6)
- `CONDUCTOR-START.md` (tracker — verified parses: 65 checkpoints, 13 stages)
- `plans/conductor.self.plan.json` + `plans/baton-templates/` (session/fix/resume/audit/advisor,
  tuned: value-only gates, audit-fixes-leftovers, fix-session leftover sweep)
- `examples/README.md` + `examples/shamshir/parity-pipeline.TRACKER.md` (drivability proof)

## How to run (with the STABLE driver)
```powershell
C:\Code\conductor\bin\conductor.exe run --dry-run -p .conductor\plans\conductor-debt.plan.json
C:\Code\conductor\bin\conductor.exe run         -p .conductor\plans\conductor-debt.plan.json
```

## QA protocol (added 2026-07-09)
- Skip previous-session QA when last session ended `advanced` or `progress` with all gates green.
- Run QA only when last session was `gatesRed`, `stalled`, `noProgress`, or `interrupted`.
- **Tracker rule:** always update BOTH handoff block AND checkpoint row (DONE + commit + evidence). If row stays TODO, conductor re-launches the same stage.

## Current state (2026-07-09)
- **B0-B12** all DONE (66 checkpoints). **C1-C3** all DONE (sessions #68-#70).
- **Phase C — Cleanup + Audit (8 sessions).** C4 (small debt sweep, 12 items) is next.
- 493 tests pass. Plan `.conductor/plans/conductor-debt.plan.json` in progress.

## Gotchas
- **`claudeSessionId`** is a legacy field name storing ANY agent's session id (B2 renames/abstracts).
- Templates for the self-plan live in `plans/baton-templates/` (NOT `plans/templates/`, which are the
  Loom templates B1 relocates to `examples/loom/`).
- `Conductor.slnx` already exists on master — B0 verifies, doesn't recreate.
- Stage-id convention: the current `TrackerParser` regex does NOT match `P-0` (hyphen) — proven
  against `examples/shamshir/parity-pipeline.TRACKER.md` (16/17 rows). B1.4 makes it configurable.
- Value-only gates/tests (BRIEF §5.1): don't add ceremony; audit fixes leftovers; followups feed the
  next phase / B12 fix-lanes.
