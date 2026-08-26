using Conductor.Core;
using Conductor.Core.Integrations.Cloud;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>DV5.2 / findings §2.3 CL-1 — the cloud lane, behind a flag, default off.
///
/// <para>CL-1 is named in the findings as an EXPERIMENT, and the honesty rule that makes it one is
/// pinned here: <b>the cost of a cloud lane is unknown, and it must be reported as unknown, never as
/// zero.</b> A run that quietly prices a checkpoint at $0 because it could not see the meter is
/// exactly the class of lie this repo built KS4 to catch.</para>
///
/// <para>What the lane RUNS was measured, not assumed. DV5.1 established that <c>claude --cloud</c>
/// refuses every non-interactive invocation, so an engine cannot spawn a cloud session at all;
/// <c>claude ultrareview</c> is the one cloud surface on this CLI that answers without a terminal,
/// and it is the CL-1 shape exactly — no conductor tools, no verdict, a second opinion.</para>
///
/// <para>"The referee never moves" is not asserted here: it is a source rule over the whole cloud
/// namespace, in <c>ArchitectureBoundaryTests.TheCloudLaneNeverReachesTheReferee</c>, because a
/// promise one test class makes about one code path is not the same thing.</para></summary>
public sealed class DV5_2CloudLaneTests : IDisposable
{
    private const string Sha = "3333333333333333333333333333333333333333";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dv5-lane-" + Guid.NewGuid().ToString("N")[..8]);

    public DV5_2CloudLaneTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ────────────────────────────── the flag ──────────────────────────────

    /// <summary>Off is the default and off is where every plan that has never heard of the block
    /// already is. Both readings matter: the config's own default, and a plan whose <c>cloud</c>
    /// property was never set at all.</summary>
    [Fact]
    public void The_lane_is_off_by_default_and_a_plan_that_says_nothing_has_no_block_at_all()
    {
        Assert.False(new CloudLaneConfig().Enabled);
        Assert.Null(new PlanConfig().Cloud);
    }

    [Fact]
    public async Task A_disabled_lane_never_reaches_the_process_seam()
    {
        var spy = new SpyCli();

        foreach (var config in new[] { null, new CloudLaneConfig(), new CloudLaneConfig { Enabled = false } })
        {
            var r = await new CloudLane(config, spy, Never).RunAsync(_dir, _dir, "z1", CancellationToken.None);

            Assert.Equal(CloudLaneOutcome.Disabled, r.Outcome);
            Assert.False(r.Spawned);
        }

        Assert.Empty(spy.Calls);
    }

    /// <summary>The preflight is not consulted either. A lane that is off must not shell out to git to
    /// discover it is off — on a plan with no cloud block that is a git call per session, for ever,
    /// for a feature nobody asked for.</summary>
    [Fact]
    public async Task A_disabled_lane_does_not_even_measure_the_repo()
    {
        var r = await new CloudLane(null, new SpyCli(), Never).RunAsync(_dir, _dir, "z1", CancellationToken.None);

        Assert.Equal(CloudLaneOutcome.Disabled, r.Outcome);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(241, false)]
    [InlineData(1, true)]
    [InlineData(30, true)]
    public void A_nonsense_timeout_is_refused_at_plan_load_not_discovered_by_a_hanging_lane(
        int minutes, bool ok)
        => Assert.Equal(ok, new CloudLaneConfig { TimeoutMinutes = minutes }.Refusal() is null);

    // ────────────────────────────── what stops it, and what does not ──────────────────────────────

    /// <summary>Narrower than <c>/cloud</c>'s gate on purpose. The review verb BUNDLES the local
    /// branch — the CLI's own refusal names only local edits — so requiring a pushed branch here would
    /// refuse work that would have succeeded, which is the mistake DV5.1 recorded about inventing a
    /// stricter session id than the CLI's.</summary>
    [Theory]
    [InlineData(CloudPreflightVerdict.NothingToClone, true)]
    [InlineData(CloudPreflightVerdict.DetachedHead, true)]
    [InlineData(CloudPreflightVerdict.DirtyTree, true)]
    [InlineData(CloudPreflightVerdict.NoUpstream, false)]
    [InlineData(CloudPreflightVerdict.RemoteMissingBranch, false)]
    [InlineData(CloudPreflightVerdict.RemoteDiffersFromHead, false)]
    [InlineData(CloudPreflightVerdict.Ok, false)]
    public void Only_what_the_cli_itself_refuses_blocks_a_bundled_lane(
        CloudPreflightVerdict verdict, bool blocks)
        => Assert.Equal(blocks, CloudLane.Blocks(verdict));

