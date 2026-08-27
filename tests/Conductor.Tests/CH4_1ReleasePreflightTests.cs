using Conductor.Core.Release;

namespace Conductor.Tests;

/// <summary>
/// CH4.1 — the era-close checklist, as something that can be wrong out loud.
///
/// <para><b>What this is pinning.</b> <c>.conductor/evidence/KS12/ks12-3-owner-runbook.md</c> and its
/// DV7 successor were the same document written twice, and the second one's first finding was that
/// the first had not been carried out. Six of KS12.3's seven acts went unperformed and nothing
/// anywhere said so. A prose checklist has no failure mode: it is equally quiet whether it was
/// followed or ignored.</para>
///
/// <para><b>So every assertion here is a negative control.</b> The point is not that a green fact
/// produces a green line — that is trivially arrangeable. The point is that a RED fact produces a red
/// line and moves the process exit code, one precondition at a time, and that the two conditions the
/// runbook could not distinguish by hand (a decision the owner has not made yet, versus something
/// actually broken) come out as different exit codes.</para>
/// </summary>
public sealed class CH4_1ReleasePreflightTests
{
    private static MergeFacts Merge(int ahead = 18, int behind = 0, int baseBehindRemote = 0,
        int branchBehindRemoteBase = 0, bool dirty = false, bool hasRemoteBase = true)
        => new("master", "feat/charkh", BaseExists: true, BranchExists: true,
               ahead, behind, baseBehindRemote, branchBehindRemoteBase, hasRemoteBase, dirty);

    private static ChangelogFacts Changelog(string? version = "0.6.0", int exit = 0,
        IReadOnlyList<string>? body = null)
        => new(version, FileExists: true, ["## [0.6.0] - 2026-08-27", "## [0.5.0] - 2026-08-26"],
               ScriptRan: true, exit, body ?? ["### Added", "- a thing", "- another thing", "### Fixed", "- a bug"], "");

    private static CourierFacts Courier(bool token = true, string? scope = "User", bool registered = true,
        bool running = true, int chats = 1, bool repoAllowed = true)
        => new(token, scope, registered, "Ready", running, 33884, chats, 4, repoAllowed);

    // ---- the exit code is the contract -----------------------------------------------------

    /// <summary>The checkpoint's own bar: a non-zero exit when any line is red. 2 is reserved for
    /// "nothing is broken, something is yours to decide" so a script can tell a wall from a person —
    /// the distinction KS12.3's runbook had no way to express, and the reason its owner-only acts
    /// read exactly like its performed ones.</summary>
    [Fact]
    public void The_exit_code_separates_broken_from_undecided_from_ready()
    {
        var green = new ReleaseCheck("a", ReleaseCheck.Ok, "fine", []);
        var owner = new ReleaseCheck("b", ReleaseCheck.Owner, "yours", []);
        var red = new ReleaseCheck("c", ReleaseCheck.Fail, "broken", []);

        Assert.Equal(0, ReleasePreflight.ExitCode([green, green]));
        Assert.Equal(2, ReleasePreflight.ExitCode([green, owner]));
        Assert.Equal(1, ReleasePreflight.ExitCode([green, red]));

        // Red outranks owner: a release blocked by a fact must not report as blocked by a decision.
        Assert.Equal(1, ReleasePreflight.ExitCode([owner, red]));
    }

    /// <summary>A count is unactionable and a name is a place to start. "2 of 6 red" tells the owner
    /// to re-run the verb and read it again; "red: merge, processes" does not.</summary>
    [Fact]
    public void The_verdict_names_the_red_lines_rather_than_counting_them()
    {
        var checks = new[]
        {
            ReleasePreflight.Merge(Merge(behind: 3)),
            ReleasePreflight.Changelog(Changelog(version: null)),
            ReleasePreflight.Courier(Courier(chats: 0)),
        };

        var verdict = ReleasePreflight.Verdict(checks);
        Assert.Contains(ReleasePreflight.MergeCheck, verdict, StringComparison.Ordinal);
        Assert.Contains(ReleasePreflight.CourierCheck, verdict, StringComparison.Ordinal);
        Assert.Contains(ReleasePreflight.ChangelogCheck, verdict, StringComparison.Ordinal);
        Assert.StartsWith("NOT READY", verdict, StringComparison.Ordinal);
    }

