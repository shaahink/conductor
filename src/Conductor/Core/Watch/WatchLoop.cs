using System.Text.Json;

using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Core.Watch;

/// <summary>
/// SF5.1 — the blocking half of <c>conductor watch</c>. Subscribes to a run by tailing its
/// append-only event log and returns exactly once, on the wake set.
///
/// <para>The wait costs nothing that scales: a file-length check every couple of seconds, an
/// incremental read of only what was appended (<see cref="FileLineTail"/>), and a pid liveness probe.
/// No model, no context, no accumulation — which is the whole argument for this verb existing.</para>
///
/// <para>Arming is deliberately two steps. The backlog is folded through the classifier with its
/// wakes DISCARDED, so the watch starts knowing which stage the run is on, what the previous
/// session's outcome was and how many RED phase batteries a stage has already had — otherwise the
/// first event after arming could never complete a two-event pattern. Then the CURRENT run state is
/// checked, so a watch armed on a run that is already parked returns immediately instead of blocking
/// forever on an event that was emitted before anyone was listening.</para>
/// </summary>
public sealed class WatchLoop
{
    private readonly string _stateDir;
    private readonly TimeSpan _poll;
    private readonly WatchWakeSet _wakeSet = new();
    private readonly FileLineTail _tail = new();
    private bool _engineWasLive;
    private int _engineMissedPolls;

    /// <summary>Polls the lock must report no engine before <see cref="WatchReason.EngineGone"/>
    /// fires. The lock file is rewritten, not held open, so a single missed read is not a death.</summary>
    public const int EngineGracePolls = 2;

    public WatchLoop(string stateDir, TimeSpan poll)
    {
        _stateDir = stateDir;
        _poll = poll;
    }

    public string EventsPath => Path.Combine(_stateDir, "events.jsonl");

    public string StatePath => Path.Combine(_stateDir, "state.json");

    /// <summary>The run state as it is on disk right now, or null if there is none to read. Read at
    /// wake time (never cached) so the brief describes the moment that fired, not the moment the
    /// watch was armed.</summary>
    public RunState? ReadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return null;
            return RunState.LoadOrNew(StatePath, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>True while an engine holds this plan's lock.</summary>
    public bool EngineAlive() => EngineLock.IsHeldByLiveEngine(_stateDir);

    /// <summary>Fold the backlog for context without waking on any of it, and position the tail at
    /// the end of the log. Returns how many events were folded (diagnostic).</summary>
    public int Arm()
    {
        _tail.Follow(EventsPath);
        var folded = 0;
        foreach (var evt in Parse(_tail.ReadAppended())) { _wakeSet.Observe(evt); folded++; }
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
        if (FromState(ReadState()) is { } already) return already;

        var deadline = timeout is { } t ? DateTimeOffset.UtcNow + t : (DateTimeOffset?)null;
        while (true)
        {
            if (Drain() is { } wake) return wake;

            // Liveness is checked AFTER the drain so a run that ended cleanly reports run-ended (the
            // reason) rather than engine-gone (the symptom): the loop emits RunFinished and only then
            // releases the lock.
            if (_engineWasLive && !EngineAlive())
            {
                if (++_engineMissedPolls >= EngineGracePolls)
                {
                    if (Drain() is { } last) return last;
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

    /// <summary>Read whatever was appended since the last call and classify it. Exposed for tests, so
    /// the drain can be driven event by event without a timer.</summary>
    public WatchWake? Drain()
    {
        foreach (var evt in Parse(_tail.ReadAppended()))
            if (_wakeSet.Observe(evt) is { } wake) return wake;
        return null;
    }

    // A torn trailing line is normal (the writer flushes between events) and a line this build cannot
    // deserialise is normal too (an engine ahead of this binary may emit a type it does not know). A
    // watch that crashed on either would be a worse outage than the one it is supervising.
    private static IEnumerable<ConductorEvent> Parse(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            ConductorEvent? evt = null;
            try { evt = JsonSerializer.Deserialize(line, EventJsonContext.Default.ConductorEvent); }
            catch (JsonException) { }
            catch (NotSupportedException) { }
            if (evt != null) yield return evt;
        }
    }
}