    [Fact]
    public async Task A_dirty_tree_refuses_the_lane_quoting_the_files_and_spawns_nothing()
    {
        var spy = new SpyCli();
        var lane = new CloudLane(Enabled(), spy,
            _ => CloudPreflight.Judge(Snap(dirty: true, dirtyCount: 1, summary: "M src/Foo.cs"), Sha));

        var r = await lane.RunAsync(_dir, _dir, "z1", CancellationToken.None);

        Assert.Equal(CloudLaneOutcome.Refused, r.Outcome);
        Assert.False(r.Spawned);
        Assert.Empty(spy.Calls);
        Assert.Contains("M src/Foo.cs", r.Summary, StringComparison.Ordinal);
    }

    /// <summary>An unpushed branch is REPORTED and not refused — the counterpart of the rule above,
    /// and the one that would silently stop the lane doing anything useful if it were a gate.</summary>
    [Fact]
    public async Task An_unpushed_branch_still_runs_the_lane()
    {
        var spy = new SpyCli { Answer = new CloudCliResult(0, "no findings", "", false) };
        var lane = new CloudLane(Enabled(), spy,
            _ => CloudPreflight.Judge(Snap(upstream: null, ahead: null, behind: null), Sha));

        var r = await lane.RunAsync(_dir, _dir, "z1", CancellationToken.None);

        Assert.Equal(CloudLaneOutcome.Reviewed, r.Outcome);
        Assert.True(r.Spawned);
    }

    // ────────────────────────────── the invocation ──────────────────────────────

    /// <summary><c>--no-post</c> is the CLI's own default and is passed anyway. A lane the ENGINE
    /// spawns must never write a comment on a pull request as the owner, and leaning on a research
    /// preview's default for that is one release note away from being wrong.</summary>
    [Fact]
    public void The_review_argv_names_the_verb_refuses_to_post_and_carries_the_timeout()
    {
        Assert.Equal(["ultrareview", "--no-post", "--timeout", "30"], CloudCliFacts.ReviewArgs(null, 30));
        Assert.Equal(["ultrareview", "main", "--no-post", "--timeout", "5"], CloudCliFacts.ReviewArgs("main", 5));
        Assert.Equal(["ultrareview", "--no-post", "--timeout", "30"], CloudCliFacts.ReviewArgs("   ", 30));
    }

    [Fact]
    public async Task The_configured_base_and_timeout_reach_the_cli()
    {
        var spy = new SpyCli { Answer = new CloudCliResult(0, "no findings", "", false) };
        var lane = new CloudLane(new CloudLaneConfig { Enabled = true, Base = "master", TimeoutMinutes = 7 },
            spy, Clean);

        await lane.RunAsync(_dir, _dir, "z1", CancellationToken.None);

        Assert.Equal((_dir, "master", TimeSpan.FromMinutes(7)), Assert.Single(spy.Calls));
    }

    // ────────────────────────────── the honesty rule ──────────────────────────────

