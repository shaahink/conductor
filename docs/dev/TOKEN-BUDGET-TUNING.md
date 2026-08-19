# Tuning conductor's token budget — what the three live runs actually measured

**Written 2026-08-02**, from `run.db` and `conductor.log` of three real runs on one machine:
`conductor` (sarban-face, 41 sessions, complete), `sk-studio` (69 sessions), and `DevContext2`
(graph-v2 autonomous remainder, live at the time of writing).

Every number here is measured. Where the ledger and git disagree, git wins and the discrepancy is
itself a finding (§6).

> **Re-measured 2026-08-05 (K7.1), and the tool won.** `conductor budget` (shipped by K4.2) now
> computes every figure below from the ledger. Run against this repo it **contradicted four numbers
> that were hand-derived in the 2026-08-02 draft**. They are corrected in place — struck through with
> the measured value beside them, never quietly deleted — and marked ⚠. The rule in §7 step 4 was
> also too weak: clearing the *floor* is not enough, and §7 now says why. §9 is this era's own
> numbers. Raw output: `.conductor/evidence/K7/K7.1-budget-raw.txt`.

---

## 1. The two numbers people conflate

| | what it measures | where it lives |
|---|---|---|
| **"keep context under ~150k"** | the size of the prompt **at one turn** | the agent's working discipline |
| **`limits.maxSessionTokens`** | the **integral** of that over the whole session | the plan's limits block |

```
cumulative session tokens  ≈  Σ over turns ( context size at that turn )
```

They are a rate and its area, so a cap is not a context limit. `maxSessionTokens = 20000000` means
roughly **130 turns at a 150k context**, or **~65 turns at 300k**. Conductor picked 8M for its own
run on exactly this reasoning — the commit that made the ceiling real (`616b7ba`) reports that
splitting one measured **164-turn** session into three took about half off the bill. 8M ≈ 55 turns at
150k.

The data supports the 150k figure as a real operating point. DevContext2 session #1 logged **172,654
uncached input tokens against 18,569,638 cache reads** — a ratio only reachable if a large prefix is
re-sent every turn, which is what a 150k+ context does.

## 2. The tail is not more expensive per token — it is less productive per token

Blended rate, measured across sessions of very different sizes:

| session | tokens | cost | $/M |
|---|---|---|---|
| DevContext2 #17 | 11.46M | $8.42 | 0.735 |
| DevContext2 #1 | 18.81M | $12.67 | 0.674 |
| DevContext2 #20 | 52.05M | $31.78 | **0.611** |
| conductor #41 | 2.48M | $1.36 | 0.548 |

Flat, if anything **cheaper** as sessions grow — cache reads dominate and bill at a steep discount.

So the saving from a ceiling is **not** a better price per token. Cost per *turn* rises with context
(a turn at 300k costs twice a turn at 150k) while the work delivered per turn does not. The waste is
in **work per token**, and that is what the tables in §3 measure.

## 3. A cap pays 3–4× — but only above the repo's floor

Tokens per **delivered checkpoint**, pre- and post-cap, aggregated so that a rollover's tokens and
its successor's checkpoint fall in the same bucket:

| run | cap | tokens / checkpoint | verdict |
|---|---|---|---|
| conductor (sarban-face) ⚠ | uncapped (sessions 1–8) | ~~58.1M ($41.9)~~ → **26.5M** | |
| conductor (sarban-face) ⚠ | 8M / 0.76 (sessions 9–41) | ~~14.7M ($11.7)~~ → **17.0M** | ~~4.0× better~~ → **1.6× better** |
| conductor (sarban-core) | uncapped | 19.4M ($13.85) | the run the 32M ceiling was derived from |
| **conductor (karvan-core)** | **32M / 0.70** | **15.5M ($12.17)** | **best of the three · 0 rollovers** (§9) |
| sk-studio stage A | uncapped | 20.0M | |
| sk-studio stages C/E/F | 6M / 0.7 | **25–54M** | **worse than uncapped** |
| sk-studio stages G/H | 9M / 0.7 | 12.8–15.3M | better; stage H **0 rollovers** |

