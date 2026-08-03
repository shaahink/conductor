# CI health - the public repos go green Phase Tracker

**Plan:** CI health - the public repos go green | **Branch:** `chore/ci-health` | **Design doc:** ci-health/README.md

## Handoff (overwrite this block, ≤12 lines, no history)

last: s10 STOPPED WAITING FOR THE OWNER AND FIXED SHAMSHIR'S TWO ARCHITECTURE VIOLATIONS AT
  THE SOURCE. Neither was a design decision: IAuditableEntity.cs carries a TODO(iter-38 T1)
  demanding exactly that retrofit and M48/M49/M50 are three prior instalments of it, and
  EnginePurityTests' own AF6 note accepts time entering the Engine via a Domain-owned type.
  Pushed to fix/release-node-and-gh-release as 403aced: VenueSymbolSpecEntity now implements
  IAuditableEntity with generated migration M56_VenueSymbolSpecAudit, and ReconcileToVenue
  takes a new Domain value object SimTime instead of a bare DateTime (one call site). Local
  proof: Architecture suite 8/8. NOTHING under tests/ was touched. af9900c also closes bug
  #4 - pr.yml now fires on PRs into main too, so PR 3 finally has real checks.
proof: PR run 30832397417 job `build-and-test` = SUCCESS on a hosted runner. It runs the same
  filter Release does, so Unit+Integration+Simulation+ARCHITECTURE all pass remotely - the
  fix is proven, not just local. Its sibling job `lint` = FAILURE, and that is PRE-EXISTING
  debt newly uncovered: dotnet format reports whitespace errors in
  src/TradingEngine.Adapters.CTrader/ShamshirTradeLogger.cs (~148-190), untouched by anyone
  here. lint is NOT in release.yml, so it cannot affect fleet-green - it only blocks PR 3.
