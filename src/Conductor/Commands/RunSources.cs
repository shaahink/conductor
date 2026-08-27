using Conductor.Core.History;
using Conductor.Core.Store;
using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// Which run databases a measuring verb should read, in order of how explicit the operator was: a path
/// they typed, then the machine catalogue, then the repo-local <c>.conductor/run.db</c>. The last one
/// is not a nicety — a run started by an engine older than the catalogue keeps its database beside the
/// working tree, and this repo's own live run is one of those.
/// <para>K4.3 lifted this out of <see cref="BudgetCommand"/> so <see cref="MoneyCommand"/> resolves a
/// run exactly the way <c>budget</c> does. Two verbs that answer questions about the same run must not
/// disagree about which run that is.</para>
/// </summary>
internal static class RunSources
{
    /// <summary>The databases to measure, or null when the selector was wrong — in which case the
    /// reason has already been printed and the caller should exit non-zero.</summary>
    public static List<(string Db, ArchivedRun Run, string Label)>? Resolve(
        string root, RunHistoryFilter filter, string? selector, string? repo, string? envOverride = null)
    {
        var direct = AsDatabasePath(selector);
        if (direct is not null) return FromDatabase(direct, filter);

        // Bug #61: StateHome documents CONDUCTOR_RUN_DB as the bluntest override there is, and the
        // measuring verbs did not honour it — `budget` resolved by repo first and answered "no runs
        // to measure" with the variable pointing at a database holding four. It is the documented way
        // to measure a run WITHOUT opening the live store (bug #45), so it has to work here. A typed
        // path still wins (above); a selector is looked up inside the named database, not the catalogue.
        if (EnvDatabase(envOverride) is { } fromEnv)
        {
            Console.Error.WriteLine($"note: measuring {fromEnv} ({StateHome.RunDbEnvVar})");
            var inEnv = FromDatabase(fromEnv, filter);
            if (string.IsNullOrWhiteSpace(selector)) return inEnv;
            var matched = inEnv.Where(r => MatchesSelector(r.Run, selector)).ToList();
            if (matched.Count > 0) return matched;
            AnsiConsole.MarkupLine(
                $"[red]no run matches '{Markup.Escape(selector)}' in {Markup.Escape(fromEnv)}[/] " +
                $"[grey]({StateHome.RunDbEnvVar})[/] - unset it to search the catalogue.");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(selector))
        {
            var row = RunHistory.Find(root, selector, out var ambiguous, filter);
            if (row is not null) return [(row.RunDbPath, row.Run!, row.RepoLabel)];
            if (ambiguous.Count == 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]no run matches '{Markup.Escape(selector)}'.[/] try [grey]conductor history[/], " +
                    "or pass the path to a run.db.");
                return null;
            }
            AnsiConsole.MarkupLine($"[yellow]'{Markup.Escape(selector)}' matches {ambiguous.Count} runs:[/]");
            foreach (var c in ambiguous)
                AnsiConsole.MarkupLine($"  [aqua]{Markup.Escape(c.Run!.RunId)}[/]  {Markup.Escape(c.RepoLabel)}  [grey]{Markup.Escape(c.Plan)}[/]");
            return null;
        }

        var catalogued = RunHistory.List(root, filter)
            .Where(r => r.Readable)
            .Reverse()
            .Select(r => (r.RunDbPath, r.Run!, r.RepoLabel))
            .ToList();
        if (catalogued.Count > 0) return catalogued;

        var local = Path.Combine(repo ?? Directory.GetCurrentDirectory(), StateHome.ScratchDirName, StateHome.RunDbFileName);
        return File.Exists(local) ? FromDatabase(local, filter) : [];
    }

    /// <summary>Every run inside one database file, oldest first.</summary>
    public static List<(string Db, ArchivedRun Run, string Label)> FromDatabase(string dbPath, RunHistoryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var archive = RunArchive.TryOpen(dbPath);
        if (archive is null) return [];
        return archive.Runs()
            .Where(r => filter.Plan is null || r.PlanName.Contains(filter.Plan, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.StartedUtc, StringComparer.Ordinal)
            .Select(r => (dbPath, r, RunHistory.RepoLabel(r.Repo)))
            .ToList();
    }

    /// <summary>The database <see cref="StateHome.RunDbEnvVar"/> names, when it names one that exists.
    /// Null when unset — and null, with a line saying so, when it names a path that is not there,
    /// because a variable pointing at nothing must not fall through to a different run in silence.</summary>
    /// <param name="value">The variable's value, or null to read the process environment.</param>
    internal static string? EnvDatabase(string? value = null)
    {
        var raw = value ?? Environment.GetEnvironmentVariable(StateHome.RunDbEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var full = Path.GetFullPath(raw.Trim());
        if (File.Exists(full)) return full;
        Console.Error.WriteLine($"warning: {StateHome.RunDbEnvVar} names {full}, which does not exist - ignoring it");
        return null;
    }

    internal static bool MatchesSelector(ArchivedRun run, string selector) =>
        run.RunId.StartsWith(selector, StringComparison.OrdinalIgnoreCase)
        || run.PlanName.Contains(selector, StringComparison.OrdinalIgnoreCase);

    /// <summary>A selector that is really a filesystem path: a run.db, or a directory holding one.</summary>
    public static string? AsDatabasePath(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector)) return null;
        if (File.Exists(selector)) return selector;
        var nested = Path.Combine(selector, StateHome.ScratchDirName, StateHome.RunDbFileName);
        if (File.Exists(nested)) return nested;
        var inside = Path.Combine(selector, StateHome.RunDbFileName);
        return File.Exists(inside) ? inside : null;
    }
}
