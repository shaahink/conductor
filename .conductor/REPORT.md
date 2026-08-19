# Conductor — Karvansara edge - gates that can't be gamed, and the courier run report

_Updated 2026-08-18 23:14 UTC · branch `feat/karvansara-edge` · HEAD `ba39a6d`_

**Status:** Idle
**Stage:** KS7 — Platform catch-up - posture, hooks, usage, lifecycle, context economics · attempts used 0 · working ▸ KS7.5
**Checkpoints:** 9/24 done · **Sessions run:** 8 · **Cost:** $114.0458 (agent $113.9790 + gates $0.0668) · **Tokens:** 1,685,025 in / 740,324 out
**Confirmed phases:** KS11

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| KS11 | Chapar - the remote surface: profiles, onboarding, evidence on demand | ██████████ 5/5 | confirmed ✓ |
| KS7 | Platform catch-up - posture, hooks, usage, lifecycle, context economics | ████████░░ 4/5 | **← active** |
| KS6 | Quality lane - hygiene that buys design | ░░░░░░░░░░ 0/4 | todo |
| KS4 | Verification that can't be gamed | ░░░░░░░░░░ 0/5 | todo |
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

<details><summary>KS7 — Platform catch-up - posture, hooks, usage, lifecycle, context economics (4/5)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS7.1 | Permission posture: an allowlist/deny settings profile replaces dangerously-skip-permissions for unattended runs if the installed CLI sustains it - a karvan-class stage runs green under the restricted profile with refusals telemetered, OR a filed finding says precisely why not; blast-radius posture stated honestly in ARCHITECTURE.md | ✅ DONE | [`0c3380f`](https://github.com/shaahink/conductor/commit/0c3380f) |
| KS7.2 | Hooks as ground truth: tool events by hook (extending the hook-budget channel) become the primary source, transcript parsing the fallback; hook-derived digests match transcript-derived on a replay corpus; a hook-less agent still works; digest claim-counting (bug 19 class) fixed; skills-vs-promptExtra decided and recorded | ✅ DONE | [`5b8d56e`](https://github.com/shaahink/conductor/commit/5b8d56e) |
| KS7.3 | Cost/usage: per-turn usage with cache split parsed from the stream; OTel emit mirroring gen_ai names from the event log; an OTLP collector renders a run's spans; the per-turn context curve reconciles with K4.1's derivation | ✅ DONE | - |
| KS7.4 | Session lifecycle: fork-instead-of-cold-resume for fix/audit sessions where supported, with the measured token delta vs the resume baseline; resume flags re-verified; model lineup and context ceilings re-measured into TOKEN-BUDGET-TUNING | ✅ DONE | - |
| KS7.5 | Context economics (B7): gate output truncated in-prompt with full text as an evidence file; RepoMapBattery + definition-of-done recap battery on the IPromptBattery seam; templates teach search-delegation; measured cache-read tokens per session DROP vs the karvan baseline on a comparable stage, reported by conductor budget | 🔄 IN PROGRESS | - |

</details>

<details><summary>KS6 — Quality lane - hygiene that buys design (0/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS6.1 | Curated Roslynator set (~25 design-shaped rules) as errors, everything else explicitly off, each rule adopted with a one-line reason | ⬜ TODO | - |
| KS6.2 | Analyzer-debt count ratchet extending ratchet.ps1 semantics; the referee not editable by the agent - a seeded baseline rewrite goes red | ⬜ TODO | - |
| KS6.3 | Complexity budgets (CA1502/1505/1506) with ratchets; first targets the largest partial surfaces - VerdictEngine (8 files) and ControlPlaneServer (11) | ⬜ TODO | - |
| KS6.4 | The pure evidence-to-verdict function extracted from VerdictEngine - the taxonomy testable without the loop; the seam KS4.5 plugs into | ⬜ TODO | - |

</details>

<details><summary>KS4 — Verification that can't be gamed (0/5)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS4.1 | Holdout gates: a visibility holdout gate class excluded from prompts, tool contract and agent-readable logs, run only at verdict time; grep of composed prompt + transcript proves absence; a seeded gaming fake-agent passes visible gates, fails holdout, verdict red | ⬜ TODO | - |
| KS4.2 | Regression gate class (PASS-TO-PASS semantics): nothing-that-worked-broke as a named class with distinct reporting; a seeded regression flips the verdict with the class named in evidence | ⬜ TODO | - |
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

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 7 | 130.2M | 98.5% | $93.79 | 7 | 18.6M | $13.40 |
| stage KS11 | 5 | 83.9M | 98.4% | $61.37 | 5 | 16.8M | $12.27 |
| stage KS7 | 2 | 46.3M | 98.6% | $32.41 | 2 | 23.2M | $16.21 |
| 2026-08 | 7 | 130.2M | 98.5% | $93.79 | 7 | 18.6M | $13.40 |

_Where the money goes: agent $93.73 (100%) · gate $0.06 (0%) · blended $0.72/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-18 19:17:04  • session #2 KS11 Resume started (attempt 1/10)
08-18 19:56:04  ▪ gate engine-fast pass [session]  (58.0s)
08-18 19:56:04  ▪ gate face-fast pass [session]  (38.0s)
08-18 19:56:05  • session #2 KS11 → Advanced · done KS11.1 · 4 commit(s)  (39m00s)
08-18 19:56:06  • session #3 KS11 Deliver started (attempt 1/10)
08-18 20:47:20  ▪ gate engine-fast pass [session]  (1m03s)
08-18 20:47:20  ▪ gate face-fast pass [session]  (27.1s)
08-18 20:47:20  • session #3 KS11 → Advanced · done KS11.2,KS11.3 · 3 commit(s)  (51m14s)
08-18 20:47:21  • session #4 KS11 Deliver started (attempt 1/10)
08-18 21:15:41  ▪ gate engine-fast pass [session]  (57.2s)
08-18 21:15:41  ▪ gate face-fast pass [session]  (31.5s)
08-18 21:15:41  • session #4 KS11 → Advanced · done KS11.4 · 4 commit(s)  (28m19s)
08-18 21:15:43  • session #5 KS11 Deliver started (attempt 1/10)
08-18 21:56:18  ▪ gate engine-fast pass [session]  (59.6s)
08-18 21:56:18  ▪ gate face-fast pass [session]  (32.6s)
08-18 21:56:19  • session #5 KS11 → Advanced · done KS11.5 · 6 commit(s)  (40m35s)
08-18 22:00:10  ▪ gate engine-fast pass [phase]  (0.0s)
08-18 22:00:10  ▪ gate face-fast pass [phase]  (0.0s)
08-18 22:00:10  ▪ gate engine-full pass [phase]  (3m20s)
08-18 22:00:10  ▪ gate face-full pass [phase]  (25.6s)
08-18 22:00:10  § owner approval requested — KS11
08-18 22:02:47  § owner approval granted — KS11
08-18 22:02:47  ✓ checkpoint KS11.1 confirmed
08-18 22:02:47  ✓ checkpoint KS11.2 confirmed
08-18 22:02:47  ✓ checkpoint KS11.3 confirmed
08-18 22:02:47  ✓ checkpoint KS11.4 confirmed
08-18 22:02:47  ✓ checkpoint KS11.5 confirmed
08-18 22:02:47  ▸ stage KS11 confirmed  (2h53m20s)
08-18 22:02:47  ▸ stage KS7 entered — Platform catch-up - posture, hooks, usage, lifecycle, context economics
08-18 22:02:47  • session #6 KS7 Deliver started (attempt 1/10)
08-18 22:46:17  ▪ gate engine-fast pass [session]  (1m02s)
08-18 22:46:17  ▪ gate face-fast pass [session]  (27.4s)
08-18 22:46:18  • session #6 KS7 → Advanced · done KS7.1 · 4 commit(s)  (43m30s)
08-18 22:46:19  • session #7 KS7 Deliver started (attempt 1/10)
08-18 23:25:28  ▪ gate engine-fast pass [session]  (1m30s)
08-18 23:25:28  ▪ gate face-fast pass [session]  (28.6s)
08-18 23:25:28  • session #7 KS7 → Advanced · done KS7.2 · 2 commit(s)  (39m09s)
08-18 23:25:29  • session #8 KS7 Deliver started (attempt 1/10)
08-19 00:14:37  ▪ gate engine-fast pass [session]  (1m06s)
08-19 00:14:37  ▪ gate face-fast pass [session]  (25.0s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 8 · retries 0 (0 %) · overall Warn
⚠ [context-saturation] session #3: 28,433,638 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 25,994,603 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/karvansara-edge
working tree: clean
```

### Commits by session

- **s2 (KS11 Resume)** — 4 commit(s):
  - [`003dfe7`](https://github.com/shaahink/conductor/commit/003dfe7) docs(tracker): KS11.1 - the handoff, pointing KS11.2 at the router
  - [`ffbff68`](https://github.com/shaahink/conductor/commit/ffbff68) test(ks11): the fake channel and the boundary that keeps the seam a boundary
  - [`897e295`](https://github.com/shaahink/conductor/commit/897e295) refactor(ks11): the messenger seam - composition, routing and browsing leave the transport
  - [`7e64866`](https://github.com/shaahink/conductor/commit/7e64866) test(ks11): the pre-seam goldens - fifteen cases of what the wire carries today
- **s3 (KS11 Deliver)** — 3 commit(s):
  - [`278602d`](https://github.com/shaahink/conductor/commit/278602d) feat(ks11): onboarding per profile, and pushes that read like evidence
  - [`7641396`](https://github.com/shaahink/conductor/commit/7641396) docs(tracker): KS11.2 - the handoff, pointing KS11.3 at the browse list
  - [`1471ef9`](https://github.com/shaahink/conductor/commit/1471ef9) feat(ks11): chat profiles - one gate, and the closed observer surface
- **s4 (KS11 Deliver)** — 4 commit(s):
  - [`192d287`](https://github.com/shaahink/conductor/commit/192d287) docs(tracker): KS11.4 - the handoff, pointing KS11.5 at the same six goldens
  - [`88ec389`](https://github.com/shaahink/conductor/commit/88ec389) docs(ks11): /evidence in operating.md, and CH-6's caveat said out loud
  - [`cb84b1e`](https://github.com/shaahink/conductor/commit/cb84b1e) test(ks11): rebaseline the six ask-line goldens for /evidence
  - [`df5048e`](https://github.com/shaahink/conductor/commit/df5048e) feat(ks11): evidence on demand - a reader asks, and the artifact arrives
- **s5 (KS11 Deliver)** — 6 commit(s):
  - [`cfbcb3e`](https://github.com/shaahink/conductor/commit/cfbcb3e) docs(ks11): correct the wire rig's per-checkpoint figure in the KS11.5 evidence
  - [`acfc042`](https://github.com/shaahink/conductor/commit/acfc042) test(ks11): the full suite behind the KS11.5 claim - 2787/2787, nothing red
  - [`63a1bdb`](https://github.com/shaahink/conductor/commit/63a1bdb) docs(tracker): KS11.5 - the handoff, and a stage that is complete
  - [`2230ccc`](https://github.com/shaahink/conductor/commit/2230ccc) docs(ks11): the KS11.5 evidence - three paths to one figure, and the wire
  - [`7d461bb`](https://github.com/shaahink/conductor/commit/7d461bb) test(ks11): rebaseline the goldens KS11.5 moved - the ask line, and the digest
  - [`d6be308`](https://github.com/shaahink/conductor/commit/d6be308) feat(ks11): the figures tier - /progress, /money, /tokens, and a digest that reads like a message
- **s6 (KS7 Deliver)** — 4 commit(s):
  - [`129d944`](https://github.com/shaahink/conductor/commit/129d944) docs(tracker): KS7.1 - the handoff, and what the next session should not re-probe
  - [`4bc1fff`](https://github.com/shaahink/conductor/commit/4bc1fff) docs(ks7): the KS7.1 evidence - two rig runs, and the conclusion that did not hold
  - [`efe1e69`](https://github.com/shaahink/conductor/commit/efe1e69) fix(ks7): a restricted posture must not shut the run's own claim path
  - [`0c3380f`](https://github.com/shaahink/conductor/commit/0c3380f) feat(ks7): the permission posture, and refusals that leave a trace
- **s7 (KS7 Deliver)** — 2 commit(s):
  - [`cf2bfb7`](https://github.com/shaahink/conductor/commit/cf2bfb7) docs(ks7): the KS7.2 evidence, the architecture section, and a decision recorded
  - [`5b8d56e`](https://github.com/shaahink/conductor/commit/5b8d56e) feat(ks7): hooks as ground truth - the call the agent made, not the one it printed
- **s8 (KS7 Deliver)** — 3 commit(s):
  - [`ba39a6d`](https://github.com/shaahink/conductor/commit/ba39a6d) feat(ks7): gate output spills to a file, and the prompt-side accounting that reframes KS7.5
  - [`87b5364`](https://github.com/shaahink/conductor/commit/87b5364) feat(ks7): a fix session branches off the work it is fixing, and the lineup is measured
  - [`5794417`](https://github.com/shaahink/conductor/commit/5794417) feat(ks7): the trace a collector renders, and the cache half that used to vanish

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

> **KS7.3 and KS7.4 landed with live proofs; KS7.5 partial, green, unclaimed**
> - KS7.3: `conductor otel` exports a run as an OTLP trace; official otelcol 0.159.0 rendered 27 spans, and the per-turn context curve matched K4.1 exactly on all 7 finished sessions. Cache split now four-way per turn.
> - KS7.4: measured `--fork-session` — composes with `--session-id`, 0.15% more tokens than resume and $0.0001 cheaper; fix/audit sessions can branch off their stage's work. Model lineup re-measured into TOKEN-BUDGET-TUNING §11.
> - KS7.5 partial: gate output spills to a file with an excerpt in-prompt; measured that a whole prompt is only 3–4% of a turn, so prompt trimming cannot move the 66% — card amended.
>
> artefacts: 5794417, 87b5364, ba39a6d, src/Conductor.Core/Telemetry/, src/Conductor.Core/Orchestration/SessionFork.cs, src/Conductor.Core/GateFailureSpill.cs, docs/dev/TOKEN-BUDGET-TUNING.md
>
> evidence: .conductor/evidence/KS7/ks7-3-cost-usage-and-otel.md, .conductor/evidence/KS7/ks7-4-fork-lifecycle.md
>
> gaps: KS7.5 unclaimed — RepoMapBattery and DefinitionOfDoneBattery are written but not registered in PromptBuilder.BatterySection, no BatteriesConfig flags, no tests, and the subagent-delegation template line is not written. Its stated exit (a measured per-session cache-read drop) needs future sessions running under the new prompts. Bugs #53 (cache_creation TTL split dropped) and #54 (MSBuild node reuse…

## Tracker handoff

```
last: KS7.3 DONE (5794417) and KS7.4 DONE (87b5364). `conductor otel` exports a run as an OTLP trace -
  official otelcol 0.159.0 rendered 27 spans of this run; the per-turn curve reconciles EXACTLY with
  K4.1 on all 7 finished sessions. Fork measured: --fork-session composes with --session-id, costs
  0.15% more than resume and $0.0001 less, so fix/audit sessions can branch without losing id control.
KS7.5 IS PARTIAL AND UNCLAIMED - the tree is green, the work is committed, do not redo it.
  landed: GateFailureSpill (wired at all 3 VerdictEngine sites), RepoMapBattery + DefinitionOfDoneBattery
  written but NOT YET REGISTERED in PromptBuilder.BatterySection - that is the single next action,
  plus BatteriesConfig flags, tests, and the subagent-delegation line in PromptBuilder.BuiltIns.
read the amendment on the KS7.5 card first: measured, a whole composed prompt is 17.7k-26.3k CHARS
  (4.4k-6.6k tokens) against a 135k-195k mean turn - 3-4% of a turn. Prompt trimming CANNOT move the
  66%. The exit as written needs N future sessions under the new prompts; one session cannot produce it.
build: use the MSBuild switches nodeReuse:false and UseSharedCompilation=false (bug #54) or you get 9
  bogus Conductor.Planning analyzer errors. Bug #53: cache_creation 5m/1h TTL split is dropped.
```
