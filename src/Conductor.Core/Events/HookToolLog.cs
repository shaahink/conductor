using System.Globalization;
using System.Text;
using System.Text.Json;
using Conductor.Core.Providers;

namespace Conductor.Core.Events;

/// <summary>
/// KS7.2 — the ground-truth channel for what a session's tools DID, written by the agent CLI's own
/// PostToolUse hook instead of scraped out of the assistant stream.
/// </summary>
/// <remarks>
/// <para>Transcript parsing was always a reconstruction. The stream is a rendering of the model's
/// turn: it carries the tool_use blocks the assistant emitted, in the shape the provider chose, and
/// conductor re-derives structure from it. That works until the provider changes a field name, until
/// a turn is cut short, or until a call the model made never reaches the text at all — and every one
/// of those has happened here. The hook fires INSIDE the agent's loop, once per completed tool call,
/// carrying the argument object verbatim. It is not a rendering of the call; it is the call.</para>
/// <para>Measured on claude 2.1.235 (the KS7.2 probe): the PostToolUse payload's <c>tool_input</c> is
/// the same object as stream-json's <c>tool_use.input</c>. That is the whole reason the two digests
/// can be made to match rather than merely to agree approximately — both go through
/// <see cref="ToolEventExtractor.Extract"/>, so any difference is a difference in DELIVERY, never in
/// vocabulary. <c>--include-hook-events</c> is a different thing and was checked: it emits
/// <c>system/hook_started</c> and <c>system/hook_response</c> lifecycle records with no
/// <c>tool_input</c> in them, so it cannot be this channel.</para>
/// <para>Written by a short-lived hook process, one per tool call, and the agent runs tool calls in
/// PARALLEL — so the append has to survive two processes reaching the file at the same instant. It
/// takes the file exclusively and retries briefly; a hook that cannot write must give up silently
/// rather than become the reason a session ends, and a lost line degrades the digest to what the
/// transcript already had.</para>
/// </remarks>
public static class HookToolLog
{
    /// <summary>Where one session's hook-delivered tool events live, under the run's state dir. Named
    /// by session number rather than by the agent's own session id because conductor knows the number
    /// when it writes the settings file and the hook does not — and a file per session is what keeps
    /// session N+1 from inheriting N's events.</summary>
    public static string PathFor(string stateDir, int sessionNumber) =>
        Path.Combine(stateDir, "hook-tools", sessionNumber.ToString("000", CultureInfo.InvariantCulture) + ".jsonl");

    /// <summary>Attempts before an append gives up. Each write is a few hundred bytes, so contention
    /// between parallel tool calls is measured in microseconds; this is a wide margin, not a guess at
    /// a real wait.</summary>
    private const int AppendAttempts = 25;

