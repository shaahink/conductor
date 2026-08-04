# Tuning conductor's token budget — what the three live runs actually measured

**Written 2026-08-02**, from `run.db` and `conductor.log` of three real runs on one machine:
`conductor` (sarban-face, 41 sessions, complete), `sk-studio` (69 sessions), and `DevContext2`
(graph-v2 autonomous remainder, live at the time of writing).

Every number here is measured. Where the ledger and git disagree, git wins and the discrepancy is
itself a finding (§6).

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
| conductor (sarban-face) | uncapped | 58.1M ($41.9) | |
| conductor | 8M / 0.75 | **14.7M ($11.7)** | **4.0× better** |
| sk-studio stage A | uncapped | 20.0M | |
| sk-studio stages C/E/F | 6M / 0.7 | **25–54M** | **worse than uncapped** |
| sk-studio stages G/H | 9M / 0.7 | 12.8–15.3M | better; stage H **0 rollovers** |

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

**The wrap-up cost is absolute, and scales with context size — not with the cap.** So expressing the
reserve as a *ratio* shrinks it exactly when it must stay constant. Headroom against observed
rollover rate:

| configuration | headroom | wrap-up needed | rollover rate |
|---|---|---|---|
| sk-studio 6M / 0.7 | 1.8M | ~1–2M | **67%** (31 of 46 across stages B/C/E/F) |
| conductor 8M / 0.75 | 2.0M | ~1–2M | 11 rollovers over the run |
| sk-studio 9M / 0.7 | 2.7M | ~1–2M | 22%; **stage H 0 of 6** |
| DevContext2 20M / 0.7 | 6.0M | 2.63M measured | 0 of 1 |

**Headroom ≥ ~1.5–2× the observed wrap-up is where rollovers stop dominating.**

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
3. **`maxSessionTokens` = floor + 1.5–2× wrap-up**, rounded up. It must also be well under the
   uncapped mean or there is nothing to save.
4. **`softBreakRatio`** = chosen so the *nudge* sits clear of the floor and the *headroom* stays
   ≥1.5× the wrap-up. A ratio that puts the nudge exactly at the floor fires on every session before
   it could have finished naturally.
5. **Re-measure after a stage.** sk-studio needed one correction (6M → 9M) and was worse than
   uncapped until it made it.

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
4× that conductor got become available to a repo like this one.
