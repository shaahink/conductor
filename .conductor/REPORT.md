# Conductor — Karvansara edge - gates that can't be gamed, and the courier run report

_Updated 2026-08-19 14:27 UTC · branch `feat/karvansara-edge` · HEAD `fe0da1b`_

**Status:** Idle
**Stage:** KS4 — Verification that can't be gamed · attempts used 0 · working ▸ KS4.3
**Checkpoints:** 16/24 done · **Sessions run:** 16 · **Cost:** $214.3486 (agent $214.2018 + gates $0.1468) · **Tokens:** 3,203,108 in / 1,547,548 out
**Confirmed phases:** KS11, KS7, KS6

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| KS11 | Chapar - the remote surface: profiles, onboarding, evidence on demand | ██████████ 5/5 | confirmed ✓ |
| KS7 | Platform catch-up - posture, hooks, usage, lifecycle, context economics | ██████████ 5/5 | confirmed ✓ |
| KS6 | Quality lane - hygiene that buys design | ██████████ 4/4 | confirmed ✓ |
| KS4 | Verification that can't be gamed | ████░░░░░░ 2/5 | **← active** |
| KS8 | Interop - the run as a readable artifact (cut-first) | ░░░░░░░░░░ 0/2 | todo |
| KS12 | Ship edge - close the era | ░░░░░░░░░░ 0/3 | todo |

