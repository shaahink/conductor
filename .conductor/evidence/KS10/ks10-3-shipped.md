# KS10.3 — the era ships. Every step performed, with what it returned

**2026-08-15, by the owner's explicit authorisation** ("i autorize you to the release on my behalf
all the way and reinstall here"). Session #25 left this checklist runnable and measured; this is the
record of running it. The runbook it follows is
[`ks10-3-owner-runbook.md`](ks10-3-owner-runbook.md) — its nine steps, in its order.

## Verdict

Shipped. `conductor update --check` on the reinstalled binary says **"running the latest release
(latest release v0.4.1)"** — the checkpoint's own acceptance, measured by the tool it ships.

## Step by step

### 1. No live run — clear

`conductor ps` → *"no conductor runs answering on ports 4317-4336."* The engine that supervised
session #25 (PID 6716) had exited, as the runbook predicted it would.

### 2. Working tree committed before anything moved

Two files were dirty at the start: `.conductor/REPORT.md` (a real diff — the report as the last
session left it) and `plans/karvansara/CORE-TRACKER.md` (line endings only, no content diff).
Committed as `588464b` so the release could not carry an unrecorded working tree.

### 3. `## [Unreleased]` → `## [0.4.1] - 2026-08-15` — the mandatory rename, confirmed mandatory

Before: `sh tools/changelog-section.sh 0.4.1` → **exit 1**, *"no section for 0.4.1 in CHANGELOG.md."*
After: **exit 0, 131 lines**. That text is the release body verbatim. Committed as `1274197`.

The runbook called this the step that kills the release in the guard job before five platforms
compile. It was not theoretical: the guard job resolves the section by the tag's version, and the
tag is `v0.4.1`.

### 4. Merge to master — fast-forward, as measured

`git merge --ff-only feat/karvansara` from master. No conflict was possible: master `304fc5b` was an
ancestor, 100 commits behind (the runbook measured 99; two doc commits landed after it wrote that,
and one of those was its own). Pushed `304fc5b..1274197`.

### 5. Tag and release — the guard passed, five platforms built

`v0.4.1` annotated, pushed. Run
[31885190092](https://github.com/shaahink/conductor/actions/runs/31885190092):

| Job | Result |
|---|---|
| tag is releasable (guard) | success — the changelog section resolved |
| linux-x64 | success — this is the leg carrying the version-equals-tag assert (`release.yml:157-163`) |
| macos-arm64 · macos-x64 · windows-x64 · linux-arm64 | success |
| attach to release | success |

Release **v0.4.1** published, not a draft, not a prerelease, six assets: five platform archives plus
`SHA256SUMS.txt`.

### 6. Reinstall — `tools/install.ps1` from the tagged commit

```
version: 0.4.1-alpha.0.105+ac2501d61eb3.dirty  ->  0.4.1+12741973f209
```

MinVer over tag height resolves the tagged commit to a clean `0.4.1`, so the local install and the
published release are the same version. Both engine and Go face were rebuilt into
`%LOCALAPPDATA%\Programs\conductor`, and the shim repointed.

**The acceptance, measured:** `conductor update --check` → *"✓ running the latest release (latest
release v0.4.1)"*. Before this step the same command said *"newer than the latest release v0.4.0 (a
local or prerelease build)"*.

This is also what retired bug #45 in practice: the pre-install shim could not read a schema-14
store. It reads it now.

### 7. `github sync --backfill` — the first real use of KS9, and it is idempotent on a real repo

Target `shaahink/conductor` (PUBLIC, **0 existing issues**), chosen by the owner when asked; the plan
carries no `github` block, so the destination was explicit on the command line. Token from
`CONDUCTOR_GITHUB_TOKEN`, sourced from `gh auth token`.

```
33 created · 0 updated · 0 unchanged · 0 retired · 24 comments · 0 errors
99 requests
```

**Then the claim KS9.1 has been making since it was written, tested against a repository that now
has issues in it:**

```
0 created · 0 updated · 33 unchanged · 0 retired · 0 comments · 0 errors
```

Zero duplicates on the second pass. This is the first time that ran against a real repo rather than
a scratch one.

What it writes, read rather than assumed —
[issue #3](https://github.com/shaahink/conductor/issues/3) is `KS0.1`, **CLOSED**, labelled
`conductor:status:done` · `conductor:source:tracker` · `conductor:confirmed`, carrying the stage,
the commit, the evidence path and the attempt count, under the disclaimer the mirror stamps on
every issue: *"This board is a VIEW: the tracker and the run's event log are the contract, and
nothing here is ever read back into the run."*

### 8. payesh PR #1 — read, then merged

Merged 2026-08-15T12:47:04Z. All four checks green beforehand (`gates`, `build / gates`, and the
Vercel preview deployment).

The runbook flagged commit `e9077b7` as relaxing a privacy rule and said it *"wants a real read, not
a rubber stamp."* Read. Both exemptions are narrower than the phrase suggests:

- **The whole-repo-name exemption** fires only when the private repo's basename is a word the site
  is *already* allowed to say (`GENERIC` — site, code, docs, repo, build, and now `website`). The
  repository **path** keeps its own entry, so `C:\code\website` is still forbidden. The rule this
  replaces was unfalsifiable: it flagged the English word "website" on 22 pages including 404.html,
  and the only route to green was deleting prose.
- **The run-slug exemption** is scoped by `if (publicNames.has(repoName.toLowerCase())) continue;`
  — public repositories only. A private repository's slug and phrases stay secret in all three
  fields, and the `harrowgate-linens` fixture still proves it. The machine-path keeps run *above*
  that line, so run-store and plan paths remain forbidden for public repos too.

Both pinned by 69 lines of new tests in `test/anonymity.test.mjs`.

**Still open, and correctly so:** this run `9647f1b8` stays out of the published corpus until
`anonymise.json` gives it a label, scenario, repoKey and disposition. Merging the PR did not change
that.

### 9. Closing the era — and the one place the convention does not apply

`docs/dev/README.md` says an era's brief moves to `docs/history/` and its tracker to
`docs/history/archive/trackers/` when it closes. **Neither moves here, for the reason that document
already records for karvan:**

- `plans/karvansara/core.plan.json:36` names `plans/karvansara/CORE-TRACKER.md` as its `tracker`.
- `karvansara-edge` (KS4, KS6–KS8, per the design doc's ND-4) is **not yet authored** and belongs in
  that same directory, and its design lives in the brief that would move.

Moving either file breaks a plan that has not run yet — the same trap the W-series and karvan
paragraphs describe. What closed today is the **core plan**, not the Karvansara era. Both files move
when edge closes. Recorded in `docs/dev/README.md` beside karvan's exception.

## What remains after this, for the record

| Item | State |
|---|---|
| `gh auth refresh -s project` | Owner's, interactive, optional. Grants the scope KS9.3 named — it does not build the board, which is deliberately unwritten (`GithubProjects.UnimplementedRefusal`). |
| Run `9647f1b8` in the payesh corpus | Excluded until `anonymise.json` describes it. |
| `karvansara-edge` | Unauthored. KS4 (verification that can't be gamed), KS6 (quality lane), KS7 (platform catch-up), KS8. |
| Bug #44 — 43 pragmas against a ceiling of 38 | Pre-existing, RED, untouched by this era. |
