using Conductor.Core.Release;

namespace Conductor.Tests;

/// <summary>
/// CH4.2 — perform what is mechanical, refuse what is judgement, and never do either quietly.
///
/// <para><b>The failure this is against.</b> <c>ks12-3-owner-runbook.md</c> listed seven acts and six
/// were never carried out. The reason is in the shape of the document, not in anyone's diligence: in
/// prose, "this one is yours to do" and "nobody did this one" are the same sentence. The next era's
/// runbook opened by discovering that. So <c>Kind</c> and <c>State</c> are separate here — whose act
/// it is, ever, versus what became of it this time — and an owner act is <c>stopped</c> on every run,
/// never <c>nothing</c>.</para>
///
/// <para><b>Every assertion is about a refusal or a distinction.</b> That an act performs when its
/// preconditions hold is the easy half and the live rig proves it end to end
/// (<c>.conductor/evidence/CH4/ch4-2-rig-perform.txt</c>). What a test can pin, and a rig run cannot,
/// is that each precondition refuses BY NAME rather than the act silently doing nothing — and that
/// "already done" never renders as "refused", which is the one confusion that lost six acts.</para>
/// </summary>
public sealed class CH4_2ReleasePerformTests
{
    private static ChangelogRenameFacts Changelog(bool hasUnreleased = true, bool placeholder = false,
        int bodyLines = 12, bool already = false)
        => new(FileExists: true, hasUnreleased, placeholder, bodyLines, already, "2026-08-27");

    private static MergeFacts Merge(int ahead = 18, int behind = 0, bool dirty = false)
        => new("master", "feat/charkh", BaseExists: true, BranchExists: true,
               ahead, behind, 0, 0, HasRemoteBase: true, dirty);

    private static DocMove Move(string from, string to, bool exists = true, bool occupied = false)
        => new(from, to, exists, occupied, ReferencedByPlan: true);

    // ---- the two questions kept apart -------------------------------------------------------

    /// <summary>The whole point of the checkpoint, as one assertion. An owner act is
    /// <c>stopped</c> on every run whatever the state — it is never <c>nothing</c>, because
    /// "nothing to do" is exactly what six unperformed acts looked like.</summary>
    [Fact]
    public void An_owner_act_is_stopped_at_on_every_run_and_is_never_reported_as_nothing_to_do()
    {
        foreach (var facts in new[]
        {
            new OwnerFacts("0.6.0", "master", "shaahink/conductor", ["aa91"], AnyConductorLive: true),
            new OwnerFacts(null, null, null, [], AnyConductorLive: false),
        })
        {
            var acts = ReleasePerform.OwnerActs(facts);

            Assert.Equal(ReleasePerform.OwnerOrder, [.. acts.Select(a => a.Name)]);
            Assert.All(acts, a => Assert.Equal(ReleaseAct.Owner, a.Kind));
            Assert.All(acts, a => Assert.Equal(ReleaseAct.Stopped, a.State));
            Assert.All(acts, a => Assert.NotEmpty(a.Detail));
        }
    }

    /// <summary>Each owner act carries the command, because a refusal without one is just a wall.
    /// The corpus act names the run ids it will not decide about, and the publish act names both
    /// pushes — the branch and the tag, which are different consequences.</summary>
    [Fact]
    public void Each_owner_act_carries_the_command_the_owner_types()
    {
        var acts = ReleasePerform.OwnerActs(
            new OwnerFacts("0.6.0", "master", "shaahink/conductor",
                ["9491891fe700463ba0d876c06280cce2"], AnyConductorLive: false));

        var by = acts.ToDictionary(a => a.Name, StringComparer.Ordinal);
        Assert.Contains(by[ReleasePerform.CorpusAct].Detail,
            d => d.Contains("github sync --backfill 9491891fe700463ba0d876c06280cce2", StringComparison.Ordinal));
        Assert.Contains(by[ReleasePerform.CorpusAct].Detail, d => d.Contains("#79", StringComparison.Ordinal));
        Assert.Contains(by[ReleasePerform.ReinstallAct].Detail, d => d.Contains("tools/install.ps1", StringComparison.Ordinal));
        Assert.Contains(by[ReleasePerform.PublishAct].Detail, d => d.Contains("git push origin master", StringComparison.Ordinal));
        Assert.Contains(by[ReleasePerform.PublishAct].Detail, d => d.Contains("git push origin v0.6.0", StringComparison.Ordinal));
    }

