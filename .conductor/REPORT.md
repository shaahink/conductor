# Conductor — Karvan core - the engine knows what it did and what it cost run report

_Updated 2026-08-05 08:35 UTC · branch `feat/karvan` · HEAD `5fa8150`_

**Status:** Idle — stage K1 used all 8 attempts without completing — inspect and `conductor resume` (or `conductor skip`) · advisor: DNS failure on machine (ENOTFOUND github.com) is blocking K1.3 push; network connectivity must be restored and commits pushed before K1.4 can proceed. [9h 06m ago, 23:29:12Z]
**Stage:** K6 — The surfaces read · attempts used 0 · working ▸ K6.2
**Checkpoints:** 20/32 done · **Sessions run:** 21 · **Cost:** $223.6584 (agent $223.5366 + gates $0.1218) · **Tokens:** 3,121,090 in / 1,537,110 out
**Confirmed phases:** K1, K2, K3, K4, K5

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| K1 | The ledger stops lying | ██████████ 4/4 | confirmed ✓ |
| K2 | The architecture becomes navigable | ██████████ 4/4 | confirmed ✓ |
| K3 | Conductor remembers | ██████████ 3/3 | confirmed ✓ |
| K4 | Token truth - measure it before shrinking it | ██████████ 4/4 | confirmed ✓ |
| K5 | The result contract and the channels | ██████████ 4/4 | confirmed ✓ |
| K6 | The surfaces read | ██░░░░░░░░ 1/4 | **← active** |
| K7 | Ship the plan | ░░░░░░░░░░ 0/2 | todo |

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

