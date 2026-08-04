# Next era — findings, measurements, and the item list the plan was written from

**Written 2026-08-04** from the owner's field notes after three real runs (conductor's own Sarban
core + face, sk-studio, DevContext2) and a fresh read of the tree at `feat/sarban` @ `0bbc972`.

**Revised the same day (second pass)** after the owner's review. What changed in this pass:

- **Parallel and multi-repo moved to the centre.** The owner's real case is two client sites, or two
  pages of the studio site — *two repositories, or two worktrees* — and it was the note he marked
  "crucial" three times. Theme C is rewritten from four paragraphs into the era's second plan, with
  six new findings read out of the lane code (**C4–C9**), and it is now grounded in what the 2026
  orchestrator ecosystem actually ships (new section: *What everyone else does*).
- **Caveman is scrubbed.** Owner's instruction. The measured ceiling was ~4–6% against a bill that is
  two-thirds cache reads, it would have mutated a harness conductor cannot assert, and **E4** takes the
  same win in a format we own. No spike, no plugin, no decision to make. Nothing else in the plan
  depended on it.
- **Architecture hygiene got teeth.** The owner reads this repo daily and shows it; "the files are just
  there with no organisation" is a defect in the product, not a nicety. **G7** (the front door — an 80KB
  append-only `AGENTS.md`, two era trackers loose at the root, a 44KB duplicate that has since diverged
  from its twin, no `ARCHITECTURE.md`) and **G8** (layering that is documented convention and nothing
  else) are new, and G3 now ships an *enforced* boundary rather than a hopeful one.
- **The token numbers were re-measured from `run.db`, not carried over.** The 8M cap's real score,
  and a new finding: **every single rollover died at the hard ceiling, not at the cooperative nudge**
  (**B9**). That sets this era's own limits (§ *Setting this era's budget*), which are no longer copied
  from Sarban.
- Every "today" claim was re-checked against the code on 2026-08-04. Line numbers and file paths were
  verified, not remembered. Numbers are measured unless labelled *estimate*.

**This is not the plan.** The plan is `docs/history/CONDUCTOR-KARVAN.md` (spec) plus
`plans/karvan/core.plan.json` and `plans/karvan/lanes.plan.json`. This document is the research those
were written from, and it stays the place where a claim can be checked against a measurement.

Sizes are S (one checkpoint) / M (2–3) / L (a stage of its own).

### Coverage map — every note the owner listed, and where it went

| owner's note | item(s) | plan stage |
|---|---|---|
| evidence seeing and knowing · evidence — files on system? | **E1** | K5.3 |
| code and architecture hygiene (go face + backend) | **G1, G2, G3, G7, G8** | K2, K6 |
| caveman — token saving | **scrubbed** — see the revision note above; **E4** carries the safe part | K5.1 |
| review previous sessions for token efficiency, we tuned it multiple times | **B2, B9, B10** | K1.2, K4.2 |
| telegram messages | **E2** | K5.2, K5.4 |
| the multi repo worktree merge and support | **C2, C5** | L2, L4.2 |
| conductor auto mode for future | **D3** | parked, named |
| internal runner and planner | **D1** (in-process supervisor), **D3** (planner) | L6.1 |
| **parallel work ×3 — "this is crucial"** | **C1, C4, C6, C7, C8, C9** | L3, L4 |
| GitHub syncing · conductor talk to github api for tasks | **C3** | L6.3 (optional) |
| UI reflects budget updates | **B5** | K4.4 |
| solution for long text | **F1** | K6.2 |
| embed ai | **A4** | parked below B1 |
| review token consumptions stuff | **B1, B4** | K4.1, K4.3 |
| conductor money report | **B4** | K4.3 |
| stateful · history with consumption and money | **A1, A2** | K3.1, K3.2 |
| remove .conductor · general vs specific | **A1** | K3.1 |
| git worry | **G4** | L1.1, L1.2 |
| conductor better token understanding (match what Claude does) | **B1** | K4.1 |
| conductor event sourced advisor | **D2** | L6.2 |
| conductor need for the human should be less | **D1, D3** | L6.1 |
| history aware | **A1, A2** | K3.1, K3.2 |
| monitor session sits inside · remote friendly / Claude remote | **D1** (inside), **E3** (remote) | L6.1, K5.4 |
| glow + other bubble tea for UI/UX best practice | **F2** | K6.1 |
| refactoring go and .net | **G1** (go), **G2, G3** (.net) | K2, K6.3 |
| **code should be browsable and readable** (this pass) | **G7, G8** | K2.2, K2.4 |

