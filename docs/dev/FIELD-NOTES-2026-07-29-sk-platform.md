# Field notes — driving `sk-fleet 09.6 finish the list` with conductor (2026-07-29)

Findings from a complete autonomous run of the `sk-fleet 09.6 finish the list` plan on
`C:/code/sk-platform`, engine master `5cf77f1`. **The run finished: 29/29 checkpoints, 13/13 stages
confirmed, 24 paid sessions, $224.20, 14h08m wall clock of which 7h53m was session time.** Nothing
was lost, nothing was rolled back, and no verdict in the run was wrong.

Written by the operator agent. I watched sessions #23–#25 live and reconstructed the rest from
`.conductor/conductor.log`, `.conductor/lessons.md` and `conductor.plan.json`. Where a claim comes
from the log rather than from something I saw happen, it says so. Anything I could not verify says
that too.

Ordered by what it cost, not by severity label. This is a second data point next to
[FIELD-NOTES-2026-07-29-devcontext.md](FIELD-NOTES-2026-07-29-devcontext.md); where the two runs
agree I say so explicitly, because one project's quirk and two projects' quirk are different things.

---

## 1. An external rate limit burns paid sessions re-reading a clock

**Severity:** high — the single largest avoidable cost in the run.

**What happened.** Stage S4 (merge two image-shape PRs) could not proceed because Vercel's rolling
24-hour deploy window was full. Conductor's only response to "cannot proceed yet" is to spawn
another paid agent session. It did so three times:

| session | time | duration | cost | what it learned |
|---|---|---|---|---|
| #15 | 12:59 | 5m | $3.26 | window 100/100, next slot ~15:12:10 |
| #16 | 13:06 | 3m | $2.01 | window 100/100, next slot ~15:12:10 |
| #17 | 13:36 | 4m | $2.43 | window 100/100, next slot ~15:12:10 |
| #18 | 15:12 | 7m | $3.77 | window open — merged, stage done |

`.conductor/lessons.md` for S4-17 states it plainly: *"a third consecutive matching measurement …
identical to 12:59 and 13:07, so the reset time is a stable fact."* Sessions #16 and #17 cost
**$4.44 to re-derive a timestamp session #15 had already written down.**

Worse, session #18 did not start on its own. The log reads `[15:12:09] control: ResumeRun` /
`resumed by user` — **a human sat and watched for the window to open**, one minute after the reset
time the agent had predicted two hours earlier. That is precisely the job an orchestrator exists to
do.

**Impact.** S4 consumed **10 stage attempts across 8 sessions and $51.98** — 23% of the run's total
spend — for work whose actual content was two PR merges. Most of that was not the work being hard;
it was the engine having no way to express *wait*.

**Suggested fix.** A session needs a way to exit "blocked until `<timestamp>`" — a distinct outcome
from Advanced / NoProgress. Given one, the engine sleeps until then and respawns once, instead of
paying an agent to look at a clock. Concretely, either:
- a new `conductor task --blocked-until <iso8601> --reason "<text>"` verb the agent can call, feeding
  a `BlockedUntil` outcome the run loop honours; or
- a stage-level `notBefore` / `waitFor` gate — a command whose non-zero exit defers the stage rather
  than failing it, with a backoff the engine owns.

The first is better: only the agent knows the unblock time, and here it *knew it exactly*, wrote it
into the lessons file, and had no way to tell the engine.

---

## 2. "FULL battery green" is logged when no battery exists

**Severity:** high — the confirmation language asserts something that did not happen.

**Evidence.** Gates in this plan are scoped per stage via `gates[].stages`. Only S2, S4, S10 and S12
have any gate at all. For the other **nine of thirteen stages** the log still reads:

```
[15:59:19] phase gate S6: running FULL battery at 18ba9a8 to confirm the phase
[15:59:19] phase gate S6 finished in 0s — GREEN: gates green (none configured)
[15:59:19] ✓ phase S6 CONFIRMED (full battery green) — advancing
```

