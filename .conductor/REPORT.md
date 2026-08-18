# Conductor — Karvansara edge - gates that can't be gamed, and the courier run report

_Updated 2026-08-18 18:56 UTC · branch `feat/karvansara-edge` · HEAD `003dfe7`_

**Status:** Idle
**Stage:** KS11 — Chapar - the remote surface: profiles, onboarding, evidence on demand · attempts used 0 · working ▸ KS11.2
**Checkpoints:** 1/24 done · **Sessions run:** 2 · **Cost:** $15.3812 (agent $15.3716 + gates $0.0096) · **Tokens:** 328,319 in / 112,062 out

## Stage progress

| Stage | Title | Progress | State |
|---|---|---|---|
| KS11 | Chapar - the remote surface: profiles, onboarding, evidence on demand | ██░░░░░░░░ 1/5 | **← active** |
| KS7 | Platform catch-up - posture, hooks, usage, lifecycle, context economics | ░░░░░░░░░░ 0/5 | todo |
| KS6 | Quality lane - hygiene that buys design | ░░░░░░░░░░ 0/4 | todo |
| KS4 | Verification that can't be gamed | ░░░░░░░░░░ 0/5 | todo |
| KS8 | Interop - the run as a readable artifact (cut-first) | ░░░░░░░░░░ 0/2 | todo |
| KS12 | Ship edge - close the era | ░░░░░░░░░░ 0/3 | todo |

<details><summary>KS11 — Chapar - the remote surface: profiles, onboarding, evidence on demand (1/5)</summary>

| # | Title | Status | Commit |
|---|---|---|---|
| KS11.1 | The messenger seam: composition, chat profiles and evidence browsing extracted channel-agnostic; TelegramService becomes the transport adapter; golden replay proves current pushes byte-identical through the seam; a fake channel drives the full surface in tests; an architecture test forbids Telegram types outside the adapter | ✅ DONE | - |
| KS11.2 | Profiles admin and observer, per chat: old-shape allowedChatIds plans behave byte-identically (pinned); an unknown profile string is refused by name at plan load; the observer surface is closed to status/tasks/progress/evidence/daily, a control or inject attempt refused by name - proven by an exhaustive command-by-profile matrix test | ⬜ TODO | - |
| KS11.3 | Onboarding + the push grammar: run start and /start post a per-profile onboarding message (what the run is, what will be pushed, what this chat may ask); every push type recomposed to headline / proof / telemetry with money and tokens in monospace; goldens pin both profiles' renderings; a checkpoint push reads standalone | ⬜ TODO | - |
| KS11.4 | Evidence on demand: /evidence lists checkpoints with evidence, /evidence with an id sends the artifact (document upload for files, chunked text otherwise) with size caps and a per-chat rate limit; an observer pulls a real evidence artifact end-to-end in the rig; the clip constants no longer bound what a reader can reach | ⬜ TODO | - |
| KS11.5 | Metrics on demand: /progress /money /tokens answer with figures that cross-check against status and money on the same run.db to the cent (billed money only, no price table in the diff); the daily digest re-rendered in the same grammar, golden pinned | ⬜ TODO | - |

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
```

## Health

_Execution-health signals, folded from the event log (`.conductor/events.jsonl`)._

```
sessions 2 · retries 0 (0 %) · overall Ok
✓ no health concerns detected
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

> **messenger seam extracted, proven byte-identical, boundary enforced with no allowlist**
> - KS11.1 done: 15 pre-seam goldens pass unchanged through the seam, zero diff to the golden files
> - fake channel drives the whole surface in 17 tests; 3 boundary rules, incl. ITelegramService renamed to IRunNotifier across 37 files
> - full suite 2690/2691; the one red predated this session (CHANGELOG [Unreleased] gone since KS10.3) and is now fixed
>
> artefacts: 7e64866, 897e295, ffbff68, 003dfe7, src/Conductor.Core/Integrations/Messaging/, tests/Conductor.Tests/testdata/ks11/
>
> evidence: .conductor/evidence/KS11/ks11-1-messenger-seam.md
>
> gaps: ChatProfile defined but every chat still resolves Admin - KS11.2 owns plan-read profiles and the observer matrix; WatchRemote still names TelegramService outside the seam, recorded in the boundary test's list rather than fixed

## Tracker handoff

```
last: KS11.1 DONE (ffbff68). The messenger seam is real: Messaging/{IMessageChannel, MessageComposer
  (+.Views), CommandRouter, RemoteSurface}; TelegramService is now the transport adapter. And
  byte-identical is MEASURED - 15 goldens generated by the pre-seam engine (7e64866) pass unchanged.
stage: KS11 in flight, 1 of 5 done.
gate: full suite 2690/2691 at 897e295; the one red was CHANGELOG missing [Unreleased] since KS10.3 -
  unrelated to the seam, now written, SC8_2Versioning 3/3 after it.
next: **KS11.2** - profiles. CommandRouter.Route already TAKES a ChatProfile and ignores it: that is
  the one place to enforce CH-3, and the command-by-profile matrix belongs against the router (pure,
  no channel, no HTTP) rather than TelegramService. TelegramService.ProfileFor(chatId) is the stub
  that always answers Admin. TelegramConfig still has only allowedChatIds; no `chats` block yet.
trap: the ks11 goldens came from an OLD-SHAPE allowedChatIds plan, so keeping them green IS KS11.2's
  back-compat proof - never regenerate them. Scratch bot and scratch chats only for live proofs.
```
