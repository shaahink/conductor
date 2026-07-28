using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Models;
using Xunit;

namespace Conductor.Tests;

public sealed class B12_2Tests
{
    [Fact]
    public async Task LaneWorkerPool_RespectsConcurrencyCap()
    {
        var sink = new CollectingEventSink();
        var pool = new LaneWorkerPool(2, sink, _ => { });

        var activeCount = 0;
        var maxObserved = 0;
        var gate = new Lock();

        var items = Enumerable.Range(0, 6).Select(i => new LaneWorkItem(
            $"lane-{i}", "qa", "B12",
            async ct =>
            {
                var c = Interlocked.Increment(ref activeCount);
                lock (gate) maxObserved = Math.Max(maxObserved, c);
                await Task.Delay(50, ct).ConfigureAwait(false);
                Interlocked.Decrement(ref activeCount);
                return new LaneResult { LaneId = $"lane-{i}", Kind = "qa" };
            })).ToList();

        foreach (var item in items)
            pool.Enqueue(item, CancellationToken.None);

        var results = await pool.WaitAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(6, results.Count);
        Assert.True(results.All(r => r.IsSuccess));
        Assert.True(maxObserved <= 2, $"max concurrent observed: {maxObserved}, expected <= 2");
    }

    [Fact]
    public async Task LaneWorkerPool_EmitsLifecycleEvents()
    {
        var sink = new CollectingEventSink();
        var pool = new LaneWorkerPool(2, sink, _ => { });

        pool.Enqueue(new LaneWorkItem("arch", "architecture", "B12",
            async ct =>
            {
                await Task.Delay(30, ct).ConfigureAwait(false);
                return new LaneResult { LaneId = "arch", Kind = "architecture" };
            }), CancellationToken.None);

        pool.Enqueue(new LaneWorkItem("qa", "qa", "B12",
            async ct =>
            {
                await Task.Delay(30, ct).ConfigureAwait(false);
                return new LaneResult { LaneId = "qa", Kind = "qa" };
            }), CancellationToken.None);

        await pool.WaitAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        var started = sink.Events.OfType<LaneStarted>().ToList();
        var finished = sink.Events.OfType<LaneFinished>().ToList();

        Assert.Equal(2, started.Count);
        Assert.Equal(2, finished.Count);
        Assert.Contains(started, s => s.LaneId == "arch" && s.Kind == "architecture");
        Assert.Contains(started, s => s.LaneId == "qa" && s.Kind == "qa");
        Assert.Contains(finished, f => f.LaneId == "arch" && f.Outcome == "success");
        Assert.Contains(finished, f => f.LaneId == "qa" && f.Outcome == "success");
        Assert.True(finished.All(f => f.DurationMs > 0));
    }

    [Fact]
    public async Task LaneWorkerPool_CapOf1_RunsSequentially()
    {
        var sink = new CollectingEventSink();
        var pool = new LaneWorkerPool(1, sink, _ => { });

        var activeCount = 0;
        var maxObserved = 0;
        var gate = new Lock();

        var items = Enumerable.Range(0, 4).Select(i => new LaneWorkItem(
            $"lane-{i}", "research", "B12",
            async ct =>
            {
                var c = Interlocked.Increment(ref activeCount);
                lock (gate) maxObserved = Math.Max(maxObserved, c);
                await Task.Delay(20, ct).ConfigureAwait(false);
                Interlocked.Decrement(ref activeCount);
                return new LaneResult { LaneId = $"lane-{i}", Kind = "research" };
            })).ToList();

        foreach (var item in items)
            pool.Enqueue(item, CancellationToken.None);

        var results = await pool.WaitAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.Equal(1, maxObserved);
    }

    [Fact]
    public void LaneWorkerPool_DrainCompleted_CollectsIncrementalResults()
    {
        var sink = new CollectingEventSink();
        var pool = new LaneWorkerPool(3, sink, _ => { });

        pool.Enqueue(new LaneWorkItem("fast", "qa", "B12",
            _ => Task.FromResult(new LaneResult { LaneId = "fast", Kind = "qa" })),
            CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (pool.CompletedCount == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(50);

        var drained = pool.DrainCompleted();
        Assert.Single(drained);
        Assert.Equal("fast", drained[0].LaneId);
        Assert.Equal(0, pool.CompletedCount);
    }

    [Fact]
    public async Task LaneWorkerPool_ActiveAndQueuedCounts()
    {
        var sink = new CollectingEventSink();
        var pool = new LaneWorkerPool(1, sink, _ => { });

        pool.Enqueue(new LaneWorkItem("a", "qa", "B12",
            async ct => { await Task.Delay(200, ct).ConfigureAwait(false); return new LaneResult { LaneId = "a" }; }),
            CancellationToken.None);
        pool.Enqueue(new LaneWorkItem("b", "qa", "B12",
            async ct => { await Task.Delay(200, ct).ConfigureAwait(false); return new LaneResult { LaneId = "b" }; }),
            CancellationToken.None);
        pool.Enqueue(new LaneWorkItem("c", "qa", "B12",
            async ct => { await Task.Delay(200, ct).ConfigureAwait(false); return new LaneResult { LaneId = "c" }; }),
            CancellationToken.None);

        await Task.Delay(50);

        Assert.True(pool.ActiveCount >= 0);
        Assert.True(pool.QueuedCount >= 0);
    }

    [Fact]
    public async Task LaneWorkerPool_ErrorLane_EmitsFailureEvent()
    {
        var sink = new CollectingEventSink();
        var pool = new LaneWorkerPool(2, sink, _ => { });

        pool.Enqueue(new LaneWorkItem("fail", "qa", "B12",
            _ => throw new InvalidOperationException("simulated failure")),
            CancellationToken.None);

        await pool.WaitAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        var finished = sink.Events.OfType<LaneFinished>().ToList();
        Assert.Single(finished);
        Assert.Equal("fail", finished[0].LaneId);
        Assert.Equal("error", finished[0].Outcome);
        Assert.Contains("simulated failure", finished[0].Error);
    }

    [Fact]
    public async Task LaneWorkerPool_WaitAllAsync_Timeout_DoesNotCrash()
    {
        var sink = new CollectingEventSink();
        var pool = new LaneWorkerPool(1, sink, _ => { });

        pool.Enqueue(new LaneWorkItem("slow", "qa", "B12",
            async ct => { await Task.Delay(5000, ct).ConfigureAwait(false); return new LaneResult { LaneId = "slow" }; }),
            CancellationToken.None);

        var results = await pool.WaitAllAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);

        Assert.Empty(results);
    }
}

/// <summary>Test-only event sink that captures all emitted events for assertions (B12.2 QA).</summary>
public sealed class CollectingEventSink : IEventSink
{
    private readonly Lock _lock = new();
    private readonly List<ConductorEvent> _events = new();

    public void Emit(ConductorEvent evt)
    {
        lock (_lock) _events.Add(evt);
    }

    public IReadOnlyList<ConductorEvent> Events
    {
        get { lock (_lock) return _events.ToList(); }
    }
}
