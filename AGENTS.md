# Conductor — session handoff

## What this is
Conductor is an autonomous multi-session engineering orchestrator (C# / .NET 9 + Spectre.Console).
It spawns headless opencode/deepseek-v4-pro agent sessions, verifies work independently via a gate
battery (build/tests/pnpm/mcp-qa/loom-guards), handles resumability across crashes, and reports to
GitHub (.conductor/REPORT.md). The live TUI dashboard shows thinking, cost/tokens, activity, and
stage progress.

## Current state (2026-07-07)
- **Active branch:** `feat/dashboard-v2` in worktree `C:\Code\conductor-dev`
- **Master:** original baseline (pre-v2)
- **GitHub:** `https://github.com/shaahink/conductor` (private) — default branch `feat/dashboard-v2`
  (has all v2 work); `master` = pre-v2 baseline. Pull with `git clone` or `git fetch origin`.
- **v2 committed** — MVVM refactor, header-stacking fix, thinking dedup, cost separation, activity
  indicator, state-machine action bar, checkpoint titles, token totals, offline `preview` command,
  gate rework (one battery/phase, HEAD-sha cache, stage filter, live timers), scrollable pop-out
  modals (T/O/D/V/X), live instruction injection (I key + inject command), status agent (G), AFK
  reporting heartbeat with live-activity section, no-op duplicate-commit elimination, light visual
  polish (accent headers, rounded borders).
- **Tests:** 56 pass, 0 warnings, 0 errors. E2E smoke test verified end-to-end (dry-run → once →
  completion). Published binary: `C:\Code\conductor-dev\bin\release\conductor.exe`
- **Not yet merged to master or pushed to GitHub.**

## Live plan (DevContext2-ui Loom)
The live run is on `feat/loom-l1` at `C:\code\DevContext2-ui`. It **advanced to L1, completed L1**
(5 checkpoints DONE, audit done, handover written). The running binary is the pre-v2 one from
`C:\Code\conductor\bin\conductor.exe`. **The user needs to gracefully pause it and swap to the v2
binary.**

## Next steps for the user
1. In the running conductor TUI: press `P` (pause after session), or `Q` (quit after session).
2. Let the current session finish, conductor will pause/quit cleanly.
3. Replace the binary:
   ```
   Copy-Item C:\Code\conductor-dev\bin\release\conductor.exe C:\Code\conductor\bin\conductor.exe -Force
   ```
4. Resume: `.\bin\conductor.exe run -p plans\loom.opencode.plan.json`
   The new binary will load the existing `state.json` (compatible, tested via StateCompatTests) and
   resume from wherever it left off.
5. Verify the dashboard: `.\bin\conductor.exe preview -p plans\loom.opencode.plan.json`

## NEXT-FEATURES.md (prioritised backlog)
1. **Observability:** Serilog structured logging + no silent failures + diagnostic console + graceful
   Ctrl+C final heartbeat.
2. **Token efficiency:** collapse double gate battery → one source of truth; terser prompts;
   optional planning turn for complex checkpoints.
3. **Ritual enforcement:** branch-per-stage, clean-tree, push assertions (currently agent is told;
   should be enforced).
4. **Handover-gap → follow-up:** parse handover weak/deferred items into tracked follow-ups so they
   don't silently persist.
5. **Context batteries:** pluggable prompt sections (repo-map, lessons brief, DoD recap) — bounded,
   resume-friendly, opt-in per plan.
6. **Zero-config bootstrap** + **pause→redeploy→resume** doctor command.
7. **Child-process visibility** lane in the dashboard (agent tool tree + gate ops).

## Research (RESEARCH.md)
Surveyed aider (ask/architect modes, repo-map, git-first), OpenHands (headless JSONL,
always-approve), Claude Code / opencode (tool-tree UX, permission modes). Key adopted patterns:
structured event output, status-agent as read-only-by-construction, state-machine UI, progressive
disclosure (live summary + pop-out detail). Recommendations for v-next in RESEARCH.md.

## Remaining to ship (this session)
- Merge `feat/dashboard-v2` → master.
- Push to GitHub (`https://github.com/shaahink/Conductor`).
- Optionally: rebuild main-tree binary so it's ready for the next `conductor run`.
