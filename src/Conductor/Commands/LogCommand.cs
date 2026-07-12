using System.ComponentModel;
using System.Text.Json;

using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// O1 — Structured log query. Reads the rolling JSON log files (<c>.conductor/logs/conductor-*.json</c>)
/// and filters entries by query expression. Each line is a valid compact JSON object with correlation
/// properties (runId, sessionId, stage, gate, outcome) plus the message (<c>@m</c>).
/// </summary>
public sealed class LogCommand : Command<LogCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("-q|--query <EXPR>")]
        [Description("Filter expression: key=value pairs separated by ' and ' (case-insensitive). Example: --query \"stage=P7 and gate=build and outcome=fail\"")]
        public string? Query { get; init; }

        [CommandOption("--since <DATETIME>")]
        [Description("Only show entries on or after this UTC datetime (ISO 8601).")]
        public string? Since { get; init; }

        [CommandOption("--tail <N>")]
        [Description("Show only the last N matching entries.")]
        public int? Tail { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        var plan = PlanConfig.Load(settings.ResolvePlanPath());
        var logDir = Path.Combine(plan.StateDir, "logs");
        if (!Directory.Exists(logDir))
        {
            AnsiConsole.MarkupLine("[yellow]No log directory found.[/] Run conductor at least once to generate logs.");
            return 0;
        }

        var pattern = settings.Query;
        var filters = ParseQuery(pattern);

        DateTime? sinceUtc = null;
        if (!string.IsNullOrWhiteSpace(settings.Since))
        {
            if (DateTime.TryParse(settings.Since, null,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
                sinceUtc = parsed;
            else
            {
                AnsiConsole.MarkupLine($"[red]Invalid --since value: '{Markup.Escape(settings.Since)}'. Use ISO 8601 (e.g. 2026-07-09T12:00Z).[/]");
                return 1;
            }
        }

        var jsonFiles = Directory.EnumerateFiles(logDir, "conductor-*.json")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (jsonFiles.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No JSON log files found.[/] Run conductor at least once to generate structured logs.");
            return 0;
        }

        var matched = new List<JsonLogEntry>();
        foreach (var file in jsonFiles)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var entry = System.Text.Json.JsonSerializer.Deserialize<JsonLogEntry>(line,
                        new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (entry == null) continue;
                    if (!Matches(entry, filters)) continue;
                    if (sinceUtc.HasValue && entry.Timestamp < sinceUtc.Value) continue;
                    matched.Add(entry);
                }
                catch (System.Text.Json.JsonException) { /* tolerate corrupt lines */ }
            }
        }

        if (settings.Tail is { } limit and > 0 && matched.Count > limit)
            matched = matched.Skip(matched.Count - limit).ToList();

        if (matched.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No matching log entries.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[bold aqua]conductor log[/] — {matched.Count} match{(matched.Count == 1 ? "" : "es")}" +
                               (pattern != null ? $" for '{Markup.Escape(pattern)}'" : ""));
        AnsiConsole.WriteLine(new string('-', 80));
        foreach (var e in matched)
            AnsiConsole.WriteLine(FormatEntry(e));
        AnsiConsole.WriteLine(new string('-', 80));

        return 0;
    }

    internal sealed record JsonLogEntry
    {
        [System.Text.Json.Serialization.JsonPropertyName("@t")]
        public DateTime Timestamp { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("@m")]
        public string Message { get; init; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("@l")]
        public string? Level { get; init; }
        public string? RunId { get; init; }
        [System.Text.Json.Serialization.JsonPropertyName("sessionId")]
        public string? SessionId { get; init; }
        public string? Stage { get; init; }
        public string? Gate { get; init; }
        public string? Outcome { get; init; }
        [System.Text.Json.Serialization.JsonExtensionData]
        public Dictionary<string, object>? Extra { get; init; }
    }

    /// <summary>Parses <c>key=value and key=value</c> into a case-insensitive filter dictionary.</summary>
    internal static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return result;
        var parts = query.Split([" and ", " AND "], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }

    internal static bool Matches(JsonLogEntry entry, Dictionary<string, string> filters)
    {
        if (filters.Count == 0) return true;
        foreach (var (key, value) in filters)
        {
            var fieldValue = key.ToLowerInvariant() switch
            {
                "runid" => entry.RunId,
                "sessionid" => entry.SessionId,
                "stage" => entry.Stage,
                "gate" => entry.Gate,
                "outcome" => entry.Outcome,
                "level" => entry.Level,
                _ => null,
            };
            if (fieldValue == null || !string.Equals(fieldValue, value, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    internal static string FormatEntry(JsonLogEntry e)
    {
        var tags = new List<string>();
        if (e.Stage != null) tags.Add($"stage:{e.Stage}");
        if (e.Gate != null) tags.Add($"gate:{e.Gate}");
        if (e.Outcome != null) tags.Add(e.Outcome.ToUpperInvariant());
        var tagStr = tags.Count > 0 ? $" [{string.Join(" ", tags)}]" : "";
        return $"{e.Timestamp:yyyy-MM-dd HH:mm:ss} [{e.Level ?? "?"}]{tagStr} {e.Message}";
    }
}
