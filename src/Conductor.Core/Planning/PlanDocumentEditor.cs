using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>
/// KS3.2 — the plan writer that stops destroying the file it edits.
///
/// <para>System.Text.Json has no comment-preserving writer: JsonSerializer, JsonDocument and
/// JsonNode all drop trivia, so every save that round-tripped the file through the model threw away
/// the operator's <c>//</c> comments, reordered nothing but reformatted everything, and — the
/// separate half of the trap — materialised every serializer default into a file that never carried
/// it (adding one stage used to change <c>progress.kind</c>, gate timeouts and <c>gatePolicy</c>).</para>
///
/// <para>This editor never re-serialises the document. It computes the semantic diff between two
/// model snapshots (both serialised with <see cref="PlanConfig.JsonOpts"/>, so defaults appear on
/// BOTH sides and cancel out) and splices exactly those changes into the raw bytes of the original
/// file. Comments, key order, indentation, unknown keys and the file's own BOM state all survive,
/// because the bytes that carry them are never touched.</para>
/// </summary>
public static partial class PlanDocumentEditor
{
    /// <summary>Persist <paramref name="plan"/> to its own <see cref="PlanConfig.PlanFilePath"/>,
    /// changing only what differs from the file's current content. Falls back to a whole-file
    /// rewrite only when there is no original to preserve (fresh path, unreadable or unparseable
    /// file) — the pre-KS3.2 behaviour, kept as the safety valve, never the path.</summary>
    public static void Save(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var path = plan.PlanFilePath;
        var after = JsonSerializer.SerializeToNode(plan, PlanConfig.JsonOpts)
            ?? throw new InvalidOperationException("plan serialised to null");

        byte[] raw;
        try
        {
            if (!File.Exists(path)) { WriteWhole(path, after); return; }
            raw = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteWhole(path, after);
            return;
        }

        var bom = HasUtf8Bom(raw);
        var utf8 = bom ? raw[3..] : raw;

        JsonNode? before = null;
        try
        {
            if (JsonSerializer.Deserialize<PlanConfig>(utf8, PlanConfig.JsonOpts) is { } beforePlan)
                before = JsonSerializer.SerializeToNode(beforePlan, PlanConfig.JsonOpts);
        }
        catch (JsonException) { /* unparseable original — nothing to preserve */ }
        if (before is null) { WriteWhole(path, after); return; }

        byte[] edited;
        try
        {
            edited = ApplyDiff(utf8, before, after);
            if (!SelfCheck(edited, after)) { WriteWhole(path, after); return; }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            WriteWhole(path, after);
            return;
        }

        WriteBytes(path, edited, bom);
    }

    /// <summary>Splice the <paramref name="before"/>→<paramref name="after"/> diff into
    /// <paramref name="path"/>, preserving everything the diff does not name — including the file's
    /// own BOM state. The caller owns validation; this only writes.</summary>
    public static void WriteEdited(string path, byte[] originalRaw, JsonNode before, JsonNode after)
    {
        ArgumentNullException.ThrowIfNull(originalRaw);
        var bom = HasUtf8Bom(originalRaw);
        var utf8 = bom ? originalRaw[3..] : originalRaw;
        WriteBytes(path, ApplyDiff(utf8, before, after), bom);
    }

    /// <summary>Write the payload with the original file's BOM state restored. A public sync write
    /// at a sync CLI/save boundary — the same idiom as <c>TrackerGenerator.Write</c> and
    /// <c>RunState.Save</c>.</summary>
    public static void WriteBytes(string path, byte[] utf8, bool bom)
    {
        if (!bom) { File.WriteAllBytes(path, utf8); return; }
        var withBom = new byte[utf8.Length + 3];
        withBom[0] = 0xEF;
        withBom[1] = 0xBB;
        withBom[2] = 0xBF;
        utf8.CopyTo(withBom, 3);
        File.WriteAllBytes(path, withBom);
    }

    /// <summary>The pure core: return <paramref name="utf8"/> edited so it parses to
    /// <paramref name="after"/>'s content wherever that differs from <paramref name="before"/>'s,
    /// and is byte-identical everywhere else.</summary>
    public static byte[] ApplyDiff(byte[] utf8, JsonNode before, JsonNode after)
    {
        ArgumentNullException.ThrowIfNull(utf8);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var root = ParseSpans(utf8);
        var nl = Array.IndexOf(utf8, (byte)'\r') >= 0 ? "\r\n" : "\n";
        var edits = new List<Edit>();
        DiffValue(before, after, root, utf8, nl, edits);
        return Splice(utf8, edits);
    }

