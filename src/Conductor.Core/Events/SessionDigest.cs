using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Conductor.Core.Providers;

namespace Conductor.Core.Events;

/// <summary>
/// SC7.2 — what one session DID, in the five facts a reader wants before reading anything else:
/// how many tool calls and of what mix, which files it touched and how often, what it claimed, what
/// it put in the background and why, and which build/test commands it ran.
/// </summary>
/// <remarks>
/// devcontext #10's worked example is the shape here, and its author's point was that the digest
/// could not be built at all from the old capture: <c>file_path</c> and <c>command</c> were cut out
/// of the argument blob before storage. SC7.1 made them recoverable; this is what they were made
/// recoverable FOR.
/// <para>Accumulated live, one <see cref="Add"/> per tool event through the same funnel that already
/// feeds the out-of-repo write check, so a session that dies mid-flight still has a digest of what it
/// managed to do. Every collection is capped: a session that writes a thousand files under %TEMP%
/// must not put a thousand strings in state.json to say so, and the counts stay honest past the cap
/// because <see cref="ToolCalls"/> keeps counting after the lists stop growing.</para>
/// <para>Mutable and property-per-field on purpose: it round-trips through state.json and through
/// run.db's <c>sessions.digest</c> column as plain JSON.</para>
/// </remarks>
public sealed class SessionDigest
{
    /// <summary>Distinct files tracked by name. Past this the per-file counts stop growing; the
    /// rendered digest says so rather than quietly showing a short list as if it were the whole one.</summary>
    public const int MaxTrackedFiles = 200;
    public const int MaxTrackedTools = 80;
    public const int MaxClaims = 60;
    public const int MaxJobs = 60;
    public const int MaxCommands = 40;

    /// <summary>Longest stored command. The capture already caps a value at 400; a digest is a
    /// skim surface, so it keeps less.</summary>
    public const int MaxCommandChars = 200;

    /// <summary>KS7.2 — where these counts came from: <c>hook</c> when the agent CLI's own tool hooks
    /// delivered them, <c>transcript</c> when they were re-derived from the assistant stream.
    /// Stored rather than inferred, because "hooks are the primary source" is a claim a reader must be
    /// able to CHECK on any given session — a run where the hook silently never fired would otherwise
    /// look exactly like one where it did.</summary>
    public string Source { get; set; } = TranscriptSource;

    public const string HookSource = "hook";
    public const string TranscriptSource = "transcript";

    public int ToolCalls { get; set; }

    /// <summary>KS7.2 — how many of those calls did not come back: refused by the permission posture,
    /// exited nonzero, or cut off when the session died. Only the hook source can know this (the
    /// transcript sees the request, never the outcome), so it stays 0 on a transcript-derived digest
    /// rather than pretending every call succeeded.</summary>
    public int FailedCalls { get; set; }

