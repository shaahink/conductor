# CI health - the public repos go green - tracker

The authority is `ci-health/README.md`. This file is the checkpoint surface conductor drives;
it is a **generated view** of the work graph in `.conductor/run.db`. Claim with
`conductor task --done <id> --evidence <path>` - hand-editing a row claims nothing.

**Out of scope, deliberately:** whether KataFlow should be archived at all (the owner decided
it on 2026-08-03 - it is authorised, not open); whether to pin actions to commit SHAs; adding
CI to repos that have none.

## Handoff  (overwrite this block, <=12 lines, no history)
last: stage K1 complete - KataFlow is retired. CI disabled on the remote, Dependabot config
  removed (commit f47e28d), all 20 Dependabot PRs closed with a retirement comment, README
  notice on main (af8930a), repo archived 2026-08-03T13:50:54Z. Evidence in ci-health/evidence.
stage: **K1 DONE** - all four checkpoints claimed with an evidence file each.
gate: no battery this session. Read by hand on the real remote: KataFlow isArchived true,
  open PRs 0, no active workflow but Dependabot's synthetic entry. Untouched and still red:
  conductor CI, Shamshir Release, site Check links.
next: C1, S1, B1 and N1 are all unstarted; nothing in K1 blocks any of them.
trap: KataFlow CI run 30765473647 on main is red PERMANENTLY and correctly - the last run of
  a now-disabled workflow in a read-only repo. Do not chase it green; an archived repo is out
  of scope for any latest-run-green sweep. Paths still do not match names: site is
  `C:/Code/site-blog`, conductor's fix branch is `C:/Code/conductor-ci`.

## Checkpoints

<!-- THE ESCALATION TOKEN - the word HUMAN followed by a colon - parks the run at NeedsHuman
     and notifies the owner when it appears ANYWHERE in the handoff block above. The match is
     a plain substring (`ProgressConventions.cs:59`), not a line anchor: inside backticks,
     mid-sentence, or in prose merely DESCRIBING the convention all park it just as hard as
     raising one. That is why this legend spells the token out rather than using it, and why
     it sits BELOW the handoff block rather than inside it - written the obvious way, a
     legend parks the run on its own instructions. In handoff prose the word is "escalation".
     A row flipping to BLOCKED parks the same way, and a BLOCKED row also means its stage can
     never close, because the phase gate only fires when every row is DONE or SKIPPED.

     SECOND TRAP, specific to this plan: the handoff block is composed into the next session's
     prompt and then validated for unresolved placeholders. Workflow YAML is full of brace
     expressions. Describe workflow changes in words; never paste the YAML here.

     THIRD, learned at plan time: a checkpoint id must be LETTERS THEN DIGITS then a dot then
     digits - K1.1, not K.1. `ProgressConventions.StageIdPattern` is [A-Za-z]+\d+, so a row
     with a bare-letter stage id silently fails to parse and the checkpoint vanishes with no
     error anywhere except doctor's "no declared work items" warning. -->

Status in TODO / IN PROGRESS / DONE / BLOCKED / SKIPPED. Evidence = artifact path.

### K1 - Retire KataFlow

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K1.1 | KataFlow's `CI` workflow and its Dependabot config are disabled on the remote, and no workflow other than Dependabot's synthetic entry reports itself active | TODO | | |
| K1.2 | The 20 open Dependabot pull requests are closed with a one-line comment saying the repo is being retired, and an open-PR count returns zero | TODO | | |
| K1.3 | KataFlow's README carries a short retirement notice at the top saying the repo is archived and why, committed to main | TODO | | |
| K1.4 | KataFlow is archived - the repository reports archived true. This is the authorised irreversible step and archiving makes the repo read-only, so K1.1 to K1.3 must all be genuinely done first | TODO | | |

### B1 - site: the link checker goes green

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| B1.1 | The two links to the site root in the README point at the real published URL, verified by fetching it and getting a 200 rather than by assuming | TODO | | |
| B1.2 | lychee is given a root directory so the 15 root-relative links resolve; no correct link was rewritten to make the checker happy | TODO | | |
| B1.3 | The `Check links` workflow, dispatched manually on the fix branch, finishes with zero errors - run id recorded | TODO | | |
| B1.4 | site's workflow actions are on current majors, the pull request is merged with checks green, and a fresh green run of `Check links` exists on the default branch | TODO | | |

### S1 - Shamshir: the release workflow goes green

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| S1.1 | `release.yml` gains a manual-dispatch trigger, so the workflow can be exercised from a branch instead of only by pushing to main | TODO | | |
| S1.2 | The Angular build succeeds in CI - either Node is set up before the .NET build, or the MSBuild target degrades honestly when Node is absent. Whichever is chosen is justified in the commit message | TODO | | |
| S1.3 | The archived release action in the final step is replaced with a maintained equivalent, and the replacement is actually exercised rather than assumed | TODO | | |
| S1.4 | `Release`, dispatched on the fix branch, is green end to end - run id recorded - the pull request is merged, and a fresh green run exists on the default branch | TODO | | |

### C1 - conductor: the version test stops breaking on every commit

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| C1.1 | The version test's merge guard covers merges anywhere between the newest tag and HEAD, not just at HEAD. The prerelease-shape assertion above it still runs in both branches of the guard | TODO | | |
| C1.2 | The full local gate battery is green in `C:/Code/conductor-ci`, and conductor's workflow actions are on current majors | TODO | | |
| C1.3 | The pull request's `CI` run is green on both the windows and ubuntu legs, the pull request is merged, and master's own `CI` run after the merge is green too | TODO | | |

### N1 - The Node 20 action sweep across the remaining repos

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| N1.1 | DevContext2, sitekit, site-template and blog-code each have a branch bumping their workflow actions off the Node 20 runtime, with CI green on the pull request | TODO | | |
| N1.2 | Those four pull requests are merged and each repo's default branch is green | TODO | | |
| N1.3 | The two reusable workflows in the org's dotfile-named repo are bumped, with the shared site pipeline proven by a downstream caller's CI going green. If the agent-running workflow's credential guard cannot be demonstrated to still hold, it is left alone and the reason is recorded here - that is an acceptable completion, not a failure | TODO | | |

### Z1 - Close out: the whole fleet reads green

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| Z1.1 | Every public repo in scope reports a green latest run for each of its active workflows on its default branch, read from the real remote and captured as one evidence file | TODO | | |
| Z1.2 | A short close-out report names what was fixed, what was retired, and anything left deliberately undone with its reason | TODO | | |
