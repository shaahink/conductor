using Conductor.Core.Events;

namespace Conductor.Core.Providers;

/// <summary>
/// SC7.2 — turns a structured <see cref="ToolCall"/> into the ONE LINE a human reads on the wire:
/// <c>Edit LibrarySurfaceRenderer.cs (+12/-3)</c> · <c>Bash dotnet build src/DevContext.Mcp</c> ·
/// <c>conductor task_update G1.1 -&gt; done</c> · <c>bg_start "G1.1 full solution build"</c>.
/// </summary>
/// <remarks>
/// This is the display half of SC7. SC7.1 stopped the loss at capture — the tool's real
/// <c>file_path</c> and <c>command</c> now survive as fields instead of being cut out of a 150-char
/// argument blob — but the line it emitted was still the structural
/// <c>Edit path=C:/code/… linesAdded=12 linesRemoved=3</c>: complete, and nobody's idea of readable.
/// The Face, the timeline and the report all render that string verbatim, so the last hop from
/// "recoverable" to "legible" happens here (devcontext #10, screenshot critique #5).
/// <para>Nothing is thrown away to make the line short. The full structure travels beside it on the
/// same transcript line (<see cref="Events.TranscriptLine.Tool"/>), so a reader who wants the whole
/// path or the whole command still has it; this is a rendering, not a second capture.</para>
/// </remarks>
public static class ToolLine
{
    /// <summary>Whole-line cap. A line this long is already past what any pane shows without
    /// wrapping, and the structured fields beside it are the place to go for the rest.</summary>
    public const int MaxChars = 200;

    private const int MaxCommandChars = 140;
    private const int MaxPurposeChars = 90;
    private const int MaxScopeChars = 60;

    /// <summary>Trailing words in an MCP server name that only repeat what the tool name already
    /// says: <c>mcp__conductor-tasks__task_update</c> reads as <c>conductor task_update</c>, which is
    /// the spec's worked example, rather than the stuttering <c>conductor-tasks task_update</c>.</summary>
    private static readonly HashSet<string> RedundantServerWords =
        new(StringComparer.OrdinalIgnoreCase) { "tasks", "task", "mcp", "server", "tools", "tool" };

    /// <summary>The wire line for one call.</summary>
    public static string Render(ToolCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var head = DisplayName(call.Name);
        var detail = Detail(ToolEventExtractor.Normalize(call.Name), call);
        // A known tool that carried none of the fields its own branch reads still renders whatever it
        // DID carry: opencode sends an Edit with only its rendered title, and `Edit` alone on the wire
        // would be less than the same call told us.
        if (detail.Length == 0) detail = Fallback(call);
        return Clip(detail.Length == 0 ? head : head + " " + detail, MaxChars);
    }

    /// <summary>The tool's own name with any MCP server prefix stripped — <c>bg_start</c>, not
    /// <c>mcp__conductor-tasks__bg_start</c>. This is the name the digest's tool mix counts under, so
    /// the same logical tool reads the same however the harness exposed it.</summary>
    public static string ShortName(string? name)
    {
        var s = (name ?? "").Trim();
        if (s.Length == 0) return "tool";
        var idx = s.LastIndexOf("__", StringComparison.Ordinal);
        return idx >= 0 && idx + 2 < s.Length ? s[(idx + 2)..] : s;
    }

    /// <summary>The head of the wire line: the bare tool name, or — for an MCP tool — the server that
    /// owns it plus the tool, so <c>task_update</c> is visibly conductor's board being written and not
    /// some other server's verb of the same name.</summary>
    public static string DisplayName(string? name)
    {
        var s = (name ?? "").Trim();
        var tool = ShortName(s);
        if (!s.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase)) return tool;

