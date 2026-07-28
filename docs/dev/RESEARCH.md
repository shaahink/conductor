# Conductor — orchestration research & findings

Survey of comparable tools and terminal-UX patterns, with concrete recommendations for the next
version. Sources reviewed: **aider** (pair-programming, ask/architect modes, repo-map, git
integration), **OpenHands** (headless JSONL, always-approve automation), **Claude Code** &
**opencode** (agent tool-tree UX, permission modes, session/plan modes), **Spectre.Console** (TUI).

## What others do that we should blend in

### 1. Planning / architect separation (aider)
aider's **ask → architect → code** flow: discuss/plan first, then a (possibly cheaper) editor model
applies edits. Two requests cost more but improve hard changes.
→ **For us:** optional planning step per checkpoint (backlog: token/pipeline efficiency). Conductor
could run a cheap "plan" turn (deepseek reasoning) that writes a short plan into the prompt for the
delivery turn — only for checkpoints flagged complex. Keeps simple checkpoints cheap.

### 2. Headless structured output (OpenHands)
OpenHands streams **JSONL events** (`action`/`observation`) in `--headless --json`, always-approve.
→ **For us:** we already parse opencode `--format json` (text/reasoning/tool/cost/tokens). Confirmed
this is the right design. Next: normalise our event model to a small typed schema and log it
structured (Serilog backlog) so the dashboard, report, and future integrations share one source.

### 3. Repo map / context batteries (aider)
aider builds a **repo-map** (tree-sitter ranked symbols) to give the model cheap whole-repo context.
→ **For us:** a "repo-map / hot-files" prompt battery (backlog: learning pipeline). Cheap, bounded,
resume-friendly. Feed the phase's most-touched files into the next session.

### 4. Git-native discipline (aider)
aider auto-commits each change with sensible messages and is git-first.
→ **For us:** we already verify via git independently. Add enforced git hygiene (backlog: rituals) —
branch-per-stage, clean-tree assertion, push assertion — rather than trusting the agent.

### 5. Permission / mode surface (Claude Code, opencode)
Both expose explicit modes (plan vs act, auto-approve) and a tool-call tree in the UI.
→ **For us:** the status-agent already runs read-only-by-construction. Add a visible "processes"
lane (backlog: child-process visibility) showing agent tool calls + conductor gates/hooks as a tree.

## Terminal UX / readability (Claude Code, opencode, Spectre)
- **Consistent accent + semantic colors**: one accent (aqua), green=pass/done, yellow=running/active,
  red=fail/blocked, grey=idle/muted, orange=backoff/warn. (Applied in DashboardRenderer.)
- **Live spinner + elapsed** so long operations never look hung. (Applied: activity line + gate timers.)
- **Truncate, never wrap** in fixed regions; height-aware layout. (Applied: fixed the stacking bug.)
- **Pop-out pagers** for dense content instead of cramming panels. (Applied: T/O/D/V/X modals.)
- **Progressive disclosure**: summary in the live view, detail on demand. (Applied.)

## Prioritised recommendations for v-next
1. **Serilog structured logging + no silent failures** (observability backlog) — foundational.
2. **Collapse double gate battery** (agent ritual + conductor) → one source of truth (token/pipeline).
3. **Optional planning turn** for complex checkpoints (aider architect pattern).
4. **Enforced git/ritual batteries** (branch/commit/push/clean-tree).
5. **Handover-gap → follow-up tracking** so documented weaknesses get fixed, not forgotten.
6. **Context batteries** (repo-map, lessons brief, DoD recap) as pluggable, bounded prompt sections.
7. **Zero-config bootstrap** + **pause→redeploy→resume** doctor command.

See `NEXT-FEATURES.md` for the full backlog these map onto.
