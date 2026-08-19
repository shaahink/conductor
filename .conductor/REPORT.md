# Conductor — Karvansara edge - gates that can't be gamed, and the courier run report

_Updated 2026-08-19 02:08 UTC · branch `feat/karvansara-edge` · HEAD `8ddbe37`_

**Status:** Paused
**Stage:** KS6 — Quality lane - hygiene that buys design · attempts used 0 · working ▸ KS6.3
**Checkpoints:** 12/24 done · **Sessions run:** 12 · **Cost:** $163.1368 (agent $163.0315 + gates $0.1053) · **Tokens:** 2,425,855 in / 1,123,313 out
**Confirmed phases:** KS11, KS7

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| KS11 | Chapar - the remote surface: profiles, onboarding, evidence on demand | ██████████ 5/5 | confirmed ✓ |
| KS7 | Platform catch-up - posture, hooks, usage, lifecycle, context economics | ██████████ 5/5 | confirmed ✓ |
| KS6 | Quality lane - hygiene that buys design | █████░░░░░ 2/4 | **← active** |
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

<details> ✅<summary>KS7 — Platform catch-up - posture, hooks, usage, lifecycle, context economics (5/5)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS7.1 | Permission posture: an allowlist/deny settings profile replaces dangerously-skip-permissions for unattended runs if the installed CLI sustains it - a karvan-class stage runs green under the restricted profile with refusals telemetered, OR a filed finding says precisely why not; blast-radius posture stated honestly in ARCHITECTURE.md | ✅ DONE | [`0c3380f`](https://github.com/shaahink/conductor/commit/0c3380f) |
| KS7.2 | Hooks as ground truth: tool events by hook (extending the hook-budget channel) become the primary source, transcript parsing the fallback; hook-derived digests match transcript-derived on a replay corpus; a hook-less agent still works; digest claim-counting (bug 19 class) fixed; skills-vs-promptExtra decided and recorded | ✅ DONE | [`5b8d56e`](https://github.com/shaahink/conductor/commit/5b8d56e) |
| KS7.3 | Cost/usage: per-turn usage with cache split parsed from the stream; OTel emit mirroring gen_ai names from the event log; an OTLP collector renders a run's spans; the per-turn context curve reconciles with K4.1's derivation | ✅ DONE | [`5794417`](https://github.com/shaahink/conductor/commit/5794417) |
| KS7.4 | Session lifecycle: fork-instead-of-cold-resume for fix/audit sessions where supported, with the measured token delta vs the resume baseline; resume flags re-verified; model lineup and context ceilings re-measured into TOKEN-BUDGET-TUNING | ✅ DONE | [`5794417`](https://github.com/shaahink/conductor/commit/5794417) |
| KS7.5 | Context economics (B7): gate output truncated in-prompt with full text as an evidence file; RepoMapBattery + definition-of-done recap battery on the IPromptBattery seam; templates teach search-delegation; measured cache-read tokens per session DROP vs the karvan baseline on a comparable stage, reported by conductor budget | ✅ DONE | [`3d7414a`](https://github.com/shaahink/conductor/commit/3d7414a) |

</details>

<details><summary>KS6 — Quality lane - hygiene that buys design (2/4)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS6.1 | Curated Roslynator set (~25 design-shaped rules) as errors, everything else explicitly off, each rule adopted with a one-line reason | ✅ DONE | [`af6d93e`](https://github.com/shaahink/conductor/commit/af6d93e) |
| KS6.2 | Analyzer-debt count ratchet extending ratchet.ps1 semantics; the referee not editable by the agent - a seeded baseline rewrite goes red | ✅ DONE | [`0cb514d`](https://github.com/shaahink/conductor/commit/0cb514d) |
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
| 9 | KS7 | Deliver | 1 | 08-18 23:14 | 0:46 | Advanced | KS7.5 | 2 | engine-fast:OK · face-fast:OK | $10.9477 | $0.0083 | 171,145/78,053 |
| 10 | KS7 | Fix | 2 | 08-19 00:11 | 0:22 | Progress |  | 2 | engine-fast:OK · face-fast:OK | $5.3947 | $0.0114 | 113,064/40,939 |
| 11 | KS6 | Deliver | 1 | 08-19 00:39 | 0:52 | Advanced | KS6.1 | 2 | engine-fast:OK · face-fast:OK | $20.6082 | $0.0103 | 256,670/149,412 |
| 12 | KS6 | Deliver | 1 | 08-19 01:33 | 0:33 | Advanced | KS6.2 | 5 | engine-fast:OK · face-fast:OK | $12.1019 | $0.0086 | 199,951/114,585 |

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 12 | 224.8M | 98.5% | $163.14 | 12 | 18.7M | $13.59 |
| stage KS11 | 5 | 83.9M | 98.4% | $61.37 | 5 | 16.8M | $12.27 |
| stage KS7 | 5 | 97.1M | 98.5% | $69.03 | 5 | 19.4M | $13.81 |
| stage KS6 | 2 | 43.8M | 98.4% | $32.73 | 2 | 21.9M | $16.36 |
| 2026-08 | 12 | 224.8M | 98.5% | $163.14 | 12 | 18.7M | $13.59 |

_Where the money goes: agent $163.03 (100%) · gate $0.11 (0%) · blended $0.73/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-18 22:46:18  • session #6 KS7 → Advanced · done KS7.1 · 4 commit(s)  (43m30s)
08-18 22:46:19  • session #7 KS7 Deliver started (attempt 1/10)
08-18 23:25:28  ▪ gate engine-fast pass [session]  (1m30s)
08-18 23:25:28  ▪ gate face-fast pass [session]  (28.6s)
08-18 23:25:28  • session #7 KS7 → Advanced · done KS7.2 · 2 commit(s)  (39m09s)
08-18 23:25:29  • session #8 KS7 Deliver started (attempt 1/10)
08-19 00:14:37  ▪ gate engine-fast pass [session]  (1m06s)
08-19 00:14:37  ▪ gate face-fast pass [session]  (25.0s)
08-19 00:14:37  • session #8 KS7 → Advanced · done KS7.3,KS7.4 · 3 commit(s)  (49m08s)
08-19 00:14:39  • session #9 KS7 Deliver started (attempt 1/10)
08-19 01:02:53  ▪ gate engine-fast pass [session]  (1m03s)
08-19 01:02:53  ▪ gate face-fast pass [session]  (19.1s)
08-19 01:02:54  • session #9 KS7 → Advanced · done KS7.5 · 2 commit(s)  (48m15s)
08-19 01:11:23  ▪ gate engine-fast pass [phase]  (0.0s)
08-19 01:11:23  ▪ gate face-fast pass [phase]  (0.0s)
08-19 01:11:23  ▪ gate engine-full FAIL [phase]  (4m18s)
08-19 01:11:23  ▪ gate face-full pass [phase]  (5.1s)
08-19 01:11:23  • session #10 KS7 Fix started (attempt 2/10)
08-19 01:35:49  ▪ gate engine-fast pass [session]  (1m28s)
08-19 01:35:49  ▪ gate face-fast pass [session]  (25.4s)
08-19 01:35:50  • session #10 KS7 → Progress · 2 commit(s)  (24m26s)
08-19 01:39:49  ▪ gate engine-fast pass [phase]  (0.0s)
08-19 01:39:49  ▪ gate face-fast pass [phase]  (0.0s)
08-19 01:39:49  ▪ gate engine-full pass [phase]  (3m50s)
08-19 01:39:49  ▪ gate face-full pass [phase]  (2.5s)
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
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 12 · retries 1 (8 %) · overall Warn
⚠ [context-saturation] session #11: 28,600,968 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #3: 28,433,638 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 25,994,603 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #8: 28,984,293 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/karvansara-edge
working tree: M .conductor/REPORT.md, M plans/karvansara/EDGE-TRACKER.md
```

### Commits by session

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

> **KS6.2 analyzer-debt ratchet landed; bug 44 closed downward, 45 to 31 pragmas**
> - Six suppression kinds counted, bar is a window minimum over history - the single-commit anchor was provably inside the game, and so is all of ratchet section 3
> - 14 pragmas proved dead by build measurement and removed; maxPragmas ratcheted down 38 to 31; zero unjustified suppressions of any kind
> - Eight seeded attacks caught; five made permanent as tests; ratchet exit 0, 35/35 scoped tests pass
>
> artefacts: tools/gates/analyzer-debt.ps1, tools/gates/ratchet.ps1, tools/gates/ratchet-baseline.json, tests/Conductor.Tests/KS6_2AnalyzerDebtRatchetTests.cs, .editorconfig, 12 src files, commits 0cb514d 4b25081 42f846f 965e7ac 8ddbe37
>
> evidence: .conductor/evidence/KS6/KS6.2-analyzer-debt-ratchet.md, .conductor/evidence/KS6/KS6.2-seeded-attacks.log
>
> gaps: ratchet 3c "tests may not be deleted" has the same vacuous-anchor hole and minTests=1932 sits against an actual 2494, so 562 test attributes could be deleted with both halves of the gate silent - recorded in the ledger, not owned by this checkpoint. Bugs #53/#54/#55 still open.

## Tracker handoff

```
last: KS6.2 DONE (0cb514d, 4b25081, 42f846f). tools/gates/analyzer-debt.ps1 counts SIX kinds of
  suppression, not one, wired from ratchet.ps1 so no plan edit was needed. Bug 44 CLOSED downward: 14
  of the 45 pragmas measured dead and removed, 45 -> 31, maxPragmas ratcheted DOWN 38 -> 31, zero
  unjustified. Evidence: .conductor/evidence/KS6/KS6.2-analyzer-debt-ratchet.md + -seeded-attacks.log.
THE FINDING KS6.3 MUST NOT REPEAT: ALL of ratchet.ps1 section 3 is vacuous in this run's flow. It
  anchors on origin/<branch>, but a session commits AND PUSHES before conductor runs the battery, so
  at gate time origin/<branch> IS HEAD and 3a/3b/3c/3d compare the tree against itself - attack 8 in
  the log commits a suppression and walks through. Do NOT write KS6.3's complexity ratchets against
  origin/<branch>; use the window minimum (Get-AnchorCommits, one function, 25 commits, ~7s).
  Same hole leaves 3c open with minTests=1932 against an actual 2487: 555 attributes could go silently.
Two traps that cost me time: a $-anchored regex silently misses every CRLF line here, and a proof
  script that git reset --hard ate an hour of uncommitted gate work. Commit first, then seed.
next: KS6.3 complexity budgets. Bugs #53/#54/#55 still open.
```