    /// <summary>Tool name (MCP prefix stripped) to call count.</summary>
    public Dictionary<string, int> Mix { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Written path to write count, repo-relative where the path is inside the repo.
    /// Reads and greps are not writes and are not here — this answers "what did it CHANGE".</summary>
    public Dictionary<string, int> FilesTouched { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Board writes as <c>SC7.2 -&gt; done</c>, in the order they happened.</summary>
    public List<string> Claims { get; set; } = [];

    /// <summary>The <c>purpose</c> of every <c>bg_start</c>, in order. devcontext #10 singled these
    /// out: agents write genuinely descriptive purposes there, so read end to end they are already a
    /// written narrative of the session's reasoning — and they were buried in the raw stream.</summary>
    public List<string> BackgroundJobs { get; set; } = [];

    /// <summary>The shell calls worth a reviewer's attention — builds, tests, gates, golden
    /// regenerations — deduped, out of what is usually a much longer list of shell noise.</summary>
    public List<string> Commands { get; set; } = [];

    [JsonIgnore] public int DistinctTools => Mix.Count;

    /// <summary>Total writes across <see cref="FilesTouched"/> — the "17 edits over 10 files" half.</summary>
    [JsonIgnore] public int FileWrites => FilesTouched.Values.Sum();

    [JsonIgnore] public bool IsEmpty => ToolCalls == 0;

    /// <summary>Folds one captured call in. <paramref name="repoRoot"/>, when given, is what a written
    /// path is made relative to; a path outside it stays absolute, which is exactly the read a
    /// reviewer wants (and the same signal SC7.1's out-of-repo note reports as a count).</summary>
    public void Add(ToolCall call, string? repoRoot = null)
    {
        ArgumentNullException.ThrowIfNull(call);
        ToolCalls++;

        var name = ToolLine.ShortName(call.Name);
        if (Mix.ContainsKey(name) || Mix.Count < MaxTrackedTools)
            Mix[name] = Mix.TryGetValue(name, out var n) ? n + 1 : 1;

        if (ToolEventExtractor.IsWrite(call.Name) && call.Field("path") is { Length: > 0 } path)
        {
            var key = Relative(path, repoRoot);
            if (FilesTouched.ContainsKey(key) || FilesTouched.Count < MaxTrackedFiles)
                FilesTouched[key] = FilesTouched.TryGetValue(key, out var f) ? f + 1 : 1;
        }

        if (call.Field("taskId") is { Length: > 0 } id && call.Field("status") is { Length: > 0 } status)
            AddClaim(id, status);

        var normalized = ToolEventExtractor.Normalize(call.Name);
        if (normalized == "bg_start" && (call.Field("purpose") ?? call.Field("command")) is { Length: > 0 } purpose
            && BackgroundJobs.Count < MaxJobs)
            BackgroundJobs.Add(Clip(purpose));

        if (call.Field("command") is { Length: > 0 } command)
        {
            // Bug #19 class. The claim counter used to read ONE shape — the MCP task_update call's
            // taskId/status pair — while the sessions doing the claiming ran `conductor task --done`
            // through the shell, because that is what their own prompt tells them to do and the MCP
            // tools arrive deferred in some harnesses. So the digest reported "0 claims" for sessions
            // that had claimed, and the number was read as evidence that nothing was delivered.
            if (TryReadCliClaim(command, out var cliId, out var cliStatus)) AddClaim(cliId, cliStatus);
            if (IsNotable(command))
            {
                var clipped = Clip(command);
                if (Commands.Count < MaxCommands && !Commands.Contains(clipped, StringComparer.Ordinal))
                    Commands.Add(clipped);
            }
        }
    }

    private void AddClaim(string id, string status)
    {
        var entry = id + " -> " + status;
        if (Claims.Count < MaxClaims && !Claims.Contains(entry, StringComparer.Ordinal)) Claims.Add(entry);
    }

    /// <summary>The board-moving flags of <c>conductor task</c>, mapped to the status word the MCP
    /// path already writes so one board move reads the same however it was made. <c>--amend</c> is
    /// absent on purpose: it attaches a note and moves nothing, and a digest that reported it as a
    /// claim would overstate what the session did.</summary>
    private static readonly Dictionary<string, string> ClaimFlags = new(StringComparer.Ordinal)
    {
        ["--done"] = "done",
        ["--in-progress"] = "in_progress",
        ["--todo"] = "todo",
        ["--blocked"] = "blocked",
        ["--skipped"] = "skipped",
    };

    /// <summary>Verbs that only ever LOOK at text. A session greps its own prompt for
    /// <c>task --done</c> often enough that reading one as a board move would put checkpoints on the
    /// evidence trail that were never claimed — and a fabricated claim is a worse failure than a
    /// missed one.</summary>
    private static readonly HashSet<string> ClaimReaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "grep", "rg", "cat", "head", "tail", "sed", "awk", "echo", "type", "less", "more", "find", "wc",
    };

    /// <summary>Reads a <c>conductor task --&lt;move&gt; &lt;id&gt;</c> out of a shell command line.
    /// Deliberately narrow: the <c>task</c> word must be present as its own token, the flag must be
    /// one that moves a card, and the id must be the very next token and not itself a flag.</summary>
    internal static bool TryReadCliClaim(string command, out string id, out string status)
    {
        id = "";
        status = "";
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 3) return false;
        if (ClaimReaders.Contains(ToolLine.BaseName(tokens[0].Trim('"', '\'')))) return false;

        var taskAt = Array.FindIndex(tokens, t => string.Equals(t.Trim('"', '\''), "task", StringComparison.Ordinal));
        if (taskAt < 0) return false;
        for (var i = taskAt + 1; i < tokens.Length - 1; i++)
        {
            if (!ClaimFlags.TryGetValue(tokens[i], out var mapped)) continue;
            var candidate = tokens[i + 1].Trim('"', '\'');
            if (candidate.Length == 0 || candidate.StartsWith('-')) return false;
            id = candidate;
            status = mapped;
            return true;
        }
        return false;
    }

    /// <summary>The one-line form the run log carries at session end — the whole digest at a glance,
    /// in a line short enough to sit beside the exit line it follows.</summary>
    public string Summary()
    {
        var parts = new List<string>
        {
            $"{ToolCalls} tool call{(ToolCalls == 1 ? "" : "s")}",
            $"{DistinctTools} tool{(DistinctTools == 1 ? "" : "s")}",
        };
        if (FilesTouched.Count > 0)
            parts.Add($"{FilesTouched.Count} file{(FilesTouched.Count == 1 ? "" : "s")} ({FileWrites} write{(FileWrites == 1 ? "" : "s")})");
        if (Claims.Count > 0) parts.Add($"{Claims.Count} claim{(Claims.Count == 1 ? "" : "s")}");
        if (BackgroundJobs.Count > 0) parts.Add($"{BackgroundJobs.Count} bg job{(BackgroundJobs.Count == 1 ? "" : "s")}");
        if (Commands.Count > 0) parts.Add($"{Commands.Count} build/test command{(Commands.Count == 1 ? "" : "s")}");
        if (FailedCalls > 0) parts.Add($"{FailedCalls} failed/refused");
        parts.Add("via " + Source);
        return string.Join(" · ", parts);
    }