⚠ **Correction (K7.1).** The two sarban-face rows were hand-aggregated on 2026-08-02 and both were
wrong. `conductor budget` splits that run at the session where the ceiling first appears and divides
each window's own tokens by its own closed checkpoints: **1–8 uncapped → 7 costed sessions, 158.9M
tokens, 6 checkpoints = 26.5M**; **9–41 capped → 33 sessions, 238.3M tokens, 14 checkpoints =
17.0M**. The old 58.1M/14.7M pair came from summing a whole run's dollars against a checkpoint count
taken from a different window, which flatters the cap by 2.5×. The cap did help — but it bought
**1.6×, not 4.0×**, and that is the number any repo copying this page should plan against.

One honest caveat on the denominator: the tool counts checkpoints from `sessions.newly_done`, and
that column misses a checkpoint claimed during a Verify or Audit session (bug #10, since fixed). Two
sarban-face checkpoints — `SF4.1` and `SF5.1` — are DONE in the tracker and attributed to no session,
so the capped window's true figure is somewhere in **17.0M (14 ckpt) … 14.9M (16 ckpt)**. Either way
it is not 14.7M-against-58.1M, and either way the cap's multiple is under 2×.

sk-studio stage F is the cautionary case: **9 sessions, 53.8M tokens, one checkpoint**, 7 of the 9
rolled over. The cap did not save tokens there; it bought churn.

**The rule.** A cap must sit above the repo's **session floor** — the tokens one session needs to
orient, do a unit of work, and commit. Below the floor it inverts: nothing ever lands in one session,
every session pays orientation again, and the total rises.

### Measuring a repo's floor

The smallest session that ever closed a checkpoint. For DevContext2 (16 such sessions):

```
min 13.81M · median 25M · max 52.05M
```

```sql
SELECT s.number, ROUND((c.tin+c.tout+c.tth+c.tc)/1e6,2) AS Mtok, s.newly_done
FROM sessions s JOIN (
  SELECT session_number n, SUM(tokens_in) tin, SUM(tokens_out) tout,
         SUM(tokens_think) tth, SUM(tokens_cache) tc
  FROM costs WHERE category='agent' GROUP BY session_number) c ON c.n = s.number
WHERE s.newly_done IS NOT NULL AND s.newly_done <> '' ORDER BY Mtok;
```

DevContext2's floor is **~2× conductor's and ~2.5× sk-studio's**, because its sessions build a large
.NET solution, run gate batteries, and analyse multi-repo canary poles. Copying conductor's 8M here
would have rolled over every session before anything could land — stage F, exactly.

## 4. The dial nobody names: wrap-up headroom

`softBreakRatio` fires the cooperative nudge at `ratio × cap`. What matters is not the ratio but what
is left after it:

```
headroom = (1 − softBreakRatio) × maxSessionTokens
```

That headroom has to cover the **wrap-up** — land the sub-task, commit, write the handoff. Measured
(session's final tokens minus its nudge threshold, for sessions that did *not* roll over):

| run | wrap-up spend |
|---|---|
| sk-studio stage H (5 sessions) | 1.03M · 1.63M · 1.91M · 1.97M · 2.01M |
| DevContext2 #27 | **2.63M** (nudge 12:23:12 → clean exit 12:25:27, 3 commits, G9.1 closed) |
| conductor sarban-face 8M | **1.37M** median of 20 nudged-and-clean sessions |
| conductor karvan-core 32M | **1.89M** median of 6 nudged-and-clean sessions |

Measure it as `BudgetAnalyzer.Measure` does (`src/Conductor.Core/Budget/BudgetAnalyzer.cs:161-165`):
the session's final tokens **minus the tokens live at the moment the rail actually fired**, over
sessions that were nudged and then ended clean — not minus `ratio × cap`. The rail rides a tool call
and lands on the first turn *past* the threshold, so the two differ: for karvan-core the measured
nudge point is 22.5M against a configured 22.4M, and subtracting the configured number instead
inflates the wrap-up from 1.89M to 2.27M.

**The wrap-up cost is absolute, and scales with context size — not with the cap.** So expressing the
reserve as a *ratio* shrinks it exactly when it must stay constant. Headroom against observed
rollover rate:

| configuration | headroom | wrap-up needed | rollover rate |
|---|---|---|---|
| sk-studio 6M / 0.7 | 1.8M | ~1–2M | **67%** (31 of 46 across stages B/C/E/F) |
| conductor 8M / 0.76 ⚠ | **1.93M** (1.4× wrap-up) | **1.37M measured** | **30%** (10 of 33 capped sessions) |
| sk-studio 9M / 0.7 | 2.7M | ~1–2M | 22%; **stage H 0 of 6** |
| DevContext2 20M / 0.7 | 6.0M | 2.63M measured | 0 of 1 |
| **conductor 32M / 0.70** | **9.46M** (5.0× wrap-up) | **1.89M measured** | **0 of 22** |

**Headroom ≥ ~1.5–2× the observed wrap-up is where rollovers stop dominating.** The sarban-face row
sat at 1.4× and rolled 30%; the karvan row sits at 5.0× and rolled none.

⚠ The `0.75` in this row was the plan's declared ratio; the rail's measured firing point over 20
sessions was 6.07M against an 8M cap, i.e. **0.76**. Prefer the measured nudge point — that is what
the sessions actually experienced.

## 5. What a rollover actually costs

A hard ceiling cross is a **kill mid-turn** (`SessionRunner.OverSessionTokenBudget` → `EndOnBudget`
→ `agent.Kill()`). The working tree survives; the in-flight reasoning does not; the engine writes a
synthetic handoff. The agent's own commit step never runs — *unless the soft break got there first*.

So the soft break is the **only** path that ends a capped session on its own terms. It is delivered
once, by a `PostToolUse` hook riding a tool call. Miss it and the hard kill is the outcome.

Worth knowing: that hook once died silently on Windows because a backslash path was eaten by the
shell — fixed in `616b7ba`, with forward slashes asserted in a test. A silently dead hook makes
*every* rollover a hard kill, and the only symptom is a cooperative rail that looks wired and does
nothing.

## 6. Correction — the ledger cannot tell you whether a rollover committed

The obvious query says no rollover ever committed:

| run | RolledOver | with `commit_count` > 0 | with `newly_done` |
|---|---|---|---|
| sk-studio | 34 | **0** | **0** |
| conductor | 11 | **0** | **0** |

**That is an accounting artifact, not the truth.** `SessionRunner.cs:411` sets
`rec.Outcome = RolledOver` and returns *before* the verdict pass that populates `NewCommits` (whence
`commit_count`) and `newly_done`. The engine never looks.

Git ground truth over each rolled-over session's own `started_utc..ended_utc` window, excluding
`chore(conductor):` bookkeeping:

| run | rolled over | left ≥1 agent commit | agent commits |
|---|---|---|---|
| sk-studio | 34 | **19 (56%)** | 28 |
| conductor | 11 | **10 (91%)** | 20 |

So rollovers usually *do* commit. What is always zero is the **record** of it. Anyone reading the
board after a rollover sees a session that apparently produced nothing, on every rollover, whether or
not that is true — which is a good way to conclude the cap is destroying work when it is not.

The aggregate tables in §3 are unaffected: tokens and checkpoints were summed over the same session
sets, so a rollover's tokens and its successor's checkpoint credit stay in the same bucket.

## 7. How to set the numbers for a new repo

1. **Measure the floor** — the query in §3, over an uncapped stretch. No cap below it, ever.
2. **Measure the wrap-up** — final tokens minus nudge threshold, for sessions that ended clean. Or
   assume ~1–3M and correct later.
3. **`maxSessionTokens` = nudge + 2× wrap-up**, rounded up to a configurable grain — where the nudge
   is step 4. It must also be well under the uncapped mean or there is nothing to save.
4. ⚠ **`softBreakRatio`** = chosen so the *nudge* clears the **largest session that ever closed a
   checkpoint** — `max(1.05 × largest closer, floor + wrap-up)` — and the *headroom* stays ≥1.5× the
   wrap-up.

   **This step used to say "clear the floor", and that was wrong.** sarban-face's nudge sat at 6.07M
   against a 4.66M floor — **1.30× the floor, comfortably clear of it** — and the cap still bought
   30% rollovers, because 6.07M is **0.84× the 7.26M median closing session**. The floor is the
   *smallest* session that ever closed anything; a nudge that only beats the floor still interrupts
   the median session before it could have finished naturally. `conductor budget` prints
   `nudge vs floor` and `vs median closer` side by side for exactly this reason, and raises
   `NUDGE BELOW THE MEDIAN CLOSER` when the second one drops under 1.0×.

5. **Re-measure after a stage.** sk-studio needed one correction (6M → 9M) and was worse than
   uncapped until it made it.
6. **Or skip 1–5 and run `conductor budget`** (K4.2). It does all of the above from the ledger, for
   every run in the catalogue, and prints the `limits` block to paste. `BudgetAnalyzer.cs:223-227`
   is this section, executable. Where this page and that tool disagree, the tool is the one reading
   the data — §3 and §4 above carry four corrections it found.

Applied to DevContext2 — floor 13.81M, wrap-up 2.63M:

```jsonc
"limits": {
  "maxSessionTokens": 20000000,   // floor 13.81M + 2.3× wrap-up
  "softBreakRatio":   0.75        // nudge 15.0M (1.09× floor) · headroom 5.0M (1.9× wrap-up)
}
```

`0.7` was the first setting and it worked — session #27 took the nudge at 14.13M and exited clean at
16.76M with 3 commits and `G9.1` closed. It was raised to `0.75` only because 14.0M sits **0.19M
above the floor**, so the nudge would fire on essentially every session before it had a chance to
finish in one piece. One data point; re-measure before trusting the change.

## 8. The part no limit can fix

The cap is downstream of context discipline. DevContext2's floor is 13.81M *because* its sessions
carry large contexts — whole-file reads, build output, multi-repo analysis. Shrink what a session has
to hold — delegate searches so file dumps never enter the main context, keep checkpoints small
enough to land in one session, let `batteryCollapse` stop paying an agent to run gates the engine
runs anyway — and the floor drops. Only then does a lower cap become safe, and only then does the
1.6× that conductor got become available to a repo like this one.

## 9. This era's own numbers — 32M / 0.70, measured (K7.1, re-measured at K7.2 session 29)

Produced by `dotnet run --project src/Conductor -- budget` against this repo's own catalogue, not by
hand. Raw output: `.conductor/evidence/K7/K7.1-budget-raw.txt`, re-run at
`.conductor/evidence/K7/K7.2-ship-rehearsal.md`.

**Every karvan-core figure below is as of session 29 and moves with every session that follows it.**
K7.1 wrote 22 sessions / 323.6M / 23 checkpoints / 14.1M; four sessions later the same command says
26 / 371.7M / 24 / 15.5M. Nothing regressed — the run got longer. Re-run `budget` and `money` when
the era is tagged rather than carrying these forward; the numbers here are a measurement with a date
on it, not a constant.

| run | ceiling | sess | tokens | ckpt | tok/ckpt | floor | median closer | rollover | wrap-up |
|---|---|---|---|---|---|---|---|---|---|
| sarban-core | uncapped | 28 | 504.5M | 26 | 19.4M | 5.52M | 17.5M | 0 | — |
| sarban-face | uncapped (1–8) | 7 | 158.9M | 6 | 26.5M | 14.7M | 23.4M | 1/7 (14%) | — |
| sarban-face | 8M / nudge 6.07M | 33 | 238.3M | 14 | 17.0M | 4.66M | 7.26M | **10/33 (30%)** | 1.37M (n=20) |
| **karvan-core** | **32M / nudge 22.5M** | **26** | **371.7M** | **24** | **15.5M** | **3.27M** | **13.8M** | **0** | **1.86M (n=7)** |

`sess` is **costed** sessions — the ones that recorded agent tokens, which is the denominator every
rate in the row uses (`BudgetCommand.cs:135`, `BudgetWindow.RolloverRate`). karvan-core has 28
sessions in all; sessions 3 and 4 died `AgentError` at 3.0 and 2.9 minutes with zero agent tokens
(queried 2026-08-05), so "26" is not a miscount and "zero rollovers in 26 sessions" is the honest
form of the claim.

**The 32M ceiling was the right call and the evidence is the rollover column.** It was derived from
sarban-core — the one run that ran uncapped on this model — and set so the nudge cleared that run's
median closer rather than merely its floor, which is the correction §7 step 4 now carries. Result:
`nudge vs floor 6.90×`, `vs median closer 1.63×`, headroom 9.46M at 5.1× the wrap-up, and **zero
rollovers in 26 sessions** against sarban-face's ten in thirty-three. It is also the cheapest
per-checkpoint window of the three runs — 15.5M and $12.17 against sarban-face's 17.0M — so a *large*
correctly-placed ceiling beat a small one on both churn and cost. Cost per token is not why: all
three runs blend to ~$0.74/M and 98.3% cache reads (`conductor money`,
`.conductor/evidence/K7/K7.1-money-raw.txt`). The saving is work per token, exactly as §2 predicted.

`conductor budget`'s prescription for this run is **32M at 0.85** — the cap is where the measurements
put it, and the ratio could rise, because 9.46M of headroom is 5× a 1.89M wrap-up and the extra 4.8M
is more usefully spent before the nudge than after it (the wrap-up it is measured against is 1.86M).
That is a one-line change to
`plans/karvan/core.plan.json` for whoever runs the next era; it is **not** applied here, because
changing the ceiling mid-run would re-tune the budget under the sessions still running against it.

Two caveats, both measured rather than assumed:

- **n=6.** Only six of twenty-two karvan sessions were ever nudged, so the 1.89M wrap-up rests on six
  observations. The 0.85 prescription is only as good as that sample.
- **Zero rollovers is not the same as zero waste.** Nineteen of twenty-four sessions closed at least
  one checkpoint; five closed none. That is a far better ratio than sarban-face's 14 of 33, but the
  tail exists, and §8 — context discipline — is still where it gets shorter.

---

## 10. The karvansara era's own numbers — 32M confirmed, 0.70 → **0.85** prescribed (KS10.1, 2026-08-15)

Produced by `dotnet run --project src/Conductor -- budget --json` and `dotnet run --project
src/Conductor -- money --json` against this repo's own catalogue — the **deduped** one KS0.1 produced
— and deliberately *not* by the `conductor` on PATH, which is the mid-era snapshot driving this run
(`0.4.1-alpha.0.49+9bf2742`). Every figure below is copied out of the raw output, which is committed:
`.conductor/evidence/KS10/ks10-1-budget-remeasure.json`,
`.conductor/evidence/KS10/ks10-1-money-remeasure.json`. Nothing here is typed by hand.

**As of session 23 of the karvansara run, and it moves with every session after it** — the same
caveat §9 carries, and §9 is the proof it matters: K7.1 wrote 26 costed / 371.7M / 15.5M for
karvan-core, and re-run today the same command says **30 costed / 403.9M / 16.8M**. Nothing
regressed; the run got longer.

### The measurement

| run | ceiling | costed sess | tokens | ckpt | tok/ckpt | floor | median closer | max closer | rollover | wrap-up |
|---|---|---|---|---|---|---|---|---|---|---|
| karvan-core (re-measured today) | 32M / nudge 22.5M | 30 of 32 | 403.9M | 24 | 16.8M | 3.27M | 13.82M | 26.06M | **0** | 1.86M (n=7) |
| **karvansara-core** | **32M / nudge 22.4M** | **12 of 21** | **155.2M** | **28 ⚠ see below** | **5.54M ⚠** | **2.95M** | **22.08M** | **25.56M** | **0** | **2.28M (n=3)** |

Money, same store: karvansara-core is **$115.89** over 21 sessions at **$0.7468/M** and **98.25% cache
reads**; the project total across all four runs is now **122 sessions, 1.461B tokens, $1,091.12, 98
checkpoints, $0.7469/M, 98.31% cache reads**. Cost per token has not moved in three eras — §2's claim
that the saving is *work per token*, never price per token, survives a fourth run.

### The prescription — and it wins over this plan's estimate

`budget`'s verdict for karvansara-core, verbatim from the JSON:

> the ceiling is right and the nudge is not: keep maxSessionTokens at 32M and move softBreakRatio
> 0.7 -> 0.85 - the nudge goes 22.4M -> 27.2M, which clears the 25.6M largest closer, headroom 4.8M
> is 2.1x the measured 2.28M wrap-up.

Its `findings` array is **empty** — no warning fired on this window. The ceiling is where the
measurements put it: **zero rollovers in 12 costed sessions**, three sessions nudged and **all three
ended clean** (`nudged: 3, nudgedAndEndedClean: 3`), against sarban-face's ten kills in thirty-three.

**The ratio is the number that is wrong, and this run is the first to show it hurting.** The nudge
fires at 22.42M against a **22.08M median closer** — a ratio of **1.02×**. The median closing session
is being told to wrap up at almost exactly the point it would have closed on its own. §7 step 4's
rule is that the nudge must *clear* the median closer; here it lands on it. Karvan prescribed 0.85
from headroom alone (§9, "the ratio could rise"); karvansara prescribes it from the closers, which is
the stronger evidence. **Both runs now agree, so the plan doc's `32M / 0.70` is corrected in place** —
see `KARVANSARA-PLAN-2026-08-13.md` §"Budget prescription", which now reads 32M / 0.85 with this
measurement beside it.

Not applied to a live plan file here, for the same reason §9 gave: changing the ratio mid-run
re-tunes the budget under the sessions still running against it. It is a one-line change for whoever
launches **karvansara-edge**, and that is where it belongs.

### ⚠ The 28-checkpoint count is real and misleading — corrected, not repeated

`BudgetAnalyzer.cs:190` computes `Checkpoints: costed.Sum(s => s.ClosedCheckpoints.Count)` — every
checkpoint a costed session *closed*, which is the right definition for a normal run and the wrong
denominator for a **finish** run. Measured against `run.db`, not assumed:

```sql
SELECT number, stage_id, newly_done FROM sessions
WHERE run_id='9647f1b80d1841e9997a801562a267c7' AND newly_done IS NOT NULL;
-- 3/KS0 -> KS0.2 | 5/KS0 -> KS0.3 | 6/KS0 -> KS0.1,KS3.1..KS3.3,KS1.1..KS1.6,KS2.1..(22 ids)
-- 7/KS3 -> KS3.4 | 17/KS3 -> KS3.5 | 18/KS9 -> KS9.1 | 19/KS9 -> KS9.2
```

**Twenty-two of the twenty-eight closed in a single session** — #6, the phase-gate confirmation of the
wave-era lanes, which were delivered by lane runs whose tokens are not in this run's costs at all.
So `tokensPerCheckpoint: 5542257` is not this era's cost per checkpoint; it is 155.2M divided by other
runs' work. The same distortion is visible in `money`'s stage table: **KS0, 5 sessions, 24
checkpoints, 2.47M/ckpt**.

The honest figure divides this run's tokens by the checkpoints *these sessions* delivered — seven,
which is exactly `budget`'s own independent `closers: 7`:

**155,183,184 ÷ 7 = 22.2M tokens and $115.89 ÷ 7 = $16.56 per checkpoint.**

That is **32% above** the plan's 16.8M / $13.24 karvan basis, and the reason is in the stage table
rather than in the engine: this run is the hard remainder. `money`'s stages say **KS3: 11 sessions,
2 checkpoints, 15.85M/ckpt, $11.79** (KS3.4 took eight rounds) and **KS9: 5 sessions, 2 checkpoints,
32.08M/ckpt, $23.29** (two of the five were fix sessions after a red battery). A remainder costs more
per checkpoint than a green field; that is a finding about *what was left*, not about the budget.

### What karvansara-edge compiles against

- **Ceiling 32M, softBreakRatio 0.85** (nudge 27.2M). Measured, prescribed twice, model-bound to
  `claude-opus-5` exactly as §9 and the plan say — a model change re-derives the pair.
- **Per-checkpoint estimate: 16.8M / $13.24**, the karvan basis, re-verified today against a longer
  karvan run and unchanged. Edge is new work, not a remainder, so this is the right basis — but
  budget the **variance**, not the mean: KS9's 32.1M/ckpt is the shape a checkpoint takes when its
  first attempt fails a gate.
- **Wrap-up 2.28M** measured on n=3 here (1.71M–2.98M). §7's assumed 1.5M is now low on two eras'
  evidence; at 0.85 the headroom is 4.8M, which is 2.1× that measured wrap-up and still inside the
  ≥1.5× rule.

## 11. The model lineup and the context ceilings, re-measured (KS7.4, 2026-08-19)

§9 and §10 bind the 32M / 0.85 pair to `claude-opus-5`, and say a model change re-derives it. That
statement had never been checked against what the CLI *reports*, only against what the plan asks for.
It is checked now, on **claude 2.1.235**, from the `modelUsage` block of a real `result` envelope —
provider-reported, not a spec sheet:

| model id reported | canonical | context window | max output | provider |
|---|---|---|---|---|
| `claude-opus-5[1m]` | `claude-opus-5` | **1,000,000** | 64,000 | firstParty |
| `claude-haiku-4-5-20251001` | `claude-haiku-4-5` | 200,000 | 32,000 | firstParty |

Three things follow, and two of them matter to the budget:

**1 · The sessions in this era run the 1M variant.** The id on the wire is `claude-opus-5[1m]`, not
`claude-opus-5` — the same canonical model, a 10× larger window. Nothing in the budget assumed a
window size, so the pair does not move; but the reason the 32M ceiling works is now visible. A session
is capped by conductor's *integral* long before the model's window binds.

**2 · A session is not one model.** The probe billed **two**: opus for the turn, and
`claude-haiku-4-5` for 897 input / 8 output tokens of the same request, $0.000937 of a $0.1044 total.
Conductor takes `total_cost_usd` from the CLI and models no rates, so the ledger is right — but any
future per-model cost attribution must not assume the session's declared model is the only one billed.

**3 · The measured windows, against the measured ceiling.** From this run's own `sessions` rows
(context high water per session, K4.1's live meter):

| session | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|
| high water | 91k | 278k | 298k | 213k | 228k | 250k | 218k |
| mean turn | 64k | 195k | 166k | 139k | 148k | 152k | 135k |

Peak context reached **298k of a 1,000,000-token window — 30%**. So the thing that ends a session here
is conductor's 32M *cumulative* ceiling, never the model's window: at a 200k mean turn, 32M is ~160
turns of re-sent prefix. That is the same arithmetic §1 makes, now with the window measured rather
than assumed, and it is why the era's cost lever is the **size of the carried prefix** (KS7.5) rather
than the ceiling.

**Fork versus resume, measured** (KS7.4, same rig — the lifecycle half of this): resuming a 30k
conversation cost 2 fresh / 58 write / 29,995 read; forking it cost 2 / 45 / 30,053 — **0.15% more
tokens and $0.0001 less**, because the carried prefix lands as a cache read rather than a write. A
fork is therefore not a more expensive resume, and a fix or audit session that forks its stage's
delivery session pays cache-read rates for context it would otherwise pay fresh-input rates to
rediscover.

## 12. The karvansara-**edge** era's own numbers — 0.85 held, **35M / 0.9** prescribed (KS12.1, 2026-08-19)

§10 prescribed `32M / 0.85` and edge is the first era to have *run* under it. This section is the
verdict on that pair and the number the **next** era compiles against.

Produced by `dotnet run --project src/Conductor -- budget --json <db>` and `… money --json <db>`
through the fresh build, raw output committed at `.conductor/evidence/KS12/ks12-1-budget-remeasure.json`
and `…/ks12-1-money-remeasure.json`. Nothing below is typed by hand.

> **Why a `<db>` argument and not the live store.** `MigrationRunner.CurrentVersion` is **15** on this
> branch (`src/Conductor.Core/Store/MigrationRunner.cs:11`) and **14** on master, which is what the
> installed `0.4.1` engine driving this run supports. Opening the live `run.db` with the fresh build
> migrates it to 15, after which the driving engine refuses it at `MigrationRunner.cs:27-29` and
> `conductor task` / `note` / `bug` die for the rest of the era — that is bug **#45**, filed at KS10.1
> when exactly this happened. So the measurement runs against a `sqlite3.backup` **copy** of the store
> and the live file is never opened for write. Note that `CONDUCTOR_RUN_DB` does **not** redirect
> `budget`: it resolves the run by repo path first and answers *"no runs to measure"*. The positional
> db path is the working seam.

### The measurement — as of session 20, mid-KS12

| run | ceiling | costed sess | tokens | ckpt | tok/ckpt | floor | median closer | max closer | rollover | nudged / clean | wrap-up |
|---|---|---|---|---|---|---|---|---|---|---|---|
| karvansara-core (§10) | 32M / nudge 22.4M | 12 of 21 | 155.2M | 28 ⚠ | 5.54M ⚠ | 2.95M | 22.08M | 25.56M | **0** | 3 / 3 | 2.28M (n=3) |
| **karvansara-edge** | **32M / nudge 27.3M** | **19 of 20** | **366.8M** | **21** | **17.47M** | **0.79M** | **20.19M** | **29.36M** | **0** | **5 / 5** | **1.74M (n=4)** |

Money, same store: edge is **$290.07** over 20 sessions at **$0.7908/M** and **98.43% cache reads**.
Per stage — `money`'s own table, which is where the variance lives:

| stage | sessions | ckpt | tokens/ckpt | $/ckpt |
|---|---|---|---|---|
| KS4 (gates that can't be gamed) | 5 | 5 | 19.16M | **$17.93** |
| KS7 (agent-side catch-up) | 5 | 5 | 19.43M | $13.81 |
| KS11 (the courier) | 5 | 5 | 16.77M | $12.27 |
| KS6 (analyzer set + ratchets) | 4 | 4 | 15.88M | $12.83 |
| KS8 (MCP + ATIF, the cut-first stage) | 1 | 2 | 13.26M | $9.35 |

### The count needs no correction this time — checked, not assumed

§10 had to correct a 28-checkpoint denominator that was really 7. The same check on edge **passes**:
`budget` reports `checkpoints: 21` and, independently, `closers: 18` — and `money`'s stage table sums
to the same 21 (5+5+4+5+2). Every one of the 21 was closed by a session inside this run; there is no
lane-era phase-gate bulk confirmation in the window. **17.47M / $13.81 per checkpoint is the honest
figure**, not an artefact.

### The prescription — 32M held, and it is now the *ceiling* that is loose

`budget`'s verdict for karvansara-edge, verbatim from the JSON, `findings` array **empty**:

> set maxSessionTokens to 35M at softBreakRatio 0.9 - nudge 31.5M clears the 29.4M largest closer,
> headroom 3.5M is 2.0x the measured 1.74M wrap-up.

Three things this window proves, and they are the reason the prescription moved:

1. **0.85 was right.** The realised ratio is `nudgeRatio: 0.8541` — the pair §10 prescribed is the
   pair that ran. Under it: **zero rollovers in 19 costed sessions**, and **five sessions nudged, all
   five ended clean** (`nudged: 5, nudgedAndEndedClean: 5`). §10's complaint — a nudge landing *on*
   the median closer at 1.02× — is gone: 27.33M against a 20.19M median closer is **1.35×**.
2. **Wrap-up is cheaper than two eras assumed.** Measured on n=4 here: **1.52M–1.98M, median 1.74M**,
   against karvansara-core's 2.28M (n=3) and §7's assumed 1.5M. Three eras of evidence now put the
   real figure between 1.7M and 2.3M; §7 step 2's 1.5M assumption should be read as a floor.
3. **The ceiling is now the binding constraint, not the ratio.** The largest closer is **29.36M**
   against a 27.33M nudge — i.e. the biggest sessions are already running *past* the nudge and
   finishing anyway, using 2M of the 4.67M headroom. Raising to 35M/0.9 puts the nudge at 31.5M,
   which clears that 29.36M with 3.5M left, still 2.0× the measured wrap-up and inside §7 step 4's
   ≥1.5× rule.

**Where this doc's earlier estimate and the prescription disagree, the prescription wins.** The
plan doc's `32M / 0.85` line is corrected in place at `KARVANSARA-PLAN-2026-08-13.md`
§"Budget prescription".

### What the next era compiles against

- **Ceiling 35M, softBreakRatio 0.9** (nudge 31.5M, headroom 3.5M). Still bound to `claude-opus-5`
  — §9, §10 and §11 all say a model change re-derives the pair, and nothing here changes that.
- **Per-checkpoint basis: 17.47M / $13.81.** The karvan basis this era planned against was
  **16.8M / $13.24**; the outturn is **+4.0% tokens / +4.3% cost**. That is the first time a
  per-checkpoint estimate in this project has been checked against a full era's outturn and held.
  Use 17.5M / $13.80 for the next plan and stop rounding it down.
- **Budget the variance, not the mean.** KS4's $17.93/ckpt is +30% on the era mean and it is the
  shape of an *adversarial* stage — one where the deliverable is a gate that must go red on purpose,
  so every checkpoint pays for a rig as well as the feature. §10 said the same thing about fix
  rounds; this run says it about the work's *kind*.
- **KS7.5 did not show up as a cache-read saving, and should not have been expected to.** Edge's
  cache-read share is **98.43%**, against core's 98.29% and karvan's 98.3% — the share is a constant
  of this architecture, not a lever. What KS7.5 moves is the *size of the carried prefix* within a
  session (§11), which lands in tokens-per-checkpoint, and there the era came in +4% on an estimate
  built from a *less* engine-deep era. Read that as the saving paying for the extra depth.