<details><summary>K6 — The surfaces read (1/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K6.1 | An ADR fixes the TUI conventions — pager keys, focus model, help, one scroll idiom, viewport versus list versus table — after an actual read of glow, soft-serve, gh-dash and lazygit | ✅ DONE | - |
| K6.2 | bubbles v2 is a declared dependency and Report and Knowledge scroll through a viewport, with the golden, frame-invariant and glitch-sweep tests green, any baseline regenerated in a separate rebaseline commit, and a captured frame of a long document scrolled to its end as evidence | ⬜ TODO | - |
| K6.3 | Each tab owns its own model, state, update and view, the root update becomes a dispatch instead of 826 lines and 80 cases, and the mnemonic map and the hand-maintained help legend change together | ⬜ TODO | - |
| K6.4 | One markdown renderer honours the active theme everywhere markdown belongs, the remaining primitive swaps the ADR calls for are done as far as the goldens allow, and anything deliberately left is named | ⬜ TODO | - |

</details>

<details><summary>K7 — Ship the plan (0/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| K7.1 | The docs match the engine and this era's own measurements are written back — the cap's real score, the corrected nudge rule, and this run's figures produced by conductor budget rather than by hand — with every wrong claim corrected in place and a closure ledger naming an owner for everything still open | ⬜ TODO | - |
| K7.2 | feat/karvan is merged to master by the owner, the release is tagged through the existing pipeline, and the installed version matches the releases page | ⬜ TODO | - |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | K1 | Deliver | 1 | 08-04 21:49 | 0:35 | Advanced | K1.1 K1.2 | 3 | engine-fast:OK · face-fast:OK | $18.0603 | $0.0083 | 219,310/95,366 |
| 2 | K1 | Deliver | 1 | 08-04 22:27 | 0:30 | Advanced | K1.3 | 6 | engine-fast:OK · face-fast:OK | $10.2462 | $0.0055 | 147,283/76,748 |
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

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-05 06:52:55  ▪ gate engine-fast pass [session]  (41.8s)
08-05 06:52:55  ▪ gate face-fast pass [session]  (4.6s)
08-05 06:52:56  • session #16 K4 → Advanced · done K4.4 · 5 commit(s)  (47m50s)
08-05 06:59:05  ▪ gate engine-fast pass [phase]  (0.0s)
08-05 06:59:05  ▪ gate face-fast pass [phase]  (0.0s)
08-05 06:59:05  ▪ gate engine-full FAIL [phase]  (2m59s)
08-05 06:59:05  ▪ gate face-full pass [phase]  (9.0s)
08-05 06:59:05  • session #17 K4 Fix started (attempt 2/8)
08-05 07:06:14  ▪ gate engine-fast pass [session]  (44.2s)
08-05 07:06:14  ▪ gate face-fast pass [session]  (3.4s)
08-05 07:06:14  • session #17 K4 → Progress · 1 commit(s)  (7m08s)
08-05 07:09:10  ▪ gate engine-fast pass [phase]  (0.0s)
08-05 07:09:10  ▪ gate face-fast pass [phase]  (0.0s)
08-05 07:09:10  ▪ gate engine-full pass [phase]  (2m52s)
08-05 07:09:10  ▪ gate face-full pass [phase]  (1.1s)
08-05 07:09:10  ✓ checkpoint K4.4 confirmed
08-05 07:09:10  ▸ stage K4 confirmed  (2h22m02s)
08-05 07:09:11  ▸ stage K5 entered — The result contract and the channels
08-05 07:09:11  • session #18 K5 Deliver started (attempt 1/8)
08-05 07:51:11  ▪ gate engine-fast pass [session]  (46.8s)
08-05 07:51:11  ▪ gate face-fast pass [session]  (3.0s)
08-05 07:51:11  • session #18 K5 → Advanced · done K5.1,K5.2 · 7 commit(s)  (42m00s)
08-05 07:51:11  • session #19 K5 Deliver started (attempt 1/8)
08-05 08:33:07  ▪ gate engine-fast pass [session]  (44.6s)
08-05 08:33:07  ▪ gate face-fast pass [session]  (4.1s)
08-05 08:33:08  • session #19 K5 → Advanced · done K5.3 · 5 commit(s)  (41m56s)
08-05 08:33:08  • session #20 K5 Deliver started (attempt 1/8)
08-05 09:20:15  ▪ gate engine-fast pass [session]  (47.0s)
08-05 09:20:15  ▪ gate face-fast pass [session]  (3.7s)
08-05 09:20:16  • session #20 K5 → Advanced · done K5.4 · 4 commit(s)  (47m07s)
08-05 09:23:26  ▪ gate engine-fast pass [phase]  (0.0s)
08-05 09:23:26  ▪ gate face-fast pass [phase]  (0.0s)
08-05 09:23:26  ▪ gate engine-full pass [phase]  (2m59s)
08-05 09:23:26  ▪ gate face-full pass [phase]  (9.0s)
08-05 09:23:26  ✓ checkpoint K5.4 confirmed
08-05 09:23:26  ▸ stage K5 confirmed  (2h14m15s)
08-05 09:23:26  ▸ stage K6 entered — The surfaces read
08-05 09:23:26  • session #21 K6 Deliver started (attempt 1/8)
08-05 09:35:52  ▪ gate engine-fast pass [session]  (46.2s)
08-05 09:35:52  ▪ gate face-fast pass [session]  (2.9s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 21 · retries 4 (19 %) · overall Warn
⚠ [context-saturation] session #10: 23,623,416 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #18: 23,816,486 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #19: 24,146,164 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #1: 24,653,507 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #20: 25,687,748 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #7: 24,094,247 context tokens (≥ 20,000,000)
⚠ [gate-oscillation] gate 'engine-full' flipped pass/fail 5x
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/karvan
working tree: clean
vs upstream: up to date
```

### Commits by session

- **s14 (K4 Deliver)** — 2 commit(s):
  - [`1ec436f`](https://github.com/shaahink/conductor/commit/1ec436f) feat(doctor): K4.2 - doctor warns when the cap is under the floor or the nudge under the median closer
  - [`1fcbd0b`](https://github.com/shaahink/conductor/commit/1fcbd0b) feat(budget): K4.2 - the engine measures its own token ceiling and prescribes the next one
- **s15 (K4 Deliver)** — 2 commit(s):
  - [`725f4de`](https://github.com/shaahink/conductor/commit/725f4de) docs(evidence): K4.3 evidence artifact and the handoff to K4.4
  - [`20842e2`](https://github.com/shaahink/conductor/commit/20842e2) feat(money): K4.3 - conductor money prices a run from its own ledger
- **s16 (K4 Deliver)** — 5 commit(s):
  - [`ad6f2de`](https://github.com/shaahink/conductor/commit/ad6f2de) feat(face): K4.4 - the demo computes its token rail instead of freezing one
  - [`60be019`](https://github.com/shaahink/conductor/commit/60be019) docs(evidence): K4.4 evidence artifact and the handoff to K5.1
  - [`2c265d5`](https://github.com/shaahink/conductor/commit/2c265d5) test(budget): K4.4 - the gauge measured on a real run, not on hand-written events
  - [`eafd49b`](https://github.com/shaahink/conductor/commit/eafd49b) feat(face): K4.4 - the Home ceiling row, and a gauge built to be multiplied
  - [`b4ea829`](https://github.com/shaahink/conductor/commit/b4ea829) feat(budget): K4.4 - the token rail goes on the wire beside the money one
- **s17 (K4 Fix)** — 1 commit(s):
  - [`1b4e87c`](https://github.com/shaahink/conductor/commit/1b4e87c) fix(history): the archive probes for newly_done like every other column
- **s18 (K5 Deliver)** — 7 commit(s):
  - [`ff7812d`](https://github.com/shaahink/conductor/commit/ff7812d) docs(tracker): s18 handoff - K5.1 and K5.2 done, K5.3 engine half committed
  - [`e618c06`](https://github.com/shaahink/conductor/commit/e618c06) feat(evidence): K5.3 part 1 - the model, the event and the registry (no surface yet)
  - [`b9a54d0`](https://github.com/shaahink/conductor/commit/b9a54d0) docs(tracker): K5.1 and K5.2 landed - handoff points at K5.3
  - [`d1f55cc`](https://github.com/shaahink/conductor/commit/d1f55cc) feat(telegram): K5.2 - the five defects that made the feed unreadable
  - [`379abc7`](https://github.com/shaahink/conductor/commit/379abc7) docs(tracker): K5.1 handoff - the result contract, and what it costs to add prompt prose
  - [`ae7ada5`](https://github.com/shaahink/conductor/commit/ae7ada5) docs(result): K5.1 - the templates teach the format the engine now parses
  - [`c04175d`](https://github.com/shaahink/conductor/commit/c04175d) feat(result): K5.1 - one format for the session result, one parser for it
- **s19 (K5 Deliver)** — 5 commit(s):
  - [`f6900c1`](https://github.com/shaahink/conductor/commit/f6900c1) docs(tracker): K5.3 claimed and handed off - evidence is first class end to end
  - [`370aba1`](https://github.com/shaahink/conductor/commit/370aba1) fix(face): K5.3 - elide an evidence path from the left, and rebaseline two goldens
  - [`f4b272f`](https://github.com/shaahink/conductor/commit/f4b272f) feat(face): K5.3 part 4 - the evidence surface, re-homed rather than an eleventh tab
  - [`fe5ab5f`](https://github.com/shaahink/conductor/commit/fe5ab5f) feat(evidence): K5.3 part 3 - GET /evidence, and the ratchet reds the engine half left
  - [`6df3a58`](https://github.com/shaahink/conductor/commit/6df3a58) test(evidence): K5.3 part 2 - the tests, and the defect they found
- **s20 (K5 Deliver)** — 4 commit(s):
  - [`cc7ad14`](https://github.com/shaahink/conductor/commit/cc7ad14) docs(tracker): K5.4 claimed and handed off - the composition layer is closed
  - [`aaee340`](https://github.com/shaahink/conductor/commit/aaee340) feat(telegram): K5.4 part 3 - the templates the owner edits, and the push-only ADR
  - [`baa2c56`](https://github.com/shaahink/conductor/commit/baa2c56) feat(telegram): K5.4 part 2 - the composition, so a push says which checkout and what is left
  - [`43dd6d2`](https://github.com/shaahink/conductor/commit/43dd6d2) feat(telegram): K5.4 part 1 - the transport, so a screenshot arrives and a long message is not dropped
- **s21 (K6 Deliver)** — 2 commit(s):
  - [`5fa8150`](https://github.com/shaahink/conductor/commit/5fa8150) docs(tracker): K6.1 claimed and handed off - the conventions are settled
  - [`76b66aa`](https://github.com/shaahink/conductor/commit/76b66aa) docs(face): K6.1 - the TUI conventions ADR, and the measurement that settles it

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

> SESSION-RESULT: K6.1 done: ADR 0006 settles TUI conventions, backed by a measured 389-keystroke dead pane
> - Six decisions K6.2-K6.4 implement without re-deciding, each anchored to a fetched file+symbol in glow, soft-serve, gh-dash or lazygit; bubbles v2 measured at charm.land/bubbles/v2 v2.1.1, the path K6.2 blocks on.
> - Root cause measured not inferred: unclamped offset in Update, clamp only in a renderer-local copy. Report freezes for 389 keystrokes; Knowledge has no paging or jump keys at all. Filed bug #30, which K6.2 closes by construction.
> - Stale ADR index (stuck at 0003) now lists 0004-0006; STYLE.md's Keys section points at the ADR so the next face change reads it before inventing a…

## Tracker handoff

```
last: **K6.1 DONE** (s21) - `76b66aa`, evidence `.conductor/evidence/K6/K6.1-tui-conventions-adr.md`.
  `docs/dev/adr/0006-tui-conventions.md` makes six decisions K6.2-K6.4 implement without re-deciding,
  each anchored to a file+symbol in glow / soft-serve / gh-dash / lazygit that was actually fetched.
  It rests on a measurement: `m.reportScroll++` (`tab_report.go:46`) has NO clamp in `Update` - the
  only clamp is a renderer-local copy (`:83`) never written back - so 400 `down` presses left the
  offset at 400 against a body that stops at 12, and **389 `up` presses** were needed before one line
  moved. That is the owner's "can't read long text". Bug #30; K6.2 closes it by construction.
next: **K6.2** - declare `charm.land/bubbles/v2 v2.1.1` (measured, matches bubbletea v2) and move
  Report + Knowledge onto a `viewport`. Read ADR 0006 first, not the four upstream repos again.
red: none. `go build`, `go vet`, tui + widgets suites green. bugs #29 (K7.2) and #30 (K6.2) open.
watch: the mnemonic loop (`update.go:608`) is an EXACT-string match resolving before any pane handler,
  so lowercase `k`/`b`/`g` are unreachable in a pane (this is why Knowledge's `k` does nothing) while
  uppercase `G` is free. Bind `↓`/`j` `↑`, `d`/`u`, `pgdn`/`f` `pgup`, `end`/`G` `home` - and nothing
  else. Evidence files need `git add -f`: `.conductor/.gitignore` line 1 is `*`.
```
