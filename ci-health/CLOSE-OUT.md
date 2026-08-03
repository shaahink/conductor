# CI health — close-out report

Written at stage Z1, session 12, 2026-08-03. The plan opened with four red workflows across
four public repositories and a Node 20 action-runtime deprecation running through the rest.
All four are resolved. The fleet-wide state is captured from the real remote in
`evidence/s12-Z1.1-fleet-sweep-default-branches.md`: **16 active workflows, 12 of which can run
on a default branch, and all 12 are green there.**

## Where it started (measured 2026-08-03, before any work)

| Repo | Workflow | Verdict at plan time |
| --- | --- | --- |
| conductor | `CI` (windows leg) | RED — 1 of 1773 tests |
| KataFlow | `CI` (both jobs) | RED — restore fails; 20 open Dependabot PRs blocked behind it |
| Shamshir | `Release` | RED — 12 consecutive failures since 2026-07-16 |
| site | `Check links` | RED — 17 link errors on the weekly schedule |
| DevContext2, sitekit, site-template, blog-code | various | GREEN, but on the deprecated runtime |

## What was fixed

### conductor — a version test that broke on every commit (stage C1, PR #1)

The version test's merge guard only looked at HEAD, so any commit that was not itself a merge
tripped it. The guard was widened to cover the whole `tag..HEAD` range, and the
prerelease-shape assertion above it was kept live in **both** branches of the guard rather than
being short-circuited away. The full local gate battery was run green in `C:/Code/conductor-ci`,
the PR's `CI` run passed on both the windows and ubuntu legs, and `master`'s own run after the
merge is green: **run 30826830593 = success**. `Release` on master is likewise green (30385802454).

The tempting fake fix here was to skip or loosen the assertion. It was not taken.

### site — the link checker, two unrelated causes (stage B1, PR #1)

Two genuinely separate faults were producing one red workflow. Two README links pointed at a URL
that was not the published site; those were corrected to the real one and verified by fetching it
and reading a 200, not by assuming. The remaining 15 errors were root-relative links that lychee
could not resolve because it had no root directory configured — fixed by giving lychee the root,
**not** by rewriting correct links to satisfy a misconfigured checker. `Check links` was then
dispatched on the fix branch and finished with zero errors, the actions were bumped off Node 20,
the PR merged on green checks, and `main` now reads **Check links 30821400002 = success** and
**Deploy to GitHub Pages 30821258479 = success**.

### Shamshir — the release build had no Node, then three more layers (stage S1, PR #3)

The longest thread in the plan. `release.yml` triggered only on a push to `main`, so it could not
be exercised from a branch at all; giving it a manual-dispatch trigger was the first piece of
work, not an afterthought. Underneath that:

1. **No Node in CI.** `wwwroot` is gitignored, so the Angular UI must be built before `dotnet`
   or `TradingEngine.Web`'s `EnsureAngularCurrent` target fails the build. Node setup was moved
   ahead of the .NET build.
2. **A deprecated release action**, replaced with a maintained one (`softprops/action-gh-release@v3`).
3. **Two architecture-suite violations**, fixed in product code, not in the test.
4. **NETSDK1152 on publish.** `TradingEngine.Web` references `TradingEngine.Host` — an executable
   worker — for engine types, and Host's `appsettings*.json` ride that project reference into
   Web's publish set at Web's own relative paths, colliding. Fixed in `TradingEngine.Web.csproj`
   with a target that drops the Host-owned files from `ResolvedFileToPublish`. The fake fix —
   setting `ErrorOnDuplicatePublishOutputFiles=false`, which silences the diagnostic and lets an
   arbitrary copy win — was explicitly rejected.

The fix branch's Release ran green end to end (30833477182, all 16 steps, including the
replacement release action actually producing a release). Both PR checks were read green before
merging (`build-and-test` 5m37s, `lint` 6m52s), and **`main`'s Release run 30834317700 = success** —
the repo's first green Release in its history after 12 straight failures.

### The Node 20 action-runtime sweep (stage N1)