    /// <summary>The rule CL-1 turns on: unknown, on every outcome, and never a zero. Walks all five —
    /// including the two that never spawned, because "it did not run" is exactly when a surface is
    /// most tempted to render a nought.</summary>
    [Fact]
    public async Task Every_outcome_prices_the_lane_as_unknown_and_none_of_them_prints_a_zero()
    {
        var cases = new (CloudLane Lane, CloudLaneOutcome Expected)[]
        {
            (new CloudLane(null, new SpyCli(), Never), CloudLaneOutcome.Disabled),
            (new CloudLane(Enabled(), new SpyCli(),
                _ => CloudPreflight.Judge(Snap(dirty: true, dirtyCount: 1, summary: "M x.cs"), Sha)),
                CloudLaneOutcome.Refused),
            (new CloudLane(Enabled(), new SpyCli { Answer = new CloudCliResult(0, "findings", "", false) }, Clean),
                CloudLaneOutcome.Reviewed),
            (new CloudLane(Enabled(), new SpyCli { Answer = new CloudCliResult(2, "", "boom", false) }, Clean),
                CloudLaneOutcome.Failed),
            (new CloudLane(Enabled(), new SpyCli { Answer = new CloudCliResult(0, "", "", true) }, Clean),
                CloudLaneOutcome.TimedOut),
        };

        foreach (var (lane, expected) in cases)
        {
            var r = await lane.RunAsync(_dir, _dir, "z1", CancellationToken.None);

            Assert.Equal(expected, r.Outcome);
            Assert.Equal("unknown", r.Cost);
            // The ledger's own "unknown, not zero" branch is reached by handing it nothing.
            Assert.Null(r.Spend);
            foreach (var zero in new[] { "$0", "0.00", "cost: 0", "free" })
                Assert.DoesNotContain(zero, r.Summary, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The other half of the same rule, at the seam that would render it: a lane hands the
    /// ledger a null receipt, and <c>RunSpendLedger.Record</c> answers false — no row, no zero, and a
    /// line in the run's log saying out loud that it cannot price this one.</summary>
    [Fact]
    public void A_lane_with_no_receipt_is_logged_as_unknown_and_writes_no_cost_row()
    {
        var lines = new List<string>();
        var ledger = new Core.Accounting.RunSpendLedger(null, "run-1", null, lines.Add);

        var wrote = ledger.Record(null, 3, "cloud lane 'cloud-review'");

        Assert.False(wrote);
        Assert.Contains("unknown, not zero", Assert.Single(lines), StringComparison.Ordinal);
        Assert.DoesNotContain("$0", lines[0], StringComparison.Ordinal);
    }

    // ────────────────────────────── the payload stays opaque ──────────────────────────────

    /// <summary>Stored whole, never parsed. DV5.1 already paid for guessing at a shape this engine had
    /// not observed; a review summarised by a parser that misread it is worse than one nobody
    /// summarised, because it reads as a conclusion.</summary>
    [Fact]
    public async Task The_review_is_stored_byte_for_byte_and_the_summary_only_counts_it()
    {
        const string payload = "{\"bugs\":[{\"file\":\"a.cs\",\"severity\":\"high\"}]}\nand some trailing prose";
        var lane = new CloudLane(Enabled(), new SpyCli { Answer = new CloudCliResult(0, payload, "", false) }, Clean);

        var r = await lane.RunAsync(_dir, _dir, "DV5.2-s16", CancellationToken.None);

        Assert.Equal(CloudLaneOutcome.Reviewed, r.Outcome);
        Assert.Equal(payload, await File.ReadAllTextAsync(r.ArtifactPath!, CancellationToken.None));
        Assert.Contains("stored whole and unparsed", r.Summary, StringComparison.Ordinal);
        Assert.Contains("settles nothing", r.Summary, StringComparison.Ordinal);
        // Nothing from inside the payload is repeated as if the engine had understood it.
        Assert.DoesNotContain("high", r.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("a.cs", r.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_artifact_name_survives_a_label_that_is_not_a_filename()
    {
        var lane = new CloudLane(Enabled(), new SpyCli { Answer = new CloudCliResult(0, "x", "", false) }, Clean);

        var r = await lane.RunAsync(_dir, _dir, "feat/divan: DV5.2", CancellationToken.None);

        Assert.NotNull(r.ArtifactPath);
        Assert.True(File.Exists(r.ArtifactPath));
        Assert.DoesNotContain(':', Path.GetFileName(r.ArtifactPath));
    }

    // ────────────────────────────── the rig ──────────────────────────────

    private static CloudLaneConfig Enabled() => new() { Enabled = true };

    private static CloudPreflightResult Clean(string _) => CloudPreflight.Judge(Snap(), Sha);

    /// <summary>A preflight that FAILS the test if it is called. Used where the lane must decide
    /// before it measures anything.</summary>
    private static CloudPreflightResult Never(string _)
        => throw new InvalidOperationException("a lane that is off must not measure the repo");

    private static GitSnapshot Snap(string branch = "feat/x", string? upstream = "origin/feat/x",
        int? ahead = 0, int? behind = 0, bool dirty = false, int dirtyCount = 0, string summary = "clean")
        => new(branch, false, upstream, ahead, behind, Sha, "a commit", dirty, dirtyCount, summary, []);

    private sealed class SpyCli : ICloudCli
    {
        public List<(string Repo, string? Target, TimeSpan Timeout)> Calls { get; } = [];

        public CloudCliResult Answer { get; init; } = new(0, "ok", "", false);

        public Task<CloudCliResult> ReviewAsync(string repoDir, string? target, TimeSpan timeout,
            CancellationToken ct)
        {
            Calls.Add((repoDir, target, timeout));
            return Task.FromResult(Answer);
        }

        /// <summary>The lane never talks to a cloud session; a call here is a bug, not a fixture gap.</summary>
        public Task<CloudCliResult> FollowUpAsync(string repoDir, string sessionId, string message,
            TimeSpan timeout, CancellationToken ct)
            => throw new InvalidOperationException("a review lane must never message a cloud session");
    }
}
