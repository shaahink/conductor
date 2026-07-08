using System.Text.Json;
using System.Threading.Channels;

namespace Conductor.Core.Events;

/// <summary>Where the orchestrator publishes its transition events. Abstracted so a dry-run (or a
/// unit test) can drop them via <see cref="NullEventSink"/> without touching disk.</summary>
public interface IEventSink
{
    /// <summary>Enqueue an event. Non-blocking and thread-safe; envelope fields are stamped by the sink.</summary>
    void Emit(ConductorEvent evt);
}

/// <summary>No-op sink (dry-run / tests): the run still exercises every emission site, nothing is written.</summary>
public sealed class NullEventSink : IEventSink
{
    public static readonly NullEventSink Instance = new();
    private NullEventSink() { }
    public void Emit(ConductorEvent evt) { }
}

/// <summary>
/// Append-only NDJSON writer for <c>.conductor/events.jsonl</c> — the B2 event-sourced spine.
/// A single background task drains an unbounded channel and appends one compact JSON line per event,
/// so the synchronous orchestrator never blocks on I/O and lines are never interleaved or torn
/// (single-writer discipline, BATON-BRIEF §3.2 / stage trap). The OS buffer is flushed after every
/// drained batch (survives a process kill with no partial line) and fsync'd on dispose. The log is
/// emitted alongside <c>state.json</c>; if the tail is ever truncated by power loss the fold tolerates
/// a trailing partial line and <c>state.json</c> remains the fast-load cache (additive discipline).
/// </summary>
public sealed class EventLog : IEventSink, IAsyncDisposable, IDisposable
{
    private readonly string _path;
    private readonly string _runId;
    private readonly TimeProvider _clock;
    private readonly Channel<ConductorEvent> _channel;
    private readonly Task _drain;
    private readonly ManualResetEventSlim _drainReady = new(false);
    private long _seq;

    public EventLog(string path, string runId, TimeProvider? clock = null)
    {
        _path = path;
        _runId = runId;
        _clock = clock ?? TimeProvider.System;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Continue the sequence across restarts so a resumed run's events stay monotonically ordered.
        _seq = File.Exists(path) ? CountLines(path) : 0;

        _channel = Channel.CreateUnbounded<ConductorEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _drain = Task.Run(DrainAsync);

        // Block briefly until the drain has opened the file and entered its read loop.
        // This eliminates the scheduling race that made live-read tests flaky (~50% on CI).
        _drainReady.Wait(TimeSpan.FromSeconds(5));
    }

    public string FilePath => _path;

    public void Emit(ConductorEvent evt)
    {
        var stamped = evt with
        {
            Seq = Interlocked.Increment(ref _seq),
            Ts = _clock.GetUtcNow(),
            RunId = _runId,
        };
        // Unbounded channel → TryWrite only fails once the writer is completed (post-dispose); dropping
        // a late event during teardown is intended, not a swallowed failure.
        _channel.Writer.TryWrite(stamped);
    }

    private async Task DrainAsync()
    {
        // The stream/writer live only on this dedicated drain thread. The StreamWriter owns the
        // FileStream (disposes + flushes it), so a single `await using` covers both; `stream` stays a
        // bare local we can fsync before the writer tears it down.
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream);
        await using (writer.ConfigureAwait(false))
        {
            var reader = _channel.Reader;
            _drainReady.Set(); // signal constructor: file open, reader ready
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var evt))
                {
                    var line = JsonSerializer.Serialize(evt, EventJsonContext.Default.ConductorEvent);
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
                // Caught up → push the batch to the OS so a process kill can't leave a torn line.
                await writer.FlushAsync().ConfigureAwait(false);
            }
            await writer.FlushAsync().ConfigureAwait(false);
            Fsync(stream); // durable to disk at the run boundary
        }
    }

    // Isolated so the intentional blocking fsync runs on the drain thread, not inside the async
    // state machine (there is no async flush-to-disk API).
    private static void Fsync(FileStream stream) => stream.Flush(flushToDisk: true);

    private static long CountLines(string path)
    {
        long n = 0;
        foreach (var _ in File.ReadLines(path)) n++;
        return n;
    }

    /// <summary>Reads the whole log back into typed events, tolerating a trailing torn line (crash
    /// safety). Used by the round-trip test and, later, the fold/replay projections (B2.2/B2.3).</summary>
    public static IReadOnlyList<ConductorEvent> ReadAll(string path)
    {
        if (!File.Exists(path)) return [];
        var events = new List<ConductorEvent>();
        var lines = ReadAllLinesShared(path);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            ConductorEvent? evt;
            try
            {
                evt = JsonSerializer.Deserialize(line, EventJsonContext.Default.ConductorEvent);
            }
            catch (JsonException) when (i == lines.Count - 1)
            {
                break; // trailing partial line from an interrupted flush — safe to ignore
            }
            if (evt != null) events.Add(evt);
        }
        return events;
    }

    // Crash recovery (RecoverFromCrash) reads events.jsonl while THIS process's drain writer still
    // holds it open (FileAccess.Write, FileShare.Read). File.ReadAllLines opens with FileShare.Read,
    // which excludes the existing Write handle → a sharing violation. Opening with FileShare.ReadWrite
    // lets the recovery read coexist with the live writer (regression proven by EventLogTests).
    private static List<string> ReadAllLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) != null) lines.Add(line);
        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _drain.ConfigureAwait(false); // observes any drain fault (no silent swallow, A15)
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _drain.GetAwaiter().GetResult(); // blocks only at the run boundary, never in a hot path
    }
}
