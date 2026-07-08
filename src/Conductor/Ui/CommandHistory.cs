namespace Conductor.Ui;

/// <summary>Which slice of the agent history the command pane shows (B4.6). <c>Commands</c> = tool
/// calls, <c>Thoughts</c> = reasoning + narrative, <c>Errors</c> = stderr.</summary>
public enum HistoryCategory { All, Commands, Thoughts, Errors }

/// <summary>One flattened history line — the agent stream and the reasoning buffer merged into a
/// single time-ordered feed for searching/filtering.</summary>
public readonly record struct HistoryEntry(string Kind, string Text, DateTime Utc);

/// <summary>A parsed history query: a category filter plus a free-text substring. Immutable so it can
/// be diffed in tests and echoed in the modal header.</summary>
public sealed record HistoryQuery(HistoryCategory Category, string Search)
{
    public static readonly HistoryQuery None = new(HistoryCategory.All, "");

    /// <summary>True when the query would narrow the feed (a non-All category or a search term).</summary>
    public bool IsActive => Category != HistoryCategory.All || Search.Length > 0;
}

/// <summary>
/// Pure command-history logic (B4.6): parse a query line and filter the merged agent feed. Supports
/// the documented slash syntax — category tokens (<c>/commands</c>, <c>/thoughts</c>, <c>/errors</c>,
/// <c>/all</c> and short aliases) set the category; any other token (including <c>/build</c>,
/// <c>/git</c>, <c>/test</c>) becomes a case-insensitive search term (a leading slash is stripped so
/// <c>/build</c> finds "dotnet build"). No IO, no terminal — fully unit-testable.
/// </summary>
public static class CommandHistory
{
    public static HistoryQuery Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return HistoryQuery.None;

        var category = HistoryCategory.All;
        var terms = new List<string>();
        foreach (var token in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (MatchCategory(token) is { } cat) { category = cat; continue; }
            terms.Add(token.StartsWith('/') ? token[1..] : token);
        }
        return new HistoryQuery(category, string.Join(' ', terms).Trim());
    }

    /// <summary>A leading-slash category token, or null when the token is a plain search term.</summary>
    private static HistoryCategory? MatchCategory(string token)
    {
        if (token.Length < 2 || token[0] != '/') return null;
        return token[1..].ToLowerInvariant() switch
        {
            "all" => HistoryCategory.All,
            "commands" or "cmds" or "cmd" => HistoryCategory.Commands,
            "thoughts" or "thought" or "thinking" or "think" => HistoryCategory.Thoughts,
            "errors" or "error" or "errs" or "err" => HistoryCategory.Errors,
            _ => null,
        };
    }

    public static IReadOnlyList<HistoryEntry> Filter(IReadOnlyList<HistoryEntry> entries, HistoryQuery query)
    {
        IEnumerable<HistoryEntry> feed = query.Category switch
        {
            HistoryCategory.Commands => entries.Where(e => e.Kind == "tool"),
            HistoryCategory.Thoughts => entries.Where(e => e.Kind is "thinking" or "text"),
            HistoryCategory.Errors => entries.Where(e => e.Kind == "stderr"),
            _ => entries,
        };
        if (query.Search.Length > 0)
            feed = feed.Where(e => e.Text.Contains(query.Search, StringComparison.OrdinalIgnoreCase));
        return feed.ToList();
    }

    /// <summary>Cycles the category filter for the Tab key: All → Commands → Thoughts → Errors → All.</summary>
    public static HistoryCategory NextCategory(HistoryCategory c) => c switch
    {
        HistoryCategory.All => HistoryCategory.Commands,
        HistoryCategory.Commands => HistoryCategory.Thoughts,
        HistoryCategory.Thoughts => HistoryCategory.Errors,
        HistoryCategory.Errors => HistoryCategory.All,
        _ => HistoryCategory.All,
    };

    public static string CategoryLabel(HistoryCategory c) => c switch
    {
        HistoryCategory.All => "all",
        HistoryCategory.Commands => "commands",
        HistoryCategory.Thoughts => "thoughts",
        HistoryCategory.Errors => "errors",
        _ => "all",
    };
}
