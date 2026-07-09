using System.Collections.Concurrent;
using System.Diagnostics;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// Bounded worker pool for Tier A read-only analysis lanes (B12.2). Enforces a configurable
/// concurrency cap via <see cref="SemaphoreSlim"/> and emits <see cref="LaneStarted"/> /
/// <see cref="LaneFinished"/> lifecycle events. The pool is FIFO — lanes execute in enqueue order,
/// up to <see cref="LimitsConfig.MaxConcurrentLanes"/> at a time.
/// </summary>
public sealed class LaneWorkerPool
{
    private readonly int _maxConcurrency;
    private readonly SemaphoreSlim _semaphore;
    private readonly IEventSink _events;
    private readonly Action<string> _log;
    private readonly ConcurrentBag<LaneResult> _completed = new();
    private int _activeCount;
    private int _queuedCount;
    private readonly Lock _lock = new();
    private readonly List<Task> _tasks = new();

    public LaneWorkerPool(int maxConcurrency, IEventSink events, Action<string> log)
    {
        _maxConcurrency = maxConcurrency > 0 ? maxConcurrency : 1;
        _semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
        _events = events;
        _log = log;
    }

    public int ActiveCount => Volatile.Read(ref _activeCount);
    public int QueuedCount => Volatile.Read(ref _queuedCount);
    public int CompletedCount => _completed.Count;

    /// <summary>Enqueue a lane for execution. Non-blocking — returns immediately.
    /// The lane will run when a concurrency slot is available.</summary>
    public void Enqueue(LaneWorkItem item, CancellationToken ct)
    {
        Interlocked.Increment(ref _queuedCount);
        var t = ExecuteAsync(item, ct);
        lock (_lock) { _tasks.Add(t); }
    }

    /// <summary>Collect any lanes that have completed since the last drain.</summary>
    public IReadOnlyList<LaneResult> DrainCompleted()
    {
        var results = new List<LaneResult>();
        while (_completed.TryTake(out var r))
            results.Add(r);
        // Also remove completed tasks from the tracking list
        lock (_lock)
        {
            _tasks.RemoveAll(t => t.IsCompleted);
        }
        return results;
    }

    /// <summary>Wait up to <paramref name="timeout"/> for all enqueued lanes to complete,
    /// collect results, and return them. Any lane not finished by the timeout continues in
    /// the background but is not returned here.</summary>
    public async Task<IReadOnlyList<LaneResult>> WaitAllAsync(TimeSpan timeout, CancellationToken ct)
    {
        Task[] snapshot;
        lock (_lock) { snapshot = _tasks.ToArray(); }

        if (snapshot.Length == 0) return [];

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(timeout);
        try
        {
            await Task.WhenAll(snapshot).WaitAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }

        return DrainCompleted();
    }

    private async Task ExecuteAsync(LaneWorkItem item, CancellationToken ct)
    {
        Interlocked.Decrement(ref _queuedCount);
        await _semaphore.WaitAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _activeCount);

        _events.Emit(new LaneStarted
        {
            LaneId = item.LaneId,
            Kind = item.Kind,
            StageId = item.StageId,
        });
        _log($"lane '{item.LaneId}' ({item.Kind}) started (active: {ActiveCount}/{_maxConcurrency})");

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await item.Work(ct).ConfigureAwait(false);
            sw.Stop();

            var outcome = result.IsSuccess ? "success" : "failure";
            _events.Emit(new LaneFinished
            {
                LaneId = item.LaneId,
                Kind = item.Kind,
                Outcome = outcome,
                Error = result.Error,
                DurationMs = sw.ElapsedMilliseconds,
            });
            _log($"lane '{item.LaneId}' completed → {outcome} ({sw.ElapsedMilliseconds}ms)");

            _completed.Add(result);
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            _events.Emit(new LaneFinished
            {
                LaneId = item.LaneId,
                Kind = item.Kind,
                Outcome = "cancelled",
                DurationMs = sw.ElapsedMilliseconds,
            });
            _log($"lane '{item.LaneId}' cancelled");

            _completed.Add(new LaneResult { LaneId = item.LaneId, Kind = item.Kind, Error = "cancelled" });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _events.Emit(new LaneFinished
            {
                LaneId = item.LaneId,
                Kind = item.Kind,
                Outcome = "error",
                Error = ex.Message,
                DurationMs = sw.ElapsedMilliseconds,
            });
            _log($"lane '{item.LaneId}' error: {ex.Message}");

            _completed.Add(new LaneResult { LaneId = item.LaneId, Kind = item.Kind, Error = ex.Message });
        }
        finally
        {
            Interlocked.Decrement(ref _activeCount);
            _semaphore.Release();
        }
    }
}

/// <summary>A single lane work item enqueued into the <see cref="LaneWorkerPool"/> (B12.2).</summary>
public sealed record LaneWorkItem(string LaneId, string Kind, string? StageId, Func<CancellationToken, Task<LaneResult>> Work);
