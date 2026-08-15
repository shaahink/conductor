using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Integrations.Github;
using Conductor.Core.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS9.2 — the live mirror, asserted against a REAL run store and a recording fake.
///
/// <para><b>Why a real SqliteRunStore and not a stub.</b> Every claim here is about the cursor: that
/// it advances only after a clean push, that it survives a process restart, that replaying it from
/// zero mints nothing. A stubbed store would let all three be true of the stub — the seq numbers,
/// the drain, and the round trip through <c>ReadEventsAfter</c> are the mechanism under test, not
/// scaffolding around it.</para>
///
/// <para><b>Why the same fake instance across passes.</b> Convergence-after-outage and
/// replay-from-zero are statements about a SECOND pass seeing the FIRST pass's issues. A fresh fake
/// per pass would make every pass look idempotent for the wrong reason.</para>
/// </summary>
public sealed class KS9_2ReconcilerTests : IDisposable
{
    private const string RunId = "run-ks92000000";
    private const string Repo = "owner/scratch";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ks92-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly SqliteRunStore _store;
    private readonly List<string> _log = [];

    public KS9_2ReconcilerTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId(RunId);
        _store.InitializeRun(RunId, "karvansara", "C:/code/conductor", "feat/karvansara",
            new EngineStamp("0.4.1", "abc123", false));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* the temp dir is not the assertion */ }
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Emit through the store's own <c>Emit</c>, so the events carry the seq numbers the
    /// store assigns rather than seq numbers a fixture invented — the cursor is compared against
    /// those, and a hand-numbered log would prove nothing about the real path.</summary>
    private void Emit(params ConductorEvent[] events)
    {
        foreach (var e in events) _store.Emit(e);
        _store.FlushEvents();
    }

    private void SeedFirstSession()
    {
        Emit(
            new TaskAdded { TaskId = "KS9.1", CheckpointId = "KS9.1", Title = "the client", Source = "plan", Kind = "checkpoint", StageId = "KS9" },
            new TaskAdded { TaskId = "KS9.2", CheckpointId = "KS9.2", Title = "the live mirror", Source = "plan", Kind = "checkpoint", StageId = "KS9" },
            new TaskStatusChanged { TaskId = "KS9.1", Status = "done", Source = "agent", Commit = "abcdef1234567890" },
            new CheckpointConfirmed { CheckpointId = "KS9.1", StageId = "KS9" },
            new SessionFinished { Number = 1, StageId = "KS9", Outcome = "Delivered", NewlyDone = ["KS9.1"], CostUsd = 6.25m });
    }

    private void SeedSecondSession()
    {
        Emit(
            new TaskStatusChanged { TaskId = "KS9.2", Status = "in_progress", Source = "agent" },
            new SessionFinished { Number = 2, StageId = "KS9", Outcome = "Progress", CostUsd = 4.0m });
    }

    private GithubMirror Mirror(FakeGithub fake) =>
        new(_store, RunId, Repo, "t", "conductor", includeDiary: true, _log.Add, fake);

    private static int Posts(FakeGithub fake, string path) =>
        fake.Requests.Count(r => r.Method == "POST" && r.Path == path);

    /// <summary>Every comment POST, whatever issue it landed on — the diary's issue number is the
    /// fake's business, not the assertion's.</summary>
    private static int CommentPosts(FakeGithub fake) =>
        fake.Requests.Count(r => r.Method == "POST" && r.Path.EndsWith("/comments", StringComparison.Ordinal));

    private static int IssuePosts(FakeGithub fake) => Posts(fake, "/repos/owner/scratch/issues");

    // ── the cursor ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NothingNewMeansZeroRequests()
    {
        SeedFirstSession();
        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);

        var first = await mirror.ReconcileAsync("session 1 end").ConfigureAwait(true);
        Assert.True(first.Ran);
        Assert.True(first.Ok);
        var afterFirst = fake.Requests.Count;
        Assert.True(afterFirst > 0, "the first pass must actually push");

        // The boundary fires again with no events in between — a park, an owner gate, a run-complete
        // on a run that finished the moment its last session did. The pass must cost NOTHING.
        var second = await mirror.ReconcileAsync("owner-gate KS9").ConfigureAwait(true);
        Assert.False(second.Ran);
        Assert.Equal(0, second.Requests);
        Assert.Equal(afterFirst, fake.Requests.Count);
    }

    [Fact]
    public async Task CursorAdvancesToTheHeadOfTheBatchAndOnlyAfterACleanPush()
    {
        SeedFirstSession();
        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);

        Assert.Equal(0L, _store.ReadGithubCursor(RunId, Repo).Seq);
        var head = _store.ReadEventsAfter(RunId, 0).Max(e => e.Seq);

        var pass = await mirror.ReconcileAsync("session 1 end").ConfigureAwait(true);
        Assert.True(pass.Ok);
        Assert.Equal(head, _store.ReadGithubCursor(RunId, Repo).Seq);
        Assert.Equal(1, _store.ReadGithubCursor(RunId, Repo).Passes);
    }

    [Fact]
    public async Task OneSessionBoundaryIsOneBatchedPassNotOneRequestPerEvent()
    {
        SeedFirstSession();
        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);

        var pass = await mirror.ReconcileAsync("session 1 end").ConfigureAwait(true);

        // Five events went in. The bar is that the request count is a function of the BOARD (two
        // cards + one diary issue + one milestone + the lists), not of the event count.
        Assert.True(pass.Requests <= 12,
            $"a single boundary must not fan out per event — {pass.Requests} requests for 5 events");
        Assert.Equal(3, IssuePosts(fake));      // 2 cards + 1 diary
        Assert.Equal(1, CommentPosts(fake));    // one SessionFinished

        // And the second boundary, with ONE new session on the log, must cost strictly less than the
        // first: the whole point of a cursor is that a steady run does not re-post its history.
        SeedSecondSession();
        var second = await mirror.ReconcileAsync("session 2 end").ConfigureAwait(true);
        Assert.True(second.Ok);
        Assert.True(second.Requests < pass.Requests,
            $"second boundary cost {second.Requests}, first cost {pass.Requests}");
        Assert.Equal(3, IssuePosts(fake));                                             // still 3 issues, ever
    }

    // ── the outage ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFailedPassHoldsTheCursorAndRecordsWhy()
    {
        SeedFirstSession();
        using var fake = new FakeGithub { Outage = "connection refused" };
        using var mirror = Mirror(fake);

        var pass = await mirror.ReconcileAsync("session 1 end").ConfigureAwait(true);

        Assert.True(pass.Ran);
        Assert.False(pass.Ok);
        var cursor = _store.ReadGithubCursor(RunId, Repo);
        Assert.Equal(0L, cursor.Seq);
        Assert.NotNull(cursor.LastError);
        // Not silent: the failure posture the design asked for is one line, not a swallowed fault.
        Assert.Contains(_log, l => l.Contains("github mirror", StringComparison.Ordinal)
                                && l.Contains("cursor held", StringComparison.Ordinal));
        Assert.Equal(1, mirror.FailedPasses);
    }

    [Fact]
    public async Task ConvergesOnReconnectWithoutAFullRePostAndWithoutDuplicates()
    {
        SeedFirstSession();
        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);

        // Session 1 lands.
        Assert.True((await mirror.ReconcileAsync("session 1 end").ConfigureAwait(true)).Ok);
        var afterGood = fake.Requests.Count;
        var cursorAtOutage = _store.ReadGithubCursor(RunId, Repo).Seq;
        Assert.True(cursorAtOutage > 0);

        // The network dies, and two boundaries fire into it.
        fake.Outage = "connection refused";
        SeedSecondSession();
        Assert.False((await mirror.ReconcileAsync("session 2 end").ConfigureAwait(true)).Ok);
        Assert.False((await mirror.ReconcileAsync("owner-gate KS9").ConfigureAwait(true)).Ok);
        // Held exactly where it was — not advanced, and not reset either.
        Assert.Equal(cursorAtOutage, _store.ReadGithubCursor(RunId, Repo).Seq);

        // It comes back. The next pass pushes the missed deltas and the board equals the fold.
        fake.Outage = null;
        var recovered = await mirror.ReconcileAsync("session 3 end").ConfigureAwait(true);
        Assert.True(recovered.Ok);
        Assert.Equal(3, IssuePosts(fake));      // no second copy of anything
        Assert.Equal(2, CommentPosts(fake));    // session 2's comment, once
        Assert.True(recovered.Requests < afterGood,
            $"convergence is a delta push, not a second full backfill ({recovered.Requests} vs {afterGood})");

        // KS9.2's own acceptance: the board now says what the fold says.
        Assert.Contains(recovered.Result!.Updated, k => k == "KS9.2");
    }

    // ── the read replica ─────────────────────────────────────────────────────────────────────────

    /// <summary>The defect the live rig found second, and the expensive one. GitHub's issue LIST is a
    /// read replica: one pass created four issues and the pass two seconds behind it listed the
    /// repository, saw none of them, and created four more — two complete copies of one board, with
    /// correct code on both ends. KS9.1's identity-by-marker is still right and still what a human
    /// reads; it just cannot be ASKED of a replica. The local map is the authority, and this is the
    /// test that says so: the listing lies, completely, and the board stays one board.</summary>
    [Fact]
    public async Task AStaleListingCannotTalkTheMirrorIntoASecondCopyOfTheBoard()
    {
        SeedFirstSession();
        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);

        Assert.True((await mirror.ReconcileAsync("run start").ConfigureAwait(true)).Ok);
        Assert.Equal(3, IssuePosts(fake));
        Assert.Equal(1, CommentPosts(fake));

        // Now the replica knows nothing — the exact live shape, at maximum severity.
        fake.ListsAreStale = true;
        SeedSecondSession();
        var blind = await mirror.ReconcileAsync("session 2 end").ConfigureAwait(true);

        Assert.True(blind.Ok);
        Assert.Equal(3, IssuePosts(fake));    // NOT six
        Assert.Equal(2, CommentPosts(fake));  // session 2's comment, once — session 1's not re-posted
        Assert.Empty(blind.Result!.Created);

        // And a third pass, still blind, still changes nothing it has already said.
        var again = await mirror.ReconcileAsync("owner-gate S1").ConfigureAwait(true);
        Assert.False(again.Ran);
        Assert.Equal(3, IssuePosts(fake));
        Assert.Equal(2, CommentPosts(fake));
    }

    /// <summary>The map is not a cache in one process's head: a new process reloads it from run.db,
    /// which is the only reason it survives the once-mode exit that fires every boundary in this
    /// engine's shortest run.</summary>
    [Fact]
    public async Task TheMapIsReloadedByTheNextProcessAndStillBeatsAStaleListing()
    {
        SeedFirstSession();
        using var fake = new FakeGithub();
        using (var first = Mirror(fake))
            Assert.True((await first.ReconcileAsync("run start").ConfigureAwait(true)).Ok);

        // Two cards and the diary issue — every issue this run has put there, by key.
        Assert.Equal(3, _store.ReadGithubMap(RunId, Repo).Count(r => r.Kind == GithubMap.IssueKind));
        Assert.Contains(_store.ReadGithubMap(RunId, Repo), r => r.Key == "run:" + RunId);

        fake.ListsAreStale = true;
        SeedSecondSession();
        using var second = Mirror(fake);
        Assert.True((await second.ReconcileAsync("run start").ConfigureAwait(true)).Ok);

        Assert.Equal(3, IssuePosts(fake));
        Assert.Equal(2, CommentPosts(fake));
    }

    // ── the replay ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReplayFromCursorZeroMintsZeroDuplicateIssuesAndZeroDuplicateComments()
    {
        SeedFirstSession();
        SeedSecondSession();
        using var fake = new FakeGithub();

        using (var mirror = Mirror(fake))
            Assert.True((await mirror.ReconcileAsync("session 2 end").ConfigureAwait(true)).Ok);

        var issues = IssuePosts(fake);
        var comments = CommentPosts(fake);
        Assert.Equal(3, issues);
        Assert.Equal(2, comments);

        // Wind the cursor all the way back — the operator's "mirror this run again from scratch", and
        // the shape a restored database or a new destination has on its first pass.
        _store.WriteGithubCursor(RunId, Repo, 0, null);

        // A NEW mirror instance, because a replay in the field is a new process.
        using var replay = Mirror(fake);
        var pass = await replay.ReconcileAsync("replay").ConfigureAwait(true);

        Assert.True(pass.Ok);
        Assert.Equal(issues, IssuePosts(fake));
        Assert.Equal(comments, CommentPosts(fake));
        Assert.Empty(pass.Result!.Created);
    }

    [Fact]
    public async Task ANewProcessResumesFromThePersistedCursorRatherThanFromZeroOrFromNow()
    {
        SeedFirstSession();
        using var fake = new FakeGithub();

        long banked;
        using (var first = Mirror(fake))
        {
            Assert.True((await first.ReconcileAsync("session 1 end").ConfigureAwait(true)).Ok);
            banked = _store.ReadGithubCursor(RunId, Repo).Seq;
        }

        // The process dies here. A second one starts against the same database.
        SeedSecondSession();
        using var second = Mirror(fake);
        var pass = await second.ReconcileAsync("run start").ConfigureAwait(true);

        Assert.True(pass.Ran, "a resumed process must push the tail its predecessor never sent");
        Assert.True(pass.Cursor > banked);
        // Not from zero: the resumed pass does not re-create the issues the first process made.
        Assert.Equal(3, IssuePosts(fake));
        // Not from now either: session 2's comment is the one thing the dead process owed.
        Assert.Equal(2, CommentPosts(fake));
    }

    // ── shutdown ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The defect the live rig found: a run in once-mode returns from the loop the instant
    /// its session ends, and the teardown used to dispose the mirror while a fired pass was halfway
    /// through creating a board — one issue on GitHub out of three, no diary, and a cancellation
    /// where a real error belonged. Fire is fire-and-forget; DRAINING it is what makes the boundary
    /// pass land in THIS process rather than converge in the next one.</summary>
    [Fact]
    public async Task ADrainAtShutdownLetsTheFiredPassFinish()
    {
        SeedFirstSession();
        using var fake = new FakeGithub { Latency = TimeSpan.FromMilliseconds(40) };
        using var mirror = Mirror(fake);

        var inFlight = mirror.Fire("session 1 end");
        Assert.False(inFlight.IsCompleted, "the boundary must not have waited");

        await mirror.DrainAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);

        Assert.True(inFlight.IsCompleted);
        Assert.True((await inFlight.ConfigureAwait(true)).Ok);
        Assert.Equal(3, IssuePosts(fake));                                   // the WHOLE board, not a prefix
        Assert.True(_store.ReadGithubCursor(RunId, Repo).Seq > 0);
    }

    /// <summary>The second half of the same live finding. A run-start pass against the real API takes
    /// seconds; the session that follows takes one. Its session-end boundary lands while the first
    /// pass is still open, and the first design DROPPED it — so that session's events sat unmirrored
    /// until the next process. One coalesced follow-up is the fix: however many boundaries fire
    /// during a pass, exactly one more pass runs, and it carries everything they would have.</summary>
    [Fact]
    public async Task ABoundaryThatLandsDuringAPassIsCoalescedIntoAFollowUpNotDropped()
    {
        SeedFirstSession();
        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);

        // HELD at its first request, not merely slowed. Fire queues onto the thread pool, and a pass
        // that has not started holds no gate — with latency alone the boundary below could reach the
        // gate first, take it, and RUN, which is the opposite of the claim and is how this test went
        // red under the full suite's load. Awaiting arrival makes "a pass is in flight" observed.
        var arrived = fake.HoldNextRequest();
        var slow = mirror.Fire("run start");
        await arrived.ConfigureAwait(true);

        // The next session finishes while that pass is still walking the API.
        SeedSecondSession();
        var coalesced = await mirror.ReconcileAsync("session 2 end").ConfigureAwait(true);
        Assert.False(coalesced.Ran);
        Assert.Contains("coalesced", coalesced.Error, StringComparison.Ordinal);

        fake.Release();
        await mirror.DrainAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        Assert.True((await slow.ConfigureAwait(true)).Ok);

        // The follow-up ran and carried session 2 — both diary comments are up, in THIS process.
        Assert.Equal(2, CommentPosts(fake));
        Assert.Equal(3, IssuePosts(fake));
        var head = _store.ReadEventsAfter(RunId, 0).Max(e => e.Seq);
        Assert.Equal(head, _store.ReadGithubCursor(RunId, Repo).Seq);
    }

    [Fact]
    public async Task DrainingWithNothingInFlightCostsNothing()
    {
        SeedFirstSession();
        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);

        await mirror.DrainAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
        Assert.Empty(fake.Requests);
        Assert.Equal(0L, _store.ReadGithubCursor(RunId, Repo).Seq);
    }

    // ── nothing inbound ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AHumanEditingTheBoardOnGithubChangesNothingInTheRun()
    {
        SeedFirstSession();
        using var fake = new FakeGithub();
        using var mirror = Mirror(fake);
        Assert.True((await mirror.ReconcileAsync("session 1 end").ConfigureAwait(true)).Ok);

        var graphBefore = new TaskGraph();
        graphBefore.Fold(_store.ReadAllEvents(RunId));
        var statusBefore = graphBefore.Checkpoints().Single(c => c.TaskId == "KS9.2").Status;

        // A human drags the card: closes the issue and adds their own triage label.
        var number = fake.NumberOfTask("KS9.2");
        fake.Close(number);
        fake.AddLabel(number, "needs-discussion");

        SeedSecondSession();
        var pass = await mirror.ReconcileAsync("session 2 end").ConfigureAwait(true);
        Assert.True(pass.Ok);

        // Run state is untouched — the mirror pushed, it did not read anything back into the fold.
        var graphAfter = new TaskGraph();
        graphAfter.Fold(_store.ReadAllEvents(RunId));
        Assert.Equal("in_progress", graphAfter.Checkpoints().Single(c => c.TaskId == "KS9.2").Status);
        Assert.Equal("todo", statusBefore);

        // And the human's own label survived the correction: never-clobber cuts both ways.
        Assert.Contains("needs-discussion", fake.LabelsOf(number));
    }
}
