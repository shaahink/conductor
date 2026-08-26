# Charkh — the wheel: what the owner still does by hand

**Compiled 2026-08-26, the day `v0.5.0` shipped.** The design authority for
`plans/charkh/core.plan.json`.

Divan gave the run a mouth and an ear. Karvansara-edge made a green gate mean something. Both
shipped together as `v0.5.0` — and shipping them exposed the thing neither era touched: **the parts
of this project that only work because a person is standing there.**

That is not a feeling. It is four measurements taken during that release:

| What happened at the release | What it says |
|---|---|
| `KS12.3` was recorded as performed. It was **one-seventh** performed — `master` was moved and nothing else. No tag, no CHANGELOG rename, no doc move, no backfill. The edge era sat shipped-but-untagged for a week while `docs/dev/README.md` called it current work. | A runbook whose steps are prose has no idea which of its steps ran. |
| GitHub CI's `windows - full gate battery` had been **red for the entire Divan era** — 40+ runs, none green — while the local battery was green for all 23 checkpoints. | The gate battery and CI disagree, and nothing compares them. |
| `docs/assets/demo.gif` and payesh's four social cards were both stale against the shipped product. Payesh **refused to merge** over its cards; conductor shipped its GIF without noticing. | One repo made staleness a gate. The other did not. |
| `github sync --backfill` of an older run would have retired the newer run's whole board. Found by reading source after a dry run looked wrong — nothing in the tool says so. | A destructive default with no guard. |

Every one is the same shape: **a step that depends on a human remembering.** Charkh's job is to
turn each into something the machine performs, refuses, or measures. The wheel, not the push.

---

## CH1 — CI green, and the reason it was not

Two causes, both diagnosed, both already reproduced locally. Fixing them is the small half; the
stage's real deliverable is **closing the class**.

### CH1.1 — the renderer inherits the checkout's line endings

`src/Conductor.Core/Publishing/BoardSnapshotHtml.cs:277` holds the inline CSS in a **C# raw string
literal**. A raw string literal inherits the line endings of *its source file*. Every other line of
that renderer appends an explicit `\n`, so on a CRLF checkout the CSS block alone carries CRLF and
`Render()` emits a mixed document, while the file `Publish` writes is LF-normalised.
`DV6_3BoardPageTests.Publishing_writes_one_file_atomically_and_hands_back_what_it_rendered`
compares the two and fails.

The published page's bytes must not depend on how the repository was cloned. Normalise the constant
at load, and **assert the property, not the symptom** — a test that `Render()` output contains no
`\r` at all, so the next raw string to arrive in that file cannot reintroduce it quietly.

### CH1.2 — the repo's own plan is not loadable anywhere but this machine

`plans/*/core.plan.json` carry `"repo": "C:/code/conductor"`. Three `KS1_4DoctorPlanLintsTests`
load this repo's own plan and call `Validate()`, which refuses a `repo` that does not exist:
`DoctorIsGreenOnThisReposOwnPlan`, `TheShimCeilingIsAFailWhenItIsTheOneThatApplies`,
`ThisReposOwnPlanIsAlreadyOverTheCmdShimCeiling`. On any machine but this one — CI included — they
fail on a hardcoded path.

The decision is the checkpoint's to make and to record: either the tests resolve `repo` to the
repository root they are running in before validating, or plans learn a repo-relative form. Whatever
is chosen, **a plan file in this repo must be loadable on a fresh clone**, and that must be pinned.

### CH1.3 — the two batteries stop being able to disagree silently

This is the checkpoint that pays for the stage. The local battery and GitHub CI ran different
verdicts for a whole era and nothing compared them. A run's phase gate is what this project trusts;
if it can be green while CI is red, the trust is misplaced.

Deliver a check that makes the divergence **visible where the run can see it** — at minimum, a way
to ask "is CI green on the commit this run is about to build on, and does it run the same battery I
just ran?" The answer must reach the report and the owner queue the way DV1.1's channel health does.
A green local battery beside a red CI is a finding, not a footnote.

**Exit:** GitHub CI green on Windows and Linux for `master`, and a seeded divergence proven to
surface rather than pass.

---

## CH2 — the tour that matches the engine

`docs/assets/demo.gif` is the first thing under the README's H1. It is recorded by
`tools/demo/make-demo-gif.ps1`, which runs the **live Face binary** under `--demo` through the VHS
container (`ghcr.io/charmbracelet/vhs`, which bundles vhs + ttyd + ffmpeg) and cross-compiles the
Face for linux to run inside it. `docs/assets/demo.tape` is what describes the tour. **Docker is a
prerequisite and was not answering when this brief was compiled — verify it before planning around
it, and if it cannot be made to work, say so with the exact failure rather than hand-assembling a
GIF from stills; that path was already tried and produced a monochrome, silently-clipped result.**

### CH2.1 — re-record against what shipped

The tape predates the courier, the inbox, the board page and the hub's current surfaces. Re-record
it against `v0.5.0`'s Face, and extend the tour to the surfaces the last two eras added. The geometry
is not negotiable: 1176x736 gives the shell exactly 110x34, the one size
`face-go/internal/tui/golden_test.go` covers.

### CH2.2 — staleness becomes a gate

