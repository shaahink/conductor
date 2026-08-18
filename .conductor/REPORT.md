# Conductor — Karvansara edge - gates that can't be gamed, and the courier run report

_Updated 2026-08-18 22:25 UTC · branch `feat/karvansara-edge` · HEAD `cf2bfb7`_

**Status:** Idle
**Stage:** KS7 — Platform catch-up - posture, hooks, usage, lifecycle, context economics · attempts used 0 · working ▸ KS7.3
**Checkpoints:** 7/24 done · **Sessions run:** 7 · **Cost:** $93.7872 (agent $93.7296 + gates $0.0576) · **Tokens:** 1,435,539 in / 610,063 out
**Confirmed phases:** KS11

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| KS11 | Chapar - the remote surface: profiles, onboarding, evidence on demand | ██████████ 5/5 | confirmed ✓ |
| KS7 | Platform catch-up - posture, hooks, usage, lifecycle, context economics | ████░░░░░░ 2/5 | **← active** |
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

<details><summary>KS7 — Platform catch-up - posture, hooks, usage, lifecycle, context economics (2/5)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS7.1 | Permission posture: an allowlist/deny settings profile replaces dangerously-skip-permissions for unattended runs if the installed CLI sustains it - a karvan-class stage runs green under the restricted profile with refusals telemetered, OR a filed finding says precisely why not; blast-radius posture stated honestly in ARCHITECTURE.md | ✅ DONE | [`0c3380f`](https://github.com/shaahink/conductor/commit/0c3380f) |
| KS7.2 | Hooks as ground truth: tool events by hook (extending the hook-budget channel) become the primary source, transcript parsing the fallback; hook-derived digests match transcript-derived on a replay corpus; a hook-less agent still works; digest claim-counting (bug 19 class) fixed; skills-vs-promptExtra decided and recorded | ✅ DONE | - |
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
| 6 | KS7 | Deliver | 1 | 08-18 21:02 | 0:41 | Advanced | KS7.1 | 4 | engine-fast:OK · face-fast:OK | $17.9837 | $0.0090 | 230,547/106,999 |
| 7 | KS7 | Deliver | 1 | 08-18 21:46 | 0:37 | Advanced | KS7.2 | 2 | engine-fast:OK · face-fast:OK | $14.4092 | $0.0119 | 197,912/102,671 |

## Money

_What this run has cost, from its own `costs` rows. Same numbers as `conductor money`._

| scope | sessions | tokens | cache reads | cost | checkpoints | tok/ckpt | $/ckpt |
|---|---|---|---|---|---|---|---|
| **run total** | 6 | 110.2M | 98.5% | $79.37 | 6 | 18.4M | $13.23 |
| stage KS11 | 5 | 83.9M | 98.4% | $61.37 | 5 | 16.8M | $12.27 |
| stage KS7 | 1 | 26.3M | 98.7% | $17.99 | 1 | 26.3M | $17.99 |
| 2026-08 | 6 | 110.2M | 98.5% | $79.37 | 6 | 18.4M | $13.23 |

_Where the money goes: agent $79.32 (100%) · gate $0.05 (0%) · blended $0.72/M tokens._

## Timeline

_Transitions with duration, from the event log (`.conductor/events.jsonl`)._

```
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
08-18 22:02:47  ▸ stage KS11 confirmed  (2h53m20s)
08-18 22:02:47  ▸ stage KS7 entered — Platform catch-up - posture, hooks, usage, lifecycle, context economics
08-18 22:02:47  • session #6 KS7 Deliver started (attempt 1/10)
08-18 22:46:17  ▪ gate engine-fast pass [session]  (1m02s)
08-18 22:46:17  ▪ gate face-fast pass [session]  (27.4s)
08-18 22:46:18  • session #6 KS7 → Advanced · done KS7.1 · 4 commit(s)  (43m30s)
08-18 22:46:19  • session #7 KS7 Deliver started (attempt 1/10)
08-18 23:25:28  ▪ gate engine-fast pass [session]  (1m30s)
08-18 23:25:28  ▪ gate face-fast pass [session]  (28.6s)
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 7 · retries 0 (0 %) · overall Warn
⚠ [context-saturation] session #3: 28,433,638 context tokens (≥ 20,000,000)
⚠ [context-saturation] session #6: 25,994,603 context tokens (≥ 20,000,000)
```

## Repo

_Live git snapshot (branch, working tree, sync vs upstream)._

```
branch: feat/karvansara-edge
working tree: M .conductor/REPORT.md
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

> **KS7.2 landed — hooks are the digest's primary source, transcript demoted to fallback**
> - PostToolUse skips failed/refused calls (measured twice); recording moved to PreToolUse, outcome merged by tool_use_id
> - Hook and transcript digests agree exactly: on a two-channel replay corpus and live at 8 calls each in a rig session
> - Bug #19 class dead (CLI claims counted); skills-vs-promptExtra decided with measurements; bug #52 filed
>
> artefacts: 5b8d56e, cf2bfb7, src/Conductor.Core/Events/HookToolLog.cs, tests/Conductor.Tests/KS7_2HookGroundTruthTests.cs, ARCHITECTURE.md
>
> evidence: .conductor/evidence/KS7/ks7-2-hooks-as-ground-truth.md, .conductor/evidence/KS7/ks7-2-rig-hook-tools.jsonl
>
> gaps: bug #52 open (digest counts a failed claim attempt); KS7.3/7.4/7.5 still TODO

## Tracker handoff

```
last: KS7.2 DONE (5b8d56e channel+tests+corpus, this commit docs+evidence). The digest is now written
  by the agent CLI's own PreToolUse/PostToolUse hooks into .conductor/hook-tools/NNN.jsonl and
  promoted over the transcript at session end; absent/empty = fallback, and the digest stores its
  source. Bug #19's class is dead: a `conductor task --done` made through Bash is counted.
gate: scoped 96/96 (KS7_2, Architecture, SF7_1Docs, BudgetRail, SessionDigest, Transcript, Ratchet).
next: KS7.3 cost/OTel, then 7.4 lifecycle, 7.5 context economics. KS7.5 inherits a named seam and a
  decision: promptExtra stays for rails, `--plugin-dir` carries the reference half as skills.
do-not-re-probe: claude 2.1.235 flags in .conductor/evidence/KS7/ks7-2-hooks-as-ground-truth.md -
  --include-hook-events is LIFECYCLE ONLY (no tool_input), --plugin-dir works with empty
  --setting-sources, and the six hook events that fire are listed there.
trap: PostToolUse does NOT fire for a refused or failed call - measured twice. Any design that reads
  it as "the calls this session made" is counting successes. And never `dotnet run` a rig while a
  `dotnet build` is in flight: Conductor.Planning fails with analyzer errors that reproduce in
  neither build alone. Drive src/Conductor/bin/Debug/net10.0/conductor.exe instead. Bug #52 is open.
```
