# Karvansara edge - gates that can't be gamed, and the courier Phase Tracker

**Plan:** Karvansara edge | **Branch:** `feat/karvansara-edge` | **Design doc:** docs/dev/KARVANSARA-PLAN-2026-08-13.md (Plan II) + docs/dev/CHAPAR-REMOTE-SURFACE-2026-08-18.md (KS11)

## Handoff (overwrite this block, <=12 lines, no history)

last: plan authored 2026-08-18; nothing has run. Core shipped as v0.4.1 on 2026-08-15 (32/32);
  edge is the era's second half - KS11 remote surface added on the owner's ask, spec'd in the
  Chapar doc; KS12 closes the era and moves both trackers to docs/history.
stage: **KS11 not started**.
gate: never run (branch feat/karvansara-edge not yet created; create it from master at launch).
next: **KS11.1** - the messenger seam extraction, behaviour-preserving, golden replay as the proof.
trap: KS11 live proofs use a scratch bot token and scratch chats ONLY - the owner's chat and the
  BookToCourse group are production. The BookToCourse run shares this machine; promptExtra trap 3.

## Checkpoints

<!-- THE ESCALATION TOKEN - the word HUMAN followed by a colon - parks the run at NeedsHuman when it
     appears ANYWHERE in the handoff block above. The match is a plain substring: quoting it,
     describing it, or explaining the convention in the handoff parks the run just as hard as
     raising it. That is why this legend spells the token out and sits BELOW the handoff block.
     In handoff prose the word is "escalation". A row flipping to BLOCKED parks the same way. -->

Status is TODO / IN PROGRESS / DONE / DONE (confirmed) / BLOCKED / SKIPPED. Evidence = artifact path
produced by a run this phase (a code path is not evidence). Agent claims are marked DONE; the engine
confirms. Checkpoint ids share their stage's letters-then-digits prefix (KS11.1 belongs to KS11).

### KS11 - Chapar: the remote surface (ownerGate - parks for the early-reinstall option)

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS11.1 | The messenger seam: composition, chat profiles and evidence browsing extracted channel-agnostic; TelegramService becomes the transport adapter; golden replay proves current pushes byte-identical through the seam; a fake channel drives the full surface in tests; an architecture test forbids Telegram types outside the adapter | TODO | | |
| KS11.2 | Profiles admin and observer, per chat: old-shape allowedChatIds plans behave byte-identically (pinned); an unknown profile string is refused by name at plan load; the observer surface is closed to status/tasks/progress/evidence/daily, a control or inject attempt refused by name - proven by an exhaustive command-by-profile matrix test | TODO | | |
| KS11.3 | Onboarding + the push grammar: run start and /start post a per-profile onboarding message (what the run is, what will be pushed, what this chat may ask); every push type recomposed to headline / proof / telemetry with money and tokens in monospace; goldens pin both profiles' renderings; a checkpoint push reads standalone | TODO | | |
| KS11.4 | Evidence on demand: /evidence lists checkpoints with evidence, /evidence with an id sends the artifact (document upload for files, chunked text otherwise) with size caps and a per-chat rate limit; an observer pulls a real evidence artifact end-to-end in the rig; the clip constants no longer bound what a reader can reach | TODO | | |
| KS11.5 | Metrics on demand: /progress /money /tokens answer with figures that cross-check against status and money on the same run.db to the cent (billed money only, no price table in the diff); the daily digest re-rendered in the same grammar, golden pinned | TODO | | |

### KS7 - Platform catch-up (every checkpoint opens by verifying flags against the installed CLI)

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS7.1 | Permission posture: an allowlist/deny settings profile replaces dangerously-skip-permissions for unattended runs if the installed CLI sustains it - a karvan-class stage runs green under the restricted profile with refusals telemetered, OR a filed finding says precisely why not; blast-radius posture stated honestly in ARCHITECTURE.md | TODO | | |
| KS7.2 | Hooks as ground truth: tool events by hook (extending the hook-budget channel) become the primary source, transcript parsing the fallback; hook-derived digests match transcript-derived on a replay corpus; a hook-less agent still works; digest claim-counting (bug 19 class) fixed; skills-vs-promptExtra decided and recorded | TODO | | |
| KS7.3 | Cost/usage: per-turn usage with cache split parsed from the stream; OTel emit mirroring gen_ai names from the event log; an OTLP collector renders a run's spans; the per-turn context curve reconciles with K4.1's derivation | TODO | | |
| KS7.4 | Session lifecycle: fork-instead-of-cold-resume for fix/audit sessions where supported, with the measured token delta vs the resume baseline; resume flags re-verified; model lineup and context ceilings re-measured into TOKEN-BUDGET-TUNING | TODO | | |
| KS7.5 | Context economics (B7): gate output truncated in-prompt with full text as an evidence file; RepoMapBattery + definition-of-done recap battery on the IPromptBattery seam; templates teach search-delegation; measured cache-read tokens per session DROP vs the karvan baseline on a comparable stage, reported by conductor budget | TODO | | |