    /// <summary>The bar that makes this a checklist rather than a list: seed exactly one precondition
    /// red, one at a time, and the whole run must go non-zero for that one. Six lines, six controls —
    /// so a line that stops measuring cannot go quiet, which is the failure mode this whole checkpoint
    /// exists to remove.</summary>
    [Fact]
    public void Each_precondition_alone_is_enough_to_refuse_the_release()
    {
        var allGreen = new[]
        {
            ReleasePreflight.Merge(Merge()),
            ReleasePreflight.Changelog(Changelog()),
            ReleasePreflight.Processes(new ProcessFacts([], [], null)),
            ReleasePreflight.Migration(new MigrationFacts(15, "870786f5b17a", "0.5.0", false, [], 15, "run.db")),
            ReleasePreflight.Courier(Courier()),
            ReleasePreflight.Backfill(new BackfillFacts("shaahink/conductor",
                [new MirroredRun("aa91", "Divan", "Completed", 23, InFlight: false)], null)),
        };
        Assert.Equal(0, ReleasePreflight.ExitCode(allGreen));
        Assert.All(allGreen, c => Assert.Equal(ReleaseCheck.Ok, c.State));

        var reds = new (string Name, ReleaseCheck Check)[]
        {
            (ReleasePreflight.MergeCheck, ReleasePreflight.Merge(Merge(behind: 3))),
            (ReleasePreflight.ChangelogCheck, ReleasePreflight.Changelog(Changelog(exit: 1))),
            (ReleasePreflight.ProcessesCheck, ReleasePreflight.Processes(
                new ProcessFacts(["a run is live in C:/x/.conductor (engine pid 5248)"],
                                 [new LiveEngine(5248, "C:/x/conductor.exe")], 5248))),
            (ReleasePreflight.MigrationCheck, ReleasePreflight.Migration(
                new MigrationFacts(16, "870786f5b17a", "0.5.0", false,
                                   ["src/Conductor.Core/Store/Migrations/v16_thing.sql"], 15, "run.db"))),
            (ReleasePreflight.CourierCheck, ReleasePreflight.Courier(Courier(registered: false))),
        };

        foreach (var (name, red) in reds)
        {
            Assert.Equal(ReleaseCheck.Fail, red.State);
            var mixed = allGreen.Select(c => c.Name == name ? red : c).ToList();
            Assert.Equal(1, ReleasePreflight.ExitCode(mixed));
            Assert.Contains(name, ReleasePreflight.Verdict(mixed), StringComparison.Ordinal);
        }
    }

    // ---- merge: the runbook's section 1 -----------------------------------------------------

    /// <summary>Measured on this repo at CH4.1 and the reason the merge line carries three counts
    /// instead of two: local <c>master</c> was NINE behind <c>origin/master</c> while
    /// <c>feat/charkh</c> already contained all nine. A verdict decided on the local count alone
    /// calls that ready without mentioning the stale base; one decided on the remote count alone
    /// calls a perfectly good fast-forward broken.</summary>
    [Fact]
    public void A_stale_local_base_the_branch_already_contains_is_green_and_said_out_loud()
    {
        var check = ReleasePreflight.Merge(Merge(ahead: 18, baseBehindRemote: 9, branchBehindRemoteBase: 0));

        Assert.Equal(ReleaseCheck.Ok, check.State);
        Assert.Contains(check.Detail, d => d.Contains("9 behind origin/master", StringComparison.Ordinal));
        Assert.Contains(check.Detail, d => d.Contains("git pull", StringComparison.Ordinal));
    }