    /// <summary>The readable block — devcontext #10's worked example, which is this checkpoint's
    /// acceptance shape. Served to an agent through the <c>session_detail</c> tool so reading what an
    /// earlier session did costs one call instead of a transcript crawl.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("TOOL CALLS: ").Append(ToolCalls.ToString(CultureInfo.InvariantCulture))
          .Append("  ·  distinct tools: ").Append(DistinctTools.ToString(CultureInfo.InvariantCulture))
          .Append("  ·  source: ").Append(Source);
        if (FailedCalls > 0)
            sb.Append("  ·  failed or refused: ").Append(FailedCalls.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();
        if (Mix.Count > 0)
            sb.Append("MIX: ").AppendLine(string.Join(", ", Ranked(Mix).Select(p => p.Key + " " + p.Value.ToString(CultureInfo.InvariantCulture))));

        if (FilesTouched.Count > 0)
        {
            sb.AppendLine().Append(CultureInfo.InvariantCulture, $"FILES TOUCHED ({FileWrites} writes over {FilesTouched.Count} files):").AppendLine();
            foreach (var (file, count) in Ranked(FilesTouched))
                sb.Append("    ").Append(file).Append("  ").Append(count.ToString(CultureInfo.InvariantCulture)).AppendLine("x");
        }

        if (Claims.Count > 0) sb.AppendLine().Append("CLAIMS: ").AppendLine(string.Join(", ", Claims));
        AppendList(sb, $"BACKGROUND JOBS ({BackgroundJobs.Count})", BackgroundJobs);
        AppendList(sb, $"BUILD / TEST / EVIDENCE COMMANDS ({Commands.Count})", Commands);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Serialised for run.db's <c>sessions.digest</c> column. An empty digest stores nothing
    /// rather than an empty object — a session with no captured calls should read as absent, not as a
    /// session that provably did nothing.</summary>
    public string? ToJson() => IsEmpty ? null : JsonSerializer.Serialize(this, SessionDigestJsonContext.Default.SessionDigest);

    /// <summary>Reads a stored digest back. A row from before this column existed, or one holding
    /// something unparseable, comes back null — never a fabricated empty digest.</summary>
    public static SessionDigest? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize(json, SessionDigestJsonContext.Default.SessionDigest); }
        catch (JsonException) { return null; }
    }

    /// <summary>Counts descending, then name, so the order is stable across two renders of the same
    /// data — a digest that reshuffles itself is one a reader stops trusting.</summary>
    public static IEnumerable<KeyValuePair<string, int>> Ranked(Dictionary<string, int> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal);
    }

    private static void AppendList(StringBuilder sb, string heading, List<string> items)
    {
        if (items.Count == 0) return;
        sb.AppendLine().AppendLine(heading + ":");
        foreach (var item in items) sb.Append("    ").AppendLine(item);
    }

    /// <summary>Words that make a shell call worth surfacing. Read-only inspectors are excluded by
    /// their VERB rather than by keyword, because <c>grep -n "build"</c> matches every keyword here
    /// and is not a build.</summary>
    private static readonly string[] NotableWords =
        ["build", "test", "gate", "golden", "eval", "vet", "lint", "check", "bench", "publish", "verify", "audit"];

    private static readonly HashSet<string> InspectorVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "grep", "rg", "cat", "ls", "dir", "head", "tail", "find", "echo", "sed", "awk", "wc", "type",
        "git", "which", "where", "pwd", "cd", "conductor",
    };

    private static bool IsNotable(string command)
    {
        var trimmed = command.TrimStart();
        var firstBreak = trimmed.IndexOfAny([' ', '\t']);
        var verb = firstBreak > 0 ? trimmed[..firstBreak] : trimmed;
        verb = ToolLine.BaseName(verb);
        if (InspectorVerbs.Contains(verb)) return false;
        foreach (var word in NotableWords)
            if (command.Contains(word, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A path under the repo reads repo-relative with forward slashes; anything else keeps
    /// its absolute form, so an out-of-tree write is visible as one on sight.</summary>
    private static string Relative(string path, string? repoRoot)
    {
        if (string.IsNullOrWhiteSpace(repoRoot)) return path;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
            var full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison)) return path;
            return full[(root.Length + 1)..].Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string Clip(string s)
    {
        var one = s.ReplaceLineEndings(" ").Trim();
        return one.Length <= MaxCommandChars ? one : one[..MaxCommandChars] + "…";
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SessionDigest))]
public sealed partial class SessionDigestJsonContext : JsonSerializerContext;
