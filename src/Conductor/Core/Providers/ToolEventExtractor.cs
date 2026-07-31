using System.Text;
using System.Text.Json;
using Conductor.Core.Events;

namespace Conductor.Core.Providers;

/// <summary>
/// SC7.1 — turns a provider's raw tool-call arguments into a <see cref="ToolCall"/>: the tool name
/// plus the fields that actually carry meaning, each value truncated on its own.
/// </summary>
/// <remarks>
/// The capture this replaces was <c>ClaudeProvider.cs:74-76</c> —
/// <c>Trunc(inp.GetRawText(), 150)</c> — the whole argument object cut at 150 characters, mid-string
/// and mid-escape. That is lossy AT CAPTURE, not at display: a Write whose <c>file_path</c> sat past
/// character 150 had no recoverable path, so the Face, the timeline, the report and the verdict could
/// only ever show an escaped JSON fragment, and out-of-repo writes were invisible to all of them
/// (devcontext #10, #11).
/// <para>Extract first, truncate each VALUE: the structure always survives, only an individual
/// oversized value is shortened, and what is stored is always complete JSON.</para>
/// <para>Provider-independent on purpose. Claude's <c>tool_use.input</c> and opencode's
/// <c>part.state.input</c> are the same shape — an object of arguments — so both adapters call this
/// and the two feeds cannot drift into two vocabularies.</para>
/// </remarks>
public static class ToolEventExtractor
{
    /// <summary>Per-VALUE cap. A command or a prompt longer than this is cut with an ellipsis; the
    /// object around it stays intact, which is the difference this checkpoint exists to make.</summary>
    public const int MaxFieldChars = 400;

    /// <summary>How many fields one call may contribute. Canonical keys are emitted first, so the cap
    /// only ever drops the tail of an unusually wide unknown tool's arguments.</summary>
    public const int MaxFields = 8;

    /// <summary>Canonical keys, in the order a reader wants them. Anything the wire named differently
    /// is renamed into this vocabulary so every tool and every provider answers the same questions.</summary>
    private static readonly string[] CanonicalOrder =
    [
        "path", "command", "taskId", "status", "purpose", "pattern", "glob", "url", "query",
        "agent", "bytes", "lines", "linesAdded", "linesRemoved", "edits", "evidence", "prompt",
    ];