    /// <summary>The other half of the same pair: when the remote base carries commits the branch does
    /// NOT have, the local merge still succeeds and the push is what fails. That is a red line even
    /// though every local count says fast-forward.</summary>
    [Fact]
    public void A_remote_base_ahead_of_the_branch_is_red_even_when_the_local_merge_would_work()
    {
        var check = ReleasePreflight.Merge(Merge(ahead: 18, behind: 0, baseBehindRemote: 4, branchBehindRemoteBase: 4));

        Assert.Equal(ReleaseCheck.Fail, check.State);
        Assert.Contains("origin/master", check.Headline, StringComparison.Ordinal);
        Assert.Contains(check.Detail, d => d.Contains("REJECTED", StringComparison.Ordinal));
    }

    /// <summary>The plain case the runbook wrote by hand: base carries work the branch does not, so
    /// <c>--ff-only</c> is refused. The verdict must name the flag, because the fix depends on which
    /// merge was intended.</summary>
    [Fact]
    public void A_base_ahead_of_the_branch_refuses_the_fast_forward_by_name()
    {
        var check = ReleasePreflight.Merge(Merge(ahead: 18, behind: 3));

        Assert.Equal(ReleaseCheck.Fail, check.State);
        Assert.Contains(check.Detail, d => d.Contains("--ff-only", StringComparison.Ordinal));
        Assert.Contains("3", check.Headline, StringComparison.Ordinal);
    }

    /// <summary>A dirty tree is red on its own: the era-close begins with <c>git checkout master</c>,
    /// which either refuses or drags the uncommitted work onto the release branch.</summary>
    [Fact]
    public void An_uncommitted_working_tree_is_red_on_its_own()
    {
        var check = ReleasePreflight.Merge(Merge(dirty: true));

        Assert.Equal(ReleaseCheck.Fail, check.State);
        Assert.Contains(check.Detail, d => d.Contains("uncommitted", StringComparison.Ordinal));
    }

    // ---- changelog: the runbook's section 2 -------------------------------------------------

    /// <summary>The act that was red at KS12.3, still red at DV7.3 one era later BECAUSE nobody
    /// performed it, and red again today as bug #88. The version number is the owner's — so an
    /// unnamed version is <see cref="ReleaseCheck.Owner"/>, not a failure, and it still refuses to
    /// exit 0. Naming a version the CHANGELOG has no section for IS a failure, because
    /// <c>release.yml</c> would refuse the tag.</summary>
    [Fact]
    public void An_unnamed_version_stops_at_the_owner_and_a_missing_section_is_red()
    {
        var undecided = ReleasePreflight.Changelog(Changelog(version: null));
        Assert.Equal(ReleaseCheck.Owner, undecided.State);
        Assert.Contains(undecided.Detail, d => d.Contains("judgement", StringComparison.Ordinal));
        Assert.Contains(undecided.Detail, d => d.Contains("## [0.5.0]", StringComparison.Ordinal));

        var missing = ReleasePreflight.Changelog(Changelog(version: "9.9.9", exit: 1));
        Assert.Equal(ReleaseCheck.Fail, missing.State);
        Assert.Contains("9.9.9", missing.Headline, StringComparison.Ordinal);
        Assert.Contains(missing.Detail, d => d.Contains("release.yml", StringComparison.Ordinal));
    }