Payesh already solved this and conductor did not. `npm run seo` re-renders each social card's text,
compares it to a manifest written when the PNGs were taken, and **refuses the merge** when they
disagree — which is exactly why payesh's cards were caught and conductor's GIF was not.

Port the pattern: a check that says when `demo.gif` no longer shows what the product does, with a
manifest of what it was recorded from (the Face version, the tape's hash, the surfaces it visits).
It does not need to diff pixels; it needs to fail when the thing it depicts has moved.

---

## CH3 — the docs say what shipped

### CH3.1 — the published surface against `v0.5.0`

README, `docs/cli.md`, `docs/operating.md`, `docs/plan-config.md`, `docs/quickstart.md`,
`docs/troubleshooting.md`, `docs/tracker.md` and the `docs/README.md` index, reconciled against the
engine that is actually installed. The courier is now a real always-on process on the owner's
machine and the docs describe it as a thing you might install.

### CH3.2 — the references that no longer resolve

The DV7.3 move repointed nine structural paths (`tracker`, `planDoc`, `readOrder`) and deliberately
left the plans' `notes` prose citing `docs/dev/KARVANSARA-PLAN-2026-08-13.md` and
`docs/dev/NEXT-ERA-FINDINGS-2026-08-23.md`, which are now in `docs/history/`. That was the right
call at the time — the notes are the spec as authored — but a stale path in a file a session reads
is a trap. Decide once, apply consistently, and record the rule in `docs/dev/README.md`.

Sweep the rest: every relative link in `docs/`, every path named in a test message, every
`.conductor/contracts/*.json` reference that a future reader will follow. **The frozen run artifacts
under `.conductor/` are a record and are not rewritten** — the sweep reports them, it does not edit
them.

### CH3.3 — the docs-match-reality battery covers what Charkh changed

Extend `SF7_1DocsMatchRealityTests` for every verb and config key this era adds, and prove each new
assertion red on a seeded stale doc. That negative control is the point of the battery.

---

## CH4 — the machinery

The headline. Right now closing an era means a session writes a runbook and a person performs seven
acts by hand, in order, from prose. `.conductor/evidence/DV7/dv7-3-owner-runbook.md` is the best
version of that artifact this project has produced, and it still let six of its seven acts go
unperformed one era earlier without anyone noticing.

### CH4.1 — `release preflight` as a verb

Every precondition DV7.3 measured by hand becomes something the engine measures: the merge is a
fast-forward; the CHANGELOG has a section for the intended version and `tools/changelog-section.sh`
exits 0 on it; no conductor process is live; the installed engine's migration version matches the
tree's; the courier's token scope and task state; the run whose backfill is owed. Output is a
checklist with a verdict per line and a **non-zero exit when any line is red** — the runbook's own
GREEN/RED table, produced rather than written.

### CH4.2 — the acts that can be performed, performed

Of the seven, several are mechanical: the CHANGELOG rename carries a version number and a date; the
tag is derivable; the merge is `--ff-only`; the doc move is `git mv` plus a repoint of `tracker`,
`planDoc` and `readOrder`. Automate what is mechanical, **refuse what is judgement** (the version
number, single-vs-split release, whether a run joins the published corpus) and say which is which.
An act that needs the owner should be named and stopped at, not silently skipped — that is the exact
failure KS12.3 had.

### CH4.3 — the backfill stops being able to vandalise the board

`GithubBoardSync.SyncAsync` lists issues **repo-wide** (`client.ListIssuesAsync(repo)`) and
`RetireAsync` closes any task-marked issue whose id is not in the *current* run's card set, with the
comment "this checkpoint is no longer declared in the plan". Nothing scopes that to the run being
synced. Measured 2026-08-26: a dry run of the edge run's backfill reported `23 retired · 23
comments` — exactly the count of Divan's checkpoints, which had just been correctly recorded. The
edge run's GitHub record is unwritten today because of it.

Scope the sweep to the run, or refuse a backfill that would retire another run's board. Then write
the edge run's record. Distinct from bug **#79** (a second pass inside the API's replica lag
re-creating issues), which is about the same backfill twice.

### CH4.4 — the runbook becomes the verb's output

`.conductor/evidence/KS12/ks12-3-owner-runbook.md` and its DV7 successor were hand-written twice,
in the same shape, and the second one's first finding was that the first one had not been carried
out. A session should not be writing that document from scratch again. What remains genuinely
owner-only gets generated from the preflight's own measurements, with the commands to run.

---

## CH5 — ship Charkh with the machinery it built

The proof. Close this era **using CH4** rather than by hand: run the preflight, let it produce the
runbook, perform what it can perform, stop at what it refuses. Anything it gets wrong is a finding
worth more than the checkpoint — record it either way.

---

## Decisions taken here, so no session re-litigates them

- **`.conductor/` run artifacts are a record.** Frozen contracts and evidence from closed runs are
  never rewritten to fix a path. Sweeps report them.
- **Automate the mechanical, refuse the judgement.** The version number, single-vs-split release,
  and corpus inclusion stay the owner's. Charkh's job is to make the machine *ask* rather than
  let a step be forgotten.
- **A property test beats an example test** where the vocabulary moves. Both bugs this era starts
  from were pinned to a specific value that later changed meaning.
- **Payesh is not a satellite of this plan.** Its PRs were landed before Charkh launched and its
  `main` auto-deploys to the world. No stage here touches that repo.