        var rest = s["mcp__".Length..];
        var cut = rest.LastIndexOf("__", StringComparison.Ordinal);
        if (cut <= 0) return tool;
        var server = rest[..cut];
        var dash = server.LastIndexOf('-');
        if (dash > 0 && RedundantServerWords.Contains(server[(dash + 1)..])) server = server[..dash];
        return server.Length == 0 ? tool : server + " " + tool;
    }

    private static string Detail(string tool, ToolCall call) => tool switch
    {
        "edit" or "multiedit" or "notebookedit" or "notebook_edit" or "edit_file"
            or "str_replace" or "str_replace_editor" or "apply_patch"
            => Words(BaseName(call.Field("path")), EditCounts(call)),
        "write" or "write_file" or "create_file" => Words(BaseName(call.Field("path")), WriteSize(call)),
        "read" or "notebookread" => BaseName(call.Field("path")),
        "bash" or "shell" or "powershell" or "run_command" or "execute_command"
            => Clip(call.Field("command") ?? "", MaxCommandChars),
        "grep" or "glob" => Words(call.Field("pattern") ?? call.Field("glob"), Scope(call)),
        "task" or "agent" => Words(call.Field("agent"), Quoted(call.Field("purpose"))),
        "task_update" or "task_add" or "task_blocked_until" => Arrow(call),
        "bg_start" => Quoted(call.Field("purpose") ?? call.Field("command")),
        "toolsearch" or "tool_search" => Quoted(call.Field("query")),
        "webfetch" or "web_fetch" => call.Field("url") ?? "",
        "websearch" or "web_search" => Quoted(call.Field("query")),
        _ => Fallback(call),
    };

    /// <summary>An unknown tool still gets a useful line: whichever canonical field it did carry, in
    /// the order a reader cares about. Silence beats guessing — a tool with nothing recognisable
    /// renders as its bare name rather than as a fragment of its own arguments.</summary>
    private static string Fallback(ToolCall call)
    {
        if (call.Field("path") is { Length: > 0 } p) return BaseName(p);
        if (call.Field("command") is { Length: > 0 } c) return Clip(c, MaxCommandChars);
        if (Arrow(call) is { Length: > 0 } a) return a;
        // Bare, not quoted: here `purpose` is as often a rendered title (opencode's `Edit TRACKER.md`)
        // as a sentence. Quotes are kept for the tools where it is definitionally a human description.
        if (call.Field("purpose") is { Length: > 0 } d) return Clip(d, MaxPurposeChars);
        if (call.Field("query") is { Length: > 0 } q) return Clip(q, MaxPurposeChars);
        if (call.Field("pattern") is { Length: > 0 } g) return g;
        return call.Field("url") ?? "";
    }

    /// <summary><c>G1.1 -&gt; done</c>. Either half alone is still worth showing.</summary>
    private static string Arrow(ToolCall call)
    {
        var id = call.Field("taskId") ?? "";
        var status = call.Field("status") ?? "";
        if (id.Length > 0 && status.Length > 0) return id + " -> " + status;
        return id.Length > 0 ? id : status;
    }

    /// <summary><c>(+12/-3)</c> from an Edit's line counts, <c>(4 edits)</c> from a MultiEdit. Both
    /// come from bodies SC7.1 deliberately counted instead of storing.</summary>
    private static string EditCounts(ToolCall call)
    {
        if (call.Field("edits") is { Length: > 0 } n) return "(" + n + " edits)";
        var added = call.Field("linesAdded");
        var removed = call.Field("linesRemoved");
        if (added == null && removed == null) return "";
        return "(+" + (added ?? "0") + "/-" + (removed ?? "0") + ")";
    }

    private static string WriteSize(ToolCall call)
    {
        if (call.Field("lines") is { Length: > 0 } l) return "(" + l + " lines)";
        return call.Field("bytes") is { Length: > 0 } b ? "(" + b + " bytes)" : "";
    }

    private static string Scope(ToolCall call) =>
        call.Field("in") is { Length: > 0 } dir ? "in " + Clip(dir, MaxScopeChars) : "";

    private static string Quoted(string? s) =>
        string.IsNullOrEmpty(s) ? "" : "\"" + Clip(s, MaxPurposeChars) + "\"";

    /// <summary>The file's own name. The directory is what makes a tool line unreadable at a glance
    /// and is one field lookup away for anyone who wants it.</summary>
    internal static string BaseName(string? path)
    {
        var s = (path ?? "").Trim();
        if (s.Length == 0) return "";
        var cut = s.LastIndexOfAny(['/', '\\']);
        var tail = cut >= 0 && cut + 1 < s.Length ? s[(cut + 1)..] : s;
        return tail.Length == 0 ? s : tail;
    }

    private static string Words(string? a, string? b)
    {
        var left = (a ?? "").Trim();
        var right = (b ?? "").Trim();
        if (left.Length == 0) return right;
        return right.Length == 0 ? left : left + " " + right;
    }

    private static string Clip(string s, int max)
    {
        var one = s.ReplaceLineEndings(" ").Trim();
        return one.Length <= max ? one : one[..max] + "\u2026";
    }
}
