using System.Text.Json;

namespace Conductor.Core.Integrations;

/// <summary>K1.4: the operator's own MCP servers, read off disk so conductor's per-session config can
/// MERGE them instead of replacing them.
/// <para>Until this existed, <c>WireMcpServer</c> wrote a config containing only <c>conductor-tasks</c>
/// and launched the child with <c>--strict-mcp-config</c>, so a session saw conductor's task verbs and
/// nothing else — a user-scope chrome-devtools server was invisible to every spawned session, which is
/// the field report this checkpoint answers. Strict stays: with the union written into one file the
/// child reads exactly what the run intends, which is determinism rather than exclusion.</para>
/// <para>Nothing here throws. An operator config that is missing, unreadable or malformed degrades to
/// "no servers from that source" with a note — the run must survive a broken file in someone's home
/// directory, because the alternative is that a typo in <c>~/.claude.json</c> stops every session.</para>
/// </summary>
internal static class OperatorMcpServers
{
    /// <summary>The one name conductor owns. An operator entry using it is dropped, not merged: the
    /// task/note/bug verbs are how a session reports at all, and silently handing that name to another
    /// process would make a run look like it simply never claimed anything.</summary>
    internal const string ConductorServerName = "conductor-tasks";

    /// <summary>A config bigger than this is not an MCP config. <c>~/.claude.json</c> also carries
    /// per-project conversation history and can reach tens of megabytes; the cap keeps a pathological
    /// file from costing every session a parse.</summary>
    private const long MaxConfigBytes = 8L * 1024 * 1024;

    private static readonly JsonDocumentOptions DocOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>What was found, and where from. <see cref="Sources"/> and <see cref="Notes"/> exist so
    /// the session log can say which files were consulted — an inherited server that fails to boot is
    /// otherwise indistinguishable from a conductor bug.</summary>
    internal sealed class Merged
    {
        public Dictionary<string, JsonElement> Servers { get; } = new(StringComparer.Ordinal);
        public List<string> Sources { get; } = [];
        public List<string> Notes { get; } = [];
    }

    /// <summary>The claude dialect: a <c>mcpServers</c> map. Scopes are read in the CLI's own precedence
    /// order — user (<c>~/.claude.json</c> <c>mcpServers</c>), then project (<c>&lt;repo&gt;/.mcp.json</c>),
    /// then local (<c>~/.claude.json</c> <c>projects[&lt;repo&gt;].mcpServers</c>) — with the later scope
    /// winning a name collision, so a project override still beats what the user set globally.</summary>
    internal static async Task<Merged> ForClaudeAsync(string repoPath, string? homeDir, CancellationToken ct)
    {
        var result = new Merged();
        var user = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var local = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var project = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        // ~/.claude.json holds two scopes; parse it once and take both.
        if (!string.IsNullOrEmpty(homeDir))
        {
            var userFile = Path.Combine(homeDir, ".claude.json");
            await ReadDocumentAsync(userFile, result, root =>
            {
                Take(root, "mcpServers", user, userFile, result);
                if (FindProjectNode(root, repoPath) is { } proj)
                    Take(proj, "mcpServers", local, $"{userFile} [projects]", result);
            }, ct).ConfigureAwait(false);
        }

        var projectFile = Path.Combine(repoPath, ".mcp.json");
        await ReadDocumentAsync(projectFile, result,
            root => Take(root, "mcpServers", project, projectFile, result), ct).ConfigureAwait(false);

        Compose(result, user, project, local);
        return result;
    }

    /// <summary>The opencode dialect: an <c>mcp</c> map, global config first
    /// (<c>~/.config/opencode/opencode.json</c>) then the repo's own, which wins.</summary>
    internal static async Task<Merged> ForOpencodeAsync(string repoPath, string? homeDir, CancellationToken ct)
    {
        var result = new Merged();
        var global = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var project = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(homeDir))
            foreach (var file in Candidates(Path.Combine(homeDir, ".config", "opencode"), "opencode"))
                await ReadDocumentAsync(file, result, root => Take(root, "mcp", global, file, result), ct).ConfigureAwait(false);

