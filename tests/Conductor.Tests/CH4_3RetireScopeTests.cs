using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Integrations.Github;

namespace Conductor.Tests;

/// <summary>
/// CH4.3 - the retire sweep, scoped to the run being synced.
///
/// <para><b>The failure this pins.</b> <c>BackfillAsync</c> lists a repository's issues WHOLE, and
/// the task marker carries a checkpoint id and nothing else - so every task-marked issue in the
/// repository looked like a candidate, and the sweep closed each one the current run did not
/// declare, with the comment "this checkpoint is no longer declared in the plan". Measured
/// 2026-08-27 on shaahink/conductor: all 23 Divan checkpoint issues and every Karvansara one before
/// them carry <c>conductor:retired</c>, retired by the era that came after them. Nobody noticed,
/// because a retire is silent by construction.</para>
///
/// <para><b>Two attributions, either sufficient.</b> The owner marker in the body, which survives a
/// lost database, and the local map, which covers every issue created before that marker existed.
/// Neither present means the issue belongs to somebody else's board: it is NAMED, with its number,
/// and left exactly as it was.</para>
/// </summary>
public class CH4_3RetireScopeTests
{
    private const string Repo = "owner/scratch";
    private const string RetiredLabel = "conductor:retired";

    private static ArchivedRun Run(string runId, string plan) => new(
        RunId: runId, PlanName: plan, Repo: "C:/code/conductor", Branch: "feat/" + plan,
        EngineVersion: "0.5.0", Status: "Completed", StartedUtc: "2026-08-01T00:00:00Z",
        EndedUtc: "2026-08-02T00:00:00Z", LastActivityUtc: null, Sessions: 1, CostUsd: 0m, Tokens: 0);

    private static List<ConductorEvent> Board(params string[] taskIds) =>
        [.. taskIds.Select(id => (ConductorEvent)new TaskAdded
        {
            TaskId = id, CheckpointId = id, Title = "card " + id, Source = "plan",
            Kind = "checkpoint", StageId = id.Split('.')[0],
        })];

    private static async Task<GithubSyncResult> SyncAsync(
        FakeGithub fake, ArchivedRun run, List<ConductorEvent> log, GithubMap? map = null)
    {
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);
        var sync = new GithubBoardSync(client, Repo, "conductor", map);
        return await sync.BackfillAsync(log, run, "0.5.0", includeDiary: false, dryRun: false)
            .ConfigureAwait(true);
    }

    /// <summary>The regression, stated as the thing that actually happened: one era's run syncing to
    /// a repository that already carries the previous era's board closes none of it.</summary>
    [Fact]
    public async Task ASecondRunDoesNotRetireTheFirstRunsBoard()
    {
        using var fake = new FakeGithub();
        await SyncAsync(fake, Run("run-divan", "divan"), Board("DV1.1", "DV1.2")).ConfigureAwait(true);
        var dv11 = fake.NumberOfTask("DV1.1");
        var dv12 = fake.NumberOfTask("DV1.2");
        fake.Requests.Clear();

        var result = await SyncAsync(fake, Run("run-charkh", "charkh"), Board("CH1.1", "CH1.2"))
            .ConfigureAwait(true);

        Assert.Empty(result.Retired);
        Assert.True(fake.IsOpen(dv11) && fake.IsOpen(dv12), "the earlier era's cards were closed");
        Assert.DoesNotContain(RetiredLabel, fake.LabelsOf(dv11));
        Assert.DoesNotContain(RetiredLabel, fake.LabelsOf(dv12));
        Assert.Empty(fake.CommentsOn(dv11));
        Assert.Empty(fake.CommentsOn(dv12));
        // Named rather than silently skipped, with the number a human would have to open.
        Assert.Equal(new[] { "DV1.1 #" + dv11, "DV1.2 #" + dv12 }, result.RetireRefused);
        Assert.Contains("2 retire refused", result.Summary(), StringComparison.Ordinal);
    }

    /// <summary>The PROPERTY, not the example. Every task-marked issue that is out of the plan is
    /// either retired or named - there is no third, quiet answer. A sweep that later grows one
    /// (skipped, deferred, ignored) fails here instead of going silent, which is the only way the
    /// original defect could survive two eras.</summary>
    [Fact]
    public async Task EveryOutOfPlanCardIsEitherRetiredOrNamed()
    {
        using var fake = new FakeGithub();
        await SyncAsync(fake, Run("run-divan", "divan"), Board("DV1.1", "DV1.2")).ConfigureAwait(true);
        await SyncAsync(fake, Run("run-charkh", "charkh"), Board("CH1.1", "CH1.2")).ConfigureAwait(true);
        var ch12 = fake.NumberOfTask("CH1.2");

        // CH1.2 leaves the plan: ours, and it must still go. DV1.1 and DV1.2 are not ours at all.
        var result = await SyncAsync(fake, Run("run-charkh", "charkh"), Board("CH1.1")).ConfigureAwait(true);

        var accounted = result.Retired
            .Concat(result.RetireRefused.Select(r => r.Split(' ')[0]))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(new HashSet<string>(new[] { "DV1.1", "DV1.2", "CH1.2" }, StringComparer.Ordinal), accounted);
        Assert.Contains("CH1.2", result.Retired);
        Assert.DoesNotContain("DV1.1", result.Retired);
        Assert.Contains(RetiredLabel, fake.LabelsOf(ch12));
        Assert.False(fake.IsOpen(ch12), "a card this run created and then dropped was left open");
    }

    /// <summary>Scoping must not cost the feature. An issue created before the owner marker existed -
    /// which is every issue already on the real repository - is still attributable, from the local
    /// map v14 made the authority for "have I made this".</summary>
    [Fact]
    public async Task AnIssueFromBeforeTheOwnerMarkerIsAttributedByTheLocalMap()
    {
        using var fake = new FakeGithub();
        // A run with no id plants no owner marker: the body shape 0.5.0 left on the repository.
        await SyncAsync(fake, Run("", "legacy"), Board("KS1.1", "KS1.2")).ConfigureAwait(true);
        var ks12 = fake.NumberOfTask("KS1.2");
        Assert.DoesNotContain("<!-- conductor:owner", fake.BodyOf(ks12), StringComparison.Ordinal);

        var map = GithubMap.Transient();
        map.Seed("KS1.2", GithubMap.IssueKind, ks12);
        var result = await SyncAsync(fake, Run("run-next", "next"), Board("KS1.1"), map).ConfigureAwait(true);

        Assert.Contains("KS1.2", result.Retired);
        Assert.Empty(result.RetireRefused);
        Assert.Contains(RetiredLabel, fake.LabelsOf(ks12));
    }

    /// <summary>And without the map row, the same unmarked issue is left alone. This is the
    /// read-only backfill path - <c>github sync --backfill</c> builds a Transient map - reporting
    /// what it declined instead of reporting nothing.</summary>
    [Fact]
    public async Task TheSameIssueWithNeitherMarkerNorMapRowIsLeftAloneAndNamed()
    {
        using var fake = new FakeGithub();
        await SyncAsync(fake, Run("", "legacy"), Board("KS1.1", "KS1.2")).ConfigureAwait(true);
        var ks12 = fake.NumberOfTask("KS1.2");

        var result = await SyncAsync(fake, Run("run-next", "next"), Board("KS1.1")).ConfigureAwait(true);

        Assert.Empty(result.Retired);
        Assert.Equal(new[] { "KS1.2 #" + ks12 }, result.RetireRefused);
        Assert.True(fake.IsOpen(ks12), "an unattributable issue was closed by the sweep");
        Assert.Empty(fake.CommentsOn(ks12));
    }
}