<details> ✅<summary>KS11 — Chapar - the remote surface: profiles, onboarding, evidence on demand (5/5)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS11.1 | The messenger seam: composition, chat profiles and evidence browsing extracted channel-agnostic; TelegramService becomes the transport adapter; golden replay proves current pushes byte-identical through the seam; a fake channel drives the full surface in tests; an architecture test forbids Telegram types outside the adapter | ✅ DONE | [`7e64866`](https://github.com/shaahink/conductor/commit/7e64866) |
| KS11.2 | Profiles admin and observer, per chat: old-shape allowedChatIds plans behave byte-identically (pinned); an unknown profile string is refused by name at plan load; the observer surface is closed to status/tasks/progress/evidence/daily, a control or inject attempt refused by name - proven by an exhaustive command-by-profile matrix test | ✅ DONE | [`1471ef9`](https://github.com/shaahink/conductor/commit/1471ef9) |
| KS11.3 | Onboarding + the push grammar: run start and /start post a per-profile onboarding message (what the run is, what will be pushed, what this chat may ask); every push type recomposed to headline / proof / telemetry with money and tokens in monospace; goldens pin both profiles' renderings; a checkpoint push reads standalone | ✅ DONE | [`1471ef9`](https://github.com/shaahink/conductor/commit/1471ef9) |
| KS11.4 | Evidence on demand: /evidence lists checkpoints with evidence, /evidence with an id sends the artifact (document upload for files, chunked text otherwise) with size caps and a per-chat rate limit; an observer pulls a real evidence artifact end-to-end in the rig; the clip constants no longer bound what a reader can reach | ✅ DONE | [`df5048e`](https://github.com/shaahink/conductor/commit/df5048e) |
| KS11.5 | Metrics on demand: /progress /money /tokens answer with figures that cross-check against status and money on the same run.db to the cent (billed money only, no price table in the diff); the daily digest re-rendered in the same grammar, golden pinned | ✅ DONE | [`d6be308`](https://github.com/shaahink/conductor/commit/d6be308) |

</details>

<details> ✅<summary>KS7 — Platform catch-up - posture, hooks, usage, lifecycle, context economics (5/5)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS7.1 | Permission posture: an allowlist/deny settings profile replaces dangerously-skip-permissions for unattended runs if the installed CLI sustains it - a karvan-class stage runs green under the restricted profile with refusals telemetered, OR a filed finding says precisely why not; blast-radius posture stated honestly in ARCHITECTURE.md | ✅ DONE | [`0c3380f`](https://github.com/shaahink/conductor/commit/0c3380f) |
| KS7.2 | Hooks as ground truth: tool events by hook (extending the hook-budget channel) become the primary source, transcript parsing the fallback; hook-derived digests match transcript-derived on a replay corpus; a hook-less agent still works; digest claim-counting (bug 19 class) fixed; skills-vs-promptExtra decided and recorded | ✅ DONE | [`5b8d56e`](https://github.com/shaahink/conductor/commit/5b8d56e) |
| KS7.3 | Cost/usage: per-turn usage with cache split parsed from the stream; OTel emit mirroring gen_ai names from the event log; an OTLP collector renders a run's spans; the per-turn context curve reconciles with K4.1's derivation | ✅ DONE | [`5794417`](https://github.com/shaahink/conductor/commit/5794417) |
| KS7.4 | Session lifecycle: fork-instead-of-cold-resume for fix/audit sessions where supported, with the measured token delta vs the resume baseline; resume flags re-verified; model lineup and context ceilings re-measured into TOKEN-BUDGET-TUNING | ✅ DONE | [`5794417`](https://github.com/shaahink/conductor/commit/5794417) |
| KS7.5 | Context economics (B7): gate output truncated in-prompt with full text as an evidence file; RepoMapBattery + definition-of-done recap battery on the IPromptBattery seam; templates teach search-delegation; measured cache-read tokens per session DROP vs the karvan baseline on a comparable stage, reported by conductor budget | ✅ DONE | [`3d7414a`](https://github.com/shaahink/conductor/commit/3d7414a) |

</details>

<details> ✅<summary>KS6 — Quality lane - hygiene that buys design (4/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS6.1 | Curated Roslynator set (~25 design-shaped rules) as errors, everything else explicitly off, each rule adopted with a one-line reason | ✅ DONE | [`af6d93e`](https://github.com/shaahink/conductor/commit/af6d93e) |
| KS6.2 | Analyzer-debt count ratchet extending ratchet.ps1 semantics; the referee not editable by the agent - a seeded baseline rewrite goes red | ✅ DONE | [`0cb514d`](https://github.com/shaahink/conductor/commit/0cb514d) |
| KS6.3 | Complexity budgets (CA1502/1505/1506) with ratchets; first targets the largest partial surfaces - VerdictEngine (8 files) and ControlPlaneServer (11) | ✅ DONE | [`094c5c3`](https://github.com/shaahink/conductor/commit/094c5c3) |
| KS6.4 | The pure evidence-to-verdict function extracted from VerdictEngine - the taxonomy testable without the loop; the seam KS4.5 plugs into | ✅ DONE | [`5da5260`](https://github.com/shaahink/conductor/commit/5da5260) |

</details>

<details><summary>KS4 — Verification that can't be gamed (2/5)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS4.1 | Holdout gates: a visibility holdout gate class excluded from prompts, tool contract and agent-readable logs, run only at verdict time; grep of composed prompt + transcript proves absence; a seeded gaming fake-agent passes visible gates, fails holdout, verdict red | ✅ DONE | [`3365a3d`](https://github.com/shaahink/conductor/commit/3365a3d) |
| KS4.2 | Regression gate class (PASS-TO-PASS semantics): nothing-that-worked-broke as a named class with distinct reporting; a seeded regression flips the verdict with the class named in evidence | ✅ DONE | - |
| KS4.3 | Mutation gate kind: mutation-score >= threshold, diff-scoped, Stryker.NET first; a checkpoint adding tests must clear the score on changed files; an era-boundary run on conductor's own suite recorded | ⬜ TODO | - |
| KS4.4 | Worktree-per-stage-attempt: each attempt in a worktree, failed attempt drops the tree, verdict receives the clean attempt diff, merge ff-only on green, never branch -D an unmerged branch (lanes L1.3 fix per ND-8, amendment committed); Windows lock/removal proven; orphan sweep at startup | ⬜ TODO | - |
| KS4.5 | Judge as evidence, never verdict: second-model review joins the evidence taxonomy through KS6.4's seam as an advisory row; judge disagreement recorded as evidence; a test asserts NO code path lets a judge score flip a gate verdict | ⬜ TODO | - |

</details>

<details><summary>KS8 — Interop - the run as a readable artifact (cut-first) (0/2)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS8.1 | Read-only MCP surface: history/status/money as MCP resources, control ops excluded by design with the ADR citing MCP's 2026 attack record; an MCP client lists runs and quotes reconciled status; no write tool exists on the surface | ⬜ TODO | - |
| KS8.2 | ATIF trajectory export from the fold (history export, billed costs included) validating against the ATIF schema on the karvan-core run; AGENTS.md generated/honored via the CLAUDE.md-import pattern | ⬜ TODO | - |

</details>

<details><summary>KS12 — Ship edge - close the era (0/3)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS12.1 | Internal record: ARCHITECTURE.md + docs/dev reconciled for everything edge changed; closure ledger naming every bug/followup row closed here or its living owner (bug 44 and the KS10.1 inherited gaps included); conductor budget re-measured into TOKEN-BUDGET-TUNING - the number the next era compiles against | ⬜ TODO | - |
| KS12.2 | Published surface: README + docs user set (operating.md carries the observer-profile and group-chat setup; plan-config.md carries the telegram chats shape and every key edge added) + CHANGELOG Unreleased written as the release body; docs-match-reality tests extended and proven red on a seeded stale doc; payesh harvest re-run on a branch with a PR, never pushed to main | ⬜ TODO | - |
| KS12.3 | OWNER-ONLY: merge feat/karvansara-edge to master, tag and release through the pipeline with KS12.2's CHANGELOG section as the body, reinstall (no other live run on the machine first), github sync --backfill of THIS run, merge the payesh PR, and move CORE-TRACKER.md + EDGE-TRACKER.md + the era brief to docs/history - the Karvansara era closes | ⬜ TODO | - |

</details>

## Sessions

| # | Stage | Kind | Att | Started (UTC) | Dur | Outcome | New DONE | Commits | Gates | Cost | Overhead | Tokens |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 1 | KS11 | Deliver | 1 | 08-18 18:09 | 0:03 | Interrupted |  | 0 |  |  |  | 70,721/104 |
| 2 | KS11 | Resume | 1r1 | 08-18 18:17 | 0:37 | Advanced | KS11.1 | 4 | engine-fast:OK · face-fast:OK | $15.3716 | $0.0096 | 257,598/111,958 |
| 3 | KS11 | Deliver | 1 | 08-18 18:56 | 0:49 | Advanced | KS11.2 KS11.3 | 3 | engine-fast:OK · face-fast:OK | $21.1372 | $0.0090 | 278,057/137,875 |
| 4 | KS11 | Deliver | 1 | 08-18 19:47 | 0:26 | Advanced | KS11.4 | 4 | engine-fast:OK · face-fast:OK | $11.2847 | $0.0089 | 192,517/70,099 |
| 5 | KS11 | Deliver | 1 | 08-18 20:15 | 0:39 | Advanced | KS11.5 | 6 | engine-fast:OK · face-fast:OK | $13.5431 | $0.0092 | 208,187/80,357 |
| 6 | KS7 | Deliver | 1 | 08-18 21:02 | 0:41 | Advanced | KS7.1 | 4 | engine-fast:OK · face-fast:OK | $17.9837 | $0.0090 | 230,547/106,999 |
| 7 | KS7 | Deliver | 1 | 08-18 21:46 | 0:37 | Advanced | KS7.2 | 2 | engine-fast:OK · face-fast:OK | $14.4092 | $0.0119 | 197,912/102,671 |
| 8 | KS7 | Deliver | 1 | 08-18 22:25 | 0:47 | Advanced | KS7.3 KS7.4 | 3 | engine-fast:OK · face-fast:OK | $20.2494 | $0.0092 | 249,486/130,261 |
| 9 | KS7 | Deliver | 1 | 08-18 23:14 | 0:46 | Advanced | KS7.5 | 2 | engine-fast:OK · face-fast:OK | $10.9477 | $0.0083 | 171,145/78,053 |
| 10 | KS7 | Fix | 2 | 08-19 00:11 | 0:22 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $5.3947 | $0.0114 | 113,064/40,939 |
| 11 | KS6 | Deliver | 1 | 08-19 00:39 | 0:52 | Advanced | KS6.1 | 2 | engine-fast:OK · face-fast:OK | $20.6082 | $0.0103 | 256,670/149,412 |
| 12 | KS6 | Deliver | 1 | 08-19 01:33 | 0:33 | Advanced | KS6.2 | 5 | engine-fast:OK · face-fast:OK | $12.1019 | $0.0086 | 199,951/114,585 |
| 13 | KS6 | Deliver | 1 | 08-19 11:48 | 0:24 | Advanced | KS6.3 | 4 | engine-fast:OK · face-fast:OK | $7.6448 | $0.0102 | 141,705/85,286 |
| 14 | KS6 | Deliver | 1 | 08-19 12:15 | 0:35 | Advanced | KS6.4 | 4 | engine-fast:OK · face-fast:OK | $10.9090 | $0.0105 | 193,920/121,109 |
| 15 | KS4 | Deliver | 1 | 08-19 12:56 | 0:43 | Advanced | KS4.1 | 3 | engine-fast:OK · face-fast:OK | $17.6885 | $0.0101 | 217,984/101,516 |
| 16 | KS4 | Deliver | 1 | 08-19 13:42 | 0:43 | Advanced | KS4.2 | 3 | engine-fast:OK · face-fast:OK | $14.9280 | $0.0107 | 223,644/116,324 |

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 15 | 265.7M | 98.4% | $199.41 | 15 | 17.7M | $13.29 |
| stage KS11 | 5 | 83.9M | 98.4% | $61.37 | 5 | 16.8M | $12.27 |
| stage KS7 | 5 | 97.1M | 98.5% | $69.03 | 5 | 19.4M | $13.81 |
| stage KS6 | 4 | 63.5M | 98.0% | $51.30 | 4 | 15.9M | $12.83 |
| stage KS4 | 1 | 21.2M | 98.5% | $17.70 | 1 | 21.2M | $17.70 |
| 2026-08 | 15 | 265.7M | 98.4% | $199.41 | 15 | 17.7M | $13.29 |

_Where the money goes: agent $199.27 (100%) · gate $0.14 (0%) · blended $0.75/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-19 01:39:50  ✓ checkpoint KS7.1 confirmed
08-19 01:39:50  ✓ checkpoint KS7.2 confirmed
08-19 01:39:50  ✓ checkpoint KS7.3 confirmed
08-19 01:39:50  ✓ checkpoint KS7.4 confirmed
08-19 01:39:50  ✓ checkpoint KS7.5 confirmed
08-19 01:39:50  ▸ stage KS7 confirmed  (3h37m02s)
08-19 01:39:50  ▸ stage KS6 entered — Quality lane - hygiene that buys design
08-19 01:39:50  • session #11 KS6 Deliver started (attempt 1/8)
08-19 02:33:43  ▪ gate engine-fast pass [session]  (1m11s)
08-19 02:33:43  ▪ gate face-fast pass [session]  (30.9s)
08-19 02:33:43  • session #11 KS6 → Advanced · done KS6.1 · 2 commit(s)  (53m52s)
08-19 02:33:48  • session #12 KS6 Deliver started (attempt 1/8)
08-19 03:08:47  ▪ gate engine-fast pass [session]  (1m09s)
08-19 03:08:47  ▪ gate face-fast pass [session]  (16.1s)
08-19 03:08:47  • session #12 KS6 → Advanced · done KS6.2 · 5 commit(s)  (34m59s)
08-19 12:48:34  • session #13 KS6 Deliver started (attempt 1/8)
08-19 13:15:00  ▪ gate engine-fast pass [session]  (1m05s)
08-19 13:15:00  ▪ gate face-fast pass [session]  (37.1s)
08-19 13:15:00  • session #13 KS6 → Advanced · done KS6.3 · 4 commit(s)  (26m26s)
08-19 13:15:08  • session #14 KS6 Deliver started (attempt 1/8)
08-19 13:51:55  ▪ gate engine-fast pass [session]  (1m12s)
08-19 13:51:55  ▪ gate face-fast pass [session]  (31.7s)
08-19 13:51:55  • session #14 KS6 → Advanced · done KS6.4 · 4 commit(s)  (36m47s)
08-19 13:56:59  ▪ gate engine-fast pass [phase]  (0.0s)
08-19 13:56:59  ▪ gate face-fast pass [phase]  (0.0s)
08-19 13:56:59  ▪ gate engine-full pass [phase]  (4m30s)
08-19 13:56:59  ▪ gate face-full pass [phase]  (26.1s)
08-19 13:56:59  ✓ checkpoint KS6.1 confirmed
08-19 13:56:59  ✓ checkpoint KS6.2 confirmed
08-19 13:56:59  ✓ checkpoint KS6.3 confirmed
08-19 13:56:59  ✓ checkpoint KS6.4 confirmed
08-19 13:56:59  ▸ stage KS6 confirmed  (12h17m08s)
08-19 13:56:59  ▸ stage KS4 entered — Verification that can't be gamed
08-19 13:56:59  • session #15 KS4 Deliver started (attempt 1/10)
08-19 14:42:18  ▪ gate engine-fast pass [session]  (1m11s)
08-19 14:42:18  ▪ gate face-fast pass [session]  (29.5s)
08-19 14:42:19  • session #15 KS4 → Advanced · done KS4.1 · 3 commit(s)  (45m19s)
08-19 14:42:24  • session #16 KS4 Deliver started (attempt 1/10)
08-19 15:27:28  ▪ gate engine-fast pass [session]  (1m12s)
08-19 15:27:28  ▪ gate face-fast pass [session]  (34.6s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 16 · retries 1 (6 %) · overall Warn
⚠ [context-saturation] session #11: 28,600,968 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #15: 20,892,539 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #3: 28,433,638 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 25,994,603 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #8: 28,984,293 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/karvansara-edge
working tree: clean
```

### Commits by session

- **s9 (KS7 Deliver)** — 2 commit(s):
  - [`52a2b79`](https://github.com/shaahink/conductor/commit/52a2b79) docs(ks7): the handoff for the next session - stage KS7 closed, and the argv ceiling
  - [`3d7414a`](https://github.com/shaahink/conductor/commit/3d7414a) feat(ks7): the two batteries reach a prompt, and the ceiling a prompt has to live under
- **s10 (KS7 Fix)** — 2 commit(s):
  - [`f16780d`](https://github.com/shaahink/conductor/commit/f16780d) docs(ks7): the handoff for the next session - the battery is green, and the argv number that was measured on the wrong prompt
  - [`a36ea6d`](https://github.com/shaahink/conductor/commit/a36ea6d) fix(ks7): the record catches up with the code, and the prompt gets back under its ceiling
- **s11 (KS6 Deliver)** — 2 commit(s):
  - [`876cc78`](https://github.com/shaahink/conductor/commit/876cc78) docs(ks6): the handoff for the next session - the inert switch, and what the 45 pragmas actually are
  - [`af6d93e`](https://github.com/shaahink/conductor/commit/af6d93e) feat(ks6): a curated Roslynator set, and the master switch that turned out to be inert
- **s12 (KS6 Deliver)** — 5 commit(s):
  - [`8ddbe37`](https://github.com/shaahink/conductor/commit/8ddbe37) fix(ks6): a pragma is only a pragma at the start of a line
  - [`965e7ac`](https://github.com/shaahink/conductor/commit/965e7ac) docs(ks6): the handoff for the next session - the referee that was refereeing itself
  - [`42f846f`](https://github.com/shaahink/conductor/commit/42f846f) test(ks6): the eight seeded attacks, made permanent
  - [`4b25081`](https://github.com/shaahink/conductor/commit/4b25081) fix(ks6): the anchor was one commit, and one commit is inside the game
  - [`0cb514d`](https://github.com/shaahink/conductor/commit/0cb514d) feat(ks6): the analyzer-debt ratchet, and 14 pragmas that were guarding nothing
- **s13 (KS6 Deliver)** — 4 commit(s):
  - [`f830e88`](https://github.com/shaahink/conductor/commit/f830e88) docs(ks6): the handoff for KS6.4 - the surface it was aimed at is not the one that binds
  - [`3901ac1`](https://github.com/shaahink/conductor/commit/3901ac1) docs(ks6): the KS6.3 evidence - what binds, what does not, and what it hands KS6.4
  - [`71c1e64`](https://github.com/shaahink/conductor/commit/71c1e64) test(ks6): fourteen seeded loosenings, and the one that proved the gate blind
  - [`094c5c3`](https://github.com/shaahink/conductor/commit/094c5c3) feat(ks6): complexity budgets that bind, and the config typo that voids them in silence
- **s14 (KS6 Deliver)** — 4 commit(s):
  - [`20e1eab`](https://github.com/shaahink/conductor/commit/20e1eab) docs(ks6): the KS6.4 handoff - the seam KS4.5 needs is already built and already asserted
  - [`67be608`](https://github.com/shaahink/conductor/commit/67be608) docs(ks6): the KS6.4 evidence - what moved, and the metric that cannot reward it
  - [`a8a9066`](https://github.com/shaahink/conductor/commit/a8a9066) refactor(ks6): the verdict leaves the loop - gather, decide, apply
  - [`5da5260`](https://github.com/shaahink/conductor/commit/5da5260) feat(ks6): the evidence taxonomy as data, and a verdict function with no loop in it
- **s15 (KS4 Deliver)** — 3 commit(s):
  - [`cf5890f`](https://github.com/shaahink/conductor/commit/cf5890f) docs(ks4): the KS4.1 handoff - the rule the next checkpoint should reuse, and a build that lies
  - [`daa6e8c`](https://github.com/shaahink/conductor/commit/daa6e8c) feat(ks4): the gaming agent that passes every gate it can see, and goes red anyway
  - [`3365a3d`](https://github.com/shaahink/conductor/commit/3365a3d) feat(ks4): the holdout gate class - redacted where it is produced, not where it is shown
- **s16 (KS4 Deliver)** — 3 commit(s):
  - [`fe0da1b`](https://github.com/shaahink/conductor/commit/fe0da1b) docs(ks4): the KS4.2 handoff - the renderer a unit test cannot reach
  - [`6c9168a`](https://github.com/shaahink/conductor/commit/6c9168a) feat(ks4): the fix brief a regression writes, and the live proof it is written
  - [`8d649ea`](https://github.com/shaahink/conductor/commit/8d649ea) feat(ks4): the gate class that reads what still passes, not what failed

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

> **KS4.2 landed - regression gate class, PASS-TO-PASS, proven live and red**
> - `class: regression` red on a gate that exits 0 but lost a check; one line (IsGreen) carries it to every battery consumer, reporting deliberately distinct
> - Live 3-session rig: seeded deletion flips the verdict to GatesRed with the class named; contrast leg (a check added) stays green; fix brief names the missing check
> - Found the second fix-brief renderer that a unit test cannot reach - the live prompt said "(no gate output captured)"; both renderers now share one block
>
> artefacts: 8d649ea, 6c9168a, fe0da1b, src/Conductor.Core/Models/GateClass.cs, src/Conductor.Core/PassSetExtractor.cs, src/Conductor.Core/GateRunner.cs, src/Conductor.Core/GateFailureSpill.cs, src/Conductor.Core/Store/Migrations/v15_gate_pass_sets.sql
>
> evidence: .conductor/evidence/KS4/KS4.2-regression-gates.md
>
> gaps: KS4.3/4.4/4.5 still TODO. The tree is schema v15 while the installed engine is v14 - a fresh build must never open this repo's run.db until the KS12.3 reinstall. No owner-only rebaseline verb for a legitimate rename; documented workaround is optional:true while it lands.

## Tracker handoff

```
last: KS4.2 DONE (8d649ea, 6c9168a). `class: regression`, PASS-TO-PASS. A gate that EXITS 0 is red
  when a check that passed earlier in this run is no longer reported passing - deleted, renamed,
  skipped, filtered out. One line carries it everywhere (GateResult.IsGreen); the reporting is
  deliberately NOT shared (glyph REGRESSION, its own fix-brief block, its own verdict reason).
  Baseline advances only on a clean pass, so a deletion cannot be laundered by one red session.
  23 new tests, 447 green in the affected classes. Evidence: .conductor/evidence/KS4/KS4.2-regression-gates.md
THE RULE KS4.3 SHOULD REUSE: a new failure SHAPE must be walked to every renderer, and only a live
  rig finds them. There are TWO fix-brief renderers - GateRunner.FailureDetails (conductor gate,
  workflow path) and GateFailureSpill.Render (every ordinary session) - and my unit test was green
  while the live fix session was handed "(no gate output captured)". Read the composed prompt off disk.
RIG FACTS, measured: VerifyEachDelivery DEFAULTS TRUE, so History[1] in a multi-session rig is the
  verifier, not session 2. And a cmd.exe fake agent DIES on a fix session (prompt is an argument,
  cmd caps at 8191 chars, the fix prompt is 8.1k) - use powershell.exe.
DB WARNING: the tree is schema v15 now (gate_pass_sets); the installed engine driving this run is
  v14 and REFUSES a newer db. Never point a fresh build at this repo's .conductor/run.db.
next: KS4.3 mutation gate kind (Stryker.NET, diff-scoped). Bugs #53/#54/#55/#57 open.
```
