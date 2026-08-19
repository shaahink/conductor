# KS4.4 — worktree-per-stage-attempt, and the branch delete that stops losing work

Stage KS4 · session #18 · 2026-08-19 · branch `feat/karvansara-edge`

The checkpoint: *each attempt in a worktree; failed attempt = drop the tree (mechanical rollback);
verdict receives the clean attempt diff; merge ff-only on green; never `branch -D` an unmerged branch
(the lanes L1.3 fix per ND-8); Windows lock/removal path proven; orphan sweep at startup.*

Acceptance as the plan states it: *a failed attempt leaves the main tree untouched; attempt diff in the
evidence set; orphan sweep at startup; lanes-plan L1 amendment committed.*

**Delivered in full except one named part**, which is stated plainly in §7 and was not faked: the
delivery SESSION still runs in the primary tree. Everything the checkpoint names is real, tested and
driven live; the session-inside-the-worktree redirect needs the state-dir seam that the lanes plan
gives its own checkpoint (L3.1), and §7 says exactly why.

---

## 1. What shipped

| Concern | Where | What it does |
|---|---|---|
| Safe branch delete | `src/Conductor.Core/Git.Worktrees.cs` | `Git.DeleteBranchSafe` — `branch -d`, git's reachability check intact. **No force overload exists.** |
| Windows-safe drop | `src/Conductor.Core/Worktrees/WorktreeDrop.cs` | Deletes the directory itself (children first, `.git` link LAST, read-only cleared, bounded retry), then prunes. Reports a locked path by name; a refused branch is KEPT and named. |
| Attempt lifecycle | `src/Conductor.Core/Worktrees/AttemptWorktree.cs` | Cut at the base commit, sidecar pid marker, clean attempt diff, `merge --ff-only` on green, drop-whole on failure. |
| Orphan sweep | `src/Conductor.Core/Worktrees/WorktreeSweeper.cs` + `Orchestration/RunContext.Worktrees.cs` | Survey/reap by conductor's prefix only; a live run's tree is protected by its marker. Runs at engine startup. |
| The verb | `src/Conductor/Commands/WorktreeCommand.cs` | `conductor worktree [--reap] [--json]`. |
| Attempt diff as evidence | `src/Conductor.Core/Worktrees/AttemptDiff.cs` + `Orchestration/RunLoop.Evidence.cs` | Written per attempt to `<stateDir>/attempts/`, registered with source `attempt`. |
| The L1.3 bug, fixed at its site | `src/Conductor.Core/MutatingLaneRunner.cs` | Both `finally` blocks now drop through `WorktreeDrop`. |
| Lanes-plan amendment | `plans/karvan/lanes.plan.json` (stage L1) | L1.3 marked DONE-in-KS4.4, L1 rescoped, the remaining seam named. |

Commits: `05696d4`, `c407562`, and this checkpoint's closing commit.

## 2. The defect this fixes, at its real site

`MutatingLaneRunner.RunAsync` used to end:

```csharp
finally
{
    try { Git.WorktreeRemove(plan.Repo, lanePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    try { Git.DeleteBranch(plan.Repo, scratchBranch); }   // git branch -D
    catch { /* branch might already be merged/deleted */ }
}
```

`Git.DeleteBranch` was `branch -D`. A lane whose merge gate went red — or whose merge lost a race to
another lane — had a full session of committed work force-deleted on the way out, with only the reflog
holding it. The lanes plan named this its highest-value correctness fix (L1.3); ND-8 lands it here.

`Git.DeleteBranch` no longer exists. `Git.DeleteBranchSafe` is `branch -d` and there is **deliberately
no force overload**; a refusal is reported as `WorktreeDropResult.BranchKept`, by name, in the log and
in the result. The rule is enforced by a test, not by a doc comment:
`KS4_4WorktreeAttemptTests.No_engine_source_file_force_deletes_a_git_branch` scans `src/**/*.cs` (code
lines only, so a doc comment may still explain the fix) and fails the build if `branch -D` returns.

## 3. Two findings that changed the design, both measured

**(a) A plain recursive delete destroys the tree's own `.git` link first.** The first run of the locked-file
test failed on an assertion I expected to pass: with a `FileStream(FileShare.None)` held on
`bin/Conductor.Core.dll`, `Directory.Delete(path, recursive: true)` had *already removed* `<tree>/.git`
before it threw. Git then stops recognising the leftover directory as a worktree, the next `prune`
forgets the record, and the half-deleted tree is invisible to the very sweep that exists to finish it.
`WorktreeDrop.RemoveDirectory` now deletes children first and the `.git` link last, so a blocked drop
stays a worktree git can still see and the retry — or the next startup sweep — completes it. This is
also why `git worktree remove --force` is the wrong tool: same partial-delete hazard, and it collapses
the whole thing into one opaque exit code.

