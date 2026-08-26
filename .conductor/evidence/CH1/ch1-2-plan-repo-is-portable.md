# CH1.2 — a plan file in this repo is loadable on a fresh clone

Session 1, stage CH1, 2026-08-26. Branch `feat/charkh`.

## The cause, measured

`src/Conductor.Core/Models/PlanConfig.cs:190` — `Load` calls `Validate()` **before it returns**, and
`CollectErrors` (same file) refuses a `repo` that does not exist. Every plan under `plans/` carried an
absolute machine path: `C:/code/conductor` for six, `C:/code/conductor-baton` for eleven more written
before the directory was renamed (`C:/code/conductor-baton` does not exist on this machine either —
checked; those plans were already unloadable everywhere).

So the three `KS1_4DoctorPlanLintsTests` — `DoctorIsGreenOnThisReposOwnPlan`,
`ThisReposOwnPlanIsAlreadyOverTheCmdShimCeiling`, `TheShimCeilingIsAFailWhenItIsTheOneThatApplies` —
loaded this repo's own plan and failed on every machine but the author's. Their attempted mitigation,
`plan.Repo = root;` on the line after the Load, could never have worked: **the Load is what throws.**

## The decision, and why

Two routes were on offer. Chosen: **plans learn a repo-relative form.**

`plan.repo` may be written relative to the plan file's own directory and is resolved against it once,
at load, by `PlanConfig.ResolveRepoAgainstPlanFile()`. Sixteen shipped plans became `".."` (top level)
or `"../.."` (under `plans/<era>/`). Everything downstream — `StateDir`, `TrackerPath`, gates,
satellites — sees the same absolute string it always did.

The test-side route was rejected because it fixes the *test*, not the *file*: `conductor doctor -p
plans/karvansara/core.plan.json` on a fresh clone would still refuse to load this repo's own worked
examples, which is the first thing a reader tries.

An absolute value is still honoured untouched, and that is deliberate rather than back-compat:
**a plan that is driving a run names the checkout it drives.** `plans/charkh/core.plan.json` — this
run's own plan — is therefore left absolute, and the sweep test asserts that exclusion set *exactly*,
so it can only change by someone editing that line on purpose. It is also the safe answer: that file
is read by the **installed 0.5.0 engine**, which has no resolution; a relative `repo` there would be
tested with `Directory.Exists("../..")` against the process CWD and would kill this run.

A Windows drive-letter path counts as absolute even when read on Linux, where `Path.IsPathRooted`
disagrees — otherwise the error a Linux reader gets names a path nobody ever configured.

## The trap the route carries, and the guard

`PlanDocumentEditor.Save` (KS3.2) persists by diffing a **re-serialised model** against the file's own
text and splicing only the difference. Resolution mutates the model, so model and file now disagree
about `repo` *by construction* — unguarded, the first `plan set`, `add-stage` or Face edit would splice
the absolutised path back in, with nothing in the diff to say a path had been rewritten, and the
portability would be gone for good.

Guard: `PlanDocumentEditor.cs` restores `after["repo"] = plan.RepoAsWritten` before diffing.
`PlanSetCommand.cs` gained the same `ResolveRepoAgainstPlanFile()` call its validation pass needed.
Pinned by `Saving_a_plan_does_not_re_absolutise_the_repo_it_was_loaded_from`.

## Pinned by tests

`tests/Conductor.Tests/CH1_2PlanRepoIsPortableTests.cs`, 4 tests, all passing:

- `This_repos_own_plan_loads_from_a_clone_that_is_not_this_checkout` — copies the plan file and the
  tracker it names into a temp directory that has never heard of this checkout, loads it, and asserts
  `plan.Repo` **is that directory**. This is what a fresh clone is.
- `The_old_absolute_form_cannot_resolve_to_the_clone_it_was_read_from` — the negative control, written
  so it holds on **both** kinds of machine: where `C:/code/conductor` is absent the load throws; where
  it is present the load succeeds and silently points at a different tree, which is the worse half and
  the half a "does it throw" assertion would miss.
- `Every_shipped_plan_names_its_repo_relative_to_itself` — the sweep, with the one by-design exclusion.
- `Saving_a_plan_does_not_re_absolutise_the_repo_it_was_loaded_from` — the writer guard above.

The three `KS1_4DoctorPlanLintsTests` lost their `plan.Repo = root;` line: what the Load returns now
IS this checkout, so those three tests are themselves a pin on the form.

## Live proof through the fresh build — not the installed engine, not this repo

`.conductor/evidence/CH1/ch1-2-live-doctor.txt`. A temp clone holding only the plan file and its
tracker, `CONDUCTOR_STATE_HOME` pointed inside it so no live state was touched:

    dotnet run --project src/Conductor -- doctor -p <clone>/plans/karvansara/core.plan.json

    conductor doctor - Karvansara core - the open door
    repo: C:\Users\shahi\AppData\Local\Temp\ch12-freshclone-145c8827
    ...
    24 ok - 2 warn - 1 fail - 1623ms

The repo line is the whole proof: the plan resolved to the clone, not to `C:/code/conductor`. The one
fail is `git` — a bare copy is not a git repository — and the two warns are the pre-existing cmd-shim
argv warning and satellites.

Negative control, same clone, `repo` put back to an absolute path:

    ? plan     Invalid plan config:
      - plan.repo 'C:/code/conductor-baton' does not exist - create the dir or correct the path
    0 ok - 0 warn - 1 fail - the plan does not load, so no other check ran (199ms)

That is exactly what CI saw for a whole era.

## Also

`docs/plan-config.md` gained a `repo` section stating the rule and the key-table row now says
"absolute, or relative to the plan file itself". Nothing was weakened: 4 tests added, none removed,
skipped or relaxed.

## Full suite, and the ratchet the change tripped

`dotnet test Conductor.slnx` over the whole solution, run as a tracked background child:

    Failed!  - Failed: 1, Passed: 3477, Skipped: 0, Total: 3478, Duration: 4m35s

The single failure was the architecture ratchet, not a behaviour: the resolver took
`PlanConfig.cs` to 505 lines against its 500-line ceiling. **The ceiling was not raised.** The file was
split the way `PlanConfig.Consults.cs` was split before it — validation moved to a new partial,
`src/Conductor.Core/Models/PlanConfig.Validation.cs` (258 lines), leaving `PlanConfig.cs` at 260.

Re-run after the split, over `ArchitectureTests`, `CH1_1*`, `CH1_2*`, `KS1_4*`, `PlanSetCommandTests`,
`KS3_2*`, `KS3_3*`, `DoctorCommandTests` and `SF7_1*`:

    Passed!  - Failed: 0, Passed: 170, Skipped: 0, Total: 170

Full-suite log: `.conductor/evidence/CH1/ch1-2-full-suite.txt`.
