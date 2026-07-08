# Git Heartbeat Cleanup — Post-Phase Instructions

**Current heartbeat count per `git log --all --oneline --grep="chore(conductor):"`:** 86 of 159 commits
**HeartbeatMinutes in plan JSON:** 0 (disabled for future sessions as of 2026-07-08)

## When to run

After the CURRENT phase (B4) confirms green + audit passes. The running session should be finished.
Wait for the TUI to exit or show the session completed before proceeding.

## Cleanup commands

```powershell
# Identify the B4 phase start commit
$phaseStart = (git log --oneline --grep="stage .* B4" | Select-Object -Last 1).Split(" ")[0]

# Interactive rebase
git rebase -i $phaseStart^ --committer-date-is-author-date
# Mark chore(conductor): lines as squash (s), keep feat(bb4.x):/fix(bb4.x):/docs(bb4.x): as pick

# Push the squashed history
git push --force-with-lease origin feat/baton
```

## Note for the Baton self-run

The conductor driving this repo is the STABLE driver from master. It commits heartbeats via
`Reporter.WriteAndPublish`. After the heartbeat was disabled in the plan JSON (heartbeatMinutes: 0),
the next `conductor run` will NOT produce heartbeats. The B6.3 stage is designed to make heartbeats
amend previous commits instead of creating new ones — that will be the permanent fix.

## For B6.3 (clean heartbeat stage)

See `docs/baton/stages/B6.md` for the design and `conductor-DEBT.md` for the observed context.
The stage should produce:
- Heartbeat updates that amend the previous heartbeat commit (not new commits)
- OR heartbeat commits pushed to a dedicated `refs/reports/<branch>` ref
- PeriodicTimer migration per BATON-BRIEF.md §250
