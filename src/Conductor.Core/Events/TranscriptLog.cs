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
public sealed record TranscriptLine(long Seq, DateTimeOffset Ts, string? SessionId, string Kind, string Text)
{
    /// <summary>SC7.1 — the transcript's schema version. <c>2</c> is the structured era: a
    /// <c>tool</c> line carries <see cref="Tool"/> alongside its text. A line written before this
    /// checkpoint has no <c>v</c> at all and deserialises to <c>0</c>; <see cref="ReadV1OrV2"/> is the
    /// one place that turns such a line into a v1 line rather than a versionless one, so nothing
    /// downstream has to know the absence means anything.</summary>
    public int V { get; init; }

    /// <summary>SC7.1 — the structured payload of a <c>tool</c> line: name plus extracted fields, each
    /// value truncated on its own so the object is always complete JSON. Null on every other kind, and
    /// on a v1 tool line whose truncated blob could not be recovered.</summary>
    public ToolCall? Tool { get; init; }

    /// <summary>Reads a line at whatever schema it was written at. A v2 line is returned as-is; a v1
    /// line (no <c>v</c>, no <c>tool</c>) is stamped <c>V = 1</c> — honestly, so a reader can tell an
    /// old line from a new one — and, when it is a tool line, has whatever
    /// <see cref="Providers.ToolEventExtractor.FromLegacyText"/> can recover attached. The tool NAME
    /// always survives that recovery; the fields usually do not, which is the loss this schema
    /// exists to stop making.</summary>
    public static TranscriptLine ReadV1OrV2(TranscriptLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.V >= 2) return line;
        return line with
        {
            V = 1,
            Tool = line.Tool ?? (line.Kind == "tool" ? Providers.ToolEventExtractor.FromLegacyText(line.Text) : null),
        };
    }
}

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

    /// <summary>SC7.1: every line this writer produces is schema v2. Bumping it here rather than at
    /// each call site means a new writer can never forget to stamp it.</summary>
    public const int SchemaVersion = 2;

    /// <summary>Enqueue one transcript line. Non-blocking; safe to call from the orchestrator's
    /// synchronous drain loop exactly like <see cref="IEventSink.Emit"/>.</summary>
    /// <param name="tool">SC7.1 — the structured payload of a <c>tool</c> line. Passing it is what
    /// makes the file's <c>file_path</c>/<c>command</c> recoverable downstream; the text alone never
    /// was.</param>
    public void Append(string? sessionId, string kind, string text, ToolCall? tool = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var line = new TranscriptLine(Interlocked.Increment(ref _seq), _clock.GetUtcNow(), sessionId, kind, text)
        {
            V = SchemaVersion,
            Tool = tool,
        };
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
    /// same contract as <see cref="EventLog.ReadAll"/>. SC7.1: lines written before schema v2 come
    /// back through <see cref="TranscriptLine.ReadV1OrV2"/>, so a reader never has to ask which era
    /// wrote the file it is holding.</summary>
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
            if (line != null) result.Add(TranscriptLine.ReadV1OrV2(line));
        }
        return result;
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _drain.GetAwaiter().GetResult();
    }
}
