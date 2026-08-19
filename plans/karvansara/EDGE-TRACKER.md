# Karvansara edge - gates that can't be gamed, and the courier Phase Tracker

**Plan:** Karvansara edge - gates that can't be gamed, and the courier | **Branch:** `feat/karvansara-edge` | **Design doc:** docs/dev/KARVANSARA-PLAN-2026-08-13.md

## Handoff (overwrite this block, <=12 lines, no history)

last: KS12.1 IS DONE (eb87ad4, 914113c, e08c8dd), evidence in .conductor/evidence/KS12/. Budget
  re-measured: the era ran at 32M/0.85 and it HELD - realised nudgeRatio 0.8541, 0 rollovers in 19
  costed sessions, 5 nudged and all 5 clean. `budget` now prescribes 35M / 0.9 (nudge 31.5M) for the
  NEXT era; the per-checkpoint outturn is 17.47M / $13.81, only +4% on the karvan basis this era
  planned against. TOKEN-BUDGET-TUNING section 12 and the plan doc carry it. ARCHITECTURE.md: 27
  drifted citations corrected by opening every one, and the "exactly nine public interfaces" line was
  wrong (ten - KS11 added IMessageChannel). Closure ledger in .conductor/followups.md names all 34
  bug rows with an owner; bug 44 closed by KS6.2, bug 61 filed by me.
KS12.2 IS IN PROGRESS, NOT CLAIMED - three of its five parts landed (uncommitted work is in this
  commit): docs/cli.md now names all 82 long options with a NEW derived pin
  (SF7_1DocsMatchRealityTests.Flags.cs, 35/35 green - it went red on its first run and was right: the
  rollback row conflated --yes with --force); CHANGELOG [Unreleased] written as the release body for
  KS4/6/7/8 (tools/changelog-section.sh Unreleased, 112 lines, exit 0); troubleshooting.md gained the
  classed-gate section and a run.db-location correction. STILL OWED: README.md and docs/README.md
  (neither mentions gate classes, mcp-observe, ATIF, otel or chat profiles); operating.md section 7
  still says "as of 2026-08-15" and claims "the anti-cheat ratchet green" which is FALSE; and the
  payesh harvest re-run + PR, which is untouched.
TRAP THAT WOULD HAVE COST THE RUN: MigrationRunner.CurrentVersion is 15 on this branch and 14 on
  master, so `dotnet run -- budget` against the LIVE run.db migrates it and locks the 0.4.1 engine
  driving this run out of its own store (bug 45, it happened at KS10.1). Take a sqlite3.backup copy
  and pass it as budget's POSITIONAL db path. CONDUCTOR_RUN_DB does not work for that verb (bug 61).
