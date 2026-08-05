using System.Text.Json;

using Conductor.Core.Events;
using Conductor.Core.Store;
using Conductor.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Core.Watch;

/// <summary>
/// SF5.1 — the blocking half of <c>conductor watch</c>. Subscribes to a run by reading its event log
/// out of <c>run.db</c> and returns exactly once, on the wake set.
///
/// <para><b>The event log is a table, not a file.</b> This class first shipped tailing
/// <c>.conductor/events.jsonl</c> — a file the engine has not written since events moved into the
/// store (<c>IRunStore</c>: "events (replaces events.jsonl)"). Every unit test passed, because every
/// unit test wrote that file itself; the first live run against a real engine returned nothing at all,
/// because no engine on this machine produces it. The source of truth is
/// <see cref="IRunStore.ReadEventsAfter"/>, keyed by the run id, and so is the run state
/// (<c>state.json</c> is likewise gone). Read what the engine WRITES.</para>
///
/// <para>The wait still costs nothing that scales: one indexed <c>seq &gt; ?</c> query on a WAL
/// database every couple of seconds and a pid liveness probe. No model, no context, no accumulation —
/// which is the whole argument for this verb existing.</para>
///
/// <para>Arming is deliberately two steps. The backlog is folded through the classifier with its
/// wakes DISCARDED, so the watch starts knowing which stage the run is on, what the previous
/// session's outcome was and how many RED phase batteries a stage has already had — otherwise the
/// first event after arming could never complete a two-event pattern. Then the CURRENT run state is
/// checked, so a watch armed on a run that is already parked returns immediately instead of blocking
/// forever on an event that was emitted before anyone was listening.</para>
/// </summary>
public sealed class WatchLoop : IDisposable
{
    private readonly string _stateDir;
    private readonly string _planName;
    private readonly TimeSpan _poll;
    private readonly WatchWakeSet _wakeSet = new();
    private readonly string? _dbPath;
    private IRunStore? _store;
    private string? _runId;
    private long _lastSeq;
    private bool _engineWasLive;
    private int _engineMissedPolls;

    /// <summary>Polls the lock must report no engine before <see cref="WatchReason.EngineGone"/>
    /// fires. The lock file is rewritten, not held open, so a single missed read is not a death.</summary>
    public const int EngineGracePolls = 2;

    /// <param name="stateDir">The repo-local scratch dir — where the engine lock and the
    /// control-plane discovery file are published. Still in the working tree after K3.1.</param>
    /// <param name="runDbPath">K3.1: the run database, which is NOT under <paramref name="stateDir"/>
    /// any more. Null keeps the pre-K3.1 derivation for callers that have not been told otherwise.</param>
    public WatchLoop(string stateDir, string planName, TimeSpan poll, string? runDbPath = null)
    {
        _stateDir = stateDir;
        _planName = planName;
        _poll = poll;
        _dbPath = runDbPath;
    }

    public string DbPath => _dbPath ?? Path.Combine(_stateDir, StateHome.RunDbFileName);

    /// <summary>The run this watch attached to, or null before <see cref="Arm"/> (or if the state dir
    /// holds no run for this plan). Surfaced so the brief can name what it is watching.</summary>
    public string? RunId => _runId;