    /// <summary>A finished run is normally exit <b>2</b>, not 0. The era-close is not over until a
    /// person does their part, and a script reading 0 here would be reading "finished" off a document
    /// again — which is the whole failure being fixed.</summary>
    [Fact]
    public void A_complete_mechanical_sequence_still_exits_non_zero_because_the_owner_has_not_acted()
    {
        var mechanical = ReleasePerform.MechanicalOrder
            .Select(n => new ReleaseAct(n, ReleaseAct.Mechanical, ReleaseAct.Done, "done", []))
            .ToList();

        Assert.Equal(0, ReleasePerform.ExitCode(mechanical));

        var whole = mechanical.Concat(ReleasePerform.OwnerActs(
            new OwnerFacts("0.6.0", "master", "r/r", [], AnyConductorLive: false))).ToList();
        Assert.Equal(2, ReleasePerform.ExitCode(whole));

        var refused = whole.Select(a => a.Name == ReleasePerform.TagAct
            ? a with { State = ReleaseAct.Refused } : a).ToList();
        Assert.Equal(1, ReleasePerform.ExitCode(refused));
        Assert.Contains(ReleasePerform.TagAct, ReleasePerform.Verdict(refused, dryRun: false), StringComparison.Ordinal);
    }

    // ---- the changelog act ------------------------------------------------------------------

    /// <summary>The guard that makes this act safe to automate at all. The version number is the
    /// owner's input, but the CONTENT under the heading is the release notes the world reads
    /// verbatim — <c>release.yml</c> uses <c>changelog-section.sh</c>'s stdout as the release body.
    /// Renaming a heading over "Nothing yet" ships that sentence, permanently. Bug #88's exact shape,
    /// and the state this repository is in right now.</summary>
    [Fact]
    public void The_changelog_rename_refuses_over_a_placeholder_body()
    {
        var act = ReleasePerform.Changelog("0.6.0", Changelog(placeholder: true, bodyLines: 2));

        Assert.Equal(ReleaseAct.Refused, act.State);
        Assert.Equal(ReleaseAct.Mechanical, act.Kind);
        Assert.Contains(act.Detail, d => d.Contains("the world reads", StringComparison.Ordinal));
        Assert.Contains(act.Detail, d => d.Contains("it is yours", StringComparison.Ordinal));
    }

    /// <summary>Already renamed is <c>nothing</c>, not <c>refused</c> and not a second rename. Every
    /// mechanical act must survive being asked twice, because the owner will ask twice.</summary>
    [Fact]
    public void The_changelog_rename_is_idempotent_and_says_so_rather_than_refusing()
    {
        var act = ReleasePerform.Changelog("0.6.0", Changelog(already: true, hasUnreleased: false));

        Assert.Equal(ReleaseAct.Nothing, act.State);
        Assert.Contains("0.6.0", act.Headline, StringComparison.Ordinal);
    }

    /// <summary>No version, no rename — and the refusal says the number is the owner's rather than
    /// inventing one. Nothing in this repository can decide which release an era is.</summary>
    [Fact]
    public void Without_a_version_the_changelog_act_refuses_and_names_the_owner()
    {
        var act = ReleasePerform.Changelog(null, Changelog());

        Assert.Equal(ReleaseAct.Refused, act.State);
        Assert.Contains(act.Detail, d => d.Contains("--tag", StringComparison.Ordinal));
    }

    // ---- the merge act ----------------------------------------------------------------------

    /// <summary>The merge act does not form a second opinion: it is gated on CH4.1's own verdict and
    /// names it when it refuses. Two verbs disagreeing about whether a merge is a fast-forward is the
    /// bug this stage exists to make impossible.</summary>
    [Fact]
    public void The_merge_act_refuses_on_the_preflights_verdict_and_quotes_it()
    {
        var facts = Merge(behind: 3);
        var act = ReleasePerform.Merge(ReleasePreflight.Merge(facts), facts);

        Assert.Equal(ReleaseAct.Refused, act.State);
        Assert.Contains("preflight", act.Headline, StringComparison.Ordinal);
        Assert.Contains(act.Detail, d => d.Contains("--ff-only", StringComparison.Ordinal));
    }

    /// <summary>MEASURED on the CH4.2 rig, second run: <c>master</c> was one ahead of the branch —
    /// the doc-move commit this same verb had just landed on it — so the preflight was red and the
    /// merge act refused. But the branch carries nothing the base lacks; the merge is the no-op git
    /// itself calls "Already up to date". "Already done" and "refused" are the two answers KS12.3
    /// could not tell apart, so containment is asked BEFORE the preflight gate.</summary>
    [Fact]
    public void A_branch_the_base_already_contains_is_nothing_to_do_even_when_the_base_has_moved_on()
    {
        var facts = Merge(ahead: 0, behind: 1);
        var preflight = ReleasePreflight.Merge(facts);
        Assert.Equal(ReleaseCheck.Fail, preflight.State);   // the preflight is right: a merge would be refused

        var act = ReleasePerform.Merge(preflight, facts);   // and the ACT is right: there is nothing to merge
        Assert.Equal(ReleaseAct.Nothing, act.State);
        Assert.Contains("already contains", act.Headline, StringComparison.Ordinal);
    }

    // ---- the tag act ------------------------------------------------------------------------