next: finish KS12.2 (README, docs/README.md, operating.md section 7, then payesh on a branch with a
  PR - never that repo's main). analyzer-debt is RED: pragma-src 33 vs bar 31, both MA0045 from
  KS4.4, bug 60 - stated in the ledger, not fixed, and the bar may not be raised.

## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 24 |
| Done | 19 |
| Claimed (unconfirmed) | 2 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### KS11 — Chapar - the remote surface: profiles, onboarding, evidence on demand

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS11.1 | The messenger seam: composition, chat profiles and evidence browsing extracted channel-agnostic; TelegramService becomes the transport adapter; golden replay proves current pushes byte-identical through the seam; a fake channel drives the full surface in tests; an architecture test forbids Telegram types outside the adapter | DONE ✓ | 7e64866 | .conductor/evidence/KS11/ks11-1-messenger-seam.md |
| KS11.2 | Profiles admin and observer, per chat: old-shape allowedChatIds plans behave byte-identically (pinned); an unknown profile string is refused by name at plan load; the observer surface is closed to status/tasks/progress/evidence/daily, a control or inject attempt refused by name - proven by an exhaustive command-by-profile matrix test | DONE ✓ | 1471ef9 | .conductor/evidence/KS11/KS11.2-chat-profiles.md |
| KS11.3 | Onboarding + the push grammar: run start and /start post a per-profile onboarding message (what the run is, what will be pushed, what this chat may ask); every push type recomposed to headline / proof / telemetry with money and tokens in monospace; goldens pin both profiles' renderings; a checkpoint push reads standalone | DONE ✓ | 1471ef9 | .conductor/evidence/KS11/KS11.3-onboarding-and-grammar.md |
| KS11.4 | Evidence on demand: /evidence lists checkpoints with evidence, /evidence with an id sends the artifact (document upload for files, chunked text otherwise) with size caps and a per-chat rate limit; an observer pulls a real evidence artifact end-to-end in the rig; the clip constants no longer bound what a reader can reach | DONE ✓ | df5048e | .conductor/evidence/KS11/KS11.4-evidence-on-demand.md |
| KS11.5 | Metrics on demand: /progress /money /tokens answer with figures that cross-check against status and money on the same run.db to the cent (billed money only, no price table in the diff); the daily digest re-rendered in the same grammar, golden pinned | DONE ✓ | d6be308 | .conductor/evidence/KS11/KS11.5-metrics-on-demand.md |

### KS7 — Platform catch-up - posture, hooks, usage, lifecycle, context economics

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS7.1 | Permission posture: an allowlist/deny settings profile replaces dangerously-skip-permissions for unattended runs if the installed CLI sustains it - a karvan-class stage runs green under the restricted profile with refusals telemetered, OR a filed finding says precisely why not; blast-radius posture stated honestly in ARCHITECTURE.md | DONE ✓ | 0c3380f | .conductor/evidence/KS7/ks7-1-posture.md |
| KS7.2 | Hooks as ground truth: tool events by hook (extending the hook-budget channel) become the primary source, transcript parsing the fallback; hook-derived digests match transcript-derived on a replay corpus; a hook-less agent still works; digest claim-counting (bug 19 class) fixed; skills-vs-promptExtra decided and recorded | DONE ✓ | 5b8d56e | .conductor/evidence/KS7/ks7-2-hooks-as-ground-truth.md |
| KS7.3 | Cost/usage: per-turn usage with cache split parsed from the stream; OTel emit mirroring gen_ai names from the event log; an OTLP collector renders a run's spans; the per-turn context curve reconciles with K4.1's derivation | DONE ✓ | 5794417 | .conductor/evidence/KS7/KS7-fix-s10-gates-green.md |
| KS7.4 | Session lifecycle: fork-instead-of-cold-resume for fix/audit sessions where supported, with the measured token delta vs the resume baseline; resume flags re-verified; model lineup and context ceilings re-measured into TOKEN-BUDGET-TUNING | DONE ✓ | 5794417 | .conductor/evidence/KS7/KS7-fix-s10-gates-green.md |
| KS7.5 | Context economics (B7): gate output truncated in-prompt with full text as an evidence file; RepoMapBattery + definition-of-done recap battery on the IPromptBattery seam; templates teach search-delegation; measured cache-read tokens per session DROP vs the karvan baseline on a comparable stage, reported by conductor budget | DONE ✓ | 3d7414a | .conductor/evidence/KS7/KS7-fix-s10-gates-green.md |

### KS6 — Quality lane - hygiene that buys design

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS6.1 | Curated Roslynator set (~25 design-shaped rules) as errors, everything else explicitly off, each rule adopted with a one-line reason | DONE ✓ | af6d93e | .conductor/evidence/KS6/KS6.1-curated-roslynator.md |
| KS6.2 | Analyzer-debt count ratchet extending ratchet.ps1 semantics; the referee not editable by the agent - a seeded baseline rewrite goes red | DONE ✓ | 0cb514d | .conductor/evidence/KS6/KS6.2-analyzer-debt-ratchet.md |
| KS6.3 | Complexity budgets (CA1502/1505/1506) with ratchets; first targets the largest partial surfaces - VerdictEngine (8 files) and ControlPlaneServer (11) | DONE ✓ | 094c5c3 | .conductor/evidence/KS6/KS6.3-complexity-budgets.md |
| KS6.4 | The pure evidence-to-verdict function extracted from VerdictEngine - the taxonomy testable without the loop; the seam KS4.5 plugs into | DONE ✓ | 5da5260 | .conductor/evidence/KS6/KS6.4-pure-verdict-function.md |

### KS4 — Verification that can't be gamed

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS4.1 | Holdout gates: a visibility holdout gate class excluded from prompts, tool contract and agent-readable logs, run only at verdict time; grep of composed prompt + transcript proves absence; a seeded gaming fake-agent passes visible gates, fails holdout, verdict red | DONE ✓ | 3365a3d | .conductor/evidence/KS4/KS4.1-holdout-gates.md |
| KS4.2 | Regression gate class (PASS-TO-PASS semantics): nothing-that-worked-broke as a named class with distinct reporting; a seeded regression flips the verdict with the class named in evidence | DONE ✓ | 8d649ea | .conductor/evidence/KS4/KS4.2-regression-gates.md |
| KS4.3 | Mutation gate kind: mutation-score >= threshold, diff-scoped, Stryker.NET first; a checkpoint adding tests must clear the score on changed files; an era-boundary run on conductor's own suite recorded | DONE ✓ | 4d6ad56 | .conductor/evidence/KS4/KS4.3-mutation-gates.md |
| KS4.4 | Worktree-per-stage-attempt: each attempt in a worktree, failed attempt drops the tree, verdict receives the clean attempt diff, merge ff-only on green, never branch -D an unmerged branch (lanes L1.3 fix per ND-8, amendment committed); Windows lock/removal proven; orphan sweep at startup | DONE ✓ | 05696d4 | .conductor/evidence/KS4/KS4.4-worktree-per-attempt.md |
| KS4.5 | Judge as evidence, never verdict: second-model review joins the evidence taxonomy through KS6.4's seam as an advisory row; judge disagreement recorded as evidence; a test asserts NO code path lets a judge score flip a gate verdict | DONE ✓ | 546a092 | .conductor/evidence/KS4/KS4.5-judge-as-evidence.md |

### KS8 — Interop - the run as a readable artifact (cut-first)

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| KS8.1 | Read-only MCP surface: history/status/money as MCP resources, control ops excluded by design with the ADR citing MCP's 2026 attack record; an MCP client lists runs and quotes reconciled status; no write tool exists on the surface | DONE | e9fcfa5 | .conductor/evidence/KS8/KS8.1-read-only-mcp-surface.md |
| KS8.2 | ATIF trajectory export from the fold (history export, billed costs included) validating against the ATIF schema on the karvan-core run; AGENTS.md generated/honored via the CLAUDE.md-import pattern | DONE | e9fcfa5 | .conductor/evidence/KS8/KS8.2-atif-and-agents.md |

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