DevContext2 (PR #11), sitekit (PR #2), site-template (PR #32) and blog-code (PR #1) each moved
their workflow actions off the Node 20 runtime, with CI green on the pull request before merge
and green on the default branch after. Current state: DevContext2 `CI`/`Eval`/`Release` all green
on `develop`; sitekit `CI` 30828800198 green; site-template `CI` 30829094483 green; blog-code
`build` 30828796177 green.

In `shaahink/.github`, the sweep turned out to be a **measurement rather than an edit** — all six
action references across both reusable workflows were already on their current majors, so nothing
was bumped because nothing needed bumping. The shared site pipeline (`site-ci.yml`) is proven
green not in the abstract but through a real downstream caller: `site-template`'s `ci.yml` calls
it at `@main`, and that caller's run 30829094483 is green.

## What was retired

**KataFlow** was retired rather than repaired — the owner's decision, taken because the repo's
`CI` failure was a dependency-restore fault sitting under 20 blocked Dependabot PRs and the repo
was no longer wanted. In order: `CI` and the Dependabot config were disabled on the remote; the
20 open Dependabot PRs were closed each with a one-line note saying the repo is being retired;
the README gained a short retirement notice at the top explaining what and why; and only then was
the repo archived, since archiving is irreversible and makes the repo read-only. It now reports
`archived: true`, with one green synthetic Dependabot entry that GitHub does not allow to be
removed.

The vulnerabilities in KataFlow were **not** fixed. That was the plan's stated position from the
start, not a shortfall discovered late.

## Left deliberately undone, with reasons

1. **`shaahink/.github` → `content-request.yml` was not bumped or re-proven.** This is the file
   that runs an agent, and its safety property is structural: the agent's checkout carries no
   credential (`persist-credentials: false`), so `git push` fails everywhere from where the agent
   sits. The plan's rule is that if that guard cannot be demonstrated to still hold after a bump,
   the file is left alone and the reason recorded. It could not be demonstrated from this repo:
   the guard-proof job sits behind a `prove-guards` input on a `workflow_call`-only workflow, so
   proving it requires a **caller** invoking it with that input, and no such caller exists in the
   public fleet. Since no bump was needed either, the file is bit-for-bit the version whose guard
   was already in force. A future session that does need to change it must arrange a caller first.

2. **Four workflows have no run on their default branch, by trigger design.** `content-request.yml`
   and `site-ci.yml` are `workflow_call`-only reusables; Shamshir's `pr.yml` is `pull_request`-only;
   sitekit's `release.yml` triggers on tag pushes. None of these can produce a default-branch run,
   so no run was manufactured for them. Three are separately proven green (caller run 30829094483,
   PR 3's checks, and tag run 30645090630 for `v0.24.0`); the fourth is item 1 above.

3. **Bug #5 (open, medium) — Shamshir cosmetic build-output leak.** Host's
   `appsettings.Backtest.json` still lands in Web's `bin/` at build time via the project
   reference. Session 11 fixed the *publish* set, which is what NETSDK1152 was about; at build
   time Web's own `appsettings.json` correctly wins the collision, and Web never reads the
   Backtest config. Cosmetic. The real fix is extracting the engine types Web needs out of the
   Host executable into a library — a refactor well outside a CI-health plan.

4. **Bug #2 (open, medium) — DevContext2's non-default branch `main` has a red CI run**
   (30817933911, macos-latest Engine leg). DevContext2's default branch is `develop`; `main` is
   outside this plan's scope, which is default-branch health.

5. **Bugs #1 and #3 (open, medium) — two timing-flaky tests.** conductor's
   `LiveRun_TheGateBatteryDoesNotStartUntilTheSessionsBgChildHasExited` on hosted windows runners,
   and DevContext2's `StageWaterfallTests.Waterfall_names_every_pipeline_phase_and_accounts_for_the_wall`
   on macos-latest. Both are filed rather than papered over. Neither was made to pass by relaxing
   its threshold — that would have been exactly the "weaken a measurement to get green" move this
   plan forbids, and a flaky timing test is a real signal about the test's design.

6. **No CI was added to repos that have none, and the private site fleet was not touched** beyond
   the shared `.github` workflows. Both were out of scope from the start.

## Housekeeping state at close

- Open pull requests across all nine repos: **zero**.
- Every substantive fix and its action bump shared one branch and one PR per repo, as required.
- Every PR was merged only after its checks were read green with `gh pr checks`.
- Nothing was made green by deleting or skipping a test, relaxing an assertion, removing a
  workflow step, rewriting a correct link, or softening a gate.

## For the owner

This stage parks for review even though the fleet is green — that is intentional and is the last
look before the run ends. The three judgement calls worth a moment of the owner's attention are:
the KataFlow archive (irreversible, already authorised and executed), `content-request.yml` being
left untouched with its guard unre-proven, and whether bug #5's underlying refactor —
splitting Shamshir's engine types out of the Host executable — is worth scheduling.