    /// <summary>Tools whose call WRITES to a path — the set the out-of-repo check in
    /// <c>SessionRunner.TrackActivity</c> consults. Curated rather than inferred from the name: a
    /// substring rule ("contains edit") would count a hypothetical <c>credit_check</c> as a file
    /// write, and a verdict note that names an innocent tool is worse than one that misses a rare
    /// one. MCP-prefixed variants normalise into these names.</summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write", "edit", "multiedit", "notebookedit", "notebook_edit",
        "write_file", "edit_file", "create_file", "str_replace", "str_replace_editor", "apply_patch",
    };

    /// <summary>Argument names the wire uses for the same thing, mapped into <see cref="CanonicalOrder"/>.
    /// <c>content</c>, <c>old_string</c> and <c>new_string</c> are deliberately absent: those are file
    /// BODIES, and storing 400 characters of one is neither the file nor useful. They become
    /// <c>bytes</c>/<c>lines</c>/<c>linesAdded</c>/<c>linesRemoved</c> instead.</summary>
    private static readonly Dictionary<string, string> Renames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["file_path"] = "path",
        ["filePath"] = "path",
        ["notebook_path"] = "path",
        ["filename"] = "path",
        ["cmd"] = "command",
        ["description"] = "purpose",
        ["subagent_type"] = "agent",
        ["run_in_background"] = "background",
    };

    /// <summary>Extracts <paramref name="input"/> (a tool's argument object) into a
    /// <see cref="ToolCall"/>. A non-object input (some wires send a bare string) is kept whole under
    /// <c>args</c> rather than dropped.</summary>
    public static ToolCall Extract(string? name, JsonElement input)
    {
        var toolName = string.IsNullOrWhiteSpace(name) ? "tool" : name!.Trim();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var normalized = Normalize(toolName);

        if (input.ValueKind != JsonValueKind.Object)
        {
            if (input.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                fields["args"] = Trunc(Scalar(input));
            return new ToolCall(toolName, fields);
        }

        var extras = new List<KeyValuePair<string, string>>();
        foreach (var prop in input.EnumerateObject())
        {
            var key = CanonicalKey(prop.Name, normalized);
            if (key == null) continue; // a body — folded into the derived counts below
            var value = prop.Value.ValueKind == JsonValueKind.Object || prop.Value.ValueKind == JsonValueKind.Array
                ? Shape(prop.Value)
                : Trunc(Scalar(prop.Value));
            if (value.Length == 0) continue;
            if (Array.IndexOf(CanonicalOrder, key) >= 0) fields[key] = value;
            else extras.Add(new KeyValuePair<string, string>(key, value));
        }

        AddDerivedCounts(input, fields);

        // Canonical keys first, in CanonicalOrder, then whatever else the tool sent, in wire order.
        var ordered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in CanonicalOrder)
            if (fields.TryGetValue(key, out var v)) ordered[key] = v;
        foreach (var (key, v) in fields)
            if (!ordered.ContainsKey(key)) ordered[key] = v;
        foreach (var (key, v) in extras)
        {
            if (ordered.Count >= MaxFields) break;
            if (!ordered.ContainsKey(key)) ordered[key] = v;
        }

        return new ToolCall(toolName, ordered);
    }

    /// <summary>SC7.1 back-compat: reconstructs what can be reconstructed from a v1 transcript line's
    /// text (<c>"Edit {\"file_path\":\"C:/co…"</c>). The name always survives — it was never inside the
    /// truncated blob. The fields survive only when the cut happened to land on a valid JSON boundary,
    /// which is exactly the loss this checkpoint removes going forward; a v1 line therefore reads back
    /// as name-only far more often than not, and says so honestly rather than inventing fields.</summary>
    public static ToolCall FromLegacyText(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return new ToolCall("tool", new Dictionary<string, string>(StringComparer.Ordinal));
        var brace = s.IndexOf('{', StringComparison.Ordinal);
        var name = (brace >= 0 ? s[..brace] : s).Trim();
        if (name.Length == 0) name = "tool";
        if (brace < 0) return new ToolCall(name, new Dictionary<string, string>(StringComparer.Ordinal));

        var payload = s[brace..].TrimEnd('\u2026').Trim();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return Extract(name, doc.RootElement);
        }
        catch (JsonException)
        {
            return new ToolCall(name, new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    /// <summary>True when a call by this name writes to the path in its <c>path</c> field.</summary>
    public static bool IsWrite(string? name) => name != null && WriteTools.Contains(Normalize(name));

    /// <summary>The structural one-liner stored as the transcript line's text: the tool name plus
    /// <c>key=value</c> pairs. Readable, and — unlike the raw blob it replaces — every value in it is
    /// whole. (SC7.2 turns this into the polished wire one-liner; SC7.1 only owes it structure.)</summary>
    public static string Render(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (call.Fields.Count == 0) return call.Name;
        var sb = new StringBuilder(call.Name);
        foreach (var (key, value) in call.Fields)
        {
            sb.Append(' ').Append(key).Append('=').Append(value);
        }
        return sb.ToString();
    }

    /// <summary>Strips an MCP server prefix (<c>mcp__conductor-tasks__task_update</c> →
    /// <c>task_update</c>) and lowercases, so the same logical tool matches whichever harness
    /// exposed it.</summary>
    internal static string Normalize(string name)
    {
        var s = name.Trim();
        var idx = s.LastIndexOf("__", StringComparison.Ordinal);
        if (idx >= 0 && idx + 2 < s.Length) s = s[(idx + 2)..];
        return s.ToLowerInvariant();
    }

    /// <summary>Maps a wire argument name into the canonical vocabulary, or null when the value is a
    /// file body that <see cref="AddDerivedCounts"/> turns into counts instead.</summary>
    private static string? CanonicalKey(string wireName, string normalizedTool)
    {
        if (wireName is "content" or "old_string" or "new_string" or "new_source" or "old_str" or "new_str")
            return null;
        if (Renames.TryGetValue(wireName, out var renamed)) return renamed;
        // `id` means a checkpoint only on a task verb. On anything else it is that tool's own id and
        // labelling it taskId would put a wrong checkpoint on the board's evidence trail.
        if (wireName is "id" or "task_id" or "taskId" or "checkpointId" && normalizedTool.Contains("task", StringComparison.Ordinal))
            return "taskId";
        // `path` on a search tool is a search ROOT, not a file that was touched — keeping the name
        // apart stops the out-of-repo write check from ever seeing a Grep as a write.
        if (wireName == "path" && normalizedTool is "grep" or "glob") return "in";
        return wireName;
    }

    /// <summary>Byte and line counts derived from the bodies deliberately not stored: a Write's
    /// <c>content</c>, an Edit's <c>old_string</c>/<c>new_string</c>, a MultiEdit's <c>edits</c>.
    /// These are what SC7.2's <c>(+12/-3)</c> is computed from.</summary>
    private static void AddDerivedCounts(JsonElement input, Dictionary<string, string> fields)
    {
        if (TryString(input, "content", out var content))
        {
            fields["bytes"] = Encoding.UTF8.GetByteCount(content).ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields["lines"] = LineCount(content).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        var added = TryString(input, "new_string", out var ns) ? LineCount(ns)
            : TryString(input, "new_str", out var ns2) ? LineCount(ns2) : (int?)null;
        var removed = TryString(input, "old_string", out var os) ? LineCount(os)
            : TryString(input, "old_str", out var os2) ? LineCount(os2) : (int?)null;
        if (added is { } a) fields["linesAdded"] = a.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (removed is { } r) fields["linesRemoved"] = r.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
            fields["edits"] = edits.GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? "";
            return true;
        }
        value = "";
        return false;
    }

    private static int LineCount(string s)
    {
        if (s.Length == 0) return 0;
        var n = 1;
        foreach (var c in s) if (c == '\n') n++;
        return n;
    }

    /// <summary>A nested object or array is reported by SHAPE, never by a cut-off fragment of itself —
    /// the same rule as everything else here: what is stored is complete or it is a count.</summary>
    private static string Shape(JsonElement el) => el.ValueKind == JsonValueKind.Array
        ? $"[{el.GetArrayLength()} items]"
        : $"{{{el.EnumerateObject().Count()} fields}}";

    private static string Scalar(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => el.GetRawText(),
    };

    private static string Trunc(string s)
    {
        var one = s.ReplaceLineEndings(" ").Trim();
        return one.Length <= MaxFieldChars ? one : one[..MaxFieldChars] + "\u2026";
    }
}