**(b) The attempt diff was carrying the engine's own work.** Two separate leaks, both found by running
rather than reading:

- *Untracked*: attempt 2's diff contained attempt 1's `.diff` FILE, because artifacts land under
  `.conductor/attempts/` and are untracked. In a live repo the state dir is gitignored so
  `--exclude-standard` hides it — the accident holds until a tree without that ignore comes along.
- *Tracked*: the first live demo run put **132 lines of the engine's own `.conductor/REPORT.md`
  bookkeeping** into `D2-a1-s005.diff`, because the scaffolded default TRACKS `.conductor/` (this repo
  gitignores it, which is what hid it) and the report commit lands inside the session's window.

`AttemptDiff.Render` now excludes the state dir on both sides — a `:(exclude)` pathspec for tracked
changes, an explicit filter for untracked. An attempt diff carrying the engine's edits is worse than no
diff at all: it reads as work the agent did.

## 4. The tests — 16, against real git repositories

`tests/Conductor.Tests/KS4_4WorktreeAttemptTests.cs`. Real repos on disk, not mocks: every claim here is
a claim about what git and the filesystem actually do.

```
dotnet test Conductor.slnx --filter "FullyQualifiedName~KS4_4WorktreeAttemptTests"
Passed!  - Failed: 0, Passed: 16, Skipped: 0, Total: 16
```

- `A_failed_attempt_is_dropped_whole_and_the_primary_tree_is_untouched` — HEAD and the file set are
  byte-for-byte what they were. **This is the acceptance's "a failed attempt leaves the main tree
  untouched", proven mechanically.**
- `A_failed_attempts_branch_survives_the_drop_and_its_name_is_reported` — the L1.3 fix.
- `An_attempt_that_committed_nothing_takes_its_branch_with_it` — no orphan branch for an empty attempt.
- `A_green_attempt_fast_forwards_into_the_primary_tree_and_then_drops_clean`.
- `The_merge_refuses_rather_than_inventing_a_commit_when_the_base_moved_under_the_attempt` — ff-only.
- `The_attempt_diff_carries_committed_uncommitted_and_brand_new_work_and_nothing_from_the_primary_tree`.
- `A_locked_file_in_the_tree_is_reported_by_path_and_the_tree_is_reapable_once_it_clears` — the Windows
  path: the error names `Conductor.Core.dll`, the tree is still in `git worktree list`, and the same
  drop completes once the handle is released. Asserts the POSIX behaviour explicitly rather than
  skipping, so the platform the guarantee is measured on is stated.
- `The_sweep_reaps_an_attempt_tree_whose_run_is_gone` / `..._leaves_a_humans_worktree_alone` /
  `..._protects_a_live_runs_attempt_tree`.
- `The_attempt_diff_is_written_under_the_state_dir_and_names_its_stage_attempt_and_session`,
  `A_second_attempt_diff_holds_only_the_second_attempts_work`,
  `The_attempt_diff_excludes_the_engines_own_state_even_when_the_repo_tracks_it`.
- `No_engine_source_file_force_deletes_a_git_branch`, `The_drop_path_does_not_reach_for_git_worktree_remove`.

## 5. Driven live, against the fresh build, on a scratch rig

Everything below ran `src/Conductor/bin/Debug/net10.0/conductor.exe` — **not** the `conductor` on PATH,
which is the published engine driving this session — against a throwaway demo repo under `%TEMP%`, with
its own `CONDUCTOR_STATE_HOME`. Nothing was aimed at this repo.

**Attempt diffs, produced by a complete run of a real plan (`conductor demo`, fake agent, 6 sessions):**

```
.conductor/attempts/          bytes (run 1, before the §3(b) fix → run 2, after)
  D1-a1-s001.diff              368 → 170
  D1-a1-s002.diff             1527 → 1527
  D1-a1-s003.diff             5109 → 1703
  D1-a1-s004.diff             5172 → 723
  D2-a1-s005.diff             5446 → 986
  D2-a1-s006.diff             5268 → 618

[16:57:35] evidence: 1 artifact(s) registered — .conductor/attempts/D1-a1-s001.diff (text)
[16:57:37] evidence: 1 artifact(s) registered — .conductor/attempts/D1-a1-s002.diff (text)
... one per session, six of six

# and after the fix, on a fresh rig — no artifact carries the run's own state:
> Select-String -Path <rig2>/demo/.conductor/attempts/*.diff -Pattern "\.conductor/"
(no matches)
```

