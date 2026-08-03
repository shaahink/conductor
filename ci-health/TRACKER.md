# CI health — the public repos go green — tracker

The authority is `ci-health/README.md`. This file is the checkpoint surface conductor drives;
it is a **generated view** of the work graph in `.conductor/run.db`. Claim with
`conductor task --done <id> --evidence <path>` — hand-editing a row claims nothing.

**Out of scope, deliberately:** whether KataFlow should be archived at all (the owner decided
it on 2026-08-03 — it is authorised, not open); whether to pin actions to commit SHAs;
adding CI to repos that have none.

## Handoff  (overwrite this block, ≤12 lines, no history)
last: nothing yet — this is the first session of the run.
stage: **K not started** (attempt 1).
gate: no battery has run yet.
next: **K.1** — disable KataFlow's two CI workflows and its Dependabot config.
trap: the local clone of KataFlow is `C:/Code/KataFlow-ai`, not `C:/Code/KataFlow`. Never
  paste workflow YAML into this handoff block — describe it in prose instead.

## Checkpoints

<!-- THE ESCALATION TOKEN — the word HUMAN followed by a colon — parks the run at NeedsHuman
     and notifies the owner when it appears ANYWHERE in the handoff block above. The match is
     a plain substring (`ProgressConventions.cs:59`), not a line anchor: inside backticks,
     mid-sentence, or in prose merely DESCRIBING the convention all park it just as hard as
     raising one. That is why this legend spells the token out rather than using it, and why
     it sits BELOW the handoff block rather than inside it — written the obvious way, a
     legend parks the run on its own instructions. In handoff prose the word is "escalation".
     A row flipping to BLOCKED parks the same way.

     SECOND TRAP, specific to this plan: the handoff block is composed into the next
     session's prompt, and an unresolved single-brace token in it makes the engine EXIT.
     Workflow YAML is full of brace syntax. Describe workflow changes in words; never paste
     the YAML here. -->

Status ∈ TODO · IN PROGRESS · DONE · BLOCKED. Evidence = artifact path.

| # | Checkpoint | Status | Commit | Evidence |
|---|-----------|--------|--------|----------|
| K.1 | KataFlow's `CI` and `Dependabot Updates` workflows are disabled on the remote, and `gh workflow list` shows neither as active | TODO | | |
| K.2 | The 20 open Dependabot PRs are closed with a one-line comment saying the repo is being retired, and `gh pr list --state open` returns zero | TODO | | |
| K.3 | KataFlow's README carries a short retirement notice at the top saying the repo is archived and why, committed to main | TODO | | |
| K.4 | KataFlow is archived — `gh repo view shaahink/KataFlow --json isArchived` reports true. This is the authorised irreversible step; do K.1 to K.3 first | TODO | | |
| B.1 | The two `https://shaahink.github.io/` links in the site README point at the real published URL, verified by fetching it and getting a 200 | TODO | | |
| B.2 | lychee is given a root directory so the 15 root-relative links resolve; no correct link was rewritten to make the checker happy | TODO | | |
| B.3 | The `Check links` workflow, dispatched manually on the fix branch, finishes with zero errors — run id recorded | TODO | | |
| B.4 | site's workflow actions are on current majors, and the PR is merged with all checks green | TODO | | |
| S.1 | `release.yml` gains a manual-dispatch trigger, so the workflow can be exercised from a branch instead of only by pushing to main | TODO | | |
| S.2 | The Angular build succeeds in CI — either Node is set up before the .NET build, or the MSBuild target degrades honestly when Node is absent. Whichever is chosen is justified in the commit message | TODO | | |
| S.3 | The archived `actions/create-release@v1` step is replaced with a maintained equivalent, and the replacement is exercised rather than assumed | TODO | | |
| S.4 | `Release`, dispatched on the fix branch, is green end to end — run id recorded — and the PR is merged | TODO | | |
| C.1 | The version test's merge guard covers merges anywhere between the newest tag and HEAD, not just at HEAD. The `-alpha.0.N` shape assertion still runs in both branches of the guard | TODO | | |
| C.2 | The full local gate battery is green in `C:/Code/conductor-ci`, and conductor's workflow actions are on current majors | TODO | | |
| C.3 | The PR's `CI` run is green on both the windows and ubuntu legs, and the PR is merged — master's own `CI` run after the merge is green too | TODO | | |
| N.1 | DevContext2, sitekit, site-template and blog-code each have a branch bumping their workflow actions off the Node 20 runtime, with CI green on the PR | TODO | | |
| N.2 | Those four PRs are merged and each repo's default branch is green | TODO | | |
| N.3 | The `.github` reusable workflows are bumped, with `site-ci.yml` proven by a downstream caller's CI going green. If `content-request.yml`'s guard cannot be demonstrated to still hold, it is left alone and the reason is recorded here — that is an acceptable completion, not a failure | TODO | | |
| Z.1 | Every public repo in scope reports a green latest run for each of its active workflows on its default branch, read from the real remote and captured as one evidence file | TODO | | |
| Z.2 | A short close-out report names what was fixed, what was retired, and anything left deliberately undone with its reason | TODO | | |
