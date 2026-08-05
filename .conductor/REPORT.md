# Conductor — Karvan core - the engine knows what it did and what it cost run report

_Updated 2026-08-05 13:33 UTC · branch `feat/karvan` · HEAD `e935b8c`_

**Status:** Paused — stage K1 used all 8 attempts without completing — inspect and `conductor resume` (or `conductor skip`) · advisor: DNS failure on machine (ENOTFOUND github.com) is blocking K1.3 push; network connectivity must be restored and commits pushed before K1.4 can proceed. [14h 03m ago, 23:29:12Z]
**Stage:** K7 — Ship the plan · attempts used 0 · working ▸ K7.2
**Checkpoints:** 24/32 done · **Sessions run:** 32 · **Cost:** $317.7516 (agent $317.5561 + gates $0.1955) · **Tokens:** 4,665,548 in / 2,289,567 out
**Confirmed phases:** K1, K2, K3, K4, K5, K6

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| K1 | The ledger stops lying | ██████████ 4/4 | confirmed ✓ |
| K2 | The architecture becomes navigable | ██████████ 4/4 | confirmed ✓ |
| K3 | Conductor remembers | ██████████ 3/3 | confirmed ✓ |
| K4 | Token truth - measure it before shrinking it | ██████████ 4/4 | confirmed ✓ |
| K5 | The result contract and the channels | ██████████ 4/4 | confirmed ✓ |
| K6 | The surfaces read | ██████████ 4/4 | confirmed ✓ |
| K7 | Ship the plan | █████░░░░░ 1/2 | **← active** |