        foreach (var file in Candidates(repoPath, "opencode"))
            await ReadDocumentAsync(file, result, root => Take(root, "mcp", project, file, result), ct).ConfigureAwait(false);

        Compose(result, global, project);
        return result;
    }

    /// <summary>opencode accepts either extension; both are tried, and both are merged when both exist
    /// (last one wins) rather than guessing which the operator meant.</summary>
    private static IEnumerable<string> Candidates(string dir, string stem)
    {
        yield return Path.Combine(dir, stem + ".json");
        yield return Path.Combine(dir, stem + ".jsonc");
    }

    private static void Compose(Merged result, params Dictionary<string, JsonElement>[] scopes)
    {
        foreach (var scope in scopes)
            foreach (var kv in scope)
                result.Servers[kv.Key] = kv.Value;
    }

    /// <summary>Opens one config and hands its root to <paramref name="take"/>. Every failure mode ends
    /// as a note: a missing file is silent (the common case), anything else is worth one line.</summary>
    private static async Task ReadDocumentAsync(string file, Merged result, Action<JsonElement> take, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return;
            if (info.Length > MaxConfigBytes)
            {
                result.Notes.Add($"{file}: {info.Length / (1024 * 1024)}MB — over the {MaxConfigBytes / (1024 * 1024)}MB cap, not read");
                return;
            }

            // Parsed off the stream rather than off a string: the cap above allows a file large enough
            // that materialising it whole would be the most expensive thing this method does.
            var stream = File.OpenRead(file);
            await using (stream.ConfigureAwait(false))
            {
                using var doc = await JsonDocument.ParseAsync(stream, DocOpts, ct).ConfigureAwait(false);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    result.Notes.Add($"{file}: root is {doc.RootElement.ValueKind}, not an object — skipped");
                    return;
                }
                take(doc.RootElement);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            result.Notes.Add($"{file}: {ex.Message} — skipped");
        }
    }

    /// <summary>Copies one server map into <paramref name="into"/>. Values are cloned because the owning
    /// <c>JsonDocument</c> is disposed the moment this returns.</summary>
    private static void Take(JsonElement root, string mapName, Dictionary<string, JsonElement> into,
        string label, Merged result)
    {
        if (!root.TryGetProperty(mapName, out var map) || map.ValueKind != JsonValueKind.Object) return;
        var taken = 0;
        foreach (var entry in map.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                result.Notes.Add($"{label}: '{entry.Name}' is {entry.Value.ValueKind}, not a server object — skipped");
                continue;
            }
            if (string.Equals(entry.Name, ConductorServerName, StringComparison.OrdinalIgnoreCase))
            {
                result.Notes.Add($"{label}: '{entry.Name}' collides with conductor's own server — conductor's wins");
                continue;
            }
            into[entry.Name] = entry.Value.Clone();
            taken++;
        }
        if (taken > 0) result.Sources.Add($"{label} ({taken})");
    }

    /// <summary>The claude CLI keys its local scope by the absolute project path exactly as it saw it —
    /// separators and case included — so the lookup normalises both sides rather than trusting a match
    /// on the raw string.</summary>
    private static JsonElement? FindProjectNode(JsonElement root, string repoPath)
    {
        if (!root.TryGetProperty("projects", out var projects) || projects.ValueKind != JsonValueKind.Object)
            return null;
        var wanted = NormalisePath(repoPath);
        foreach (var entry in projects.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (string.Equals(NormalisePath(entry.Name), wanted, StringComparison.OrdinalIgnoreCase))
                return entry.Value;
        }
        return null;
    }

    private static string NormalisePath(string p)
    {
        var s = p.Replace('\\', '/').TrimEnd('/');
        try { return Path.GetFullPath(s).Replace('\\', '/').TrimEnd('/'); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return s;
        }
    }
}
