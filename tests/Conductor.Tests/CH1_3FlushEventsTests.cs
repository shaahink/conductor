using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Store;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// CH1.3 — <see cref="SqliteRunStore.FlushEvents"/> means what its doc comment says: when it
/// returns, everything emitted before the call is readable.
///
/// <para>It did not. <c>FlushEvents</c> drains <c>_eventQueue</c> on the caller's thread while
/// <c>DrainLoopAsync</c> drains the same queue every 200ms, and the two can interleave: the drain
/// loop dequeues a batch and is then somewhere between <c>TryDequeue</c> and <c>PersistBatch</c>,
/// so <c>FlushEvents</c> finds an EMPTY queue and returns at once — with that event still in a
/// local list in another thread, not in the database. Every event still lands exactly once, which
/// is what the old comment checked; the broken promise was the read-after-flush one, and it is the
/// promise the control plane's "respond only once it is durably readable" depends on.</para>
///
/// <para>This is where CI and the local battery disagreed. Measured on GitHub's windows runner
/// 2026-08-26 at 151b4293: <c>KS1_2StagesFromFoldTests.DerivedStatusMatchesTheStatusSurface</c>
/// failed 'Expected 0, Actual 1' — it reads the events INSIDE the <c>using</c> (missing the
/// in-flight one, so the surface counted no attempts) and the archive AFTER dispose, whose final
/// drain had by then persisted it. One failure in 3506, on a loaded runner, never locally.</para>
///
/// <para>The assertion here is the PROPERTY — count in, count out, every time — not the shape of
/// any one interleaving, because the interleaving is what a faster or slower machine changes.</para>
/// </summary>
public sealed class CH1_3FlushEventsTests : IDisposable
{
    private readonly string _tmp;

    public CH1_3FlushEventsTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-ch13-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    /// <summary>The race made deterministic instead of waited for. Racing it is hopeless on a fast
    /// machine — four attempts here (tight emit/flush loops, single-core affinity, a thread holding
    /// the persist gate) all stayed green while GitHub's runner failed twice in a row — so the
    /// window is WIDENED through <c>DrainWindowProbe</c> and the interleaving is then exact: the
    /// drain loop is holding this event, dequeued and uncommitted, at the moment the flush arrives.
    ///
    /// <para>Verified to be a real test and not a green-by-construction one: with the probe in place
    /// and the gate removed, this fails, and so does
    /// <c>KS1_2StagesFromFoldTests.DerivedStatusMatchesTheStatusSurface</c> with CI's exact
    /// 'Expected 0, Actual 1' at its line 175. See
    /// <c>.conductor/evidence/CH1/ch1-3-flushevents-race-proof.md</c>.</para></summary>
    [Fact]
    public void AFlushWaitsForABatchTheDrainLoopHasTakenButNotYetCommitted()
    {
        var db = Path.Combine(_tmp, "flush", "run.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        const string runId = "run-ch13-flush";

        IReadOnlyList<ConductorEvent> readable;
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(runId, "core", _tmp, "master", EngineStamp.Parse("0.3.1-alpha+test"));
            store.SetRunId(runId);

            // The drain loop now sits on every batch it has taken, uncommitted, for 600ms.
            store.DrainWindowProbe = () => Thread.Sleep(600);

            store.Emit(new SessionStarted { Number = 1, StageId = "S1", Kind = "deliver" });

            // Comfortably more than the loop's 200ms cadence and comfortably less than the probe:
            // when this returns, the event is out of the queue and not yet in the database.
            Thread.Sleep(300);

            store.FlushEvents();
            readable = store.ReadAllEvents(runId);
        }

        SqliteConnection.ClearAllPools();

        Assert.Contains(readable, e => e is SessionStarted { Number: 1 });
    }

    /// <summary>The same promise under the contention the control plane actually creates: several
    /// threads emitting and flushing at once. A flush may only speak for its OWN thread's event —
    /// another thread's may legitimately still be in flight — so the property asserted is the one
    /// that must hold regardless of interleaving: no event is ever lost, and the total after the
    /// last flush is the total emitted.</summary>
    [Fact]
    public void ConcurrentEmittersDoNotLoseEventsAcrossAFlush()
    {
        var db = Path.Combine(_tmp, "concurrent", "run.db");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        const string runId = "run-ch13-concurrent";
        const int threads = 4;
        const int perThread = 150;

        int readable;
        using (var store = new SqliteRunStore(db, NullLogger<SqliteRunStore>.Instance))
        {
            store.InitializeRun(runId, "core", _tmp, "master", EngineStamp.Parse("0.3.1-alpha+test"));
            store.SetRunId(runId);

            Parallel.For(0, threads, t =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    store.Emit(new SessionStarted { Number = (t * perThread) + i + 1, StageId = "S1", Kind = "deliver" });
                    store.FlushEvents();
                }
            });

            store.FlushEvents();
            readable = store.ReadAllEvents(runId).Count;
        }

        SqliteConnection.ClearAllPools();

        Assert.Equal(threads * perThread, readable);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_tmp, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
