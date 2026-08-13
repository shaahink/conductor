# CI health — the public repos go green

Authority doc for the `ci-health` conductor plan. Every stage's `notes` points here.

Owner authorised this run on 2026-08-03: fix the failing CI on the public repos,
retire KataFlow rather than fix it, and bring every remaining workflow's actions
off the deprecated Node 20 runtime. Lifetime spend cap 200 USD.

## The state this plan starts from (measured 2026-08-03, not assumed)

Nine public repos carry workflows. Four are red, five are green.

| Repo | Workflow | Verdict at plan time |
| --- | --- | --- |
| conductor | `CI` (windows leg) | RED — 1 of 1773 tests |
| KataFlow | `CI` (both jobs) | RED — restore fails; 20 open Dependabot PRs all blocked behind it |
| Shamshir | `Release` | RED — 12 consecutive failures since 2026-07-16 |
| site | `Check links` | RED — 17 link errors on the weekly schedule |
| DevContext2, sitekit, site-template, blog-code | various | GREEN |
| `.github` | 2 reusable workflows | not a failure — see below |

`.github` has zero workflow runs and that is **correct**, not a fault: both files are
`workflow_call` reusable workflows that execute inside the repos that call them. Do not
"fix" them by adding triggers.

## The four diagnoses

Each was read out of the actual failing run's logs. Do not re-derive them from scratch;
do verify the claim still holds before you edit.

### C — conductor: a version test that breaks on every commit

`Conductor.Tests.SC8_2VersioningTests.TheEngineVersionIsDerivedFromTheNewestReleaseTag`
asserts `0.3.1-alpha.0.10`, gets `0.3.1-alpha.0.5`.

The two numbers are two different algorithms and both are right:

- `git describe` counts every commit unique to HEAD across ALL parents — 10 here.
- MinVer walks the SHORTEST distance from HEAD back to a tagged commit — 5 here.

They diverge whenever a merge commit sits between the tag and HEAD. Two do:
`7a988d3` and `c4febc1`, both merges of `feat/sarban` into master.

The test already knows about this case — its own comment names `c4febc1` and explains
the exact divergence — but its guard asks whether **HEAD itself** is a merge commit.
HEAD is not; the merges are behind it. So the guard never fires and the strict equality
runs on a range it cannot be right about.

Evidence that this is the whole story: master was GREEN at 19:07 on 2026-08-01 and went
RED at 19:25 on a docs-only commit. Nothing about the versioning code changed; the commit
height did.

The fix is to widen the guard from "HEAD is a merge" to "the range from the newest tag to
HEAD contains any merge", which is one `git log --merges` call. The shape assertion just
above it (the `-alpha.0.N` regex) must keep running in both cases — it is the part of the
test that still has teeth. **Do not delete or skip the test.**

### K — KataFlow: retired, not repaired

Restore fails because NuGet audit is configured as error and two packages are flagged:
`Microsoft.OpenApi` 2.0.0 (high, GHSA-v5pm-xwqc-g5wc) and `MessagePack` 2.5.192 (one high
plus ten moderate, transitive through the Aspire AppHost). Nothing builds, so all 20 open
Dependabot PRs are red too.

**The owner decided not to fix this.** KataFlow gets archived. That decision is recorded
here so no later session re-opens it: the repo is a finished kata project, the vulnerable
chain is transitive through Aspire, and the repair is not worth the spend.

Archiving is the one irreversible-shaped action in this plan. It is explicitly authorised.
Do the reversible parts first, in order, so a stop halfway leaves a coherent repo.

### S — Shamshir: the release build has no Node

`dotnet build -c Release` runs an MSBuild target in `TradingEngine.Web.csproj` line 45 that
shells out to `scripts/rebuild-ng-if-stale.ps1` to rebuild the Angular UI. The Release
workflow sets up only .NET — no Node, no npm — so the script exits 1 and takes the build
with it. Error is `MSB3073`.

Two further facts that shape the work:

1. `release.yml` triggers on `push` to main and nothing else. **It cannot be tested from a
   branch at all until a manual-dispatch trigger is added.** Add that first, or the only way
   to find out whether the fix works is to merge it.
2. The workflow's last step uses `actions/create-release@v1`, which is archived and
   unmaintained. The build has never reached that step, so it has never been exercised.
   Expect it to be the next failure once the build passes, and deal with it in the same pass.

### B — site: the link checker, two unrelated causes

17 errors, and they are not 17 broken links:

- 2 real: `https://shaahink.github.io/` returns 404 in `README.md`. Confirm the correct
  published URL before editing — the site publishes under a path, not at the domain root.
- 15 config: every root-relative link (`/blog/...`, `/projects#...`, `/images/...`) reports
  `Cannot resolve root-relative link ... provide a root dir`. lychee is being run without a
  root directory, so it cannot resolve them. This is a checker configuration gap, **not**
  broken content. Fixing it by rewriting 15 correct links into relative ones would be
  weakening the measurement; give lychee the root instead.

`links.yml` already has a manual-dispatch trigger, so the fix is verifiable on the branch
before merge.

### N — the Node 20 sweep

Every run in the fleet warns that `actions/checkout@v4`, `actions/setup-dotnet@v4`,
`actions/setup-node@v4` and `actions/upload-artifact@v4` target Node 20 and are being forced
onto Node 24. This is a warning today and a failure later. `site` already uses
`actions/checkout@v5`, so the target majors exist.

Bump to current majors. Do not pin to commit SHAs — the fleet does not do that today and
changing that convention is not this plan's job.

`.github` is in scope for the sweep but is the one place to be careful: `content-request.yml`
runs an agent and its safety is structural, resting on `persist-credentials: false` and a
guard-proof job. If you cannot demonstrate the guard still holds after bumping it, leave that
file alone and record why. A green sweep with one file honestly skipped beats a bumped file
nobody proved.

## Repo map — the local paths do NOT match the repo names

Three of these will send you to the wrong directory if you guess:

| GitHub repo | Local path |
| --- | --- |
| conductor (fix branch) | `C:/Code/conductor-ci` |
| conductor (this control room) | `C:/Code/conductor-cihealth` |
| KataFlow | `C:/Code/KataFlow-ai` |
| site | `C:/Code/site-blog` |
| .github | `C:/Code/dotgithub` |
| Shamshir | `C:/Code/Shamshir` |
| DevContext2 | `C:/Code/DevContext2` |
| sitekit | `C:/Code/sitekit` |
| site-template | `C:/Code/site-template` |
| blog-code | `C:/Code/blog-code` |

`C:/Code/conductor` is the owner's own working copy, on branch `feat/sarban`, with
uncommitted work in it. **Nothing in this plan may touch it.** The conductor fix belongs in
`C:/Code/conductor-ci`.

## How work lands

One branch and one pull request per repo. The checkpoint is done when **GitHub Actions is
green on the real remote** — a local build passing is not the acceptance. Merge the PR once
its checks are actually green, read with `gh pr checks`.

Where a repo gets both a substantive fix and an action bump, they share one branch and one
PR. Only the repos with no substantive fix get a sweep-only PR.

## What this plan will not do

- It will not fix KataFlow's vulnerabilities. The owner chose retirement.
- It will not add CI to repos that have none.
- It will not weaken any test, delete any workflow step, or relax any gate to get green.
- It will not touch the private site fleet's repos beyond the shared `.github` workflows.
