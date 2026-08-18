# Karvansara edge - gates that can't be gamed, and the courier Phase Tracker

**Plan:** Karvansara edge - gates that can't be gamed, and the courier | **Branch:** `feat/karvansara-edge` | **Design doc:** docs/dev/KARVANSARA-PLAN-2026-08-13.md

## Handoff (overwrite this block, ≤ 12 lines, no history)

last: KS7.1 DONE (0c3380f posture+refusal telemetry, efe1e69 the claim-path fix, 4bc1fff evidence).
  A stage ran GREEN under the restricted profile on a scratch rig - gates green, newly DONE [R1.1],
  six refusals telemetered on three surfaces. agent.permissions {mode,allow,deny} is real config.
stage: KS7 - 7.2 hooks, 7.3 cost/OTel, 7.4 lifecycle, 7.5 context economics are still TODO.
gate: scoped 213/213 (KS7_1, Architecture, SF7_1Docs, PlanConfig, BudgetRail, Provider, AgentConfig,
  ResolveArgs, Transcript). Two suites went red first and were FIXED, not relaxed - the docs gate
  because agent.permissions was undocumented, the ratchet because PlanConfig.cs passed 500 lines.
next: KS7.2. Every checkpoint still opens by verifying its flags against the installed claude
  (2.1.235) - the six --permission-mode spellings and the settings keys are already verified in
  .conductor/evidence/KS7/ks7-1-posture.md, do not re-probe those.
trap: a probe that only runs READ-ONLY shell commands will tell you print mode approves everything;
  it does not. Bug #51 is open and worth reading before any posture work: the CLI gate refuses
  git add/git commit too, so a plan whose delivery is commits must allowlist them.

## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 24 |
| Done | 0 |
| Claimed (unconfirmed) | 5 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### KS11 — Chapar - the remote surface: profiles, onboarding, evidence on demand

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS11.1 | The messenger seam: composition, chat profiles and evidence browsing extracted channel-agnostic; TelegramService becomes the transport adapter; golden replay proves current pushes byte-identical through the seam; a fake channel drives the full surface in tests; an architecture test forbids Telegram types outside the adapter | DONE | 7e64866 | .conductor/evidence/KS11/ks11-1-messenger-seam.md |
| KS11.2 | Profiles admin and observer, per chat: old-shape allowedChatIds plans behave byte-identically (pinned); an unknown profile string is refused by name at plan load; the observer surface is closed to status/tasks/progress/evidence/daily, a control or inject attempt refused by name - proven by an exhaustive command-by-profile matrix test | DONE | 1471ef9 | .conductor/evidence/KS11/KS11.2-chat-profiles.md |
| KS11.3 | Onboarding + the push grammar: run start and /start post a per-profile onboarding message (what the run is, what will be pushed, what this chat may ask); every push type recomposed to headline / proof / telemetry with money and tokens in monospace; goldens pin both profiles' renderings; a checkpoint push reads standalone | DONE | 1471ef9 | .conductor/evidence/KS11/KS11.3-onboarding-and-grammar.md |
| KS11.4 | Evidence on demand: /evidence lists checkpoints with evidence, /evidence with an id sends the artifact (document upload for files, chunked text otherwise) with size caps and a per-chat rate limit; an observer pulls a real evidence artifact end-to-end in the rig; the clip constants no longer bound what a reader can reach | DONE | df5048e | .conductor/evidence/KS11/KS11.4-evidence-on-demand.md |
| KS11.5 | Metrics on demand: /progress /money /tokens answer with figures that cross-check against status and money on the same run.db to the cent (billed money only, no price table in the diff); the daily digest re-rendered in the same grammar, golden pinned | DONE | d6be308 | .conductor/evidence/KS11/KS11.5-metrics-on-demand.md |