    /// <summary>The step KS12.3 measured red and DV7.3 measured red again a whole era later, because
    /// nobody performed it in between: a tag whose CHANGELOG section does not exist stops the release
    /// build before five platforms compile. The act refuses rather than creating a tag that cannot
    /// build, and it names which act writes the section.</summary>
    [Fact]
    public void The_tag_refuses_until_the_section_it_will_publish_exists()
    {
        var act = ReleasePerform.Tag("0.6.0", new TagFacts(Exists: false, ChangelogSectionOk: false, "master", "abc123"));

        Assert.Equal(ReleaseAct.Refused, act.State);
        Assert.Contains(act.Detail, d => d.Contains("release.yml", StringComparison.Ordinal));
        Assert.Contains(act.Detail, d => d.Contains("the changelog act above writes that section", StringComparison.Ordinal));
    }

    /// <summary>An existing tag is left exactly where it is. Moving one is how a release becomes two
    /// different things with one name, and no measurement makes that the right act.</summary>
    [Fact]
    public void An_existing_tag_is_nothing_to_do_and_is_never_moved()
    {
        var act = ReleasePerform.Tag("v0.6.0", new TagFacts(Exists: true, ChangelogSectionOk: true, "master", "abc123"));

        Assert.Equal(ReleaseAct.Nothing, act.State);
        Assert.Contains("v0.6.0", act.Headline, StringComparison.Ordinal);
        Assert.Contains(act.Detail, d => d.Contains("will not move an existing tag", StringComparison.Ordinal));
    }

    // ---- the doc move -----------------------------------------------------------------------

    /// <summary>Trap 13: the plan's <c>tracker</c>, <c>planDoc</c> and <c>readOrder</c> are read at
    /// the start of every session, so a <c>git mv</c> that lands without the repoint means the next
    /// session opens nothing and says so to nobody. The two halves are one act or they are neither.</summary>
    [Fact]
    public void The_doc_move_refuses_when_the_plan_cannot_be_repointed_in_the_same_act()
    {
        var act = ReleasePerform.DocMove(new DocMoveFacts(
            [Move("docs/dev/PLAN.md", "docs/history/PLAN.md")],
            PlanPath: "plan.json", PlanFileWritable: false, WorkingTreeDirty: false));

        Assert.Equal(ReleaseAct.Refused, act.State);
        Assert.Contains(act.Detail, d => d.Contains("both or neither", StringComparison.Ordinal));
    }

    /// <summary>MEASURED on the CH4.2 rig, second run. The probe derives the source from the PLAN,
    /// which the first run repointed — so on the second run <c>From</c> and <c>To</c> are the same
    /// path, the file is "there", and the destination is "occupied" by itself. Before this, a
    /// completed move reported as a collision and the sentence read "X exists and X is not it".</summary>
    [Fact]
    public void A_move_the_plan_already_points_at_is_nothing_to_do_not_a_collision()
    {
        var inPlace = Move("docs/history/PLAN.md", "docs/history/PLAN.md", occupied: true);
        Assert.True(inPlace.AlreadyInPlace);

        var act = ReleasePerform.DocMove(new DocMoveFacts(
            [inPlace], "plan.json", PlanFileWritable: true, WorkingTreeDirty: false));

        Assert.Equal(ReleaseAct.Nothing, act.State);
        Assert.Contains(act.Detail, d => d.Contains("already in the record", StringComparison.Ordinal));
        Assert.DoesNotContain(act.Detail, d => d.Contains("is not it", StringComparison.Ordinal));
    }

    /// <summary>A genuine collision is still a refusal — two different files cannot both be the
    /// record's copy — and it names the pair rather than reporting a count.</summary>
    [Fact]
    public void A_destination_holding_a_different_file_is_still_refused_by_name()
    {
        var act = ReleasePerform.DocMove(new DocMoveFacts(
            [Move("plans/a/TRACKER.md", "docs/history/archive/trackers/TRACKER.md", occupied: true)],
            "plan.json", PlanFileWritable: true, WorkingTreeDirty: false));

        Assert.Equal(ReleaseAct.Refused, act.State);
        Assert.Contains(act.Detail, d => d.Contains("docs/history/archive/trackers/TRACKER.md", StringComparison.Ordinal));
    }

    /// <summary>The move rewrites the plan the run is reading, so it will not land on top of
    /// uncommitted work: the move and the repoint have to be one reviewable change or the review is
    /// of something else.</summary>
    [Fact]
    public void The_doc_move_refuses_on_a_dirty_tree()
    {
        var act = ReleasePerform.DocMove(new DocMoveFacts(
            [Move("docs/dev/PLAN.md", "docs/history/PLAN.md")],
            "plan.json", PlanFileWritable: true, WorkingTreeDirty: true));

        Assert.Equal(ReleaseAct.Refused, act.State);
        Assert.Contains(act.Detail, d => d.Contains("one reviewable change", StringComparison.Ordinal));
    }

    /// <summary>The order is not decoration. The CHANGELOG is renamed before the tag because the tag
    /// build reads the section; the merge is before the tag because the tag names the merged tip; the
    /// doc move is last because it rewrites the plan. A reordering here is a released binary whose
    /// release notes do not exist.</summary>
    [Fact]
    public void The_mechanical_acts_are_ordered_by_what_each_one_needs_from_the_last()
        => Assert.Equal(["changelog", "merge", "tag", "docmove"], ReleasePerform.MechanicalOrder);
}
