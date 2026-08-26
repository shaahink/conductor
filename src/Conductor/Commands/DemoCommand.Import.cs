using System.Globalization;
using System.Text;

using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Models;

using Spectre.Console;

namespace Conductor.Commands;

/// <summary>KS3.5 — <c>conductor demo --from &lt;file&gt;</c>: the same credential-free run, driven by
/// YOUR document instead of the built-in three checkpoints. It converts through the same zero-spend
/// bridges <c>conductor plan import</c> uses (<see cref="ImportBridge.Read"/>), writes the converted
/// stages and a tracker table into a throwaway repo, and drives it to completion with the built-in
/// fake agent — so "will conductor drive my spec-kit board?" is a question you answer in one command,
/// for nothing, before you point it at a real agent.
/// <para>The default <c>conductor demo</c> is untouched by design: this path only supplies a
/// different stages array and a different tracker to the same scaffold, so the demo's gates stay the
/// host-portable <c>git </c> pair (a throwaway repo has no build system for
/// <c>RepoKindDetector</c> to detect, so its own default is the honest fallback) and the state stays
/// pinned inside the directory.</para></summary>
public sealed partial class DemoCommand
{
    /// <summary>What a converted document contributes to the scaffold: the stages array the plan file
    /// gets, the tracker table the run schedules on, and the counts the transcript prints.</summary>
    internal sealed record DemoImport(
        string StagesJson,
        string Tracker,
        int Stages,
        int Checkpoints,
        string SourceName,
        string SourceText,
        ImportFormat Format);

    /// <summary>Read and convert the document, or explain why it is not drivable and return null.
    /// Every failure here is a message a stranger can act on: this command is the front door.</summary>
    internal static async Task<DemoImport?> LoadImportAsync(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]No such file:[/] {Markup.Escape(path)}");
            return null;
        }

        string text;
        try { text = await File.ReadAllTextAsync(path, Encoding.UTF8).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Could not read {Markup.Escape(path)}[/] — {Markup.Escape(ex.Message)}");
            return null;
        }

        var (result, format) = ImportBridge.Read(text);
        if (result is null || result.Stages.Count == 0 || result.Checkpoints.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(Path.GetFileName(path))} is not a document the deterministic " +
                "bridges recognise.[/]");
            AnsiConsole.MarkupLine("[grey]The demo converts, with no model call: a spec-kit tasks.md, a Task-Master " +
                "tasks.json, a plain markdown checklist, or a conductor plan/tracker document.[/]");
            return null;
        }

        return new DemoImport(
            StagesJsonFor(result.Stages),
            TrackerFor(result.Checkpoints),
            result.Stages.Count,
            result.Checkpoints.Count,
            Path.GetFileName(path),
            text,
            format);
    }

    /// <summary>The plan file's <c>stages</c> array. Notes are dropped and braces are already stripped
    /// by the bridges: <c>PlanConfig.CollectErrors</c> refuses an unresolved <c>{token}</c> in stage
    /// notes, and a converted plan that will not load is worse than one that carries less prose.
    /// <para>The source's declared ordering DOES survive: a Task-Master file states its own
    /// <c>dependencies</c>, and a plan file that silently dropped them would misreport what was
    /// converted to whoever reads it. It costs nothing at runtime — execution is sequential either way
    /// (<c>StageConfig.DependsOn</c>: readiness ordering only).</para></summary>
    internal static string StagesJsonFor(IEnumerable<StageConfig> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var parts = stages.Select(s =>
        {
            var head = string.Create(CultureInfo.InvariantCulture,
                $$"""{ "id": "{{Escape(s.Id)}}", "title": "{{Escape(s.Title ?? s.Id)}}", "sessions": {{Math.Max(1, s.Sessions)}}""");
            if (s.DependsOn is not { Count: > 0 } deps) return head + " }";
            var ids = string.Join(", ", deps.Select(d => $"\"{Escape(d)}\""));
            return $$"""{{head}}, "dependsOn": [{{ids}}] }""";
        });
        return string.Join(",\n    ", parts);
    }

    /// <summary>The tracker the run schedules on — the same table shape the engine regenerates after
    /// every session, so the fake agent's row picker (<c>FakeAgentCommand.FirstOpenRow</c>) finds the
    /// converted ids exactly as it finds the built-in ones.</summary>
    internal static string TrackerFor(IEnumerable<ImportedCheckpoint> checkpoints)
    {
        ArgumentNullException.ThrowIfNull(checkpoints);
        var table = new StringBuilder()
            .Append("# Conductor demo — TRACKER (imported)\n\n")
            .Append("## Handoff (overwrite this block each session, <=12 lines, no history)\n")
            .Append("last: none. Status: idle.\n\n")
            .Append("## Checkpoints\n\n")
            .Append("| # | Checkpoint | Status | Commit | Evidence |\n")
            .Append("|---|-----------|--------|--------|----------|\n");
        foreach (var c in checkpoints)
        {
            var status = string.IsNullOrWhiteSpace(c.Status) ? "TODO" : c.Status.Trim().ToUpperInvariant();
            table.Append(CultureInfo.InvariantCulture, $"| {c.Id} | {Row(c.Title)} | {status} |  |  |\n");
        }
        return table.Append('\n').ToString();
    }

    private static string Row(string title) => title.Replace("|", "/", StringComparison.Ordinal).Trim();

    private static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);
}
