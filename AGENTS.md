# Conductor — session handoff

## What this is
Conductor is an autonomous multi-session engineering orchestrator (C# / .NET 9 + Spectre.Console).
It spawns headless opencode/deepseek-v4-pro agent sessions, verifies work independently via a gate
battery (build/tests/pnpm/mcp-qa/loom-guards), handles resumability across crashes, and reports to
GitHub (.conductor/REPORT.md). The live TUI dashboard shows thinking, cost/tokens, activity, and
stage progress.

## Current state (2026-07-07 22:15 UTC)
- **Active branch:** `master` (merged from `feat/dashboard-v2` at `ef68eae`)
- **Worktree:** `C:\Code\conductor` (primary); `C:\Code\conductor-dev` (feat/dashboard-v2, legacy)
- **GitHub:** `https://github.com/shaahink/conductor` — `master` is current; `feat/dashboard-v2`
  is the pre-merge branch. Pull with `git clone` or `git fetch origin`.
- **v2 fully merged** — MVVM refactor, header-stacking fix, thinking dedup, cost separation, activity
  indicator, state-machine action bar, checkpoint titles, token totals, offline `preview` command,
  gate rework (one battery/phase, HEAD-sha cache, stage filter, live timers), scrollable pop-out
  modals (T/O/D/V/X), live instruction injection (I key + inject command), status agent (G), AFK
  reporting heartbeat with live-activity section, no-op duplicate-commit elimination, light visual
  polish (accent headers, rounded borders).
- **dry-run pause fix** (`ef68eae`): `--dry-run` now skips the paused/backoff idle loops so it
  reaches the prompt output.
- **Tests:** 56 pass, 0 warnings, 0 errors.
- **Published binary:** `C:\Code\conductor\bin\conductor.exe` (Release, built 2026-07-07 22:13)

## Live plan (DevContext2-ui Loom)
- **Repo:** `C:\code\DevContext2-ui`
- **Branch:** `feat/loom-l2`
- **Status:** **Paused** at stage L2 (BodyFacts + seam detectors)
- **Sessions:** 7 run (L0 confirmed+audited, L1 confirmed+audited)
- **L2 state:** Session #7 (opencode/deepseek-v4-pro) was interrupted. `pendingResume` queued with
  1 resume remaining. Working tree has partial BodyFacts work.
- **State file:** `C:\Code\DevContext2-ui\.conductor\state.json`

## How to resume the live run
```
.\bin\conductor.exe run -p plans\loom.opencode.plan.json
```
Will resume session #7 (opencode `--continue`), then continue L2→L8 with perPhase gates, audit,
setup/teardown hooks. Use `--dry-run` first to verify what will happen.

## Gotchas for future sessions

### 1. `claudeSessionId` is a legacy field name
`RunState.SessionRecord.ClaudeSessionId` and `PendingResume.ClaudeSessionId` are named after the
original claude agent, but they store **any** agent's session ID (claude, opencode, etc.). The
agent type is NOT persisted in state — check the plan file's `agent.command` or the session's
`ResultSummary` prose patterns to determine which agent was used. Don't assume claude from the
field name.

### 2. `glob` tool may miss hidden directories on Windows
The glob `**/.conductor/**` pattern failed to find `.conductor/` even though it existed. Use
explicit paths (`C:\Code\DevContext2-ui\.conductor\`) or `Test-Path -LiteralPath` instead. The
`**/` prefix combined with a leading-dot directory name is unreliable in the glob tool.

### 3. `--dry-run` against paused state was broken (fixed in ef68eae)
Before `ef68eae`, the pause/backoff idle loops at `Orchestrator.cs:49-62` ran BEFORE the dry-run
check at line 143, so `--dry-run` against a paused state just idled forever. The fix gates both
loops with `!opts.DryRun`. If you refactor the main loop, keep the dry-run guard at the top.

### 4. Always check all branches before assuming working tree = source of truth
When multiple worktrees exist (`C:\Code\conductor` on master, `C:\Code\conductor-dev` on
feat/dashboard-v2), the working tree may only have a subset of changes. A squash commit from the
dirty tree missed 24 files (DocsExtractor, StatusAgent, InstructionQueue, DashboardRenderer, etc.)
that only existed on the `feat/dashboard-v2` branch. Check `git branch -a` and `git log --all`
before committing.

### 5. The two plan files are NOT interchangeable
- `loom.plan.json` — claude agent, no audit, no setup/teardown, all-gates-every-session
- `loom.opencode.plan.json` — opencode agent, audit enabled, setup+teardown, perPhase gates
Switching plan files mid-run works only if the state is compatible (StateCompatTests verified),
but the queued `pendingResume` must match the new plan's agent type.

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
