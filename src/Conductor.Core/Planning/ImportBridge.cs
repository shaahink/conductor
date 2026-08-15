using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>Which deterministic reader claimed a document. Reported rather than inferred so
/// <c>conductor plan import</c> can tell the operator WHICH bridge read their file — a silent
/// mis-detection is the one failure that looks like a successful import.</summary>
public enum ImportFormat
{
    /// <summary>Nothing deterministic matched — the caller falls back to the advisor model.</summary>
    None,
    /// <summary>This project's own plan/tracker shape (<see cref="MarkdownPlanParser"/>).</summary>
    StructuredMarkdown,
    /// <summary>GitHub spec-kit <c>tasks.md</c>.</summary>
    SpecKit,
    /// <summary>Task-Master <c>tasks.json</c>.</summary>
    TaskMaster,
    /// <summary>A plain markdown checklist.</summary>
    Checklist,
}

/// <summary>KS3.5 — the one entry point for every zero-spend import, and the shared shaping rules the
/// three bridges obey. Detection is by CONTENT: a filename may be `tasks.md` and hold anything, and
/// the formats are distinguishable from their text alone (JSON with a task list; spec-kit's numbered
/// task lines; checkbox items). The order matters — JSON first because it cannot be mistaken for
/// markdown, then spec-kit's stricter shape, then the loose checklist, and this project's own
/// structured plan documents ahead of all of them so an existing import keeps its existing reader.
/// <para>Every id these bridges mint has to satisfy the engine's readers: a stage id matches
/// <c>[A-Za-z]{1,4}\d+</c>, a checkpoint id <c>[A-Za-z]{1,4}\d+\.[A-Za-z0-9]+</c>, and a checkpoint
/// hangs off its stage by that dotted prefix. Titles are stripped of braces because
/// <c>PlanConfig.CollectErrors</c> refuses an unresolved <c>{token}</c>, and a converter that passed
/// prose through verbatim could write a plan that will not load.</para></summary>
public static class ImportBridge
{
    private static readonly Regex BraceRegex = new(@"[{}]", RegexOptions.None, ProgressConventions.RegexTimeout);
    private static readonly Regex StageIdRegex = new(@"^[A-Za-z]{1,4}\d+$", RegexOptions.None, ProgressConventions.RegexTimeout);
    private static readonly Regex CheckpointIdRegex = new(@"^[A-Za-z]{1,4}\d+\.[A-Za-z0-9]+$", RegexOptions.None, ProgressConventions.RegexTimeout);

    /// <summary>The deterministic path, in one call: this project's own structured shape first, then
    /// the three foreign bridges. Returns <see cref="ImportFormat.None"/> and a null result when
    /// nothing matched — that, and only that, is when an import may cost a model call.</summary>
    public static (ImportResult? Result, ImportFormat Format) Read(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, ImportFormat.None);

        if (MarkdownPlanParser.LooksStructured(text))
        {
            var parsed = MarkdownPlanParser.Parse(text);
            if (parsed.Stages.Count > 0)
                return (MarkdownPlanParser.ToImportResult(parsed), ImportFormat.StructuredMarkdown);
        }

        if (TaskMasterImporter.Looks(text) && TaskMasterImporter.Parse(text) is { } tm) return (tm, ImportFormat.TaskMaster);
        if (SpecKitImporter.Looks(text) && SpecKitImporter.Parse(text) is { } sk) return (sk, ImportFormat.SpecKit);
        if (ChecklistImporter.Looks(text) && ChecklistImporter.Parse(text) is { } cl) return (cl, ImportFormat.Checklist);
        return (null, ImportFormat.None);
    }

    /// <summary>The human name of a bridge, for the line the import prints.</summary>
    public static string Describe(ImportFormat format) => format switch
    {
        ImportFormat.StructuredMarkdown => "a structured plan/tracker document",
        ImportFormat.SpecKit => "a spec-kit tasks.md",
        ImportFormat.TaskMaster => "a Task-Master tasks.json",
        ImportFormat.Checklist => "a markdown checklist",
        _ => "nothing deterministic",
    };

    /// <summary>Strip the emphasis, links and braces that would otherwise land in a stage title or a
    /// tracker row — including the <c>[P]</c> parallel marker spec-kit puts in front of a task.</summary>
    internal static string CleanTitle(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.StartsWith("[P]", StringComparison.OrdinalIgnoreCase)) s = s[3..].Trim();
        s = BraceRegex.Replace(s, "");
        s = s.Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
        s = s.Replace("|", "/", StringComparison.Ordinal); // a pipe would split the tracker row it lands in
        return s.Trim().Trim('-', ':').Trim();
    }

    /// <summary>Shape a bridge's stages into the import contract. <paramref name="dependsOn"/> is the
    /// source's own ordering when it declares one (Task-Master's <c>dependencies</c>); the default is
    /// the linear chain the other two documents imply, so the readiness order matches the document
    /// order rather than being invented.</summary>
    internal static ImportResult Build(
        IReadOnlyList<(string Id, string Title, List<ImportedCheckpoint> Rows)> stages,
        Func<string, List<string>?>? dependsOn = null)
    {
        var result = new ImportResult();
        string? prev = null;
        foreach (var (id, title, rows) in stages)
        {
            result.Stages.Add(new StageConfig
            {
                Id = id,
                Title = title.Length > 0 ? title : id,
                Sessions = Math.Max(2, rows.Count),
                Kind = "deliver",
                DependsOn = dependsOn is null
                    ? (prev is null ? null : [prev])
                    : dependsOn(id) is { Count: > 0 } declared ? declared : null,
            });
            result.Checkpoints.AddRange(rows);
            prev = id;
        }
        return result;
    }

    /// <summary>The shapes the engine's readers require, asserted where the ids are minted rather
    /// than discovered when a demo silently claims nothing.</summary>
    public static bool IsDrivableStageId(string id) => StageIdRegex.IsMatch(id ?? "");

    /// <summary>As <see cref="IsDrivableStageId"/>, for a checkpoint row.</summary>
    public static bool IsDrivableCheckpointId(string id) => CheckpointIdRegex.IsMatch(id ?? "");
}
