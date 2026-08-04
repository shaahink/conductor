# Karvan lanes - the caravan splits | Phase Tracker

**Plan:** Karvan lanes - the caravan splits | **Branch:** `feat/karvan-lanes` | **Design doc:** docs/history/CONDUCTOR-KARVAN.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: nothing — **this plan is authored, not launch-ready.** Two prerequisites, both in the spec:
  Karvan core must be complete (K3.1 above all — a lane worktree contains no state dir at all, so a
  lane cannot hold state until the catalogue lives outside the tree), and K7.1's re-measure must have
  corrected the limits block.
next: **L1.1** — clean-tree and branch discipline become enforceable rather than advisory. The
  branch-pattern warning in the run loop is today the only enforcement, and there is no
  requireCleanTree anywhere in the tree. Strict by default for new plans, lenient available for
  existing ones.
notes: read only your stage's section of the spec. Every concurrency knob this plan adds defaults to
  the current behaviour — a plan that has to opt in cannot regress anybody.
red: nothing yet.

## Baseline numbers

| Metric | Value |
|---|---|
| Total checkpoints | 23 |
| Done | 0 |
| Claimed (unconfirmed) | 0 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path
produced by a run this phase (a code path is not evidence). Agent claims are marked DONE; the engine
confirms as DONE ✓ after the full battery.

### L1 — Git safety is code, not prose

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| L1.1 | Clean-tree and branch discipline are enforceable: a post-session clean-tree assertion, a branch-pattern violation that can refuse rather than only warn, and a refusal on a detached HEAD or an unexpected remote — strict by default for new plans, lenient available for existing ones | TODO | - | - |
| L1.2 | A run cannot force-push, a push is asserted rather than assumed, and a checkpoint commit that does not carry the repo's trailer convention is reported instead of silently accepted | TODO | - | - |
| L1.3 | An unmerged scratch branch is never force-deleted and is kept with its name in the event and the log; worktrees are removed by deleting the directory then pruning, counted against the concurrency limit, listed and reaped by a verb, and swept for orphans at startup | TODO | - | - |

### L2 — Repos are first class

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| L2.1 | A plan declares repos with an id, path, role, branch pattern and gates, the old satellite key still works as an alias so every existing plan keeps running, and doctor reports each repo and whether it resolved | TODO | - | - |
| L2.2 | Per-repo branch and clean-tree assertions and per-repo gates run where the work is, not only where the plan is rooted | TODO | - | - |
| L2.3 | Commit and push are coordinated across repos, the verdict attributes work to the repo it landed in per session and per checkpoint, and the anchor-commit rule is an engine-side expectation rather than a paragraph in a template | TODO | - | - |
| L2.4 | The Face shows every repo in the run with its branch, dirty state, ahead-behind and the commits this session landed there | TODO | - | - |

### L3 — A lane is a real session

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| L3.1 | A lane resolves state through a redirect to the run's canonical store, one writer owns it, the store sets a busy timeout to go with its write-ahead logging, and contention is proven rather than assumed | TODO | - | - |
| L3.2 | A lane runs a real session — cost rows, token budget and soft break, rollover handling, resume rail, stall watchdog, verdict pass, tracker claim, MCP task tools and a transcript — instead of a bare process whose success is inferred from whether a merge happened | TODO | - | - |
| L3.3 | Each lane gets its own environment — lane index, base port plus offset, state dir, scratch database name — and setup and teardown commands that run inside the worktree, with the assignment logged and carried in the lane's event | TODO | - | - |
| L3.4 | Conflicts are detected with a three-way merge that touches no working tree, before dispatch and again before queueing a merge, with declared path claims kept as the cheap fast path | TODO | - | - |

### L4 — The scheduler and the merge queue

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| L4.1 | A DAG scheduler runs independent ready stages concurrently behind a limit that defaults to one, honouring declared dependencies, so no existing plan changes behaviour | TODO | - | - |
| L4.2 | The merge queue serializes, rebases onto the current base, re-runs the merge gate on the rebased tree and then integrates, with batch-then-bisect named as the stretch and serialize-and-rebase correct on day one | TODO | - | - |
| L4.3 | Budget and tokens are accounted per lane and capped globally, so the cost cap, the token cap and the approval flow still mean what they say with three sessions spending at once, and every cost row names its lane | TODO | - | - |
| L4.4 | The rehearsal: a scratch two-repo plan runs end to end with two lanes, both land, the verdict attributes each correctly, the ledger is one ledger, and neither lane's ports, database or build output touched the other's | TODO | - | - |

### L5 — The Face renders a fleet

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| L5.1 | N live sessions render at once with the focused lane selectable | TODO | - | - |
| L5.2 | Per-lane cost, tokens, headroom and gate state render while the run-wide totals stay correct | TODO | - | - |
| L5.3 | The kanban and the history surface are lane-aware — which lane owns a card, which lane a commit came from | TODO | - | - |

### L6 — Autonomy - the engine needs the human less

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| L6.1 | The supervisor runs in-process on the configured provider with its own cost line, its briefs in the store and a Face surface, and the external hook still works for people who want their own | TODO | - | - |
| L6.2 | The advisor reads the folded event projection instead of a freshly composed context, and records its real token and cost rows instead of a flat per-second estimate | TODO | - | - |
| L6.3 | One-way GitHub push, off by default: a claim closes or comments on a mapped issue and the run report can post as a PR comment, with nothing inbound — an honest skip with a reason is an acceptable completion | TODO | - | - |

### L7 — Ship the lanes

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| L7.1 | The owner's own two-repo job runs to completion as the acceptance, with the run record as the evidence — per-repo commits, per-lane cost, one ledger, no cross-talk — and the docs and an ADR carry the concurrency model, the merge policy and the limits | TODO | - | - |
| L7.2 | feat/karvan-lanes is merged to master by the owner, tagged, released and installed | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