Found in the code, not in the notes: **B3** (rollovers never record their commits), **B8**, **B9**
(the nudge is ignored), **E4** (no session-result contract), **G6** (SF7.1's filed MCP bug), and the
six concurrency findings **C4–C9**.

---

## Headline: what the measurements actually say

Two complete runs on this repo, both plans, from `.conductor/run.db`:

| run | sessions | tokens | of which cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| Sarban core | 28 | 504.5M | **98.5%** | $359.98 | 26 | 19.4M | $13.85 |
| Sarban face | 41 | 397.2M | **98.2%** | $296.98 | 20 | 19.9M | $14.85 |

Uncached input was 5.3M / 5.6M. Output was 2.34M / 1.75M. **Cache reads are 98%+ of every token this
project has ever spent.**

Pricing the face run at Opus-class rates ($5 / $25 / $0.50 / $6.25 per M in / out / cache-read /
cache-write) reproduces $267 of the billed $297, and the residual ≈4.8M cache *writes* covers the
rest — so the decomposition is trustworthy *(estimate — the engine deliberately has no price table,
see `LiveCostEstimator`)*:

| component | share of bill |
|---|---|
| **cache reads** (context size × turns) | **~66%** |
| output tokens | ~15% |
| uncached input | ~9% |
| cache writes | ~10% |

**This ranks every token idea in theme B.** Anything that shrinks *what a session carries* attacks
two-thirds of the bill. Anything that shrinks *what the agent says* attacks fifteen percent of it —
which is why the output-brevity idea did not survive this pass.

Secondary facts worth carrying into the plan:

- `costs.tokens_think` is **0 on every row ever written** — Claude bundles reasoning into output and
  `ClaudeProvider` leaves it unset (correctly). The column, the DTO field `TokensThink`, and the
  Report column are dead weight that read as "no thinking happened". → **B8**
- A composed session prompt is ~20KB ≈ 5k tokens (`.conductor/sessions/*/prompt.md`, max 20,767 B).
  Re-sent every turn, that is ~2–5% of a session — real, but not where the money is. It is the ceiling
  on **A4**, and the reason A4 stays parked.

### The 8M cap's real score, and the rail that never fired

Re-measured 2026-08-04 from `costs` joined to `sessions`, per session, for the face run. The ceiling
became effective **at session 9** — the tell is that every rollover from there on lands at 8.00–8.13M
while sessions 1–7 ran as far as 37.3M. (Most likely a mid-run cap change;
`MaxSessionTokensThisRun` exists for exactly that. Unresolved, and worth one query once **B1**'s
instrument exists.)

| window | sessions | tokens | checkpoints closed | tok/ckpt |
|---|---|---|---|---|
| 1–8, ceiling not yet effective | 7 costed | 158.9M | 6 | **26.5M** |
| 9–41, ceiling 8M / nudge 0.75 | 33 | 238.3M | 17 | **14.0M** |

**The cap paid 1.9×.** That is the headline and it is not in doubt.

The price it charged is:

- **11 rollovers in 33 sessions (33%) — and all 11 died at 8.00–8.13M.** Not one of them stopped at
  the 6.0M nudge. The cooperative rail is the *only* path that ends a capped session on its own terms
  (`SessionRunner.OverSessionTokenBudget` → `EndOnBudget` → `agent.Kill()` is the other), and in this
  run it converted **zero of eleven**. → **B9**, new, cheap, and it is the difference between a cap
  that shapes work and a cap that interrupts it.
- **9 more sessions ended voluntarily at 7.3–7.7M having closed no checkpoint.** They took the nudge
  and wrapped up with nothing to claim.
- So **20 of 33 post-cap sessions closed no checkpoint**, at roughly $6 each — about $120 of sessions
  that produced work but no verified progress.
- Sessions that *did* close a checkpoint post-cap ran **4.66M–7.75M, median ≈7.3M**.

Read those four facts together and the diagnosis is precise: **the nudge sat below the median session
that finishes.** At 0.75 × 8M it fires at 6.0M, and the typical successful session needed 7.3M. Every
session was therefore nudged before it could have finished naturally — exactly the failure
`TOKEN-BUDGET-TUNING.md` §7 step 4 warns about — and the headroom left afterwards (2.0M) was only
~1.4× the observed wrap-up spend (0.46–1.75M, typically ~1.4M), under the ≥1.5× rule in §4.

### Setting this era's budget

Applying `TOKEN-BUDGET-TUNING.md` §7 to the measurements above rather than copying Sarban's numbers:

```jsonc
"limits": {
  "maxSessionTokens": 12000000,  // 7.75M max observed closer + 2.6x wrap-up
  "softBreakRatio":   0.7        // nudge 8.4M (clear of the 7.75M closer) - headroom 3.6M (2.6x wrap-up)
}
```

The nudge now sits **above** the largest session that ever closed a checkpoint, so it interrupts only
sessions that were genuinely going long; the headroom is 2.6× the measured wrap-up, comfortably over
the 1.5–2× rule. Expect fewer rollovers and slightly larger sessions; expect tok/ckpt to land between
14.0M and 26.5M and **re-measure at the first stage boundary** — that re-measure is **B2**'s own
deliverable, and the plan dogfoods it at K7.1.

---

## What everyone else does — the 2026 orchestrator ecosystem

New in this pass, because the owner asked for it: *best practices exist on GitHub for tools built to
orchestrate AI agents; learn from them.* They do, they converge, and the convergence tells us which
of conductor's gaps are ordinary and which are its edge. Surveyed 2026-08-04
([Augment's nine-orchestrator teardown](https://www.augmentcode.com/tools/open-source-agent-orchestrators),
[the awesome-agent-orchestrators index](https://github.com/andyrewlee/awesome-agent-orchestrators),
[Zylos' worktree-isolation patterns](https://zylos.ai/research/2026-02-22-git-worktree-parallel-ai-development/),
[Gas Town's architecture](https://gastown.dev/docs/design/architecture/)).

**1. The git worktree won. It is the consensus isolation primitive** — every one of the nine
orchestrators tested uses it, and roughly forty more in the wider index do. Conductor already has it
(`MutatingLaneRunner`, `LaneCoordinator`, `Git.WorktreeAdd*`), which means the era's parallel work is
not a bet on an unproven substrate.

**2. What worktrees do *not* isolate is the thing that bites: ports, databases, containers, caches.**
Of nine orchestrators, exactly one (Emdash) solves it, by injecting a per-task `$PORT`. This is not
academic here: conductor's own control plane already needs a distinct port per run (4317/4318, a
documented hazard on this machine), and the owner's actual parallel work is *websites with dev
servers*. → **C7**

**3. Merging back is where every tool gives up.** Augment: "every tool leaves task alignment,
conflict resolution, and merge decisions on my plate," with two exceptions. Conductor is already one
of the exceptions — it has a merge gate that stages the integration in a third worktree and runs the
full battery on the merged tree before accepting. What it does not have is a **queue**. Gas Town's
"refinery" is the reference: a Bors-style *batch-then-bisect* queue — rebase the pending branches as a
stack, test the tip, fast-forward all on green, binary-bisect on red. → **C5**, **L4.2**

**4. Conflict detection can be real instead of declared.** The pattern is
`git merge-tree $(git merge-base A B) A B` — a three-way merge that touches nothing and tells you
whether two branches will conflict *before* an agent is dispatched. Conductor's `PathClaimTracker`
only knows what the plan *declared*. → **C8**

**5. Worktrees cost disk and time, and the numbers are known.** ~0.5GB per worktree in a 2GB
codebase (20 agents measured at 9.8GB); a practical ceiling of 8–10 concurrent; and
`git worktree remove` is pathologically slow (10k+ `lstat`/`unlink` on a 10k-file tree) where
`rm -rf` + `git worktree prune` is not. Conductor calls `Git.WorktreeRemove` (`worktree remove
--force`) in a `finally`. → **C9**

**6. Two-level state is the shape that works.** Gas Town splits *town* state (machine-level,
cross-project, `~/gt/`) from *rig* state (per-repository), routes between them by prefix
(`routes.jsonl`), and has every worktree **redirect** to the canonical store rather than carry its own
(`.beads/redirect`). That is independent confirmation of **A1** — and it is the missing piece under
**C6**, because a conductor lane worktree today would have no `.conductor/` at all. They also run one
SQL server per town rather than an embedded file DB, precisely because many agents write at once.

**7. The role decomposition converges too, and conductor already has most of it.** Gas Town: *mayor*
(coordinator), *witness* (per-repo liveness monitor and stuck-agent recovery), *refinery* (merge
queue), *polecats* (ephemeral workers with persistent identity), git-backed issue tracking, and a
"problems view" surfacing the agents that need a human. Map onto conductor: `RunLoop` is the mayor,
`conductor watch` + the supervisor block is the witness, the merge gate is a refinery with no queue,
the tracker is the issue store, and `OWNER-QUEUE.md` **is** the problems view. Two pieces are missing:
the queue, and a scheduler.

**8. Multi-repo is the ecosystem's open gap.** Augment, verbatim: "Coordination between agents is
either manual (most tools) or limited to task-graph scheduling within one repo. Cross-service
refactoring remains unsolved in the OSS layer." Of ~140 tools indexed, five claim multiple
repositories. **This is where conductor is unusual, it is exactly what the owner needs, and it is
worth building deliberately rather than incidentally.** → **C2**, stage **L2**

**9. The coordination ladder places conductor.** Per-edit approval (Claude Squad, Nimbalyst) →
milestone gates (Composio, Bernstein) → spec-driven verification. Conductor sits at the top rung and
past it: the checkpoint contract, the engine re-running gates itself, and agent prose never counting
as evidence. Worth writing down, because it is the project's actual claim.

**10. What the field admits it lacks is what theme A builds.** Emdash's named weakness — "no
agent-to-agent coordination, no shared config across agents… knowledge gains aren't distributed among
concurrent workers" — is the exact failure mode conductor's handovers, lessons and one catalogue
(**A1**) are for. Parallel lanes make that a requirement rather than a nicety: three lanes that each
learn the same trap the hard way have paid for it three times.

**Two cautions the survey earns.** Worktree isolation **cannot** resolve a task dependency — if lane A
builds the API lane B calls, they must be sequenced, not parallelised, and that is what
`StageConfig.DependsOn` is for. And disk bloat plus "twenty worktree folders" is a real reported
failure of the kanban-shaped tools; a ceiling with an alert beats discovering it.

---

## A. Memory — conductor stops forgetting

*Notes: "history aware", "centralized place", "stateful", "history with consumption and money",
"remove .conductor", "embed ai".*

**A1. State is repo-local and git-ignored, so history dies. (L)**
`PlanConfig.StateDir => Path.Combine(Repo, ".conductor")` (`PlanConfig.cs:109`) — hard-coded, one
line, no override. `.conductor/.gitignore` is `*` with four exceptions (`REPORT.md`, `followups.md`,
`handovers/`), so `run.db` — every session, cost, gate, bug, event — is untracked and machine-local.
Clone the repo on another box and the project has no past. `run.db` *does* already hold multiple runs
(2 rows in `runs`), so the schema is fine; the **location** is the bug.
**Shape:** a machine-level home (`%LOCALAPPDATA%/conductor` / `~/.local/share/conductor`, with a
`CONDUCTOR_STATE_HOME` override) holding one catalogue DB keyed by repo path + plan, with
`.conductor/` demoted to per-run scratch (logs, transcripts, evidence) and a pointer. Needs a
migration that imports existing `.conductor/run.db` files, and `conductor history` verbs. Do **not**
delete `.conductor` — the tracked deliverables (`REPORT.md`, `handovers/`, `followups.md`) belong in
the repo. Gas Town's town/rig split is the same answer arrived at independently (§ *What everyone else
does*, point 6).
**Risk:** touches every `IRunStore` caller and the Face's discovery path. This is the era's spine —
A2, B4, B5, C6 and the whole lanes plan depend on it.

**A2. Nothing can browse a past run. (M)**
The Face's History tab reads the *live* run only; `conductor report` regenerates the current
`REPORT.md`. There is no "show me the DevContext2 run from July", no cross-run comparison, no
"how much did this project cost me this month".
**Shape:** `conductor history [--repo|--plan|--since]`, a run picker in the Face that lists past runs
from the catalogue (the widget already exists — `runpicker.go`), and read-only replay of a finished
run's spine.

**A3. `lessons.md` is a diary, not lessons. (S)**
`.conductor/lessons.md` is a rotating log of session *narratives* ("Session 37 landed the first half
of SF7.1 across three commits…") — near-duplicates of the handovers, truncated mid-sentence with `…`,
and it currently contains **the same SF7-38 entry twice**. `LessonsBattery` pastes this into the next
prompt. It is paying cache-read rent on prose that teaches nothing.
**Shape:** extract *rules* ("gate battery scores teardown unless bg children settle → set
`batterySettleSeconds`"), one line each, deduped, capped. Fix the duplicate-append bug.

**A4. Retrieval over history instead of pasting it. (M, depends on A1/A3) — parked, on purpose.**
The owner's "embed ai" note. Once history is centralized, the batteries could stop pasting
*everything* and retrieve the 3 relevant lessons/failures for *this* checkpoint. Embeddings are one
way; BM25 over the catalogue is cheaper and probably enough. **But the batteries are ~2–5% of a
session**, so this is a small-percentage win dressed as a big feature. It stays parked until **B1**'s
instrument says the prompt prefix is worth attacking. Named here so it is not rediscovered as new.

---

## B. Token economics — measure it, then shrink it

*Notes: "review token consumptions", "better token understanding, match what Claude does",
"money report", "UI reflects budget updates".*

**B1. The engine knows cumulative tokens; it does not know context size. (M) — highest-value item in this theme.**
`LiveMetrics.SessionTokenTotals` folds `TokenDelta` into input/output/reasoning/cacheRead
**integrals**. Nothing anywhere tracks *context size at a turn* — the number Claude Code's `/context`
shows and the number that actually drives the 66%. `context_high_water` appears nowhere in the tree.
`TOKEN-BUDGET-TUNING.md` §1 states the rate-vs-area distinction correctly; the engine only implements
the area.
**Shape:** derive per-turn context from the stream (cache-read delta per turn ≈ prefix size), record a
`context_high_water` and mean-turn-context per session, and surface both. That single number tells the
operator "your sessions run at 210k context" — which is the actual diagnosis behind a bad cap, and the
only honest input to B7.

**B2. Bake `TOKEN-BUDGET-TUNING.md` into the engine. (M)**
The doc's method — measure the repo's **session floor**, measure **wrap-up spend**, set
`maxSessionTokens = floor + 1.5–2× wrap-up`, choose `softBreakRatio` so the nudge clears the floor and
headroom ≥1.5× wrap-up — is a SQL query and a paragraph. sk-studio was *worse than uncapped* for three
stages because the cap sat below the floor (stage F: 9 sessions, 53.8M tokens, **one** checkpoint).
This repo's own face run shows the subtler version of the same mistake: the cap was fine, the **nudge**
sat below the median finishing session (§ *The 8M cap's real score*).
**Shape:** `conductor budget` (or `doctor --budget`): reads the catalogue, prints floor / wrap-up /
current cap / nudge-vs-floor / rollover rate, and says "your nudge is 0.8× your median closing session
— raise the cap to 12M". Warn at `doctor` time when a plan's cap is below the measured floor **or its
nudge is below the median closing session**. Re-check after each stage.

**B3. `SessionRunner.cs:411` — a rollover's work is never recorded. (S, correctness)**
`rec.Outcome = RolledOver` returns *before* the verdict pass that fills `NewCommits` and `newly_done`
(verified 2026-08-04 at `SessionRunner.cs:411-420`). So the ledger says 0 commits on 100% of
rollovers, while git says **91%** of conductor's rollovers and **56%** of sk-studio's left real agent
commits. Every board, digest and Telegram push under-reports on every rollover. This is cheap and it
poisons every other measurement in this theme — **do it first**.

**B4. There is no money report worth the name. (M, depends on A1)**
Today: `REPORT.md` has a per-session cost column, the Face has a money widget, `run.db` has `costs`.
Missing: $/checkpoint, $/M blended rate, cache-hit share, agent-vs-gate-vs-advisor split over time,
per-stage cost, project lifetime spend, month-to-date. Every number in this document's Headline took a
hand-written SQL query to produce, and that is exactly the report the owner keeps asking for.
**Shape:** `conductor money [--run|--project|--since]` + a Report tab section. Ship the Headline
table's exact columns, plus the two windows in *The 8M cap's real score* — because "what did the cap
buy me" is the question that recurs.

**B5. Token headroom is invisible while it matters. (S)**
Money caps *are* wired end to end — `CostCap`, budget window, approvals, headroom on Home (SC2.3 /
SF2.3, `api/types.go:135-161`). The **token** side is not: `MaxSessionTokensThisRun` reaches the wire,
but there is no live "session at 6.2M of 12M · nudge in 2.2M" gauge, no burn-rate, no projection to the
run cap. Add it beside the money row.

**B7. Delegation and gate-output discipline. (M) — this is the one that attacks the 66%.**
`TOKEN-BUDGET-TUNING.md` §8 names it and nothing implements it: the floor is high *because* sessions
carry whole-file reads, build output and multi-repo analysis. Levers that exist as seams already:
`batteryCollapse` (stop paying the agent to run gates the engine runs anyway), `readOrder`, gate `tail`
truncation (`gates.tail` is stored — check what the agent is actually shown), and the `IPromptBattery`
seam.
**Shape:** measure what enters context (**B1** gives the instrument), then: truncate gate output handed
back to the agent, teach the session template to delegate searches to subagents so file dumps never
enter the main context, and finish the two designed-and-never-built batteries (`RepoMapBattery`,
definition-of-done recap) that would let a session orient without reading. **Sequenced after B1 on
purpose** — every lever here is a guess until the instrument exists.

**B8. Delete the thinking-token column, or label it. (S, hygiene)**
`tokens_think` is 0 on all 125 cost rows; `TokensThink` rides the DTO, the Report table and
`SqliteRunStore.Queries`. Either drop it or label it "n/a for this provider" — a permanent zero in a
money report is a lie of omission.

**B9. The cooperative nudge is delivered once and was ignored eleven times out of eleven. (S) — new.**
Measured above: every post-cap rollover in the face run ended at 8.00–8.13M, i.e. at the hard ceiling,
never at the 6.0M soft break. The nudge is delivered **once**, by a `PostToolUse` hook riding a single
tool call (`TOKEN-BUDGET-TUNING.md` §5), and nothing re-states it, escalates it, or checks that it was
seen. A session deep in a long tool sequence can simply miss it — and then the outcome is a mid-turn
`agent.Kill()` with the in-flight reasoning lost and a synthetic handoff written.
**Shape:** re-deliver the nudge on an interval or a token threshold rather than once; put the actual
remaining budget in it ("2.1M left — land the claim, then the handoff"); state the wrap-up order
explicitly (claim first, handoff second, because a claim is the only thing that survives); and record
per session whether the nudge was delivered, re-delivered, and obeyed, so the next tuning pass has a
measurement instead of an inference.
**Why it is worth a checkpoint on its own:** it is the difference between a cap that *shapes* work and
a cap that *interrupts* it, and it makes every later number in theme B mean what it says.

**B10. The cap's score is now known, and it belongs in the doc. (S)**
1.9× on tok/ckpt, 33% rollover, 20-of-33 sessions closing nothing. `TOKEN-BUDGET-TUNING.md` predates
that measurement and its conductor rows should be replaced with it, alongside the corrected rule from
B2 (the nudge, not just the cap, must clear the floor). Bundle with the K7.1 re-measure.

---

## C. Concurrency — parallel work across repositories

*Notes: "parallel work" (×3, "this is crucial"), "multi repo worktree merge and support",
"GitHub syncing".*

**The owner's case, stated plainly**, because it decides the design: two jobs at once are two *client
sites*, or two *pages of the studio site*. So the unit of parallelism is **a repository or a worktree**
— not two agents in one tree. That is the easy half of the problem (separate trees are a natural
isolation boundary: no merge ordering, no path claims, no verdict ambiguity), and it is the half to
build first. Same-repo stage parallelism inherits every hard problem and should follow, behind a flag.

**C1. The main loop is strictly sequential — but the substrate for parallelism is already built. (L) — the biggest single win in the era.**
`RunLoop.cs:104` is one `while (!ct.IsCancellationRequested)`, and `RunLoop.Plumbing.cs:34` picks
`Plan.Stages.FirstOrDefault(IsReady)` — one ready stage, one session, at a time.
`StageConfig.DependsOn`'s own doc comment says it: *"Execution stays sequential; this only affects
readiness ordering."* Meanwhile the tree already has **every piece a scheduler needs**:
- `StageConfig.DependsOn` — a real stage DAG, validated for cycles in `PlanConfig.cs:419-427`, rendered
  by `plan import` and by `TrackerGenerator`.
- `Conductor.Planning/WorkflowEngine.cs` + `IWorkflowResolver` — 336 LOC of workflow resolution.
- `LaneWorkerPool` (`limits.maxConcurrentLanes`), `LaneCoordinator`, `MutatingLaneRunner` — worktree
  isolation, scratch branches, and a merge gate that stages the merge in a third worktree and runs the
  battery on the integrated tree.
- `Lanes/PathClaimTracker` — atomic check-and-register of declared path claims.
The missing pieces are a **scheduler** that walks the DAG and runs independent stages concurrently, a
**merge queue** (C5), a **lane that is a real session** (C4), and a Face that can render N live
sessions.
**Shape:** promote Tier-B mutating lanes from "side quest" to "how independent stages run". Gate it
behind `limits.maxConcurrentStages` (default 1, so nothing changes for existing plans).
**Risk:** the highest in the era — verdict attribution, merge order, `run.db` write contention, budget
accounting across lanes, and a Face built around one live session. Own stage, own rehearsal in a
scratch repo before it touches a real plan.

**C2. Multi-repo is verdict-only. (M)**
`satelliteRepos` (SC4.3) is a `List<string>` (`PlanConfig.cs:24`) doing exactly two things:
`SatelliteRepos.Heads`/`CommitsSince` so the verdict counts commits landed in siblings
(`SessionRunner.cs:216`, `VerdictEngine.cs:69`), and a `doctor` check
(`DoctorCommand.cs:284`). It does **not** branch, checkout, gate, commit, push, or clean-tree-assert in
a satellite, and the agent gets no per-repo working contract beyond one sentence in `ToolContract.cs`.
Everything else is prose in `promptExtra` — which is exactly what the owner's own nine-repo CI plan had
to do (`ci-health/plan.json` on `chore/ci-health`: nine satellites, and eight numbered traps carrying
the per-repo rules the engine should own).
**Shape:** promote satellites to first-class `repos: [{id, path, role, branchPattern, gates}]` with
`satelliteRepos` kept as a back-compat alias. Per-repo branch + clean-tree assertion, per-repo gates,
coordinated commit/push, per-repo verdict attribution, and a cross-repo view in the Face. Pairs
naturally with C1 (one repo per lane) and with G4 (git safety).
**Evidence it is the right target:** the nine-satellite run worked, and every rule that made it work
is a paragraph a human wrote by hand and would have to write again.

**C3. GitHub is a release client, nothing more. (M, optional)**
`Core/Update/ReleaseClient.cs` is the *only* GitHub API caller — it asks for the latest release so
`conductor update` works. No Issues, no Projects, no PR awareness. The owner's "conductor talk to
github api for tasks" means the checkpoint table could mirror to Issues, and a PR could carry the run's
evidence.
**Shape:** one-way only (`conductor task --done` → close/comment on a mapped Issue; run report → PR
comment). Two-way sync is a trap — the tracker is the verified contract (anti-pattern A16) and must not
become an eventually-consistent mirror of someone else's board. Off by default, needs a token and
therefore `SecretsStore`.

**C4. A "lane" is not a session, and that is the real distance to parallel stages. (M) — new.**
`MutatingLaneRunner.RunAsync` spawns the agent with `ProcessRunner.RunAsync(agent.Command, args,
lanePath, …)` directly (line 78). It therefore has **no** `SessionRunner`: no cost rows, no token
budget or soft break, no rollover handling, no resume rail, no stall watchdog, no verdict pass, no
tracker claim, no MCP task tools, no transcript. It writes the prompt to a file in the worktree and
reads the process output. `LaneCoordinator.RunFollowupFixLanesAsync` then infers success from
`result.Merged`.
**Consequence:** "run a stage in a lane" is not a scheduling change. Everything the engine knows how to
do to a session — bill it, cap it, judge it, resume it — has to reach into a worktree first. **That is
the actual C1 work item**, and it is why the lanes plan spends a whole stage (L3) on it before the
scheduler (L4) exists.

**C5. `merge --ff-only` plus a force-delete loses a lane's work the moment two lanes land. (S, correctness) — new.**
`MutatingLaneRunner.cs:164` integrates a passing lane with `git merge --ff-only <scratch>`. With one
lane, base never moves and the fast-forward always succeeds. With two, the second lane's base has moved
under it, `--ff-only` fails, and the code records `Error = "fast-forward merge failed after gate
passed"`. Then the `finally` at line 209 runs `Git.DeleteBranch` — which is **`git branch -D`**
(`Git.cs:96`, force). The scratch worktree is removed and the only ref to the lane's commits is
force-deleted: a full session of verified, gate-green work reachable afterwards only through the
reflog.
**Shape:** never force-delete an unmerged scratch branch (`-d`, and keep it on failure with the branch
name in the event and the log); and integrate through a queue rather than a bare fast-forward —
serialize, rebase onto the current base, re-run the merge gate on the rebased tree, then integrate.
Batch-then-bisect (Gas Town's refinery) is the mature form and the stretch goal; **serialize-and-rebase
is correct on day one**.

**C6. A lane worktree has no `.conductor/`, so a lane session has nowhere to write. (S, blocking) — new.**
`StateDir` is `Repo/.conductor` (`PlanConfig.cs:109`), and `.conductor/.gitignore` is `*`. A fresh
`git worktree add` therefore produces a tree with **no state directory at all** — so the moment a real
session (C4) runs in a lane, it either finds nothing or creates a *second* `run.db`, splitting the run's
ledger in half. Today's lanes never hit this because they never touch the store.
**Shape:** a lane resolves state through a redirect to the run's canonical store, exactly as Gas Town's
worktrees do (`.beads/redirect`). A1's state home makes this natural — the catalogue is outside the tree
already, so a lane needs only the run id and the pointer. **A1 therefore gates the lanes plan**, which
is why it is in the core plan and not the second.

**C7. Nothing isolates ports, dev servers, or environment. (M) — new.**
Conductor's own control plane picks a port per run (4317/4318 on this machine — a documented hazard in
the Sarban promptExtra), and the owner's parallel work is *websites*: two lanes each running
`npm run dev` collide on 3000 before any git conflict exists. The ecosystem's answer is per-task env
injection (Emdash's `$PORT`) plus per-worktree setup/teardown; conductor has `setup`/`teardown` only at
the *plan* level, and no per-lane env at all.
**Shape:** per-lane env injection (a lane index, a base port + offset, a state dir, a scratch DB name)
and per-lane `setup`/`teardown` commands that run inside the worktree — the place to `npm ci`, copy a
`.env`, and claim a port. Declare it, log it, and put it in the lane's own event so a failure is
attributable.

**C8. Path claims are declared, never detected. (S) — new.**
`PathClaimTracker.TryClaim` arbitrates the `pathClaims` a plan *declared* per stage
(`LaneCoordinator.cs:52-58`). If the plan is silent — and most are — two lanes can be dispatched onto
the same files, and the collision surfaces only as a merge-gate failure after both have been paid for.
**Shape:** keep declared claims as the cheap fast path, and add real detection with
`git merge-tree $(git merge-base A B) A B` — before dispatch (would these two stages fight?) and again
before queueing a merge. It costs one git call and no working tree.

**C9. Worktree lifecycle has no ceiling, no budget, and uses the slow removal path. (S) — new.**
`Git.WorktreeRemove` is `worktree remove --force`, called in a `finally`, with `IOException` and
`UnauthorizedAccessException` swallowed — which on Windows is exactly what a locked build output
produces, so a failed removal leaves an orphaned worktree and no record of it. Nothing prunes, nothing
counts, nothing warns. At ~0.5GB per tree and a practical ceiling of 8–10, an unattended parallel run
can fill a disk silently.
**Shape:** delete the directory then `git worktree prune` (measurably faster than `worktree remove`);
count live worktrees and refuse to exceed `maxConcurrentStages` + slack; a `conductor worktrees` verb
that lists and reaps; and a startup sweep for orphans left by a killed run.

---

## D. Autonomy — the engine should need the human less

*Notes: "internal runner and planner", "conductor auto mode", "event sourced advisor", "monitor
session sits inside", "need for the human should be less".*

**D1. The supervisor is real but external. (M)**
SF5 shipped `conductor watch` — blocks on a wake set, exit 0 wake / 10 heartbeat / 1 can't-arm, with a
`supervisor` block naming a `--hook` command and standing orders. Its own doc-comment prices the
alternative correctly: a polling babysitter spends ~95% of its budget on "still running" ticks that
every later tick pays for again. The *shape* is right and event-driven, exactly as the owner asked.
What is still outside: the hook shells out to a model the engine doesn't own, doesn't budget, doesn't
record in `costs`, and can't show in the Face.
**Shape:** an in-process supervisor consuming the event stream, using the configured agent provider,
with its own line in `costs` (the `advisor` category already exists), briefs written to the store, and a
Face surface. Keep `--hook` for people who want their own. **Worth more once lanes exist** — N
concurrent sessions is precisely when a human stops being able to watch.

**D2. Make the advisor event-sourced. (S–M)**
`VerdictEngine.Advisor.cs` prices itself at a flat `0.0005 × seconds` — an estimate, not a measurement
— and is invoked on a bad session ending. The event log (`events` table, 4,199 rows on this repo;
`RunStateProjection` / `TaskGraph` / `HealthMetrics` already fold it) is a far better input than a
freshly composed context.
**Shape:** advisor reads the folded projection; record its real token/cost rows. Cheap, and it makes D1
mostly a scheduling problem.

**D3. Auto mode. (L — next era, parked on purpose)**
`IPlanner` / `CheckpointPlanner` exist but only decompose a checkpoint into advisory sub-tasks (B9.2).
A mode where conductor plans its own next stage from a goal collides with the project's own principle
that the checkpoint table is the verified contract. Scope it, don't start it in the same era as C1.

---

## E. Evidence and communication

*Notes: "evidence seeing and knowing" (opened the session), "telegram messages", "remote friendly /
Claude remote".*

**E4. The session result has no contract, and every downstream surface pays for it. (M) — the highest-leverage item in this theme, and it lands first.**
`SessionRunner.ExtractSessionResult` (`SessionRunner.cs:482`) takes whatever prose the agent wrote after
`SESSION-RESULT:`. Observed: 600+ words of unbroken narrative. Every consumer then improvises a
different mutilation — Telegram cuts at 700 chars (`RunLoop.Plumbing.cs:244`), the advisor at 1200
(`VerdictEngine.Advisor.cs:24`), `LessonsBattery` pastes a truncated copy into the next prompt, the
digest and `REPORT.md` take their own slices. The same paragraph is stored, re-sent and re-cut four
ways.
**Shape:** conductor owns the format — headline (≤15 words), ≤3 outcome bullets, changed artefacts as
links, evidence paths, explicit gaps. Prose goes to the handover, which is where prose belongs.
**This is the output-brevity idea done in the one place it is safe:** the format is ours, so the parse
contract cannot drift under us, and it cuts output tokens, Telegram noise, battery rent and advisor
input in one change. **Risk, and it is real:** `FollowupParser` and the verdict pass read this text, so
the contract must be introduced **with** their parsers in the same checkpoint, and with the session
template that teaches it.

**E1. Evidence has no first-class existence. (M)**
Today: `conductor task --done <id> --evidence <string>` stores a free-text field; `AuditCommand` scans
`docs/evidence/<stage>` and `.conductor/evidence/<stage>` for `*.txt` at replay time. That is all.
There is no evidence *event*, no file watcher, no artifact registry, no notification when an agent
produces a screenshot. The owner's actual use case — conductor is building a website, the agent takes
screenshots, and a *second agent* has to be hired to notice and forward them — is unsupported.
**Shape:** an evidence artifact model (path, kind, checkpoint, session, sha, created-at) written as an
event when the agent registers one or when a watched directory gains a file; an Evidence surface in the
Face; and an evidence hook on the notification path (→ E2). This is the note the session opened with,
and it is the largest gap between what the owner does and what the engine knows.

**E2. Telegram is one `sendMessage` call and knows nothing about the project. (M)**
`TelegramService.cs:420` — a single `sendMessage` with `parse_mode=HTML`. No `sendPhoto`, no
`sendDocument`, no threads, no albums, no `disable_notification`, no message editing. Long bodies are
not chunked against Telegram's 4096-char limit.

**Evidence — the owner's own client-site run (2026-08-02, 15 sessions, $97.46), transcribed
2026-08-04.** Nine defects, each traced to a line:

1. **The session number is printed twice.** `TelegramService.cs:391` `IdentityLine` stamps
   `<i>{plan.Name} · s{SessionCounter}</i>` onto *every* outgoing message at `SendAsync`, and the body
   from `Messages.cs:19` opens `<b>s{sessionNumber} {outcome}</b>`. Worse, the two numbers come from
   **different sources** — the stamp reads the live `_state.SessionCounter`, the body reads the
   record's — so on a late push they can disagree.
2. **The stage is a bare letter.** `— G`, `— S`, `— H`. `stage` is passed as an id and the title is
   never looked up. Nothing in the message says what G was.
3. **`result:` is 700 characters of raw agent prose, cut blind.** `RunLoop.Plumbing.cs:244` —
   `Trunc(rec.ResultSummary, 700)`. That is the `…` landing mid-word in the owner's chat. See **E4** —
   the fix is upstream of Telegram.
4. **Rollovers say nothing at all.** Two of fifteen sessions pushed `gates: (not recorded)` and **no
   result line** — and one of them had in fact shipped a PR. This is **B3** rendered on the owner's
   phone.
5. **No progress, ever.** No `12/24 checkpoints`, no stage progress, no ETA. Fifteen messages and not
   one says how far along the run is.
6. **No project identity.** Plan name only. The repo appears exactly once, in the completion line; the
   branch never appears. With two runs on one machine (a documented hazard of this setup) the messages
   are ambiguous by construction — and with N lanes it gets N times worse.
7. **Nothing is a link.** PRs, commits and evidence paths are named in prose and linked nowhere —
   `Reporter.cs:443` already knows how to build remote URLs from a commit sha.
8. **Money is four decimal places and no context.** `cost: $5.7748 | run: $67.7419`. No cap, no
   headroom, no burn rate — all of which the engine already computes for the Face.
9. **The completion push buries the outcome.** `VerdictEngine.Completion.cs:67` — the plan is named
   twice and the *engine build string* gets more room than the result. No total cost, no checkpoint
   count, no duration, no link to `REPORT.md`.

**Shape:** a message-composition layer with per-event templates (owner-editable — `TemplatesDir`
already exists for prompts), one identity block not two, repo/branch/stage-title/checkpoint in every
push, links for commits and PRs, a progress line, money with headroom, `sendPhoto`/`sendDocument` so
E1's evidence actually arrives, thread-per-run, severity → notify-vs-silent, and 4096-char chunking.
**Land defects 1–5 as a first cheap checkpoint** — a handful of lines each, and they are the ones
making the feed unreadable today.

**E3. Remote observability. (M, depends on A1/E1)**
"the monitor session sits inside and is remote friendly — can I see it in Claude remote?" The control
plane is an HTTP server on a loopback port with SSE. A remote view means either an outbound-only push
(Telegram already is one; `WebhookNotifier` exists) or an authenticated tunnel — the latter is a
security surface this project has so far deliberately avoided. **Recommend the push path** and decide
it explicitly: make the run's state, evidence and briefs readable from a chat client rather than
exposing the control plane.

---

## F. The surfaces — Go TUI

*Notes: "UI has a big glitch you cannot read long text", "glow and other bubble tea projects for
UI/UX best practice".*

**F1. The Face hand-rolls every component bubbles already ships. (L) — root cause of the long-text glitch.**
`face-go/go.mod` requires exactly three things: `charm.land/bubbletea/v2`, `charm.land/lipgloss/v2`,
`x/term`. **`bubbles` is not a dependency at all** (verified 2026-08-04). So the Face hand-rolls
scrolling (`ScrollOffset` in `TranscriptModel`, plus `consoleScroll`, `reportScroll`,
`knowledgeScroll`, `ownerQueueScroll`, `processSelected` — one bespoke scroll integer per surface), a
text area (`widgets/editor.go`), tables, lists and pagination. Bubbles provides **viewport, textarea,
table, list, paginator, help, key, spinner, progress, filepicker, timer** — every one of those is
re-implemented here, each with its own bugs, and "can't read long text" is precisely the failure a
hand-rolled scroll region has and `viewport` does not.
**Shape:** adopt `bubbles` (v2, matching bubbletea v2) and migrate surface by surface — Report and
Knowledge first (pure scroll → `viewport`: smallest diff, biggest relief), then Templates/Knowledge
input → `textarea`, then the list-shaped tabs → `list`/`table`. The golden tests (`golden_test.go`,
`frame_invariant_test.go`, `glitch_sweep_test.go`) are already the regression net this migration needs.
Do not rewrite the Face; replace its primitives.

**F2. Study glow and peers, once, and write down the rules. (S — before F1 lands)**
Glow is the canonical answer to "long markdown in a terminal": bubbles `viewport` + `glamour` with
`pager`-shaped keys. Worth reading alongside it: **soft-serve** (multi-pane app structure),
**gh-dash** (dense tabular dashboards, closest to our Kanban/Report), **lazygit** (not bubbletea, but
the reference for a many-panel git TUI). We already depend on glamour transitively and use it in
exactly one place (`markdown.go`, detail panes only).
**Deliverable:** a short ADR of adopted conventions — pager keys, focus model, help via `bubbles/key` +
`help`, one scroll idiom — that F1 then implements. `docs/dev/adr/` is the right home and already has
four.

**F3. Markdown rendering is used in one pane and hard-codes the dark theme. (S)**
`renderMarkdown` sets `glamour.WithStandardStyle("dark")` unconditionally, while `widgets/theme.go`
already has a theme system. Report, Knowledge and handover panes render markdown as plain text. Unify
after F2 decides the rules.

---

## G. Code and architecture hygiene

*Owner's note: "go face — I only buy code I don't know the architecture of; assess it, don't document
it. Same for the backend: separation of concerns, readable code, debuggable observability."*
*And this pass: "I want the code to be browsable and readable properly. At the moment some projects'
code files are just there with no organisation — and this is something I posted for my CV and use
daily."*

Baseline, measured 2026-08-04: **Go** 83 files / 14,194 non-test LOC. **.NET** 380 files / 38,398 LOC
in `src`, 174 test files / **1,547 test methods**. `go vet` clean; `gofmt -l` flags one file
(`internal/widgets/ticker_test.go`). Neither codebase is in bad shape — the findings below are about
*shape*, not rot. G7 is about what a reader meets before they reach the code at all.

**G1. Go: `tui.Model` is a 116-field god object and `Update` is an 80-case dispatch. (L)**
Verified: `model.go:125-322` is a single flat struct of **116 fields** — `paletteQuery`,
`planEnumCustom`, `timelineHistorySet`, `reportScoresErr`, `knowledgeMode` all siblings — and
`update.go` is **826 lines with 80 `case` arms**. The bubbletea idiom is **composed models**: each tab
is its own `tea.Model` with its own state, `Update` and `View`, and the root delegates. That is also
what makes F1 tractable — you cannot drop a `viewport` into a tab whose scroll state is a shared
integer on a struct 400 lines away.
**Shape:** extract per-tab models (`tab_*.go` files are *already* the right partition — the state just
hasn't followed the code), root `Update` becomes a dispatch to the focused model. Do this **with** F1,
not after: they are the same refactor. `plan.go` at 1,000 lines is the other candidate and can follow.

**G2. .NET: files are kept small by splitting, not by decomposing. (M)**
`ControlPlaneDto` is **30 partial files**. `ControlPlaneServer` 11, `ConductorEvent` 11,
`VerdictEngine` 8, `TelegramService` 8, `SqliteRunStore` 7, `SessionRunner` 5, `RunLoop` 5. No file
exceeds 500 lines, which looks healthy and isn't: the *type* still has every responsibility, the
compiler enforces no boundary, and "where does X live" needs a grep. Real seams are already there where
it counts (`IRunStore`, `IEventSink`, `IAgentProvider`, `IPromptBattery`, `IPlanner`, `IQaPolicy`,
`IWorkflowResolver` — DI wired properly in `ConductorHost`).
**Shape:** split by *responsibility* where the partials are hiding one — the DTO pile is really
per-endpoint contracts and wants a folder per feature, not 30 files of one type. Targeted, not a
rewrite, and it is the item that most directly answers "browsable".

**G3. .NET: one assembly holds CLI + orchestration + HTTP + Telegram + store + providers. (M)**
`Conductor.csproj` is everything; `Conductor.Planning` is the only extracted piece. Nothing prevents a
command from reaching into `SessionRunner`, or the store from formatting console output. 92 files
declare a `public static class` (`Git`, `SatelliteRepos`, `LiveMetrics`, …) — pragmatic, but static
means unfakeable, and it is why some tests reach for real git.
**Shape:** extract `Conductor.Core` (domain + orchestration + store, no Spectre, no HTTP) and leave
`Conductor` as CLI + hosting. The project reference direction then *enforces* the layering that today
is only convention — and G8 makes the enforcement fail loudly.

**G4. Git safety is prose, not code. (M — carried over from NEXT-FEATURES, still open)**
`RunLoop.Control.cs`'s `WarnOnBranchPattern` is the *only* enforcement of `branchPattern`: a session on
the wrong branch is told so and proceeds. There is **no `requireCleanTree` anywhere in the tree** — no
post-session clean-tree assert, no push assert, no per-checkpoint commit-convention check, nothing
refusing a detached HEAD or a wrong remote, no force-push guard. The owner's "git worry" note is this.
**Blocks the lanes plan** — multi-repo and parallel worktrees make every one of these N× worse, and
C5 shows what a missing guard costs when branches are being force-deleted.

**G5. Go module hygiene. (S)**
`glamour` is imported directly by `internal/tui/markdown.go` but marked `// indirect` in `go.mod`; the
dependency graph carries **two lipgloss majors** (`charm.land/lipgloss/v2` direct,
`github.com/charmbracelet/lipgloss` v1 pulled in by glamour). Run `go mod tidy`, pin the intent, fix
the one `gofmt` miss.

**G6. `docs/dev/NEXT-FEATURES.md` open items that are still real. (S each)**
Carried forward so nothing is lost: branch-hygiene enforcement (→ G4), the processes lane (nested
agent-tool tree), `RepoMapBattery` + definition-of-done recap (→ B7), plan import from scratch, gate on
unacknowledged handover gaps, and **SF7.1's filed MCP bug** — `SessionRunner.Mcp.cs` `WireMcpServer`
writes a config containing *only* `conductor-tasks`, replacing rather than merging the operator's own
MCP servers, and in at least one shipped harness conductor's own tools arrive **deferred** so the agent
must search for `task_update` before it can claim anything. SF6.1 shipped the prompt-side workaround;
the engine-side fix is unowned and belongs in this era.

**G7. The front door is not browsable, and it is the first thing a reader sees. (M) — new.**
Measured at the repo root: `AGENTS.md` is **80KB** of append-only session handoffs — twenty-three
`## Resume here` / `## Previous resume point` sections stacked newest-first, going back to 2026-07-09,
with the current era's entry buried among nine superseded ones and the C# standards, the read order and
the architecture notes interleaved between them. Beside it: `CONDUCTOR-WORKGRAPH.md` (44KB) which is a
**divergent duplicate** of `docs/dev/CONDUCTOR-WORKGRAPH.md` (different hashes — the two have drifted),
plus `SARBAN-CORE-TRACKER.md` and `SARBAN-FACE-TRACKER.md` left loose at the root after the era closed.
There is **no `ARCHITECTURE.md`** — the only architectural map is a section inside the 80KB handoff log,
and the newest plan bundle on another branch (`ci-health/`: plan, tracker, gates, templates, README in
one folder) shows the owner has already invented the better convention without applying it here.
**Shape:** `AGENTS.md` becomes current-state only, with superseded handoffs archived under
`docs/history/handoffs/`; a real `ARCHITECTURE.md` carries the map (assemblies and their allowed
dependency direction, the session lifecycle end to end, the seams and what implements them, "where do I
add X"); the root keeps only what a newcomer needs; era artefacts live in one folder per era
(`plans/<era>/`), as `ci-health/` already does; the duplicate WORKGRAPH resolves to one file.
**Why it is a real item:** the owner uses this repo daily and shows it. Structure is part of the
product, and every session of every future run pays cache-read rent on whatever the read order points
at.

**G8. The layering exists as documentation and nothing enforces it. (S) — new.**
`AGENTS.md` has a *Command/Query/Event layering (F5+)* section describing the intended direction, and
`LaneCoordinator`'s own doc-comment cites it as the seam it was extracted along. Nothing checks it. The
industry answer is architecture tests in the normal suite — ArchUnitNET (NetArchTest has been
unmaintained since 2023) — asserting the rules as executable statements: the core must not reference
Spectre.Console or ASP.NET, the store must not format console output, commands must not reach into
orchestration internals.
**Shape:** land the tests **with** G3, so the extraction is verified rather than asserted, and a future
session that reaches across a boundary gets a red gate instead of a review comment. Cheap, permanent,
and it is what makes "I know this architecture" true rather than claimed.

---

## The plan this became

Two plans, sequenced by dependency and by measured value. Full briefs in
`docs/history/CONDUCTOR-KARVAN.md`; the sequencing argument is here.

**Plan 1 — Karvan core, "the engine knows what it did and what it cost"**
`plans/karvan/core.plan.json`, 25 checkpoints, 7 stages.

1. **K1 Foundations** — B3, B9, B8, A3, G5, G6-MCP. Correctness first: B3 must land before any
   measurement in B is trustworthy, and B9 before the cap means anything.
2. **K2 The architecture becomes navigable** — G3 + G8 (extraction *with* enforcement), G2, G7. Before
   A1, so the store's new home is decided once and the boundary is a compiler error rather than a
   convention.
3. **K3 Memory** — A1 (state home, catalogue, migration, build stamp per run), A2. The spine.
4. **K4 Token truth** — B1 (the instrument), B2 (`conductor budget`), B4 (`conductor money`), B5. B7
   follows the instrument; A4 stays parked behind it.
5. **K5 The result contract and the channels** — E4 first (with its parsers), then E2's cheap defects,
   then E1 evidence, then the composition layer that can finally carry a screenshot.
6. **K6 The surfaces read** — F2 (ADR) → F1 + G1 as one migration → F3, protected by the golden tests.
7. **K7 Ship** — docs, CHANGELOG, the B2 re-measure written back into `TOKEN-BUDGET-TUNING.md` (B10),
   owner-gated merge, tag, release.

**Plan 2 — Karvan lanes, "the caravan splits"**
`plans/karvan/lanes.plan.json`, 23 checkpoints, 7 stages. Authored now, launched after plan 1's
re-measure.

1. **L1 Git safety is code** — G4, plus the worktree lifecycle primitives (C9) and the C5 branch-loss
   fix. Nothing parallel should exist above an engine that force-deletes unmerged branches.
2. **L2 Repos are first class** — C2. Repos first, because separate trees are the natural isolation
   boundary and it is the owner's actual case.
3. **L3 A lane is a real session** — C4, C6, C7, C8. The unglamorous stage that makes L4 possible.
4. **L4 The scheduler and the queue** — C1, C5's queue, cross-lane budget, and a scratch-repo
   rehearsal before it touches a real plan.
5. **L5 The Face renders a fleet** — N live sessions, per-lane cost, lane-aware kanban.
6. **L6 Autonomy** — D1, D2, and C3 optional. Worth most once one human cannot watch N lanes.
7. **L7 Ship** — the owner's own two-repo run as the acceptance, then release.

**One warning, unchanged from the first pass.** This is a large era: 48 checkpoints across two plans,
against Sarban's 26 + 24 at $360 + $297. C1 alone is a plan, and it is deliberately last in the second
one.

---

## Decisions the owner has to make

Seven (the caveman question is gone — scrubbed on instruction). Each changes what gets built. A
recommendation is given for every one, and the plan as authored *implements* the recommendation, so
silence is a valid answer.

**D-1 · How many plans?** *Recommendation: two, as above.* C1 is a plan on its own, and pairing the
riskiest item with the hygiene refactor risks losing both. Splitting also forces a re-measure between
them, which is exactly what `TOKEN-BUDGET-TUNING.md` §7 step 5 prescribes.

**D-2 · Telegram — cheap fix or real surface?** *Recommendation: both, in that order.* Defects 1–5 as
one early checkpoint (a few lines each, immediate relief), then the composition layer. Skipping the
full version leaves E1's screenshots with no way to reach the owner, which was the note the whole
session opened on.

**D-3 · Impose a structured session-result contract (E4)?** *Recommendation: yes, and early.* One
change fixes Telegram readability, four inconsistent truncations, battery rent and advisor input, and
cuts output tokens. The risk is bounded but real — the parsers must move in the same checkpoint.

**D-4 · Where does centralized state live (A1)?** *Recommendation: machine-level default
(`%LOCALAPPDATA%/conductor`) + `CONDUCTOR_STATE_HOME` override, and explicitly NOT a synced folder* —
SQLite on OneDrive/Dropbox corrupts under concurrent writers, and this machine already runs two engines
at once. `.conductor/` keeps the repo-tracked deliverables and per-run scratch. Migration imports
existing `run.db` files rather than orphaning them.

**D-5 · Remote observability posture (E3)?** *Recommendation: push-only.* The control plane is loopback
with no auth story; exposing it is a security surface this project has deliberately avoided. Richer
Telegram plus a shareable report covers the actual need — "can I see it from my phone" — without
opening an inbound port.

**D-6 · What does "parallel" mean first (C1/C2)?** *Recommendation: repos first, and the owner's own
description settles it.* Two client sites or two pages of the studio site are two trees. Separate
working trees are a natural isolation boundary — no merge ordering, no path claims, no verdict
ambiguity. Same-repo stage parallelism inherits all of that and follows behind
`maxConcurrentStages`, default 1.

**D-7 · GitHub sync direction (C3)?** *Recommendation: one-way, off by default, and only after G4.*
The tracker is the verified contract; making it an eventually-consistent mirror of a GitHub board is
the failure this project's own anti-pattern A16 names. Push `task --done` → Issue comment/close and the
report → PR comment. Nothing inbound.

**Not a decision, but the owner should know — and this is now different from the first pass.** The
engine currently on PATH is **`0.3.1-alpha.0.6+98a426af63d6.dirty`**, built from commit `98a426a`,
which is on **`chore/ci-health`** and **not an ancestor of `master`** — a dirty side-branch build, not
the released 0.3.0. Whatever launches this era will be driven by it unless it is reinstalled from a
clean tree first. "Which engine produced this run" is currently unanswerable from the run record; A1's
catalogue should store the build stamp per run, and a `.dirty` build is worth a warning at launch.
The launch drill in the spec's Appendix C makes the reinstall step one.