    /// <summary>A leading <c>v</c> is how a tag is spelled and how <c>changelog-section.sh</c> accepts
    /// it, so <c>--tag v0.6.0</c> must not report a section for "v0.6.0" that nobody wrote.</summary>
    [Fact]
    public void A_v_prefixed_tag_is_reported_as_the_version_it_is()
    {
        var check = ReleasePreflight.Changelog(Changelog(version: "v0.6.0"));

        Assert.Equal(ReleaseCheck.Ok, check.State);
        Assert.Contains("0.6.0", check.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("v0.6.0", check.Headline, StringComparison.Ordinal);
    }

    /// <summary>Bug #88's exact shape, and the reason exit 0 from the script is not enough:
    /// <c>changelog-section.sh Unreleased</c> exits 0 today on a section whose entire body is
    /// "Nothing yet". That body is what the world reads on the releases page.</summary>
    [Fact]
    public void A_section_that_exists_and_says_nothing_is_red_even_though_the_script_exits_zero()
    {
        var check = ReleasePreflight.Changelog(Changelog(version: "0.6.0", exit: 0,
            body: ["_Nothing yet — entries for the next era go here._"]));

        Assert.Equal(ReleaseCheck.Fail, check.State);
        Assert.Contains("says nothing", check.Headline, StringComparison.Ordinal);
    }

    /// <summary>MEASURED at CH4.1, and the reason this distinction is pinned rather than assumed. On
    /// Windows <c>sh</c> is Git's, in a directory that is not on the Windows PATH, and
    /// <c>ProcessRunner</c> reports a failure to START as exit -1 with the reason on stdout. Read
    /// naively, the first run of this verb printed "no CHANGELOG section for 0.6.0" — a verdict about
    /// the CHANGELOG produced by a shell that never ran. "Could not measure" must never render as
    /// "measured": that substitution is the family of bug this whole era exists to remove.</summary>
    [Fact]
    public void A_shell_that_never_ran_is_reported_as_unmeasured_not_as_a_missing_section()
    {
        var unmeasured = ReleasePreflight.Changelog(new ChangelogFacts("0.6.0", FileExists: true,
            ["## [0.5.0] - 2026-08-26"], ScriptRan: false, 0, [], "no POSIX shell was found"));

        Assert.Equal(ReleaseCheck.Fail, unmeasured.State);
        Assert.Contains("could not be run", unmeasured.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("no CHANGELOG section", unmeasured.Headline, StringComparison.Ordinal);
        Assert.Contains(unmeasured.Detail, d => d.Contains("no POSIX shell was found", StringComparison.Ordinal));

        // And the measured absence still reads as an absence, so the two are not merged into one line.
        Assert.Contains("no CHANGELOG section",
            ReleasePreflight.Changelog(Changelog(version: "0.6.0", exit: 1)).Headline, StringComparison.Ordinal);
    }

    /// <summary>The bar the placeholder rule must not overshoot: a real section is not a placeholder
    /// because it happens to be short, and the word "nothing" inside a genuine entry is not the
    /// scaffold's apology.</summary>
    [Fact]
    public void A_real_section_is_not_called_a_placeholder_for_being_short()
    {
        var check = ReleasePreflight.Changelog(Changelog(version: "0.6.0", exit: 0,
            body: ["### Fixed", "- the courier no longer says nothing is running when something is (#87)"]));

        Assert.Equal(ReleaseCheck.Ok, check.State);
    }

    // ---- processes and migration: the runbook's section 3 -----------------------------------

    /// <summary>The one line whose green is conditional on WHEN it was read. Another repository's run
    /// may start at any moment on this machine (trap 3) and it executes the same binary, so a green
    /// process line that does not say "re-run this immediately before the reinstall" is a green line
    /// that expires silently.</summary>
    [Fact]
    public void A_clear_process_table_still_says_to_measure_it_again_at_the_moment_of_the_swap()
    {
        var check = ReleasePreflight.Processes(new ProcessFacts([], [], null));

        Assert.Equal(ReleaseCheck.Ok, check.State);
        Assert.Contains(check.Detail, d => d.Contains("re-run this immediately before the reinstall", StringComparison.Ordinal));
    }

    /// <summary>DV7.3 had to work out by hand which of the two live pids was the run doing the
    /// asking. The verb knows: <c>CONDUCTOR_PID</c>.</summary>
    [Fact]
    public void The_run_asking_the_question_is_named_among_the_live_engines()
    {
        var check = ReleasePreflight.Processes(new ProcessFacts(
            ["a run is live in C:/x/.conductor (engine pid 5248)"],
            [new LiveEngine(5248, "C:/x/conductor.exe"), new LiveEngine(9001, "D:/other/conductor.exe")],
            5248));

        Assert.Equal(ReleaseCheck.Fail, check.State);
        Assert.Contains(check.Detail, d => d.Contains("5248", StringComparison.Ordinal) && d.Contains("CONDUCTOR_PID", StringComparison.Ordinal));
        Assert.Contains(check.Detail, d => d.Contains("9001", StringComparison.Ordinal) && !d.Contains("CONDUCTOR_PID", StringComparison.Ordinal));
    }

    /// <summary>Trap 18, which cost an era's <c>task</c>, <c>note</c> and <c>bug</c> verbs at KS10.1.
    /// The measurement is the migration FILES that landed since the installed engine's commit, not the
    /// store's current schema — the store is only migrated once something opens it for write, so by
    /// the time the schema number disagrees the damage is already done.</summary>
    [Fact]
    public void Schema_skew_is_measured_from_the_migrations_that_landed_not_from_the_store()
    {
        var skewed = ReleasePreflight.Migration(new MigrationFacts(
            16, "870786f5b17a", "0.5.0", false,
            ["src/Conductor.Core/Store/Migrations/v16_something.sql"], StoreVersion: 15, "run.db"));

        Assert.Equal(ReleaseCheck.Fail, skewed.State);
        Assert.Contains("870786f5b17a", skewed.Headline, StringComparison.Ordinal);
        Assert.Contains(skewed.Detail, d => d.Contains("v16_something.sql", StringComparison.Ordinal));

        // Same store version, no migrations since: green, which is what this repo measured at CH4.1.
        var clean = ReleasePreflight.Migration(new MigrationFacts(15, "870786f5b17a", "0.5.0", false, [], 15, "run.db"));
        Assert.Equal(ReleaseCheck.Ok, clean.State);
    }

    /// <summary>An engine that will not say which commit it is cannot be compared to anything, and
    /// "could not measure" must not render as "measured green" — that substitution is the whole
    /// family of bugs this era is about.</summary>
    [Fact]
    public void An_unidentifiable_installed_engine_is_red_rather_than_assumed_current()
    {
        var check = ReleasePreflight.Migration(new MigrationFacts(15, null, null, false, [], 15, "run.db"));

        Assert.Equal(ReleaseCheck.Fail, check.State);
        Assert.Contains(check.Detail, d => d.Contains("version --json", StringComparison.Ordinal));
    }

    // ---- courier: the runbook's section 4 ---------------------------------------------------

    /// <summary>The exact risk DV7.3 measured by hand and found green: a logon-triggered Scheduled
    /// Task inherits PERSISTED user and machine variables, never what some shell exported. A token
    /// that is set in this process and persisted nowhere would leave the courier tokenless the moment
    /// the reinstall restarts it — and <c>courier status</c> would have said "token: set".</summary>
    [Fact]
    public void A_token_set_only_in_this_shell_is_red_because_the_scheduled_task_cannot_see_it()
    {
        var check = ReleasePreflight.Courier(Courier(token: true, scope: null));

        Assert.Equal(ReleaseCheck.Fail, check.State);
        Assert.Contains(check.Detail, d => d.Contains("persisted", StringComparison.OrdinalIgnoreCase)
                                        && d.Contains("logon", StringComparison.Ordinal));
    }

    /// <summary>Each courier precondition refuses on its own, and the refusal names which one. A
    /// registered task with nobody to answer, a repo off the allowlist whose notes park in the
    /// dead-letter box, and a task that is not there at all are three different fixes.</summary>
    [Fact]
    public void Every_courier_precondition_refuses_by_name()
    {
        Assert.Contains(ReleasePreflight.Courier(Courier(chats: 0)).Detail,
            d => d.Contains("no chats are registered", StringComparison.Ordinal));
        Assert.Contains(ReleasePreflight.Courier(Courier(repoAllowed: false)).Detail,
            d => d.Contains("allowlist", StringComparison.Ordinal));
        Assert.Contains(ReleasePreflight.Courier(Courier(registered: false)).Detail,
            d => d.Contains("courier install", StringComparison.Ordinal));
        Assert.Contains(ReleasePreflight.Courier(Courier(running: false)).Detail,
            d => d.Contains("nothing is polling", StringComparison.Ordinal));

        Assert.Equal(ReleaseCheck.Ok, ReleasePreflight.Courier(Courier()).State);
    }

    // ---- backfill: the runbook's section 5 --------------------------------------------------

    /// <summary>Whether a run joins the published corpus is judgement, so an unmirrored run is named
    /// and stopped at rather than failed — but it still refuses exit 0, which is the difference
    /// between this and KS12.3, where an owner-only act read exactly like a completed one.</summary>
    [Fact]
    public void A_run_with_no_github_record_is_named_stopped_at_and_never_silently_skipped()
    {
        var check = ReleasePreflight.Backfill(new BackfillFacts("shaahink/conductor",
        [
            new MirroredRun("858b48387e4e4b0f8f9d0e2a1c3b5d7f", "Charkh", "running", 0, InFlight: true),
            new MirroredRun("9491891fe700463ba0d876c06280cce2", "Karvansara edge", "needs_human", 0, InFlight: false),
            new MirroredRun("aa91682821c14666915c16317a4fc72c", "Divan", "Completed", 23, InFlight: false),
        ], null));

        Assert.Equal(ReleaseCheck.Owner, check.State);

        // The full id, never a slug: several eras share one store directory (DV7.3, hazard 1).
        Assert.Contains(check.Detail, d => d.Contains("9491891fe700463ba0d876c06280cce2", StringComparison.Ordinal));
        Assert.Contains(check.Detail, d => d.Contains("--dry-run", StringComparison.Ordinal));
        Assert.Contains(check.Detail, d => d.Contains("#79", StringComparison.Ordinal));

        // The run doing the asking is not owed anything yet, and the one already mirrored is not named.
        Assert.Contains(check.Detail, d => d.Contains("Charkh", StringComparison.Ordinal) && d.Contains("closing act", StringComparison.Ordinal));
        Assert.DoesNotContain(check.Detail, d => d.Contains("aa91682821c14666915c16317a4fc72c", StringComparison.Ordinal));
    }

    /// <summary>KS1.6, and the reason "is this run finished" is a flag the probe computes rather than
    /// a list of status words this file matches. <c>runs.status</c> is what the last engine to write
    /// the row believed; an engine that was killed never got to correct it, and four rows on this
    /// machine say <c>running</c> for ever. A backfill line that believed the column would decline to
    /// name any of them as owed a record — the omission would be permanent and silent, which is the
    /// KS12.3 failure wearing a different hat. And a word list rots the other way too: the park
    /// vocabulary has grown twice, so the next park word would be read as "finished" and the verb
    /// would demand the backfill of a run still in flight.</summary>
    [Fact]
    public void A_run_whose_row_still_claims_running_after_its_engine_died_is_owed_a_record()
    {
        var check = ReleasePreflight.Backfill(new BackfillFacts("shaahink/conductor",
        [
            // Reconciled to `orphaned` by RunLiveness because the store is not live; the row's own
            // claim is carried alongside so the line can say both.
            new MirroredRun("9647f1b80d18", "Karvansara core", "orphaned", 0, InFlight: false) { StoredStatus = "running" },
        ], null));

        Assert.Equal(ReleaseCheck.Owner, check.State);
        Assert.Contains(check.Detail, d => d.Contains("9647f1b80d18", StringComparison.Ordinal)
                                        && d.Contains("orphaned", StringComparison.Ordinal)
                                        && d.Contains("the row still claims running", StringComparison.Ordinal));
    }

    /// <summary>A store where every finished run has a record is green, and the line says which
    /// destination it is green against — one run mirrored to a scratch repo has told the real one
    /// nothing.</summary>
    [Fact]
    public void A_fully_mirrored_store_is_green_against_the_destination_it_was_measured_on()
    {
        var check = ReleasePreflight.Backfill(new BackfillFacts("shaahink/conductor",
            [new MirroredRun("aa91", "Divan", "Completed", 23, InFlight: false)], null));

        Assert.Equal(ReleaseCheck.Ok, check.State);
        Assert.Contains("shaahink/conductor", check.Headline, StringComparison.Ordinal);
    }

    /// <summary>The six names are the grep handles a failure line is read by, and the order is the
    /// order the runbook walks the era-close in. A line that disappears from this list disappears
    /// from the checklist.</summary>
    [Fact]
    public void The_six_checks_are_named_and_ordered_as_the_runbook_walks_them()
        => Assert.Equal(
            ["merge", "changelog", "processes", "migration", "courier", "backfill"],
            ReleasePreflight.CheckNames);
}