### KS7 — Platform catch-up - posture, hooks, usage, lifecycle, context economics

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS7.1 | Permission posture: an allowlist/deny settings profile replaces dangerously-skip-permissions for unattended runs if the installed CLI sustains it - a karvan-class stage runs green under the restricted profile with refusals telemetered, OR a filed finding says precisely why not; blast-radius posture stated honestly in ARCHITECTURE.md | TODO | - | - |
| KS7.2 | Hooks as ground truth: tool events by hook (extending the hook-budget channel) become the primary source, transcript parsing the fallback; hook-derived digests match transcript-derived on a replay corpus; a hook-less agent still works; digest claim-counting (bug 19 class) fixed; skills-vs-promptExtra decided and recorded | TODO | - | - |
| KS7.3 | Cost/usage: per-turn usage with cache split parsed from the stream; OTel emit mirroring gen_ai names from the event log; an OTLP collector renders a run's spans; the per-turn context curve reconciles with K4.1's derivation | TODO | - | - |
| KS7.4 | Session lifecycle: fork-instead-of-cold-resume for fix/audit sessions where supported, with the measured token delta vs the resume baseline; resume flags re-verified; model lineup and context ceilings re-measured into TOKEN-BUDGET-TUNING | TODO | - | - |
| KS7.5 | Context economics (B7): gate output truncated in-prompt with full text as an evidence file; RepoMapBattery + definition-of-done recap battery on the IPromptBattery seam; templates teach search-delegation; measured cache-read tokens per session DROP vs the karvan baseline on a comparable stage, reported by conductor budget | TODO | - | - |

### KS6 — Quality lane - hygiene that buys design

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS6.1 | Curated Roslynator set (~25 design-shaped rules) as errors, everything else explicitly off, each rule adopted with a one-line reason | TODO | - | - |
| KS6.2 | Analyzer-debt count ratchet extending ratchet.ps1 semantics; the referee not editable by the agent - a seeded baseline rewrite goes red | TODO | - | - |
| KS6.3 | Complexity budgets (CA1502/1505/1506) with ratchets; first targets the largest partial surfaces - VerdictEngine (8 files) and ControlPlaneServer (11) | TODO | - | - |
| KS6.4 | The pure evidence-to-verdict function extracted from VerdictEngine - the taxonomy testable without the loop; the seam KS4.5 plugs into | TODO | - | - |

### KS4 — Verification that can't be gamed

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS4.1 | Holdout gates: a visibility holdout gate class excluded from prompts, tool contract and agent-readable logs, run only at verdict time; grep of composed prompt + transcript proves absence; a seeded gaming fake-agent passes visible gates, fails holdout, verdict red | TODO | - | - |
| KS4.2 | Regression gate class (PASS-TO-PASS semantics): nothing-that-worked-broke as a named class with distinct reporting; a seeded regression flips the verdict with the class named in evidence | TODO | - | - |
| KS4.3 | Mutation gate kind: mutation-score >= threshold, diff-scoped, Stryker.NET first; a checkpoint adding tests must clear the score on changed files; an era-boundary run on conductor's own suite recorded | TODO | - | - |
| KS4.4 | Worktree-per-stage-attempt: each attempt in a worktree, failed attempt drops the tree, verdict receives the clean attempt diff, merge ff-only on green, never branch -D an unmerged branch (lanes L1.3 fix per ND-8, amendment committed); Windows lock/removal proven; orphan sweep at startup | TODO | - | - |
| KS4.5 | Judge as evidence, never verdict: second-model review joins the evidence taxonomy through KS6.4's seam as an advisory row; judge disagreement recorded as evidence; a test asserts NO code path lets a judge score flip a gate verdict | TODO | - | - |

### KS8 — Interop - the run as a readable artifact (cut-first)

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS8.1 | Read-only MCP surface: history/status/money as MCP resources, control ops excluded by design with the ADR citing MCP's 2026 attack record; an MCP client lists runs and quotes reconciled status; no write tool exists on the surface | TODO | - | - |
| KS8.2 | ATIF trajectory export from the fold (history export, billed costs included) validating against the ATIF schema on the karvan-core run; AGENTS.md generated/honored via the CLAUDE.md-import pattern | TODO | - | - |

### KS12 — Ship edge - close the era

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS12.1 | Internal record: ARCHITECTURE.md + docs/dev reconciled for everything edge changed; closure ledger naming every bug/followup row closed here or its living owner (bug 44 and the KS10.1 inherited gaps included); conductor budget re-measured into TOKEN-BUDGET-TUNING - the number the next era compiles against | TODO | - | - |
| KS12.2 | Published surface: README + docs user set (operating.md carries the observer-profile and group-chat setup; plan-config.md carries the telegram chats shape and every key edge added) + CHANGELOG Unreleased written as the release body; docs-match-reality tests extended and proven red on a seeded stale doc; payesh harvest re-run on a branch with a PR, never pushed to main | TODO | - | - |
| KS12.3 | OWNER-ONLY: merge feat/karvansara-edge to master, tag and release through the pipeline with KS12.2's CHANGELOG section as the body, reinstall (no other live run on the machine first), github sync --backfill of THIS run, merge the payesh PR, and move CORE-TRACKER.md + EDGE-TRACKER.md + the era brief to docs/history - the Karvansara era closes | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
