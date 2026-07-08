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

## Read order for a Baton session
1. `CONDUCTOR-START.md` (the tracker Conductor parses — `## Handoff` block + B0.1…B12.4 checkpoints)
2. `docs/baton/BATON-BRIEF.md` (design authority — vision, architecture, event schema, task graph,
   parallelism tiers, .NET standards, delivery/gating philosophy §5.1, anti-patterns §7)
3. `docs/baton/stages/B<n>.md` (your stage's requirements, tasks, gates)
4. `.conductor/handovers/B<n-1>.md` (previous phase audit handover, once it exists)

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
C:\Code\conductor\bin\conductor.exe run --dry-run -p C:\Code\conductor-baton\plans\conductor.self.plan.json
C:\Code\conductor\bin\conductor.exe run --once   -p C:\Code\conductor-baton\plans\conductor.self.plan.json
C:\Code\conductor\bin\conductor.exe run          -p C:\Code\conductor-baton\plans\conductor.self.plan.json
```

## Current state (2026-07-08)
- Plan authored; **nothing executed**. `dotnet build Conductor.slnx` 0w/0e, 56 tests pass on net9.0
  (B0 migrates to net10 + analyzers).
- First real work: **B0.1** (net10 + Directory.Build.props/Packages.props + verify slnx).

## Gotchas
- **`claudeSessionId`** is a legacy field name storing ANY agent's session id (B2 renames/abstracts).
- Templates for the self-plan live in `plans/baton-templates/` (NOT `plans/templates/`, which are the
  Loom templates B1 relocates to `examples/loom/`).
- `Conductor.slnx` already exists on master — B0 verifies, doesn't recreate.
- Stage-id convention: the current `TrackerParser` regex does NOT match `P-0` (hyphen) — proven
  against `examples/shamshir/parity-pipeline.TRACKER.md` (16/17 rows). B1.4 makes it configurable.
- Value-only gates/tests (BRIEF §5.1): don't add ceremony; audit fixes leftovers; followups feed the
  next phase / B12 fix-lanes.