### KS6 - Quality lane: hygiene that buys design

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS6.1 | Curated Roslynator set (~25 design-shaped rules) as errors, everything else explicitly off, each rule adopted with a one-line reason | TODO | | |
| KS6.2 | Analyzer-debt count ratchet extending ratchet.ps1 semantics; the referee not editable by the agent - a seeded baseline rewrite goes red | TODO | | |
| KS6.3 | Complexity budgets (CA1502/1505/1506) with ratchets; first targets the largest partial surfaces - VerdictEngine (8 files) and ControlPlaneServer (11) | TODO | | |
| KS6.4 | The pure evidence-to-verdict function extracted from VerdictEngine - the taxonomy testable without the loop; the seam KS4.5 plugs into | TODO | | |

### KS4 - Verification that can't be gamed

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS4.1 | Holdout gates: a visibility holdout gate class excluded from prompts, tool contract and agent-readable logs, run only at verdict time; grep of composed prompt + transcript proves absence; a seeded gaming fake-agent passes visible gates, fails holdout, verdict red | TODO | | |
| KS4.2 | Regression gate class (PASS-TO-PASS semantics): nothing-that-worked-broke as a named class with distinct reporting; a seeded regression flips the verdict with the class named in evidence | TODO | | |
| KS4.3 | Mutation gate kind: mutation-score >= threshold, diff-scoped, Stryker.NET first; a checkpoint adding tests must clear the score on changed files; an era-boundary run on conductor's own suite recorded | TODO | | |
| KS4.4 | Worktree-per-stage-attempt: each attempt in a worktree, failed attempt drops the tree, verdict receives the clean attempt diff, merge ff-only on green, never branch -D an unmerged branch (lanes L1.3 fix per ND-8, amendment committed); Windows lock/removal proven; orphan sweep at startup | TODO | | |
| KS4.5 | Judge as evidence, never verdict: second-model review joins the evidence taxonomy through KS6.4's seam as an advisory row; judge disagreement recorded as evidence; a test asserts NO code path lets a judge score flip a gate verdict | TODO | | |

### KS8 - Interop (cut-first: skipped whole with a note if budget or calendar demand)

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS8.1 | Read-only MCP surface: history/status/money as MCP resources, control ops excluded by design with the ADR citing MCP's 2026 attack record; an MCP client lists runs and quotes reconciled status; no write tool exists on the surface | TODO | | |
| KS8.2 | ATIF trajectory export from the fold (history export, billed costs included) validating against the ATIF schema on the karvan-core run; AGENTS.md generated/honored via the CLAUDE.md-import pattern | TODO | | |

### KS12 - Ship edge, close the era (ownerGate)

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS12.1 | Internal record: ARCHITECTURE.md + docs/dev reconciled for everything edge changed; closure ledger naming every bug/followup row closed here or its living owner (bug 44 and the KS10.1 inherited gaps included); conductor budget re-measured into TOKEN-BUDGET-TUNING - the number the next era compiles against | TODO | | |
| KS12.2 | Published surface: README + docs user set (operating.md carries the observer-profile and group-chat setup; plan-config.md carries the telegram chats shape and every key edge added) + CHANGELOG Unreleased written as the release body; docs-match-reality tests extended and proven red on a seeded stale doc; payesh harvest re-run on a branch with a PR, never pushed to main | TODO | | |
| KS12.3 | OWNER-ONLY: merge feat/karvansara-edge to master, tag and release through the pipeline with KS12.2's CHANGELOG section as the body, reinstall (no other live run on the machine first), github sync --backfill of THIS run, merge the payesh PR, and move CORE-TRACKER.md + EDGE-TRACKER.md + the era brief to docs/history - the Karvansara era closes | TODO | | |