    /// <summary>The run state as it is on disk right now, or null if there is none to read. Read at
    /// wake time (never cached) so the brief describes the moment that fired, not the moment the
    /// watch was armed.</summary>
    public RunState? ReadState()
    {
        try
        {
            if (_store is null || string.IsNullOrEmpty(_runId)) return null;
            var json = _store.LoadRunStateJson(_runId);
            if (string.IsNullOrEmpty(json)) return null;
            return JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts);
        }
        catch (Exception ex) when (ex is IOException or JsonException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>True while an engine holds this plan's lock.</summary>
    public bool EngineAlive() => EngineLock.IsHeldByLiveEngine(_stateDir);

    /// <summary>Open the run, fold its backlog for context without waking on any of it, and position
    /// the cursor at the newest event. Returns how many events were folded (diagnostic).</summary>
    public int Arm()
    {
        _store ??= OpenStore();
        _runId = ResolveRunId();
        var folded = 0;
        if (_store is not null && !string.IsNullOrEmpty(_runId))
        {
            foreach (var evt in _store.ReadAllEvents(_runId))
            {
                _wakeSet.Observe(evt);
                if (evt.Seq > _lastSeq) _lastSeq = evt.Seq;
                folded++;
            }
        }
        _engineWasLive = EngineAlive();
        return folded;
    }

    /// <summary>The wake condition that is ALREADY true, or null. A park is a state, not just an
    /// event: it outlives the event that announced it, and a supervisor arriving late must still see
    /// it. <see cref="RunStatus.Paused"/> alone is not a wake — a human paused it on purpose — but a
    /// pause the session cap imposed is, because only a human raising the cap clears it.</summary>
    public static WatchWake? FromState(RunState? s)
    {
        if (s is null) return null;
        return s.Status switch
        {
            RunStatus.NeedsHuman => new WatchWake(WatchReason.NeedsHuman,
                s.AttentionReason ?? "run is parked at NeedsHuman", s.CurrentStage) { FiredFrom = "state" },
            RunStatus.AwaitingOwner => new WatchWake(WatchReason.OwnerPark,
                s.AttentionReason ?? $"awaiting the owner on stage {s.CurrentStage ?? "?"}", s.CurrentStage) { FiredFrom = "state" },
            RunStatus.Paused when s.ParkedBySessionCap => new WatchWake(WatchReason.NeedsHuman,
                s.AttentionReason ?? "parked by the session cap", s.CurrentStage) { FiredFrom = "state" },
            RunStatus.Completed or RunStatus.Aborted => new WatchWake(WatchReason.RunEnded,
                $"run {s.Status} after {s.SessionCounter} session(s)", s.CurrentStage) { FiredFrom = "state" },
            _ => null,
        };
    }

    /// <summary>Block until the wake set fires, the timeout expires, or the token is cancelled.
    /// Call <see cref="Arm"/> first. Never returns null: a cancelled watch throws, everything else
    /// has a reason.</summary>
    public async Task<WatchWake> RunAsync(TimeSpan? timeout, CancellationToken ct)
    {
        var deadline = timeout is { } t ? DateTimeOffset.UtcNow + t : (DateTimeOffset?)null;
        while (true)
        {
            if (Drain() is { } wake) return wake;

            // State is re-read every poll, not only at entry. The session-cap park (RunLoop sets
            // Paused + ParkedBySessionCap) emits NO event, so a watch armed before the cap was
            // reached would otherwise sleep through the one park a human has to clear by hand.
            // Events are drained first, so where both speak the event wins — it carries the detail
            // and the ordinal.
            if (FromState(ReadState()) is { } parked) return parked;

            // Liveness is checked AFTER the drain so a run that ended cleanly reports run-ended (the
            // reason) rather than engine-gone (the symptom): the loop emits RunFinished and only then
            // releases the lock.
            if (_engineWasLive && !EngineAlive())
            {
                if (++_engineMissedPolls >= EngineGracePolls)
                {
                    if (Drain() is { } last) return last;
                    if (FromState(ReadState()) is { } ended) return ended;
                    return new WatchWake(WatchReason.EngineGone,
                        "the engine that was running this plan is gone — no lock holder, and the run did not report finishing")
                    { FiredFrom = "liveness" };
                }
            }
            else
            {
                _engineMissedPolls = 0;
                if (!_engineWasLive) _engineWasLive = EngineAlive();
            }

            if (deadline is { } d && DateTimeOffset.UtcNow >= d)
                return new WatchWake(WatchReason.Timeout,
                    $"nothing on the wake set for {timeout!.Value.TotalMinutes:0.#} minute(s) — heartbeat")
                { FiredFrom = "timeout" };

            await Task.Delay(_poll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Read whatever the engine appended since the last call and classify it. Exposed for
    /// tests, so the drain can be driven event by event without a timer.</summary>
    public WatchWake? Drain()
    {
        // A run id can appear after arming: `conductor watch` is allowed to attach to a state dir whose
        // run has not written its first row yet, and re-resolving costs one indexed query.
        if (string.IsNullOrEmpty(_runId)) _runId = ResolveRunId();
        if (_store is null || string.IsNullOrEmpty(_runId)) return null;

        IReadOnlyList<ConductorEvent> batch;
        try { batch = _store.ReadEventsAfter(_runId, _lastSeq); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException or IOException)
        {
            // A read that lost a race with the engine's writer is a retry, not an outage: a watch that
            // died on a locked database would be a worse failure than the one it is supervising.
            return null;
        }

        foreach (var evt in batch)
        {
            if (evt.Seq > _lastSeq) _lastSeq = evt.Seq;
            if (_wakeSet.Observe(evt) is { } wake) return wake;
        }
        return null;
    }

    private IRunStore? OpenStore()
    {
        try
        {
            if (!File.Exists(DbPath)) return null;
            return new SqliteRunStore(DbPath, NullLogger<SqliteRunStore>.Instance);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? ResolveRunId()
    {
        _store ??= OpenStore();
        if (_store is null) return null;
        try { return _store.GetLatestRunId(_planName); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        (_store as IDisposable)?.Dispose();
        _store = null;
    }
}
