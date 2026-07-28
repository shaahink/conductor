using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Conductor.Core.Events;

/// <summary>
/// One line of agent output (text/thinking/tool/etc. — same <c>Kind</c> vocabulary as
/// <see cref="AgentEvent"/>) captured for the F6 agent pane's live transcript + thinking stream.
/// Kept in a file separate from <c>events.jsonl</c> deliberately: transcript volume (every stdout
/// line, every reasoning paragraph) is orders of magnitude higher than structured transitions, and
/// <c>/state</c>/<c>/tasks</c>/<c>/events</c> already re-read the whole of events.jsonl per request —
/// mixing the two would regress every existing control-plane endpoint for a reader that doesn't
/// need it (only the new <c>/transcript/current</c> stream does, F5 stage-map entry).
/// </summary>
public sealed record TranscriptLine(long Seq, DateTimeOffset Ts, string? SessionId, string Kind, string Text);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(TranscriptLine))]
public sealed partial class TranscriptJsonContext : JsonSerializerContext;

/// <summary>
/// Append-only NDJSON writer for <c>.conductor/transcript.jsonl</c> — same single-writer, unbounded-
/// channel, flush-after-batch discipline as <see cref="EventLog"/> (BATON-BRIEF §3.2 pattern), so a
/// process kill mid-write never tears a line and the orchestrator's drain loop never blocks on disk.
/// </summary>
public sealed class TranscriptLog : IDisposable
{
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly Channel<TranscriptLine> _channel;
    private readonly Task _drain;
    private readonly ManualResetEventSlim _drainReady = new(false);
    private long _seq;

    /// <summary>Opens the run-scoped transcript feed. The file is shared by every run in this state
    /// dir, so when the last writer was a DIFFERENT run the old file is rotated away first — otherwise
    /// <c>/transcript/current</c> replays a previous era's session into the Face (the "ghost transcript"
    /// from the 2026-07-16 dogfood: an old run's session #1 rendered as if it were the live one). A
    /// resumed run (same runId) keeps its file so reconnecting clients' <c>?since=</c> stays valid.</summary>
    public static TranscriptLog OpenForRun(string path, string runId, TimeProvider? clock = null)
    {
        var marker = path + ".runid";
        try
        {
            var prev = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
            if (!string.Equals(prev, runId, StringComparison.Ordinal))
            {
                if (File.Exists(path)) File.Delete(path);
                File.WriteAllText(marker, runId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Rotation is best-effort: a locked file just means old lines stay visible one more run.
        }
        return new TranscriptLog(path, clock);
    }

    public TranscriptLog(string path, TimeProvider? clock = null)
    {
        _path = path;
        _clock = clock ?? TimeProvider.System;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _seq = File.Exists(path) ? CountLines(path) : 0;

        _channel = Channel.CreateUnbounded<TranscriptLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        _drain = Task.Run(DrainAsync);
        _drainReady.Wait(TimeSpan.FromSeconds(5));
    }

    public string FilePath => _path;

    /// <summary>Enqueue one transcript line. Non-blocking; safe to call from the orchestrator's
    /// synchronous drain loop exactly like <see cref="IEventSink.Emit"/>.</summary>
    public void Append(string? sessionId, string kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var line = new TranscriptLine(Interlocked.Increment(ref _seq), _clock.GetUtcNow(), sessionId, kind, text);
        _channel.Writer.TryWrite(line);
    }

    private async Task DrainAsync()
    {
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream);
        await using (writer.ConfigureAwait(false))
        {
            var reader = _channel.Reader;
            _drainReady.Set();
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                {
                    var json = JsonSerializer.Serialize(line, TranscriptJsonContext.Default.TranscriptLine);
                    await writer.WriteLineAsync(json).ConfigureAwait(false);
                }
                await writer.FlushAsync().ConfigureAwait(false);
            }
            await writer.FlushAsync().ConfigureAwait(false);
        }
    }

    private static long CountLines(string path)
    {
        long n = 0;
        foreach (var _ in File.ReadLines(path)) n++;
        return n;
    }

    /// <summary>Reads the whole transcript back, tolerating a trailing torn line (crash safety) —
    /// same contract as <see cref="EventLog.ReadAll"/>.</summary>
    public static IReadOnlyList<TranscriptLine> ReadAll(string path)
    {
        if (!File.Exists(path)) return [];
        var result = new List<TranscriptLine>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var lines = new List<string>();
        string? raw;
        while ((raw = reader.ReadLine()) != null) lines.Add(raw);
        for (var i = 0; i < lines.Count; i++)
        {
            var text = lines[i];
            if (string.IsNullOrWhiteSpace(text)) continue;
            TranscriptLine? line;
            try
            {
                line = JsonSerializer.Deserialize(text, TranscriptJsonContext.Default.TranscriptLine);
            }
            catch (JsonException) when (i == lines.Count - 1)
            {
                break;
            }
            if (line != null) result.Add(line);
        }
        return result;
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
#pragma warning disable MA0045 // IDisposable.Dispose is sync by contract; drain blocks only at the run boundary
        _drain.GetAwaiter().GetResult();
#pragma warning restore MA0045
    }
}
