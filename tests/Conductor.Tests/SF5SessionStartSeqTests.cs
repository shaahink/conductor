using Conductor.Core.Events;
using Conductor.Core.Store;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SF5 (fix session 32) — the session-start marker must be DURABLE before an agent can claim.
///
/// <para>Why this file exists. <c>VerdictEngine.GraphClaimsDuringSession</c> answers "what was claimed
/// during THIS session?" by folding the event log twice: everything at or below the session's
/// <see cref="SessionStarted"/> seq is the PRE set, everything else is the claim. That makes the
/// marker's seq the boundary the whole SF0.2 guarantee rests on — and seq is NOT allocated when an
/// event is emitted. It is allocated at persist time, inside the transaction
/// (<c>PersistBatchLocked</c>, W1.1), off a 200ms drain loop.</para>
///
/// <para>The claim path is a different PROCESS. <c>conductor task --done</c> opens run.db itself and
/// writes straight through. So if the engine's drain is starved past that write — a loaded machine,
/// the full battery's parallel test hosts — the marker is stamped with a HIGHER seq than a claim that
/// happened AFTER it, the claim folds into the PRE set, and the session is credited with nothing.
/// That is the intermittent <c>SF0_2VerdictLiveTests.ClaimDuringAVerifySession</c> red: green when run
/// alone, red inside the 1717-test battery.</para>
///
/// <para>The first test below reproduces that inversion deterministically, so the failure is a
/// mechanism and not a theory. The second pins the fix: <c>SessionRunner</c> flushes the marker before
/// the agent process exists, which turns a timing hope into an ordering guarantee.</para>
/// </summary>
public sealed class SF5SessionStartSeqTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sf5-seq-" + Guid.NewGuid().ToString("N")[..8]);

    public SF5SessionStartSeqTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string DbPath => Path.Combine(_dir, "run.db");

    private static SessionStarted Marker(string runId) => new()
    {
        RunId = runId, SessionId = "2", Number = 2, StageId = "H0", Kind = "Verify", Attempt = 1, MaxAttempts = 8,
    };

    private static TaskStatusChanged Claim(string runId) => new()
    {
        RunId = runId, TaskId = "H0.2", Status = "done", Source = "agent", Evidence = "proof.md",
    };

    /// <summary>THE BUG. Seq is allocated by whoever PERSISTS first, so a marker still sitting in the
    /// engine's queue is stamped BELOW a claim another process already wrote — and the fold that
    /// defines "claimed during this session" then cannot see the claim at all.
    ///
    /// <para>Persist order is induced here by flushing the claim first, rather than by waiting on a
    /// starved 200ms drain to lose a race. Same inversion, no coin flip: a test about a timing bug is
    /// worth nothing if its own outcome is a timing question.</para></summary>
    [Fact]
    public void AMarkerPersistedAfterAClaim_IsStampedAboveIt_AndTheClaimBecomesInvisible()
    {
        var runId = Guid.NewGuid().ToString("N");

        using (var engine = Store(runId))
        using (var cli = Store(runId))          // the `conductor task --done` process, same run.db
        {
            engine.Emit(Marker(runId));         // emitted FIRST, still queued — seq only provisional

            cli.Emit(Claim(runId));
            cli.FlushEvents();                  // the claim process writes through and lands seq N

            engine.FlushEvents();               // the starved drain, catching up too late: seq N+1
        }

        var (markerSeq, claimSeq) = SeqsOf(runId);
        Assert.True(claimSeq < markerSeq,
            $"expected the inversion this test documents (claim {claimSeq} below marker {markerSeq})");

        // …and this is why it matters: nothing is credited to the session that did the work.
        Assert.Empty(ClaimsAfter(runId, markerSeq));
    }

    /// <summary>THE FIX. Flushing the marker before the agent can run makes the ordering a fact rather
    /// than a race — the claim is above it no matter how long the drain sleeps.</summary>
    [Fact]
    public void AMarkerFlushedBeforeTheAgentRuns_KeepsEveryLaterClaimAboveIt()
    {
        var runId = Guid.NewGuid().ToString("N");

        using (var engine = Store(runId))
        using (var cli = Store(runId))
        {
            engine.Emit(Marker(runId));
            engine.FlushEvents();               // what SessionRunner now does, before spawning anything

            cli.Emit(Claim(runId));
            cli.FlushEvents();
        }

        var (markerSeq, claimSeq) = SeqsOf(runId);
        Assert.True(claimSeq > markerSeq,
            $"claim {claimSeq} must sit above marker {markerSeq} — that ordering is the SF0.2 guarantee");
        Assert.Equal(["H0.2"], ClaimsAfter(runId, markerSeq));
    }

    /// <summary>SessionRunner emits the marker and MUST make it durable before the agent process it
    /// spawns can write a claim of its own. Asserted on the source because the alternative is a live
    /// orchestrator run whose verdict depends on a 200ms drain winning a process spawn — which is
    /// exactly the coin-flip this fix removes, and is no way to measure that it was removed.</summary>
    [Fact]
    public void SessionRunner_FlushesTheMarkerBeforeItSpawnsTheAgent()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Conductor.Core", "Orchestration", "SessionRunner.cs"));

        var emit = source.IndexOf("Emit(new SessionStarted", StringComparison.Ordinal);
        Assert.True(emit > 0, "SessionRunner no longer emits SessionStarted — this test needs rewriting, not deleting");

        var flush = source.IndexOf("FlushEvents()", emit, StringComparison.Ordinal);
        Assert.True(flush > 0, "SessionRunner emits SessionStarted but never flushes it — a claim from the "
            + "agent process can be stamped below the marker and be credited to nobody (SF0.2 bug #10)");

        var launch = source.IndexOf("RunHookAsync", emit, StringComparison.Ordinal);
        Assert.True(launch > 0, "expected the setup hook to still follow the marker");
        Assert.True(flush < launch, "the flush must come BEFORE anything that runs a child process");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A store scoped to the run, the way both the engine and the claim CLI open one.
    /// <c>SetRunId</c> matters: <c>Emit</c> stamps the store's run id over the event's own.</summary>
    private SqliteRunStore Store(string runId)
    {
        var store = new SqliteRunStore(DbPath, NullLogger<SqliteRunStore>.Instance);
        store.InitializeRun(runId, "seq-plan", _dir, "main", Conductor.Core.EngineStamp.Parse("1.0.0"));
        store.SetRunId(runId);
        return store;
    }

    private (long Marker, long Claim) SeqsOf(string runId)
    {
        using var read = new SqliteRunStore(DbPath, NullLogger<SqliteRunStore>.Instance);
        var events = read.ReadAllEvents(runId);
        var marker = Assert.Single(events.OfType<SessionStarted>());
        var claim = Assert.Single(events.OfType<TaskStatusChanged>());
        return (marker.Seq, claim.Seq);
    }

    /// <summary>The verdict engine's question, in miniature: which done-claims sit above the marker.</summary>
    private List<string> ClaimsAfter(string runId, long markerSeq)
    {
        using var read = new SqliteRunStore(DbPath, NullLogger<SqliteRunStore>.Instance);
        return read.ReadAllEvents(runId).OfType<TaskStatusChanged>()
            .Where(e => e.Seq > markerSeq && e.Status.Equals("done", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.TaskId).ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }
}