<details> ✅<summary>K1 — The ledger stops lying (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K1.1 | A rolled-over session records the commits and claims it actually made, proven by a harness session driven to its ceiling, with a rollover still consuming no attempt and still not running the phase gate | ✅ DONE | [`93bbae5`](https://github.com/shaahink/conductor/commit/93bbae5) |
| K1.2 | The soft break is re-stated until it is obeyed, names the actual remaining budget, states the wrap-up order (claim first, handoff second), and the session record says whether it was delivered, re-delivered and obeyed | ✅ DONE | [`93bbae5`](https://github.com/shaahink/conductor/commit/93bbae5) |
| K1.3 | Three small untruths die as a class — the thinking-token column that is zero on all 125 rows, the lessons file that is a diary and repeats one entry twice, and a go.mod that calls a directly-imported package indirect while carrying two lipgloss majors | ✅ DONE | [`890ac38`](https://github.com/shaahink/conductor/commit/890ac38) |
| K1.4 | A spawned session sees conductor's task tools and the operator's own MCP servers, because the config merges instead of replacing, with the prompt-side deferred-tool fallback kept | ✅ DONE | [`36314be`](https://github.com/shaahink/conductor/commit/36314be) |

</details>

<details> ✅<summary>K2 — The architecture becomes navigable (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K2.1 | Conductor.Core holds the domain, orchestration and store with no Spectre and no HTTP hosting, Conductor is CLI plus hosting, the reference direction only points one way, and publish plus doctor plus the Face's discovery still work | ✅ DONE | [`b05efef`](https://github.com/shaahink/conductor/commit/b05efef) |
| K2.2 | Architecture tests in the ordinary suite fail the build when a boundary is crossed, each naming the offending type and the rule, landed together with K2.1 so the extraction is verified rather than asserted | ✅ DONE | [`b05efef`](https://github.com/shaahink/conductor/commit/b05efef) |
| K2.3 | The worst partial-file piles are split by responsibility — the thirty-file DTO pile becomes per-feature endpoint contracts — and one written file-organisation convention says where a new endpoint, event or partial belongs | ✅ DONE | [`b05efef`](https://github.com/shaahink/conductor/commit/b05efef) |
| K2.4 | The front door reads: a real ARCHITECTURE.md map, an AGENTS.md cut to current state with superseded handoffs archived and indexed, closed-era trackers out of the repo root, the divergent duplicate workgraph doc resolved to one file, and the docs indexes updated | ✅ DONE | [`8b38f1b`](https://github.com/shaahink/conductor/commit/8b38f1b) |

</details>

<details> ✅<summary>K3 — Conductor remembers (3/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K3.1 | State has a machine-level home with one catalogue keyed by repo and plan, an environment override, an idempotent migration that imports existing run.db files rather than orphaning them, and a per-run scratch dir that keeps the repo's tracked deliverables | ✅ DONE | [`707992f`](https://github.com/shaahink/conductor/commit/707992f) |
| K3.2 | conductor history lists and opens past runs read-only from the catalogue, and the Face's existing run picker offers them | ✅ DONE | [`ec1f158`](https://github.com/shaahink/conductor/commit/ec1f158) |
| K3.3 | Every run records the engine version, its commit, its dirty flag and a snapshot of the limits that governed it, and a dirty build warns at launch | ✅ DONE | [`e45fa11`](https://github.com/shaahink/conductor/commit/e45fa11) |

</details>

<details> ✅<summary>K4 — Token truth - measure it before shrinking it (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K4.1 | The engine records context size per turn — a high-water and a mean per session — derived from the stream, with the derivation checked against a session that can be estimated independently | ✅ DONE | [`ea49c8d`](https://github.com/shaahink/conductor/commit/ea49c8d) |
| K4.2 | conductor budget prints floor, wrap-up, cap, nudge-versus-floor and rollover rate and prescribes a correction, and it reproduces this repo's own two runs without being told the answers | ✅ DONE | [`1fcbd0b`](https://github.com/shaahink/conductor/commit/1fcbd0b) |
| K4.3 | conductor money answers what a project cost per checkpoint, per stage and per month, with cache-read share and the before-and-after windows that say what the cap bought, cross-checked against a hand-written query | ✅ DONE | [`20842e2`](https://github.com/shaahink/conductor/commit/20842e2) |
| K4.4 | Live session tokens, the distance to the nudge, a burn rate and a projection sit beside live money in the Face and on the wire, honest when no cap is set | ✅ DONE | [`b4ea829`](https://github.com/shaahink/conductor/commit/b4ea829) |

</details>

<details> ✅<summary>K5 — The result contract and the channels (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K5.1 | The session result has one format conductor owns — short headline, at most three outcome bullets, artefacts as links, evidence paths, explicit gaps — with the followup parser, the verdict parse and the session template moving in the same checkpoint and a legacy result degrading rather than throwing | ✅ DONE | [`c04175d`](https://github.com/shaahink/conductor/commit/c04175d) |
| K5.2 | The five Telegram defects that make the feed unreadable are gone: one identity block from one source, the stage title beside the id, the structured result rendered instead of cut mid-word, a rollover that reports what it landed, and a progress line in every push | ✅ DONE | [`c04175d`](https://github.com/shaahink/conductor/commit/c04175d) |
| K5.3 | Evidence is a first-class artifact — path, kind, checkpoint, session, sha, created-at — written as an event when an agent registers one or a watched directory gains a file, with non-text kinds first-class, a Face surface, and the existing free-text evidence field still working | ✅ DONE | [`6df3a58`](https://github.com/shaahink/conductor/commit/6df3a58) |
| K5.4 | The message-composition layer ships owner-editable per-event templates, repo and branch and stage title and checkpoint in every push, commits and PRs as links, money with headroom, photo and document sending so evidence arrives, a thread per run, severity mapped to notify or silent, 4096-character chunking, and an ADR recording the push-only remote posture | ✅ DONE | [`43dd6d2`](https://github.com/shaahink/conductor/commit/43dd6d2) |

</details>

<details> ✅<summary>K6 — The surfaces read (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K6.1 | An ADR fixes the TUI conventions — pager keys, focus model, help, one scroll idiom, viewport versus list versus table — after an actual read of glow, soft-serve, gh-dash and lazygit | ✅ DONE | [`76b66aa`](https://github.com/shaahink/conductor/commit/76b66aa) |
| K6.2 | bubbles v2 is a declared dependency and Report and Knowledge scroll through a viewport, with the golden, frame-invariant and glitch-sweep tests green, any baseline regenerated in a separate rebaseline commit, and a captured frame of a long document scrolled to its end as evidence | ✅ DONE | [`fa1d3a4`](https://github.com/shaahink/conductor/commit/fa1d3a4) |
| K6.3 | Each tab owns its own model, state, update and view, the root update becomes a dispatch instead of 826 lines and 80 cases, and the mnemonic map and the hand-maintained help legend change together | ✅ DONE | [`05beb46`](https://github.com/shaahink/conductor/commit/05beb46) |
| K6.4 | One markdown renderer honours the active theme everywhere markdown belongs, the remaining primitive swaps the ADR calls for are done as far as the goldens allow, and anything deliberately left is named | ✅ DONE | [`611a8a0`](https://github.com/shaahink/conductor/commit/611a8a0) |

</details>

<details><summary>K7 — Ship the plan (1/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K7.1 | The docs match the engine and this era's own measurements are written back — the cap's real score, the corrected nudge rule, and this run's figures produced by conductor budget rather than by hand — with every wrong claim corrected in place and a closure ledger naming an owner for everything still open | ✅ DONE | [`ee68978`](https://github.com/shaahink/conductor/commit/ee68978) |
| K7.2 | feat/karvan is merged to master by the owner, the release is tagged through the existing pipeline, and the installed version matches the releases page | 🚫 BLOCKED | - |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 3 | K1 | Deliver | 1 | 08-04 22:59 | 0:02 | AgentError |  | 0 | engine-fast:cached · face-fast:cached | $0.0000 |  |  |
| 4 | K1 | Fix | 2 | 08-04 23:02 | 0:02 | AgentError |  | 0 | engine-fast:cached · face-fast:cached | $0.0000 |  |  |
| 5 | K1 | Deliver | 1 | 08-04 23:30 | 0:37 | Advanced | K1.4 | 3 | engine-fast:OK · face-fast:OK | $14.6250 | $0.0127 | 170,772/86,424 |
| 6 | K1 | Fix | 2 | 08-05 00:18 | 0:19 | Progress |  | 5 | engine-fast:OK · face-fast:OK | $6.9616 | $0.0089 | 105,085/54,557 |
| 7 | K2 | Deliver | 1 | 08-05 00:42 | 0:36 | Advanced | K2.1 K2.2 K2.3 | 5 | engine-fast:OK · face-fast:OK | $17.4644 | $0.0081 | 229,722/124,620 |
| 8 | K2 | Deliver | 1 | 08-05 01:21 | 0:16 | Advanced | K2.4 | 3 | engine-fast:OK · face-fast:OK | $11.1887 | $0.0053 | 159,355/68,728 |
| 9 | K3 | Deliver | 1 | 08-05 01:42 | 0:42 | Advanced | K3.1 | 5 | engine-fast:OK · face-fast:OK | $12.0714 | $0.0087 | 193,864/91,429 |
| 10 | K3 | Deliver | 1 | 08-05 02:26 | 0:36 | Advanced | K3.2 | 5 | engine-fast:OK · face-fast:OK | $17.6953 | $0.0049 | 205,048/111,704 |
| 11 | K3 | Deliver | 1 | 08-05 03:03 | 0:20 | Advanced | K3.3 | 2 | engine-fast:OK · face-fast:OK | $8.0198 | $0.0053 | 142,261/59,401 |
| 12 | K3 | Fix | 2 | 08-05 03:31 | 0:09 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $2.4999 | $0.0081 | 63,844/22,913 |
| 13 | K4 | Deliver | 1 | 08-05 03:47 | 0:22 | Advanced | K4.1 | 2 | engine-fast:OK · face-fast:OK | $9.1685 | $0.0045 | 146,429/65,841 |
| 14 | K4 | Deliver | 1 | 08-05 04:10 | 0:29 | Advanced | K4.2 | 2 | engine-fast:OK · face-fast:OK | $10.5312 | $0.0072 | 161,661/96,953 |
| 15 | K4 | Deliver | 1 | 08-05 04:40 | 0:23 | Advanced | K4.3 | 2 | engine-fast:OK · face-fast:OK | $8.8544 | $0.0051 | 169,401/80,290 |
| 16 | K4 | Deliver | 1 | 08-05 05:05 | 0:47 | Advanced | K4.4 | 5 | engine-fast:OK · face-fast:OK | $16.7142 | $0.0046 | 201,837/111,577 |
| 17 | K4 | Fix | 2 | 08-05 05:59 | 0:06 | Progress |  | 1 | engine-fast:OK · face-fast:OK | $1.7676 | $0.0048 | 54,483/13,809 |
| 18 | K5 | Deliver | 1 | 08-05 06:09 | 0:41 | Advanced | K5.1 K5.2 | 7 | engine-fast:OK · face-fast:OK | $17.4525 | $0.0050 | 235,397/127,419 |
| 19 | K5 | Deliver | 1 | 08-05 06:51 | 0:41 | Advanced | K5.3 | 5 | engine-fast:OK · face-fast:OK | $16.1117 | $0.0049 | 187,740/86,244 |
| 20 | K5 | Deliver | 1 | 08-05 07:33 | 0:46 | Advanced | K5.4 | 4 | engine-fast:OK · face-fast:OK | $19.4020 | $0.0051 | 246,366/126,779 |
| 21 | K6 | Deliver | 1 | 08-05 08:23 | 0:11 | Advanced | K6.1 | 2 | engine-fast:OK · face-fast:OK | $4.7018 | $0.0049 | 81,232/36,308 |
| 22 | K6 | Deliver | 1 | 08-05 08:35 | 0:19 | Advanced | K6.2 | 3 | engine-fast:OK · face-fast:OK | $8.2711 | $0.0050 | 140,639/59,279 |
| 23 | K6 | Deliver | 1 | 08-05 08:56 | 0:23 | Advanced | K6.3 | 5 | engine-fast:OK · face-fast:OK | $9.6363 | $0.0053 | 168,452/79,524 |
| 24 | K6 | Deliver | 1 | 08-05 09:21 | 0:30 | Advanced | K6.4 | 5 | engine-fast:OK · face-fast:OK | $11.6658 | $0.0072 | 176,330/75,597 |
| 25 | K7 | Deliver | 1 | 08-05 09:57 | 0:38 | Advanced | K7.1 | 9 | engine-fast:OK · face-fast:OK | $9.5886 | $0.0097 | 145,638/72,698 |
| 26 | K7 | Deliver | 1 | 08-05 10:36 | 0:19 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $5.5277 | $0.0089 | 103,088/55,975 |
| 27 | K7 | Deliver | 1 | 08-05 10:57 | 0:57 | Progress |  | 4 | engine-fast:OK · face-fast:OK | $18.2443 | $0.0074 | 235,837/148,885 |
| 28 | K7 | Deliver | 1 | 08-05 11:56 | 0:12 | Progress |  | 1 | engine-fast:OK · face-fast:OK | $5.3546 | $0.0048 | 110,529/47,606 |
| 29 | K7 | Deliver | 1 | 08-05 12:10 | 0:21 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $7.3087 | $0.0074 | 142,506/65,682 |
| 30 | K7 | Deliver | 1 | 08-05 12:33 | 0:09 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $3.1170 | $0.0051 | 76,776/28,726 |
| 31 | K7 | Deliver | 1 | 08-05 12:43 | 0:12 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $4.0909 | $0.0048 | 85,108/41,539 |
| 32 | K7 | Deliver | 1 | 08-05 12:57 | 0:34 | Progress |  | 3 | engine-fast:OK · face-fast:OK | $11.2145 | $0.0081 | 159,555/76,946 |

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-05 10:57:06  ▪ gate engine-fast pass [phase]  (0.0s)
08-05 10:57:06  ▪ gate face-fast pass [phase]  (0.0s)
08-05 10:57:06  ▪ gate engine-full pass [phase]  (3m53s)
08-05 10:57:06  ▪ gate face-full pass [phase]  (11.9s)
08-05 10:57:06  ✓ checkpoint K6.4 confirmed
08-05 10:57:06  ▸ stage K6 confirmed  (1h33m39s)
08-05 10:57:07  ▸ stage K7 entered — Ship the plan
08-05 10:57:07  • session #25 K7 Deliver started (attempt 1/4)
08-05 11:36:53  ▪ gate engine-fast pass [session]  (1m00s)
08-05 11:36:53  ▪ gate face-fast pass [session]  (36.2s)
08-05 11:36:54  • session #25 K7 → Advanced · done K7.1 · 9 commit(s)  (39m47s)
08-05 11:36:55  ◆ plan reloaded — v3 · 7 stages · 4 gates
08-05 11:36:55  • session #26 K7 Deliver started (attempt 1/4)
08-05 11:57:33  ▪ gate engine-fast pass [session]  (59.2s)
08-05 11:57:33  ▪ gate face-fast pass [session]  (29.8s)
08-05 11:57:34  • session #26 K7 → Progress · 2 commit(s)  (20m38s)
08-05 11:57:34  • session #27 K7 Deliver started (attempt 1/4)
08-05 12:56:23  ▪ gate engine-fast pass [session]  (45.6s)
08-05 12:56:23  ▪ gate face-fast pass [session]  (28.4s)
08-05 12:56:24  • session #27 K7 → Progress · 4 commit(s)  (58m49s)
08-05 12:56:24  • session #28 K7 Deliver started (attempt 1/4)
08-05 13:10:11  ▪ gate engine-fast pass [session]  (44.8s)
08-05 13:10:11  ▪ gate face-fast pass [session]  (3.0s)
08-05 13:10:12  • session #28 K7 → Progress · 1 commit(s)  (13m47s)
08-05 13:10:12  • session #29 K7 Deliver started (attempt 1/4)
08-05 13:33:02  ▪ gate engine-fast pass [session]  (44.8s)
08-05 13:33:02  ▪ gate face-fast pass [session]  (29.5s)
08-05 13:33:03  • session #29 K7 → Progress · 2 commit(s)  (22m50s)
08-05 13:33:04  • session #30 K7 Deliver started (attempt 1/4)
08-05 13:43:46  ▪ gate engine-fast pass [session]  (47.8s)
08-05 13:43:46  ▪ gate face-fast pass [session]  (3.1s)
08-05 13:43:47  • session #30 K7 → Progress · 2 commit(s)  (10m42s)
08-05 13:43:47  • session #31 K7 Deliver started (attempt 1/4)
08-05 13:57:20  ▪ gate engine-fast pass [session]  (44.6s)
08-05 13:57:20  ▪ gate face-fast pass [session]  (3.1s)
08-05 13:57:21  • session #31 K7 → Progress · 2 commit(s)  (13m34s)
08-05 13:57:21  • session #32 K7 Deliver started (attempt 1/4)
08-05 14:33:04  ▪ gate engine-fast pass [session]  (1m07s)
08-05 14:33:04  ▪ gate face-fast pass [session]  (14.0s)
08-05 14:33:05  • session #32 K7 → Progress · 3 commit(s)  (35m43s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 32 · retries 4 (12 %) · overall Warn
⚠ [context-saturation] session #10: 23,623,416 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #18: 23,816,486 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #19: 24,146,164 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #1: 24,653,507 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #20: 25,687,748 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #27: 22,272,683 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #7: 24,094,247 context tokens (≥ 20,000,000)
⚠ [gate-oscillation] gate 'engine-full' flipped pass/fail 5x
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/karvan
working tree: M .conductor/REPORT.md, M plans/karvan/CORE-TRACKER.md
vs upstream: up to date
```

### Commits by session

- **s25 (K7 Deliver)** — 9 commit(s):
  - [`5776876`](https://github.com/shaahink/conductor/commit/5776876) docs(ledger): K7.1 - bug #32's row records that deleting the stash did not clear it
  - [`7688a2b`](https://github.com/shaahink/conductor/commit/7688a2b) docs(tracker): K7.1 claimed and handed off - only the owner-gated K7.2 remains
  - [`4ea6b94`](https://github.com/shaahink/conductor/commit/4ea6b94) docs(spec): K7.1 - the three remaining wrong spec claims corrected where they were made
  - [`f51f0b5`](https://github.com/shaahink/conductor/commit/f51f0b5) docs(ledger): K7.1 - the Karvan closure ledger, and it is pinned
  - [`6f47cff`](https://github.com/shaahink/conductor/commit/6f47cff) docs(changelog): K7.1 - the Karvan core era section
  - [`ef79183`](https://github.com/shaahink/conductor/commit/ef79183) docs(backlog): K7.1 - NEXT-FEATURES carries the Karvan era's output
  - [`670b2f4`](https://github.com/shaahink/conductor/commit/670b2f4) chore(plan): drop the stashed face items the editor added by mistake
  - [`12c0e74`](https://github.com/shaahink/conductor/commit/12c0e74) docs(findings): K7.1 - the era's own research doc and spec corrected in place
  - [`ee68978`](https://github.com/shaahink/conductor/commit/ee68978) docs(budget): K7.1 - conductor budget re-measures the doc and corrects four numbers
- **s26 (K7 Deliver)** — 2 commit(s):
  - [`cbaa193`](https://github.com/shaahink/conductor/commit/cbaa193) docs(tracker): K7.2 part 1 handed off - the guard is in, the rest is the owner's
  - [`f25e2cd`](https://github.com/shaahink/conductor/commit/f25e2cd) fix(store): a schema_version row that understates the file no longer kills the engine
- **s27 (K7 Deliver)** — 4 commit(s):
  - [`8637de3`](https://github.com/shaahink/conductor/commit/8637de3) docs(tracker): K7.2 part 2 handed off - the ship gate is measured, the rest is the owner's
  - [`12daaf5`](https://github.com/shaahink/conductor/commit/12daaf5) fix(store): decide a stale copy by what it remembers, not by its timestamps
  - [`ee16c68`](https://github.com/shaahink/conductor/commit/ee16c68) fix(store): a reinstall no longer resumes from a stale copy of the run history
  - [`81d4981`](https://github.com/shaahink/conductor/commit/81d4981) fix(tracker): the generated view no longer re-declares work whose stage left the plan
- **s28 (K7 Deliver)** — 1 commit(s):
  - [`b56d457`](https://github.com/shaahink/conductor/commit/b56d457) fix(budget): a budget is a ceiling and a nudge, so the verdict compares both
- **s29 (K7 Deliver)** — 2 commit(s):
  - [`064c4df`](https://github.com/shaahink/conductor/commit/064c4df) docs(tracker): K7.2 part 4 handed off - the ship gate is rehearsed, the release body is dated
  - [`35d3cb6`](https://github.com/shaahink/conductor/commit/35d3cb6) docs(k7): the ship gate is rehearsed, and the ledger stops pinning itself
- **s30 (K7 Deliver)** — 2 commit(s):
  - [`a8c76cc`](https://github.com/shaahink/conductor/commit/a8c76cc) docs(tracker): K7.2 part 5 handed off - the CLI reference names all 41 verbs
  - [`cfccfa5`](https://github.com/shaahink/conductor/commit/cfccfa5) docs(cli): the reference names the verbs this era shipped, and a test keeps it that way
- **s31 (K7 Deliver)** — 2 commit(s):
  - [`f764904`](https://github.com/shaahink/conductor/commit/f764904) docs(tracker): K7.2 part 6 handed off - a mistyped flag is now an error, bug #17 closed
  - [`842adc6`](https://github.com/shaahink/conductor/commit/842adc6) fix(cli): a mistyped flag is an error, not silence
- **s32 (K7 Deliver)** — 3 commit(s):
  - [`e935b8c`](https://github.com/shaahink/conductor/commit/e935b8c) docs(changelog,tracker): K7.2 part 7 handed off - the ship stops leaving state on the user's machine
  - [`84c4dc4`](https://github.com/shaahink/conductor/commit/84c4dc4) fix(demo): the throwaway run stays in the throwaway directory
  - [`bdf2a63`](https://github.com/shaahink/conductor/commit/bdf2a63) fix(docs,tools): the front page describes the shipped Face, and the rig it points at is green again

## Phase handovers (audit)

- `.conductor/handovers/B0.md`
- `.conductor/handovers/B1.md`
- `.conductor/handovers/B10.md`
- `.conductor/handovers/B11.md`
- `.conductor/handovers/B2.md`
- `.conductor/handovers/B3.md`
- `.conductor/handovers/B4.md`
- `.conductor/handovers/B5.md`
- `.conductor/handovers/B6.md`
- `.conductor/handovers/B7.md`
- `.conductor/handovers/B8.md`
- `.conductor/handovers/B9.md`
- `.conductor/handovers/F0.md`
- `.conductor/handovers/F1.md`
- `.conductor/handovers/F2.md`
- `.conductor/handovers/F4.md`

## Last gate run

engine-fast:OK · face-fast:OK

## Last session result

> SESSION-RESULT: Fixed a state leak in demo and the rehearsal rig, plus the front page
> - conductor demo left a run.db and a permanent history row on every user's machine; now dies with its throwaway dir. Proven 1 entry to 0 on an isolated state home
> - tools/w5/rehearsal.ps1 was red on a healthy engine since K3.1 - 5 of 33 checks blamed the engine. Shared helper fixed, re-ran 33/33
> - README named 11 Face tabs (3 deleted an era ago, Home missing) and omitted AuthFailed from the outcome table; guard test reads both off source
> artefacts: bdf2a63, 84c4dc4, e935b8c, README.md, CHANGELOG.md, src/Conductor/Commands/DemoCommand.cs, tools/lib/run-query.ps1, tools/w5/rehearsal.ps1, tests/Conductor.Tests…

## Tracker handoff

```
last: **K7.2 part 7** (s32), commits `bdf2a63` + `84c4dc4`. K7.2 stays BLOCKED and owner-only; nothing
  here claimed it. Started as a README audit and found a live engine defect. **One root cause: K3.1
  moved `run.db` out of `<repo>/.conductor` and three things were never told.** (a) `conductor demo` -
  the front page's try-it-free command - deleted its throwaway repo but left the database plus a
  permanent `conductor history` row (`RunHistory.cs:26` walks the catalogue) on the user's machine.
  Fixed with a repo-local state pointer; proven pre/post against an isolated `CONDUCTOR_STATE_HOME`,
  1 entry -> 0. That exposed a Windows cleanup failure ("delete it by hand") from a pooled SQLite
  handle - `ClearAllPools()` in `ForceDelete`. (b) `tools/w5/rehearsal.ps1`, which README:208 hands to
  contributors, was RED on a healthy engine - 5 of 33 checks, all "no run.db", blaming the engine.
  `tools/lib/run-query.ps1` (shared by three rigs) never passed `--run-db`, the flag K3.1 added for
  it. Re-ran: **33/33**. The rigs also wrote into the operator's real store; w5 now uses its own.
  (c) README named eleven Face tabs incl. three merged away by SF1.3, omitted Home, and its outcome
  table missed `AuthFailed`/`BlockedUntil`/`AgentError`. Guard test reads both off source.
do not re-derive: no CI job runs those rigs (`grep .github/workflows` is empty) - that is why an era
  passed unnoticed. `CHANGELOG.md` v0.4.0 Fixed already carries all three. s29-s31 still stand.
next: owner-only, unchanged - confirm no other conductor run is live, merge, tag `v0.4.0`, let the
  pipeline publish, then the first `install.ps1` of this run. Re-run `conductor budget` and `money`
  at tag time and paste into `CHANGELOG.md`; today's figures are stamped s29 and move every session.
  Bug #35 is the same rot in `w3/window-close.ps1` and `sf1-2-live-proof.ps1`, neither drivable here.
```