Three lines, in sequence, saying a full battery ran and was green. It ran in **0s** because there
was nothing to run. The parenthetical `(none configured)` is the only honest token in the block, and
it is the one a watcher's eye skips.

**Why it matters here specifically.** **S13 — the stage that merged three PRs to production, moved
the live site, and closed the round — had no gates.** Its phase confirmation rests entirely on the
agent's own claim plus a git diff plus a tracker diff. That may well be acceptable for a docs-and-
merges stage; the problem is that the log says it was confirmed by a full battery, so nobody
reviewing the run can tell a genuinely-verified stage from an unverified one without going back to
the plan file and cross-referencing `gates[].stages` by hand. I did exactly that to write this
paragraph, and it is not obvious work.

**Suggested fix.** Split the outcome into three states, not two:
- `CONFIRMED (battery green: kit, kit-proof)` — gates ran and passed, name them
- `CONFIRMED (no gates configured for this stage)` — advanced on claim + git + tracker only
- `RED` — unchanged

and consider a `doctor` warning that lists stages with zero gates, so the plan author sees the
coverage gap at authoring time rather than a reviewer discovering it in the log afterwards.

---

## 3. NoProgress still fires on sibling-only delivery — twice, in a plan written to avoid it

**Severity:** medium-high — burns an attempt and queues a spurious Fix session.

**Evidence.** `repo` is `C:/code/sk-platform`, but most of this plan's real work lands in siblings
(`C:/code/sitekit`, `sk-studio`, `elfine-site`, `nimagiti`). Twice the verdict read NoProgress:

```
[11:01:26] session #10 NoProgress — queuing fix session (attempt 1/4)
```
(also session #6, same stage S4)

Session #6's NoProgress queued Fix session #7 — **$3.82 spent on a fix for nothing being broken.**

This hazard is already documented in the operator skill, and this plan already applied the
recommended workaround (sessions write dated proof-notes into anchor-repo docs). It bit anyway,
twice, on the one stage where the session's output was almost entirely a sibling-repo PR.

**Impact beyond the money:** a NoProgress verdict is *wrong information* in the run history. Session
#10 delivered a real measurement (the deploy window state) and is recorded as having delivered
nothing.

**Suggested fix.** Either (a) let `repo` be a list, with `hasCommits` true if any listed repo moved,
or (b) count `newly DONE` checkpoints as progress in the NoProgress judgement — currently a session
can claim a checkpoint *with evidence* and still be judged to have made no progress, which is the
part that reads as a bug rather than a limitation.

---

## 4. The gate cache is keyed on the anchor repo while the gate's subject is a sibling

**Severity:** medium — latent. **It did not cause a wrong verdict in this run**; I am reporting the
mechanism, not an incident.

**Evidence.** The `studio` gate runs `cd /d C:\code\sk-studio && npm run check && npm run build`.
Its cache key is anchor (`sk-platform`) HEAD. At S12's phase gate:

```
[17:15:45] gate studio: cmd /c "cd /d C:\code\sk-studio && npm run check && npm run build"
[17:16:02] gate studio: PASS in 17s
[17:16:04] phase gate S12: running FULL battery at d967790 to confirm the phase
[17:16:04] gate studio: CACHED (0s)
```

Here the cache was correct — the same gate had run 2 seconds earlier against the same tree, and
reusing it is right. But the phase gate announces it ran "at d967790" while serving a result
computed before that sha was the key. The two coincided; nothing guarantees they will. A sibling
repo can change without the anchor moving at all, which is the normal condition in a multi-repo
plan, and then `CACHED (0s)` means the battery never saw the change.

**Suggested fix.** Include the gate's own working directory HEAD in the cache key when the command
`cd`s outside the anchor, or offer `gates[].cacheKeyRepo`. Failing that, log the sha the cached
result was computed at, so `CACHED (0s)` is auditable instead of opaque.

---

## 5. `/state` cost aggregates lag until the verdict — confirmed on a second project

**Severity:** low-medium. **This corroborates finding #1 of the devcontext notes from an independent
run**, which is the reason it is here rather than being left as a known issue.

**Evidence.** At 16:22, session #23 had been running since 16:09. `GET /state` returned
`totalCostUsd: 194.71` — the post-#21 total, with #23's in-flight spend invisible. After #23's
verdict the same field read `211.15`, jumping by the full session cost at once.

The devcontext notes correctly diagnose this as display latency, not data loss. I can add that the
aggregate **is** populated and durable across the run (the devcontext entry's own correction), and
that the effect is most annoying for exactly the persona conductor is built for: a watcher deciding
whether a run will finish inside `maxRunCostUsd`. See #7.

---

## 6. Nothing survives the engine's exit — no post-run summary

**Severity:** medium — affects every completed run, which is all of them eventually.

**What happened.** The moment the plan completed, the control plane went down with the process.
`GET /state` returns an empty body. To report the final numbers — total spend, session count, which
stages churned — I had to `grep` `conductor.log` and hand-sum 24 `exited (code 0, Nm, $X)` lines.
`conductor report` needs a live engine.

**Impact.** The end of a run is when a summary is *most* wanted and it is the one moment none is
available. Everything needed already exists in `run.db`; it simply is not emitted.

**Suggested fix.** On completion, write `.conductor/RUN-SUMMARY.md` (and/or `.json`) beside the log:
plan name, start/end, wall clock, session count, per-stage attempts and cost, total spend against
cap, final checkpoint tally, and the list of sessions whose outcome was not Advanced. Alternatively
let `conductor report -p <plan>` read `run.db` without a live engine.

---

## 7. Budget projection against `maxRunCostUsd` is entirely manual

**Severity:** low — but it is the number a watching human asks for most.

**What happened.** With `maxRunCostUsd: 250` and the run at $211, I was asked whether the remaining
work would fit. Answering meant summing session costs out of the log, eyeballing the per-session
average, and guessing at how many sessions the last two checkpoints would take.

**I got it wrong.** I predicted the run would park on budget inside S13. It finished at **$224.20**,
$25.80 under, because S13 was mostly waiting on merges and deploys rather than reasoning — a shape
the per-session average could not express.

**Impact.** Nothing broke; a park is lossless and resumable, so a wrong projection costs only
credibility. But the operator was the wrong component to be doing this arithmetic.

**Suggested fix.** Surface on `/state` and in the Face: `costSpent`, `costCap`, `costRemaining`,
`meanSessionCost`, `checkpointsRemaining`. Even without a projection, those five let a watcher form
one in a glance. A blunt `projectedCompletionCost` would have been wrong here too — the honest
version is the inputs, not the guess.

---

## 8. What worked, and should not be broken

Recording these because a notes file that only lists faults misrepresents the run. Every item below
is something I watched behave correctly.

- **The resume rail.** Session #2 stalled; `#3 start — Resume S2 attempt 1/4 (resume #1 of 898d8fd5)`
  picked it up and the stage completed. No human involvement.
- **Stale control intents are not replayed.** A `KillSession` written at 16:07:40 was still on disk
  when the engine restarted at 16:08:27, and the engine logged *"stale control.json from a previous
  run (KillSession, written 16:07:40) — removed, not executed"*. That is the right call and it is
  the kind of thing that is only ever noticed when it goes wrong.
- **`NEEDS HUMAN` parks are precise.** Fired twice (07:20, 11:48), both times because the agent wrote
  a `HUMAN:` line into the tracker handoff, both times cleanly resumable. The design-direction park
  at 11:48 is the one that mattered: the run stopped, the owner picked direction B, and session #21
  resolved it as a verification rather than re-doing the work.
- **Per-stage gate scoping** (`gates[].stages`) kept a 30-minute full battery off the eleven stages
  it had no bearing on. This is the feature that makes a 13-stage plan affordable; see #2 only for
  the logging, not for the mechanism.
- **Per-stage squash.** `P4 squash: stage S12 complete` left the anchor repo's history readable —
  one prose commit per stage rather than 24 session commits.
- **`agent.model` was configured correctly here** (`claude-opus-5`, with `{model}` present in the
  arg template), so the devcontext run's most dangerous trap did not bite. The documented mitigation
  is followable; it just needs to be a `doctor` failure rather than a habit.

---

## Summary of proposals

| # | Proposal | Fixes | Rough size |
|---|---|---|---|
| 1 | `BlockedUntil` session outcome + engine-owned sleep | wasted sessions on external rate limits | medium |
| 2 | Distinguish "no gates configured" from "battery green" in phase confirmation | false confirmation in the log | small |
| 3 | Multi-repo `hasCommits`, or count `newly DONE` as progress | spurious NoProgress + Fix sessions | small-medium |
| 4 | Gate cache key includes the gate's own repo HEAD | latent stale-battery risk | small |
| 5 | Fold in-flight session usage into `/state` | invisible spend mid-session | small |
| 6 | `RUN-SUMMARY.md` on completion, or offline `report` | nothing survives engine exit | small |
| 7 | Budget fields on `/state` | manual projection | small |

Items 2, 4, 6 and 7 are all small and all in the same area: **what the engine tells a person who is
watching it**. The run's actual decision-making was sound throughout — 13 stages, 13 correct
verdicts. The gap is between what conductor knows and what it says.

---

## Closure ledger (SF7.1, 2026-08-01)

Every numbered finding above, and the commit that answered it. Measured from the commits — `c3e0813`
and `1ce4ba7` both cite "sk #3" in their own bodies — not from the era spec's Appendix B index.

| # | Finding | Stage | Commit | What closed it |
|---|---|---|---|---|
| 1 | An external rate limit burns paid sessions re-reading a clock | SC5.1 | `ac70123` | A session says `--blocked-until <iso>` with a reason and the engine sleeps until then, spawning exactly one more session; no attempt is burned and the reason is handed to the session that wakes up |
| 2 | "FULL battery green" logged when no battery exists | SC2.2 | `603fbbb` | `GateRunner.ConfirmationBasis` decides that line from the plan's stage-scoped gate set AND the battery result, in three honest states: gates GREEN naming them, no gates configured for this stage, gates RED confirmed anyway. `doctor` now warns and names stages with no battery |
| 3 | NoProgress on sibling-only delivery | SC4.2 + SC4.3 | `1ce4ba7` + `c3e0813` | Two halves, and **Appendix B names only the first.** The green condition now reads workCommits OR newlyDone OR stageComplete, so a session that claimed a checkpoint and committed nothing scores Advanced (`1ce4ba7`); and plans declare `satelliteRepos`, whose start heads the verdict diffs beside the primary's, so a session that committed only next door logs `commits 1 (incl. 1 in satellite repo(s): ...)` (`c3e0813`). A satellite path that is missing or is not a git repo now FAILS doctor by name |
| 4 | The gate cache is keyed on the anchor repo | SC4.3 | `c3e0813` | The cache key is the gate's own world rather than this repo's HEAD, so a gate whose cwd is a sibling checkout is no longer served its old pass after that checkout changed |
| 5 | `/state` cost aggregates lag until the verdict | SC2.3 | `55da220` | The live fold reads a `TokenDelta` from both providers, deduped per session, priced at the run's own learned rate — see devcontext #1 |
| 6 | Nothing survives the engine's exit | SC2.4 | `87d7fcd` | `CompletePlan` writes `.conductor/RUN-SUMMARY.md` from `run.db` — plan, wall clock, sessions by kind, per-stage attempts and cost, spend vs cap, and every session that did not end Advanced — and `conductor report` opens `run.db` offline instead of short-circuiting to empty |
| 7 | Budget projection against `maxRunCostUsd` is manual | SC2.3 | `55da220` | `/state` carries the budget block every surface was deriving wrongly: `costSpent`, `costCap`, `costRemaining`, `meanSessionCost`, `checkpointsRemaining`, and window vs lifetime. An owner approval stamps `budgetWindowStartedUtc` and logs what it forgave, so the window a cap is measured against is stated rather than inferred |

The proposal table above scored items 2, 4, 6 and 7 as small and in the same area — what the engine
tells a person watching it. All four landed, along with 1, 3 and 5.