    /// <summary>String overload for tests and diagnostics.</summary>
    public static string ApplyDiff(string originalText, JsonNode before, JsonNode after)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        return Encoding.UTF8.GetString(ApplyDiff(Encoding.UTF8.GetBytes(originalText), before, after));
    }

    /// <summary>Lines carrying a <c>//</c> or <c>/* */</c> comment — counted with string awareness
    /// so a URL inside a value is not mistaken for one. These are the lines this editor exists to
    /// keep; SC3.2 counted them to apologise for dropping them.</summary>
    public static int CountCommentLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = 0;
        var inBlock = false;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var hasComment = false;
            var inString = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                var next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (inBlock)
                {
                    hasComment = true;
                    if (c == '*' && next == '/') { inBlock = false; i++; }
                }
                else if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '/' && next == '/') { hasComment = true; break; }
                else if (c == '/' && next == '*') { hasComment = true; inBlock = true; i++; }
            }
            if (hasComment) count++;
        }
        return count;
    }

    internal static bool HasUtf8Bom(byte[] raw) =>
        raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;

    // ---------------------------------------------------------------- the diff

    private static void DiffValue(JsonNode? before, JsonNode? after, SpanNode file, byte[] utf8, string nl, List<Edit> edits)
    {
        if (NodeEquals(before, after)) return;
        if (before is JsonObject bo && after is JsonObject ao && file.Props is not null)
            DiffObject(bo, ao, file, utf8, nl, edits);
        else if (before is JsonArray ba && after is JsonArray aa && file.Items is not null)
            DiffArray(ba, aa, file, utf8, nl, edits);
        else
            edits.Add(new Edit(file.Start, file.End, Render(after, LineIndent(utf8, file.Start), nl)));
    }

    private static void DiffObject(JsonObject before, JsonObject after, SpanNode file, byte[] utf8, string nl, List<Edit> edits)
    {
        List<(string Key, JsonNode? Value)>? adds = null;
        foreach (var (key, aVal) in after)
        {
            var hasB = before.TryGetPropertyValue(key, out var bVal);
            if (hasB && NodeEquals(bVal, aVal)) continue;
            if (FindProp(file, key) is { } p)
            {
                if (hasB) DiffValue(bVal, aVal, p.Value, utf8, nl, edits);
                else edits.Add(new Edit(p.Value.Start, p.Value.End, Render(aVal, LineIndent(utf8, p.Value.Start), nl)));
            }
            else
            {
                (adds ??= []).Add((key, aVal));
            }
        }
        foreach (var (key, _) in before)
        {
            if (after.ContainsKey(key)) continue;
            if (FindProp(file, key) is { } p)
                edits.Add(DeleteMember(utf8, p.NameStart, p.Value.End));
        }
        if (adds is not null) InsertProps(file, adds, utf8, nl, edits);
    }

    private static void DiffArray(JsonArray before, JsonArray after, SpanNode file, byte[] utf8, string nl, List<Edit> edits)
    {
        var items = file.Items!;
        if (items.Count != before.Count)
        {
            // The file disagrees with its own parse — should be unreachable; rewrite this array whole.
            edits.Add(new Edit(file.Start, file.End, Render(after, LineIndent(utf8, file.Start), nl)));
            return;
        }

        if (after.Count >= before.Count)
        {
            for (var i = 0; i < before.Count; i++) DiffValue(before[i], after[i], items[i], utf8, nl, edits);
            if (after.Count > before.Count)
                AppendItems(file, [.. after.Skip(before.Count)], utf8, nl, edits);
            return;
        }

        // Fewer items than before: prefer pure deletion — everything kept stays byte-identical.
        var keep = new bool[before.Count];
        var ai = 0;
        for (var bi = 0; bi < before.Count; bi++)
        {
            if (ai < after.Count && NodeEquals(before[bi], after[ai])) { keep[bi] = true; ai++; }
        }
        if (ai == after.Count)
        {
            for (var bi = 0; bi < before.Count; bi++)
            {
                if (!keep[bi]) edits.Add(DeleteMember(utf8, items[bi].Start, items[bi].End));
            }
            return;
        }

        // Mixed change + removal (rare): rewrite in place for the shared prefix, delete the tail.
        for (var i = 0; i < after.Count; i++) DiffValue(before[i], after[i], items[i], utf8, nl, edits);
        for (var i = after.Count; i < before.Count; i++) edits.Add(DeleteMember(utf8, items[i].Start, items[i].End));
    }

    /// <summary>Structural equality with deterministic value comparison: both sides come from the
    /// same serializer, so equal values always render to equal JSON text.</summary>
    internal static bool NodeEquals(JsonNode? x, JsonNode? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        if (x is JsonObject xo)
        {
            if (y is not JsonObject yo || xo.Count != yo.Count) return false;
            foreach (var (k, v) in xo)
            {
                if (!yo.TryGetPropertyValue(k, out var w) || !NodeEquals(v, w)) return false;
            }
            return true;
        }
        if (x is JsonArray xa)
        {
            if (y is not JsonArray ya || xa.Count != ya.Count) return false;
            for (var i = 0; i < xa.Count; i++)
            {
                if (!NodeEquals(xa[i], ya[i])) return false;
            }
            return true;
        }
        if (y is JsonObject or JsonArray) return false;
        return string.Equals(x.ToJsonString(), y.ToJsonString(), StringComparison.Ordinal);
    }

    /// <summary>Exact-name match first, then case-insensitive — a file that spells a key in another
    /// case still gets its edit ON the existing key, never beside it.</summary>
    private static (string Name, int NameStart, SpanNode Value)? FindProp(SpanNode obj, string key)
    {
        foreach (var p in obj.Props!)
        {
            if (string.Equals(p.Name, key, StringComparison.Ordinal)) return p;
        }
        foreach (var p in obj.Props!)
        {
            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    /// <summary>Whether the edited bytes deserialise back to exactly the intended model content.
    /// The net under the span arithmetic: if this says no, the caller falls back to the old
    /// whole-file write rather than persisting a file that quietly means something else.</summary>
    private static bool SelfCheck(byte[] edited, JsonNode after)
    {
        try
        {
            if (JsonSerializer.Deserialize<PlanConfig>(edited, PlanConfig.JsonOpts) is not { } reparsed) return false;
            return NodeEquals(JsonSerializer.SerializeToNode(reparsed, PlanConfig.JsonOpts), after);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The legacy whole-document writer (BOM'd UTF-8, canonical serialisation, trivia
    /// gone). Only the fallbacks above reach it — never the preserving path.</summary>
    public static void WriteWhole(string path, JsonNode after) =>
        File.WriteAllText(path, after.ToJsonString(PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
}
