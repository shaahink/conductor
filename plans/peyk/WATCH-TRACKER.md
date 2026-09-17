# Peyk watch - the watch stops being a person Phase Tracker

**Plan:** Peyk watch - the watch stops being a person | **Branch:** `feat/peyk-watch` | **Design doc:** docs/dev/NEXT-ERA-FINDINGS-2026-09-17.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: nothing yet - authored 2026-09-17 from the findings doc, not launched. Launches after the
  courier plan tags and the owner reinstalls the engine from it; the engine driving this run is that
  build, and it still has every behaviour this plan fixes (trap 21).
next: PW1.1 - the ledger. Read Appendix A of the design doc in full (the only checkpoint that does),
  triage every entry into the bug ledger with one disposition each, checked against the code, and
  write the count of open-sweep rows here for PW7.2.

## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 16 |
| Done | 0 |
| Claimed (unconfirmed) | 0 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### PW1 — The ledger first

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PW1.1 | Every entry of Appendix A and every engine-tagged field-log entry triaged into the bug ledger with one disposition each (fixed / open this plan / open sweep / not a bug), checked against the code; the table in this tracker; the open-sweep count in the handoff | TODO | - | - |

### PW2 — The prompt has no ceiling

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PW2.1 | Measured first: a 60 K prompt on stdin to the installed Claude CLI, byte-identical, CLI version recorded; then agent.promptVia (stdin default for claude); preflight and doctor estimates include injections, amendments and the gate block and match the spawn; the card's amendment context capped | TODO | - | - |
| PW2.2 | inject --file / - / --list / --cancel / --for / --stage; whole text stored with both lengths printed; consumed at spawn; re-queued marked redelivered after a session with no verdict; a quoted three-paragraph 5 K injection round-trips and a --for deliver steer survives a fix session | TODO | - | - |
| PW2.3 | The six generic traps in the tool contract and plan new; the Conductor tools block measured and trimmed; the three field plans' promptExtra shortened by those rules with the diff in the evidence; a test finds every rule present with and without them in promptExtra | TODO | - | - |

### PW3 — Decisions are banked

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PW3.1 | conductor decision ask / list / answer and the MCP tool; DecisionAsked / DecisionAnswered events; .conductor/decisions.md; the unanswered list in the next stage's prompt; a rig session asks two decisions and keeps going | TODO | - | - |
| PW3.2 | The owner-queue kind with age; the two-button push through say and the courier's callback path; the answer injected at the next boundary; --as delegate recorded; one real button press in the admin DM answers a rig's decision | TODO | - | - |

### PW4 — Nothing the agent did not do burns an attempt

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PW4.1 | The BackendUnavailable outcome: $0.00 / no text / short life, a rejected rate_limit_event, a 5xx tail, a reboot - backs off, burns no attempt, no breaker count, no advisor; the Takhteh session-051 envelope replayed on a rig proves it; the log line names the shape | TODO | - | - |
| PW4.2 | GatesRed cannot be overtaken by a full board; maxProgressWithoutClaim parks churn via the advisor; status prints delivered and confirmed; TimedOut (bg alive) costs a resume; rollover commit counts from git; verdict.dirtyIgnore; the BLOCKED tell in status and the owner queue | TODO | - | - |

### PW5 — The record tells the truth

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PW5.1 | Every failed gate attempt's output written when it fails with a truthful header; head-and-tail capture under batteries.maxBytes; the phase-gate file streamed while the gate runs; session gate logs no longer tail-only; proven on a two-attempt red rig battery | TODO | - | - |
| PW5.2 | conductor events --follow and conductor ps; a dry run writes and pushes nothing; status stops calling a live run interrupted; timestamps on the attention fields; UTC in the log; log --query on a live run; the port fallback printed; the skills' filters replaced by one events line a test proves complete against the wake table | TODO | - | - |
| PW5.3 | One park rule for the matcher and the queue; a park pushes once (the 553-line fixture replayed produces one push); approve prints the arithmetic and refuses over the remaining authorisation; a satisfying reload un-parks; the cap checked after the reload; --once persists cost; resume on a token refuses; kill says Paused; goto applies once | TODO | - | - |

### PW6 — The cards and the tree

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PW6.1 | task --add / --retitle / --split as events with the tracker regenerated; a hand edit of the table reported and discarded, never silently reverted; a live plan edit keeps the handoff block; a gatePolicy reload runs the pending phase gate | TODO | - | - |
| PW6.2 | report.squash off by default with the two guards when on; the report written before the last bookkeeping commit; plan set in place and refusing an unknown leaf; the state-pointer refusal; evidence.dirs; UTF-8 bugs; bg_status live-only; engine dir on the session PATH; MCP amend/blocked/todo/skipped; owner-queue ages; ci accept; run close on a full aborted run; FU-F1-06; AgentConfig.Merge keeps Env | TODO | - | - |

### PW7 — Two runs, one box; the sweep; the close

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| PW7.1 | gates.exclusive: the machine-level battery lock with heartbeat, bounded wait, reaping and the contended stamp, proven by two rig runs on one scratch state home; conductor watches lists the box's runs and live watchers so the SUPERVISOR column stops reading none | TODO | - | - |
| PW7.2 | The one-seam sweep of PW1.1's open-sweep rows in rounds, each bug closed in the commit that fixes it or re-homed with a reason; none of that class left open | TODO | - | - |
| PW7.3 | conductor-drive and conductor-watch rewritten around the verbs, intervene.md reduced to what still needs a person, the silent-failure table re-verified against this build; release preflight and perform; the era's numbers; both plan docs to history with the repoints in the same act; the owner's acts printed and parked | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