next: (1) run `dotnet format src/TradingEngine.Adapters.CTrader` in C:/Code/Shamshir, commit,
  push - a formatter fixing formatting is not a weakened gate; never delete the lint job or
  revert pr.yml to hide it. (2) Read Release run 30832399158 on the fix branch (still
  in_progress at s10's end). (3) gh pr merge 3, then confirm Release green on main - that
  closes S1.3 + S1.4 and fleet-green. Finish ci-health/evidence/s10-S1-shamshir-
  architecture-violations-fixed.md (RUNS_PLACEHOLDER is the only gap). Then Z1.
trap: Shamshir has 6 pre-existing dirty files that are the owner's - leave them, never sweep
  them into a commit. DevContext2's default branch is develop and is checked out in ANOTHER
  worktree at C:/Code/DevContext2-ui - branch off origin/develop, never check it out there.
  dotnet ef in Shamshir needs `dotnet restore` first AND `--context TradingDbContext`.


## Baseline numbers (from run.db)

| Metric | Value |
|---|---|
| Total checkpoints | 20 |
| Done | 7 |
| Claimed (unconfirmed) | 9 |

## Checkpoints

Status ∈ TODO · IN PROGRESS · DONE · DONE ✓ (confirmed) · BLOCKED · SKIPPED. Evidence = artifact path produced by a run this
phase (a code path is not evidence). Agent claims are marked DONE; engine confirms as DONE ✓.

### K1 — Retire KataFlow

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K1.1 | KataFlow's `CI` workflow and its Dependabot config are disabled on the remote, and no workflow other than Dependabot's synthetic entry reports itself active | DONE ✓ | 9e39a36 | ci-health/evidence/K1.1-kataflow-disabled.md |
| K1.2 | The 20 open Dependabot pull requests are closed with a one-line comment saying the repo is being retired, and an open-PR count returns zero | DONE ✓ | 9e39a36 | ci-health/evidence/K1.2-kataflow-prs-closed.md |
| K1.3 | KataFlow's README carries a short retirement notice at the top saying the repo is archived and why, committed to main | DONE ✓ | 9e39a36 | ci-health/evidence/K1.3-kataflow-readme-notice.md |
| K1.4 | KataFlow is archived - the repository reports archived true. This is the authorised irreversible step and archiving makes the repo read-only, so K1.1 to K1.3 must all be genuinely done first | DONE ✓ | 9e39a36 | ci-health/evidence/K1.4-kataflow-archived.md |

### B1 — site - the link checker goes green

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| B1.1 | The two links to the site root in the README point at the real published URL, verified by fetching it and getting a 200 rather than by assuming | DONE | 1081e8e | ci-health/evidence/B1.1-site-url-fixed.md |
| B1.2 | lychee is given a root directory so the 15 root-relative links resolve; no correct link was rewritten to make the checker happy | DONE | 1081e8e | ci-health/evidence/B1.2-lychee-root-dir.md |
| B1.3 | The `Check links` workflow, dispatched manually on the fix branch, finishes with zero errors - run id recorded | DONE | 1081e8e | ci-health/evidence/B1.3-links-green-on-branch.md |
| B1.4 | site's workflow actions are on current majors, the pull request is merged with checks green, and a fresh green run of `Check links` exists on the default branch | DONE | 1081e8e | ci-health/evidence/B1.5-reverify-main-green-and-repos-clean.md |

### S1 — Shamshir - the release workflow goes green

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| S1.1 | `release.yml` gains a manual-dispatch trigger, so the workflow can be exercised from a branch instead of only by pushing to main | DONE | 4d06613 | ci-health/evidence/s4-S1.1-release-dispatch-run.json |
| S1.2 | The Angular build succeeds in CI - either Node is set up before the .NET build, or the MSBuild target degrades honestly when Node is absent. Whichever is chosen is justified in the commit message | DONE | 4d06613 | ci-health/evidence/s4-S1.2-angular-build-green.json |
| S1.3 | The archived release action in the final step is replaced with a maintained equivalent, and the replacement is actually exercised rather than assumed | BLOCKED | - | - |
| S1.4 | `Release`, dispatched on the fix branch, is green end to end - run id recorded - the pull request is merged, and a fresh green run exists on the default branch | BLOCKED | - | - |

### C1 — conductor - the version test stops breaking on every commit

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| C1.1 | The version test's merge guard covers merges anywhere between the newest tag and HEAD, not just at HEAD. The prerelease-shape assertion above it still runs in both branches of the guard | DONE ✓ | 11d8736 | ci-health/evidence/s7-C1.1-version-guard-widened.md |
| C1.2 | The full local gate battery is green in `C:/Code/conductor-ci`, and conductor's workflow actions are on current majors | DONE ✓ | 11d8736 | ci-health/evidence/s7-C1.2-local-battery-green.md |
| C1.3 | The pull request's `CI` run is green on both the windows and ubuntu legs, the pull request is merged, and master's own `CI` run after the merge is green too | DONE ✓ | 11d8736 | ci-health/evidence/s7-C1.3-pr-and-master-green.md |

### N1 — The Node 20 action sweep across the remaining repos

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| N1.1 | DevContext2, sitekit, site-template and blog-code each have a branch bumping their workflow actions off the Node 20 runtime, with CI green on the pull request | DONE | 8175f7c | ci-health/evidence/s8-N1.1-node20-sweep-branches.md |
| N1.2 | Those four pull requests are merged and each repo's default branch is green | DONE | 8175f7c | ci-health/evidence/s9-N1-fleet-green-except-owner-blocked-shamshir.md |
| N1.3 | The two reusable workflows in the org's dotfile-named repo are bumped, with the shared site pipeline proven by a downstream caller's CI going green. If the agent-running workflow's credential guard cannot be demonstrated to still hold, it is left alone and the reason is recorded here - that is an acceptable completion, not a failure | DONE | 8175f7c | ci-health/evidence/s9-N1-fleet-green-except-owner-blocked-shamshir.md |

### Z1 — Close out - the whole fleet reads green

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| Z1.1 | Every public repo in scope reports a green latest run for each of its active workflows on its default branch, read from the real remote and captured as one evidence file | TODO | - | - |
| Z1.2 | A short close-out report names what was fixed, what was retired, and anything left deliberately undone with its reason | TODO | - | - |

## Dependencies

```
(none — stages run sequentially by plan order)
```