The size collapse IS the §3(b) finding: the difference is the engine's own `REPORT.md` and `.gitignore`
bookkeeping, which had been reading as the agent's work.

**The verb, read-only:**

```
> conductor.exe worktree -p <demo>/conductor.plan.json
 path                                       branch                          owner                                     state
 .../conductor-attempt-d1-1-deadbeef        conductor-attempt-d1-1-deadbeef conductor · run killed-run-0001 · D1 att 1 orphan — reapable
 .../my-own-feature                         feature/mine                    not conductor's                           left alone
1 orphaned attempt tree(s) — conductor worktree --reap removes them.
```

**The sweep at startup**, from the engine's own log — an orphan planted with a marker naming a dead pid:

```
[16:58:23] conductor start — plan 'conductor-demo', repo .../ks4-4-rig/demo, branch master
[16:58:24] worktree sweep: reaped .../conductor-attempt-d1-1-deadbeef (run killed-run-0001, stage D1 attempt 1, pid 14056 gone)
[16:58:24] worktree sweep: 1 orphaned attempt tree(s) from a previous run reaped

orphan dir exists: False        human dir exists:  True
orphan branch:     ''           human branch:      8dca4b4100592a38c40476f76da1dc15a74c48ec
```

**And the L1.3 guarantee, live** — an orphan tree holding a commit nothing else reaches:

```
attempt sha before reap: 702644f559779edd3f1939f5a3859a8eb0b7fc03
· reaped .../conductor-attempt-d2-1-lostwork — branch 'conductor-attempt-d2-1-lostwork' KEPT (holds unmerged commits)
dir exists after reap: False
branch after reap:     702644f559779edd3f1939f5a3859a8eb0b7fc03
```

The tree is gone; the work is not.

## 6. The lanes-plan amendment (ND-8)

`plans/karvan/lanes.plan.json`, stage L1: L1.3 is recorded DONE-in-KS4.4 with what shipped enumerated,
L1 rescoped to the multi-lane generalization, and the remaining seam named as L3.1's job. Nothing else
in that plan is re-litigated.

## 7. What is NOT delivered, and why it was not faked

**The delivery session still runs in the primary tree.** `AttemptWorktree` is exercised by tests and by
the lane runner; the run loop does not yet spawn a session inside one.

The blocker is a single line: `PlanConfig.StateDir => Path.Combine(Repo, ".conductor")`
(`src/Conductor.Core/Models/PlanConfig.cs:125`). Redirecting a session to a worktree means changing
`Repo`, and that moves `state.json`, the logs, the evidence directory, the control file and the engine
lock into the throwaway tree with it — so a failed attempt would drop the run's own state, and the
agent's `conductor task --done` would target a different `.conductor` from the one the run reads. The
lanes plan already scopes exactly this as **L3.1** ("give a lane a state redirect to the run's canonical
store and make one writer own it"), and `StateHome.PointerFileName` exists as the seam for the `run.db`
half of it. Half-wiring it would produce a run that isolates the agent and loses the tracker.

There is a second consequence worth recording for whoever does L3.1: the handoff block a FAILED attempt
writes lives in the tracker, inside the tree. Mechanical rollback and "the next attempt reads what the
last one learned" pull in opposite directions, and that has to be decided, not discovered.

ND-8's own wording is what this checkpoint delivered: *"it builds the single-lane base of L1/G4 —
branch-safety code, the `-D`-loses-work fix, Windows lock handling."* All three are here, plus the
lifecycle class, the sweep, the verb and the evidence artifact.

## 8. Gate state

`dotnet build Conductor.slnx -clp:ErrorsOnly` — green. Scoped suite green (16/16). The full battery is
conductor's to run after this session exits.

Two ratchets were hit and neither was weakened:

- **CA1506**: `RunLoop` sits at exactly its measured coupling ceiling of 182
  (`src/Conductor.Core/CodeMetricsConfig.txt`), and that number may only come down. The startup sweep
  went on `RunContext` instead, and the evidence source constant went on `EvidenceArtifact` — a type
  `RunLoop` already knows — rather than on the new class.
- **Line ceiling 500**: the KS4.4 additions took `Git.cs` to 528, so the worktree section moved to
  `Git.Worktrees.cs` as a partial. The seam is the subject, not an arbitrary cut.
