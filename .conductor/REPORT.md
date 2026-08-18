# Conductor — Karvansara edge - gates that can't be gamed, and the courier run report

_Updated 2026-08-18 21:02 UTC · branch `feat/karvansara-edge` · HEAD `cfbcb3e`_

**Status:** Idle
**Stage:** KS11 — Chapar - the remote surface: profiles, onboarding, evidence on demand · attempts used 0
**Checkpoints:** 5/24 done · **Sessions run:** 5 · **Cost:** $61.3734 (agent $61.3367 + gates $0.0367) · **Tokens:** 1,007,080 in / 400,393 out
**Confirmed phases:** KS11

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| KS11 | Chapar - the remote surface: profiles, onboarding, evidence on demand | ██████████ 5/5 | confirmed ✓ |
| KS7 | Platform catch-up - posture, hooks, usage, lifecycle, context economics | ░░░░░░░░░░ 0/5 | todo |
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

<details><summary>KS7 — Platform catch-up - posture, hooks, usage, lifecycle, context economics (0/5)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS7.1 | Permission posture: an allowlist/deny settings profile replaces dangerously-skip-permissions for unattended runs if the installed CLI sustains it - a karvan-class stage runs green under the restricted profile with refusals telemetered, OR a filed finding says precisely why not; blast-radius posture stated honestly in ARCHITECTURE.md | ⬜ TODO | - |
| KS7.2 | Hooks as ground truth: tool events by hook (extending the hook-budget channel) become the primary source, transcript parsing the fallback; hook-derived digests match transcript-derived on a replay corpus; a hook-less agent still works; digest claim-counting (bug 19 class) fixed; skills-vs-promptExtra decided and recorded | ⬜ TODO | - |
| KS7.3 | Cost/usage: per-turn usage with cache split parsed from the stream; OTel emit mirroring gen_ai names from the event log; an OTLP collector renders a run's spans; the per-turn context curve reconciles with K4.1's derivation | ⬜ TODO | - |
| KS7.4 | Session lifecycle: fork-instead-of-cold-resume for fix/audit sessions where supported, with the measured token delta vs the resume baseline; resume flags re-verified; model lineup and context ceilings re-measured into TOKEN-BUDGET-TUNING | ⬜ TODO | - |
| KS7.5 | Context economics (B7): gate output truncated in-prompt with full text as an evidence file; RepoMapBattery + definition-of-done recap battery on the IPromptBattery seam; templates teach search-delegation; measured cache-read tokens per session DROP vs the karvan baseline on a comparable stage, reported by conductor budget | ⬜ TODO | - |

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

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 5 | 83.9M | 98.4% | $61.37 | 5 | 16.8M | $12.27 |
| stage KS11 | 5 | 83.9M | 98.4% | $61.37 | 5 | 16.8M | $12.27 |
| 2026-08 | 5 | 83.9M | 98.4% | $61.37 | 5 | 16.8M | $12.27 |

_Where the money goes: agent $61.34 (100%) · gate $0.04 (0%) · blended $0.73/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
08-18 19:09:25  ◆ run started · Karvansara edge - gates that can't be gamed, and the courier
08-18 19:09:26  ▸ stage KS11 entered — Chapar - the remote surface: profiles, onboarding, evidence on demand
08-18 19:09:26  • session #1 KS11 Deliver started (attempt 1/10)
08-18 19:13:13  • session #1 KS11 → Interrupted  (3m46s)
08-18 19:17:03  ◆ run resumed · Karvansara edge - gates that can't be gamed, and the courier
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
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 5 · retries 0 (0 %) · overall Warn
⚠ [context-saturation] session #3: 28,433,638 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/karvansara-edge
working tree: M .conductor/REPORT.md, M plans/karvansara/EDGE-TRACKER.md
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

engine-fast:cached · face-fast:cached · engine-full:OK · face-full:OK

## Last session result

> **KS11.5 landed - /progress /money /tokens and the digest, figures cross-checked to the cent**
> - Money and tokens read through MoneySection.Read, the same RunArchive+MoneyAnalyzer path conductor money uses; cross-check proven three ways including SQLite's own SUM
> - Live wire proof: an observer asks /money and /tokens through the Bot API stub and the departing message carries the archive's figures; /pause from the same chat still refused
> - Full suite 2787/2787 zero failures; seven goldens rebaselined in their own commit, six of them the ask line alone
>
> artefacts: src/Conductor.Core/Integrations/Messaging/MessageComposer.Metrics.cs, MessageComposer.Views.cs, CommandRouter.cs, SurfaceCommands.cs, tests/Conductor.Tests/KS11_5MetricsOnDemandTests.cs, KS11_5OnWireTests.cs, tests/Conductor.Tests/testdata/ks11-5/, docs/operating.md
>
> evidence: .conductor/evidence/KS11/KS11.5-metrics-on-demand.md, .conductor/evidence/KS11/ks11-5-full-suite.txt
>
> gaps: none — KS11 complete, next is the ownerGate park then KS12

## Tracker handoff

```
last: KS11.5 DONE (d6be308 feature+tests+docs, 7d461bb goldens, 2230ccc evidence). /progress /money
  /tokens answer, and none of the figures is computed in the composer: it calls MoneySection.Read,
  which is the four calls conductor money makes in the same order, so the cross-check is by
  construction. Proven three ways - analyzer, rendered answer, SQLite SUM - plus an observer asking
  /money and /tokens through the Bot API stub. The digest now reads in the CH-5 grammar.
stage: KS11 COMPLETE - all five checkpoints claimed. The stage parks on its ownerGate next; the park
  is the owner's window to reinstall the mid-era engine for the BookToCourse run, not an error.
gate: scoped suites 236/236 (KS11_*, Telegram, Messaging, Notify, Money, K5_2, K4_3); full suite
  2787/2787, zero failures - bug #49's parallel-load flake did not fire this run.
next: KS12 - the record (ARCHITECTURE.md + docs/dev), then the field-guide harvest re-run. Nothing
  before KS12 touches C:/code/conductor-site, and work there is a branch + PR, never a push to main.
trap: a python heredoc replacement containing a backslash-b writes a literal BACKSPACE into the C#
  file - the code looks right in every editor and the regex silently never matches. Use raw strings.
```
