using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Integrations.Github;
using Conductor.Core.Store;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV6.1 — bugs and followups as a LONG-LIVED issue class, asserted against what GitHub receives and
/// against a real <c>run.db</c>.
///
/// <para><b>The claim being tested is a lifetime, not a shape.</b> A checkpoint issue is opened, then
/// closed, then retired, all inside one run. A ledger issue is opened when the entry is filed and
/// closed when the LEDGER says so — which is usually a later run, and never merely because this run
/// ended. So the assertions that matter are the ones about a boundary: a run reaching a terminal
/// status, a bug flipping to fixed between two passes, and a checkpoint disappearing from the plan
/// while the ledger issue beside it is left alone.</para>
///
/// <para>Driven through <see cref="GithubMirror"/> — the real reconciler over the real store — rather
/// than by handing <see cref="GithubBoardSync"/> a list, because "the mirror notices a bug that was
/// filed while nothing else happened" is a claim about the cursor, and the cursor cannot see the bugs
/// table at all.</para>
/// </summary>
public sealed class DV6_1LedgerIssueClassTests : IDisposable
{
    private const string RunId = "run-dv61000000";
    private const string Repo = "owner/scratch";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dv61-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SqliteRunStore _store;
    private readonly List<string> _log = [];

    public DV6_1LedgerIssueClassTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId(RunId);
        _store.InitializeRun(RunId, "divan", "C:/code/conductor", "feat/divan",
            new EngineStamp("0.4.1", "abc123", false));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* the temp dir is not the assertion */ }
    }

    // ── the rig ──────────────────────────────────────────────────────────────────────────────────

    private string FollowupsPath => Path.Combine(_dir, "followups.md");

    private void Followups(params string[] rows)
    {
        var text = "# Tracked followups\n\n| id | item | detail | owning stage | status |\n|---|---|---|---|---|\n"
                 + string.Join("\n", rows) + "\n";
        File.WriteAllText(FollowupsPath, text);
    }

    private GithubMirror Mirror(FakeGithub fake, bool withFollowups = true) =>
        new(_store, RunId, Repo, "t", "conductor", includeDiary: false, _log.Add, fake,
            withFollowups ? FollowupsPath : null);

    private void SeedBoard()
    {
        _store.Emit(new TaskAdded { TaskId = "DV6.1", CheckpointId = "DV6.1", Title = "the issue class", Source = "plan", Kind = "checkpoint", StageId = "DV6" });
        _store.Emit(new SessionFinished { Number = 1, StageId = "DV6", Outcome = "Delivered" });
        _store.FlushEvents();
    }

    private static IEnumerable<string> CreatedBodies(FakeGithub fake) =>
        fake.Posted("/repos/owner/scratch/issues").Select(Body);

    private static string Field(string json, string name) =>
        JsonDocument.Parse(json).RootElement.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static string Body(string json) => Field(json, "body");
    private static string Title(string json) => Field(json, "title");

    private static List<string> Labels(string json) =>
        JsonDocument.Parse(json).RootElement.TryGetProperty("labels", out var v)
            ? [.. v.EnumerateArray().Select(e => e.GetString() ?? "")]
            : [];

    /// <summary>The issue a marker landed on, found the way the mirror finds it.</summary>
    private static string CreatedWith(FakeGithub fake, string marker) =>
        fake.Posted("/repos/owner/scratch/issues").Single(b => Body(b).Contains(marker, StringComparison.Ordinal));

    // ── the class ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ABugAndAFollowupBecomeIssuesInTheirOwnLabelledClass()
    {
        var bug = _store.WriteBug(RunId, "the courier does not upload files", "a push with an artifact is delivered as text", "high", "DV4", 13);
        Followups("| FU-DV6-1 | the digest has no ledger line | it is one line | DV6 | OPEN |");
        SeedBoard();

        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);
        var pass = await mirror.ReconcileAsync("test").ConfigureAwait(true);

        Assert.True(pass.Ok, pass.Error);
        var bugIssue = CreatedWith(fake, GithubIdentity.BugMarker(bug));
        Assert.Equal($"bug #{bug} — the courier does not upload files", Title(bugIssue));
        Assert.Contains("conductor:bug", Labels(bugIssue));
        Assert.Contains("conductor:severity:high", Labels(bugIssue));
        Assert.Contains("conductor:status:open", Labels(bugIssue));
        // NOT a checkpoint: a card's marker is what the retire sweep indexes, and a ledger issue must
        // be invisible to it.
        Assert.Null(GithubIdentity.TaskIdIn(Body(bugIssue)));

        var followupIssue = CreatedWith(fake, GithubIdentity.FollowupMarker("FU-DV6-1"));
        Assert.Equal("followup FU-DV6-1 — the digest has no ledger line", Title(followupIssue));
        Assert.Contains("conductor:followup", Labels(followupIssue));
        Assert.Contains("conductor:status:open", Labels(followupIssue));
        Assert.Null(GithubIdentity.TaskIdIn(Body(followupIssue)));

        // And the board is still the board: the checkpoint card is there, in ITS class.
        Assert.Contains(CreatedBodies(fake), b => GithubIdentity.TaskIdIn(b) == "DV6.1");
    }

    /// <summary>A ledger that is already closed does not arrive as history. Twenty-six runs of fixed
    /// bugs would otherwise land on the destination as a wall of closed issues — the graveyard this
    /// checkpoint exists to empty, rebuilt by the fix for it.</summary>
    [Fact]
    public async Task AnEntryThatIsAlreadyClosedIsNeverCreated()
    {
        var fixedBug = _store.WriteBug(RunId, "already fixed", null, "low", "DV1", 1);
        Assert.True(_store.UpdateBugStatus(RunId, fixedBug, "fixed", 2));
        Followups("| FU-DV6-9 | done long ago | - | DV1 | CLOSED (abc1234) |");
        SeedBoard();

        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);
        await mirror.ReconcileAsync("test").ConfigureAwait(true);

        Assert.DoesNotContain(CreatedBodies(fake), b => b.Contains(GithubIdentity.BugMarker(fixedBug), StringComparison.Ordinal));
        Assert.DoesNotContain(CreatedBodies(fake), b => b.Contains(GithubIdentity.FollowupMarker("FU-DV6-9"), StringComparison.Ordinal));
    }

    // ── the lifetime ─────────────────────────────────────────────────────────────────────────────

    /// <summary>THE claim. The run ends; the board closes; the ledger does not.</summary>
    [Fact]
    public async Task TheRunEndingClosesTheBoardAndLeavesTheLedgerOpen()
    {
        var bug = _store.WriteBug(RunId, "still open when the run ended", null, "medium", "DV6", 16);
        Followups("| FU-DV6-2 | still open too | - | DV6 | OPEN |");
        SeedBoard();

        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);
        await mirror.ReconcileAsync("run start").ConfigureAwait(true);

        // The checkpoint is done and confirmed, and the run reaches a terminal status.
        _store.Emit(new TaskStatusChanged { TaskId = "DV6.1", Status = "done", Source = "agent", Commit = "abcdef1234567890" });
        _store.Emit(new SessionFinished { Number = 2, StageId = "DV6", Outcome = "Delivered", NewlyDone = ["DV6.1"] });
        _store.FlushEvents();
        await mirror.ReconcileAsync("run complete", runStatusOverride: "Completed").ConfigureAwait(true);

        var closed = fake.Requests.Where(r => r.Method == "PATCH"
            && r.Body.Contains("\"state\":\"closed\"", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(closed);

        // Whatever was closed, none of it was ours-in-the-ledger. Asserted by NUMBER, because the
        // marker only appears in a create body and a close is a PATCH on a path.
        var bugIssue = fake.NumberOfMarker(GithubIdentity.BugMarker(bug));
        var followupIssue = fake.NumberOfMarker(GithubIdentity.FollowupMarker("FU-DV6-2"));
        Assert.DoesNotContain(closed, r => r.Path.EndsWith("/" + bugIssue.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.DoesNotContain(closed, r => r.Path.EndsWith("/" + followupIssue.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        Assert.True(fake.IsOpen(bugIssue), "the bug's issue was closed by the run ending");
        Assert.True(fake.IsOpen(followupIssue), "the followup's issue was closed by the run ending");
    }

    /// <summary>The retire sweep closes a card whose checkpoint left the plan. It must not be able to
    /// reach a ledger issue — not by policy, but because it indexes on the task marker and a ledger
    /// issue does not carry one.</summary>
    [Fact]
    public async Task TheRetireSweepNeverTouchesALedgerIssue()
    {
        // Driven through the sync rather than the mirror: retirement is what happens when a
        // checkpoint stops being DECLARED, and a store's event log cannot un-declare one.
        var ledger = GithubLedgerPlan.Cards([Bug(1, "outlives a retired checkpoint", "open")], [], "conductor");
        var declared = Log();

        using var fake = new FakeGithub();
        await SyncAsync(fake, declared, ledger).ConfigureAwait(true);
        var bugIssue = fake.NumberOfMarker(GithubIdentity.BugMarker(1));

        var shrunk = declared.Where(e => e is not TaskAdded { TaskId: "DV6.1" }).ToList();
        var result = await SyncAsync(fake, shrunk, ledger).ConfigureAwait(true);

        Assert.Contains("DV6.1", result.Retired);
        Assert.Contains("conductor:retired", fake.LabelsOf(fake.NumberOfTask("DV6.1")));
        Assert.True(fake.IsOpen(bugIssue), "the retire sweep reached a ledger issue");
        Assert.DoesNotContain("conductor:retired", fake.LabelsOf(bugIssue));
    }

    /// <summary>Two checkpoints, one of which is about to leave the plan.</summary>
    private static List<ConductorEvent> Log() =>
    [
        new TaskAdded { TaskId = "DV6.1", CheckpointId = "DV6.1", Title = "the issue class", Source = "plan", Kind = "checkpoint", StageId = "DV6" },
        new TaskAdded { TaskId = "DV6.2", CheckpointId = "DV6.2", Title = "the columns", Source = "plan", Kind = "checkpoint", StageId = "DV6" },
    ];

    private static Core.Store.CarriedBugRow Bug(long id, string title, string status) =>
        new(new BugRow(id, RunId, title, null, "medium", status, "DV6", 16, null,
            DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            ""), "divan");

    private static async Task<GithubSyncResult> SyncAsync(
        FakeGithub fake, List<ConductorEvent> log, IReadOnlyList<GithubLedgerCard> ledger)
    {
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);
        var sync = new GithubBoardSync(client, Repo, "conductor");
        var run = new Core.History.ArchivedRun(
            RunId: RunId, PlanName: "divan", Repo: "C:/code/conductor", Branch: "feat/divan",
            EngineVersion: "0.4.1", Status: "Running", StartedUtc: null, EndedUtc: null,
            LastActivityUtc: null, Sessions: 1, CostUsd: 0m, Tokens: 0);
        return await sync.BackfillAsync(log, run, "0.4.1", includeDiary: false, dryRun: false, ledger)
            .ConfigureAwait(true);
    }

    /// <summary>Closed BY THE LEDGER: the bug is fixed between two passes and the second pass closes
    /// its issue, with a comment saying which side closed it.</summary>
    [Fact]
    public async Task AFixedBugClosesItsIssueOnTheNextPassWithAComment()
    {
        var bug = _store.WriteBug(RunId, "fixed between passes", null, "medium", "DV6", 16);
        Followups("| FU-DV6-3 | closed between passes | - | DV6 | OPEN |");
        SeedBoard();

        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);
        await mirror.ReconcileAsync("first").ConfigureAwait(true);
        var bugIssue = fake.NumberOfMarker(GithubIdentity.BugMarker(bug));
        var followupIssue = fake.NumberOfMarker(GithubIdentity.FollowupMarker("FU-DV6-3"));
        Assert.True(fake.IsOpen(bugIssue));

        // The ledger moves, and NOTHING else does — no event, no session, no boundary news at all.
        Assert.True(_store.UpdateBugStatus(RunId, bug, "fixed", 16));
        Followups("| FU-DV6-3 | closed between passes | - | DV6 | CLOSED (abc1234) |");

        var second = await mirror.ReconcileAsync("second").ConfigureAwait(true);

        Assert.True(second.Ran, "the pass did not run: the cursor cannot see the bugs table, so the ledger must have its own answer");
        Assert.False(fake.IsOpen(bugIssue));
        Assert.False(fake.IsOpen(followupIssue));
        Assert.Contains(fake.CommentsOn(bugIssue), c => c.Contains("no longer lists this bug as open", StringComparison.Ordinal));
        Assert.Contains(fake.CommentsOn(followupIssue), c => c.Contains("followups.md", StringComparison.Ordinal));
        Assert.Contains("conductor:status:fixed", fake.LabelsOf(bugIssue));
    }

    /// <summary>A bug FILED while nothing else happened still gets out at the next boundary. The
    /// event cursor is blind to the bugs table, and the first version of this shipped that blindness.</summary>
    [Fact]
    public async Task ABugFiledWithNoOtherNewsStillReachesTheBoard()
    {
        SeedBoard();
        using var fake = new FakeGithub();
        using var mirror = Mirror(fake, withFollowups: false);
        await mirror.ReconcileAsync("first").ConfigureAwait(true);
        var before = fake.Requests.Count;

        var bug = _store.WriteBug(RunId, "filed mid-session", null, "high", "DV6", 16);
        var second = await mirror.ReconcileAsync("second").ConfigureAwait(true);

        Assert.True(second.Ran);
        Assert.True(fake.Requests.Count > before);
        Assert.True(fake.IsOpen(fake.NumberOfMarker(GithubIdentity.BugMarker(bug))));
    }

    /// <summary>And the other half of that: a pass with news on NEITHER side is still free. A ledger
    /// that answered "changed" every time would turn every boundary into a listing.</summary>
    [Fact]
    public async Task AnUnchangedLedgerAndNoEventsCostsZeroRequests()
    {
        _store.WriteBug(RunId, "already mirrored", null, "low", "DV6", 16);
        Followups("| FU-DV6-4 | already mirrored | - | DV6 | OPEN |");
        SeedBoard();

        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);
        await mirror.ReconcileAsync("first").ConfigureAwait(true);
        var after = fake.Requests.Count;

        var second = await mirror.ReconcileAsync("second").ConfigureAwait(true);

        Assert.False(second.Ran);
        Assert.Equal(after, fake.Requests.Count);
    }

    /// <summary>A prose status cell is still OPEN. followups.md really carries
    /// <c>**OPEN, owner-gated, and stated as such (SF0.4)** - not carried as if...</c>, and an exact
    /// match calls that row closed — an undercount of exactly the rows a human most wants to see.</summary>
    [Fact]
    public void AProseOpenRowCountsAsOpenForTheLedger()
    {
        Assert.True(FollowupParser.IsOpen(new FollowupEntry { Status = "OPEN" }));
        Assert.True(FollowupParser.IsOpen(new FollowupEntry { Status = "**OPEN, owner-gated, and stated as such (SF0.4)** - not carried" }));
        Assert.False(FollowupParser.IsOpen(new FollowupEntry { Status = "CLOSED (abc1234)" }));
        Assert.False(FollowupParser.IsOpen(new FollowupEntry { Status = "DONE" }));

        // The fix-lane path keeps the STRICTER test: counting a row is free, opening a Tier-B
        // mutating lane for one is not.
        Followups("| FU-DV6-5 | prose status | - | DV6 | **OPEN, owner-gated** - the owner |");
        Assert.Empty(FollowupParser.ReadOpenForStage(FollowupsPath, "DV6"));
        Assert.Contains(FollowupParser.Read(FollowupsPath).Where(FollowupParser.IsOpen), e => e.Id == "FU-DV6-5");
    }
}