    /// <summary>
    /// Folds one hook stdin payload into a line. Two shapes, keyed by the hook event:
    /// a PreToolUse writes the CALL (<c>tool</c> + extracted fields), a PostToolUse writes only an
    /// OUTCOME (<c>id</c> + <c>ok</c> + <c>ms</c>) that <see cref="Read"/> merges back onto it.
    /// Anything else — SessionStart, Stop, a payload with no tool in it — is not this file's business
    /// and returns false.
    /// </summary>
    /// <remarks>
    /// The call is recorded on PRE and not on POST, and that is the measured heart of KS7.2 rather
    /// than a preference. On claude 2.1.235, <b>PostToolUse does not fire for a tool call that failed
    /// or was refused</b>: across two live runs the calls with no PostToolUse were exactly the calls
    /// whose <c>tool_result</c> carried <c>is_error</c>, with no exceptions in either direction. So a
    /// PostToolUse-only channel counts SUCCESSES while the transcript counts ATTEMPTS, and the two can
    /// never agree — a session that ran forty failing test commands would have reported none. PreToolUse
    /// fires for every call the model makes, including ones the permission layer then refuses, so it is
    /// the population the transcript already had; POST turns "attempted" into "attempted and worked".
    /// <para><c>tool_use_id</c> is the same string in the stream, in PreToolUse and in PostToolUse
    /// (verified across a whole probe run), which is what lets the two lines be merged exactly instead
    /// of matched by name and timing.</para>
    /// </remarks>
    public static async Task<bool> TryAppendFromHookPayloadAsync(string path, string? stdinJson, DateTime utc)
    {
        if (string.IsNullOrWhiteSpace(stdinJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(stdinJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var eventName = root.TryGetProperty("hook_event_name", out var evEl) && evEl.ValueKind == JsonValueKind.String
                ? evEl.GetString() : null;
            if (!root.TryGetProperty("tool_name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                return false;
            var id = root.TryGetProperty("tool_use_id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString() : null;

            var record = new StringBuilder();
            if (string.Equals(eventName, "PostToolUse", StringComparison.Ordinal))
            {
                // No id, no outcome: an outcome line that cannot be attached to a call is noise the
                // reader would have to guess about.
                if (string.IsNullOrEmpty(id)) return false;
                record.Append("{\"id\":").Append(JsonSerializer.Serialize(id));
                record.Append(",\"ok\":true");
                if (root.TryGetProperty("duration_ms", out var msEl) && msEl.ValueKind == JsonValueKind.Number)
                    record.Append(",\"ms\":").Append(msEl.GetRawText());
                record.Append('}');
                return await AppendAsync(path, record.ToString()).ConfigureAwait(false);
            }

            var input = root.TryGetProperty("tool_input", out var inputEl) ? inputEl : default;
            var call = ToolEventExtractor.Extract(nameEl.GetString(), input);
            record.Append("{\"utc\":").Append(JsonSerializer.Serialize(utc.ToString("O", CultureInfo.InvariantCulture)));
            record.Append(",\"tool\":").Append(JsonSerializer.Serialize(call.Name));
            if (!string.IsNullOrEmpty(id)) record.Append(",\"id\":").Append(JsonSerializer.Serialize(id));
            record.Append(",\"f\":").Append(JsonSerializer.Serialize(call.Fields));
            record.Append('}');
            return await AppendAsync(path, record.ToString()).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Cross-process append. Exclusive by design: two hook processes appending to the same
    /// file with a shared handle can interleave mid-line, and a half-written JSON line is worse than a
    /// missing one — the reader would have to guess which of the two it holds.</summary>
    private static async Task<bool> AppendAsync(string path, string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        for (var attempt = 0; attempt < AppendAttempts; attempt++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
                var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await using (writer.ConfigureAwait(false))
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                return true;
            }
            catch (IOException)
            {
                await Task.Delay(10 + (attempt * 2)).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(10 + (attempt * 2)).ConfigureAwait(false);
            }
        }
        return false;
    }

    /// <summary>One recorded call: what the model asked for, and whether it came back.</summary>
    /// <param name="Call">The extracted call — the same <see cref="ToolCall"/> the transcript path builds.</param>
    /// <param name="Id">The provider's <c>tool_use_id</c>, or null on a record that carried none.</param>
    /// <param name="Succeeded">True once a PostToolUse outcome line arrived for this id. False means
    /// the call was refused, errored, or the session died before it returned — the three are one
    /// answer here, because the hook surface does not distinguish them.</param>
    /// <param name="DurationMs">Wall time the agent measured for the call, when it reported one.</param>
    public sealed record Entry(ToolCall Call, string? Id, bool Succeeded, int? DurationMs);

    /// <summary>Reads one session's hook-delivered calls back, in the order they were attempted, each
    /// merged with its outcome line. A line that will not parse is skipped rather than failing the
    /// read: a torn tail (the session was killed mid-append) must cost that line and nothing else.</summary>
    public static IReadOnlyList<Entry> ReadEntries(string path)
    {
        var entries = new List<Entry>();
        if (!File.Exists(path)) return entries;
        IEnumerable<string> lines;
        try { lines = File.ReadLines(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return entries; }

        var outcomes = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() : null;
                var ms = root.TryGetProperty("ms", out var msEl) && msEl.ValueKind == JsonValueKind.Number
                    && msEl.TryGetInt32(out var parsed) ? parsed : (int?)null;

                if (!root.TryGetProperty("tool", out var toolEl) || toolEl.ValueKind != JsonValueKind.String)
                {
                    // An outcome line. It may land before or after its call in file order — parallel
                    // tool calls interleave — so it is collected and applied once everything is read.
                    if (!string.IsNullOrEmpty(id)) outcomes[id] = ms;
                    continue;
                }

                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                if (root.TryGetProperty("f", out var fEl) && fEl.ValueKind == JsonValueKind.Object)
                    foreach (var prop in fEl.EnumerateObject())
                        if (prop.Value.ValueKind == JsonValueKind.String)
                            fields[prop.Name] = prop.Value.GetString() ?? "";
                entries.Add(new Entry(new ToolCall(toolEl.GetString() ?? "tool", fields), id, false, null));
            }
            catch (JsonException) { }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Id is not { Length: > 0 } id || !outcomes.TryGetValue(id, out var ms)) continue;
            entries[i] = entries[i] with { Succeeded = true, DurationMs = ms };
        }
        return entries;
    }

    /// <summary>The calls alone, for a caller that only wants what the session attempted.</summary>
    public static IReadOnlyList<ToolCall> Read(string path) => ReadEntries(path).Select(e => e.Call).ToList();

    /// <summary>The digest this channel produces, or null when the channel delivered nothing — which
    /// is the signal to keep the transcript-derived one. Null and empty are deliberately the same
    /// answer here: a hook-less agent and a hook that never fired are indistinguishable from the
    /// engine's side, and both mean "the fallback is the only source there is".</summary>
    public static SessionDigest? BuildDigest(string path, string? repoRoot)
    {
        var entries = ReadEntries(path);
        if (entries.Count == 0) return null;
        var digest = new SessionDigest { Source = SessionDigest.HookSource };
        foreach (var entry in entries) digest.Add(entry.Call, repoRoot);
        // The one number the transcript could never supply. It counts every call that did not come
        // back — refused by the posture, exited nonzero, or cut off by a kill — because from the hook
        // surface those are the same absence, and reporting them as three would be inventing detail.
        digest.FailedCalls = entries.Count(e => !e.Succeeded);
        return digest;
    }
}
