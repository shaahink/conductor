using System.Text.RegularExpressions;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>M6.1: deterministic markdown → task-graph parser. Turns a structured plan or tracker
/// document — stage headers like <c>### M6 — Plan authoring</c> plus <c>**M6.1**</c> checkpoint
/// bullets or <c>| M6.1 | … |</c> tracker table rows — into a stage graph with NO model call. This
/// is the zero-spend path for <c>conductor plan import</c>; freeform prose still falls back to the
/// advisor model. Its truth gate: importing <c>docs/MAESTRO-PLAN.md</c> yields stages M1…M9.</summary>
public static class MarkdownPlanParser
{
    // "### M6 — Plan authoring" / "## F7 — Gate caching — subtitle" → id, remainder-after-dash.
    private static readonly Regex HeaderRegex = new(
        @"^\s{0,3}#{2,4}\s+(?<id>[A-Za-z]{1,4}\d+)\b\s*[—–\-:]\s*(?<rest>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    // "- **M6.1** Do the thing." (design-doc checkpoint bullet)
    private static readonly Regex BulletRegex = new(
        @"^\s*[-*]\s+\*\*(?<id>[A-Za-z]{1,4}\d+(?:\.\w+)?)\*\*\s*(?<title>.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    // "| M6.1 | Checkpoint title | Status | …" (tracker table row)
    private static readonly Regex TableRowRegex = new(
        @"^\s*\|\s*(?<id>[A-Za-z]{1,4}\d+\.\w+)\s*\|\s*(?<title>[^|]*?)\s*\|\s*(?<status>[^|]*?)\s*\|",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    // Section separators used across the project's plan docs (em-dash, en-dash, hyphen, colon).
    private static readonly char[] TitleSeparators = ['—', '–', '-', ':'];

    /// <summary>True when the text looks like a structured plan/tracker (≥2 stage headers and at least
    /// one checkpoint bullet or table row). Used to auto-select the deterministic path over the model.</summary>
    public static bool LooksStructured(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return false;
        var headers = HeaderRegex.Matches(markdown).Count;
        var hasCheckpoints = BulletRegex.IsMatch(markdown) || TableRowRegex.IsMatch(markdown);
        return headers >= 2 && hasCheckpoints;
    }

    /// <summary>Parse a structured document into stages + their checkpoints. Stage headers explicitly
    /// annotated as already delivered (a <c>(DONE …)</c> marker, e.g. an M0 bootstrap section) are kept
    /// out of the imported graph — they document finished work, not stages to schedule.</summary>
    public static ParsedPlan Parse(string markdown)
    {
        markdown ??= "";
        var stages = new List<MutableStage>();
        var byId = new Dictionary<string, MutableStage>(StringComparer.OrdinalIgnoreCase);

        foreach (Match h in HeaderRegex.Matches(markdown))
        {
            var id = h.Groups["id"].Value;
            if (byId.ContainsKey(id)) continue; // first header wins; ignore later duplicates
            var rest = h.Groups["rest"].Value.Trim();
            var done = rest.Contains("(DONE", StringComparison.OrdinalIgnoreCase)
                    || rest.Contains("DONE)", StringComparison.OrdinalIgnoreCase);
            var (title, notes) = SplitTitle(rest);
            var stage = new MutableStage(id, title, notes, done);
            stages.Add(stage);
            byId[id] = stage;
        }

        // Checkpoints attach to their stage by id prefix (the part before the first dot), so a bullet
        // or row is never mis-attributed regardless of where it sits relative to its header.
        foreach (Match b in BulletRegex.Matches(markdown))
            AttachCheckpoint(byId, b.Groups["id"].Value, b.Groups["title"].Value, status: null);
        foreach (Match r in TableRowRegex.Matches(markdown))
            AttachCheckpoint(byId, r.Groups["id"].Value, r.Groups["title"].Value, r.Groups["status"].Value);

        return new ParsedPlan([.. stages
            .Where(s => !s.Done)
            .Select(s => new ParsedStage(s.Id, s.Title, s.Notes, s.Checkpoints))]);
    }

    private static void AttachCheckpoint(Dictionary<string, MutableStage> byId, string cpId, string title, string? status)
    {
        var dot = cpId.IndexOf('.', StringComparison.Ordinal);
        var stageId = dot > 0 ? cpId[..dot] : cpId;
        if (!byId.TryGetValue(stageId, out var stage)) return;
        if (stage.Checkpoints.Any(c => string.Equals(c.Id, cpId, StringComparison.OrdinalIgnoreCase))) return;
        var cleanStatus = string.IsNullOrWhiteSpace(status) || status.Trim() == "-" ? null : status.Trim();
        stage.Checkpoints.Add(new ParsedCheckpoint(cpId, Clean(title), cleanStatus));
    }

    /// <summary>Split "Deconstruction — delete the old face" into a short title and the trailing subtitle
    /// (kept as stage notes). A header with no separator becomes the whole title, no notes.</summary>
    private static (string Title, string? Notes) SplitTitle(string rest)
    {
        var idx = -1;
        foreach (var sep in TitleSeparators)
        {
            var candidate = rest.IndexOf($" {sep} ", StringComparison.Ordinal);
            if (candidate >= 0 && (idx < 0 || candidate < idx)) idx = candidate;
        }
        if (idx < 0) return (Clean(rest), null);
        var title = Clean(rest[..idx]);
        var notes = Clean(rest[(idx + 3)..]);
        return (title, string.IsNullOrWhiteSpace(notes) ? null : notes);
    }

    // Strip inline-markdown emphasis/backticks that would otherwise leak into stage titles.
    private static string Clean(string s) => s.Trim().Trim('`', '*', '_').Trim();

    private sealed class MutableStage(string id, string title, string? notes, bool done)
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string? Notes { get; } = notes;
        public bool Done { get; } = done;
        public List<ParsedCheckpoint> Checkpoints { get; } = [];
    }

    /// <summary>Map a parsed document to the plan-import shape. When <paramref name="linearDeps"/> is set,
    /// each imported stage depends on its predecessor (the "mostly linear" default the design docs use),
    /// so the readiness order matches the document order. Sessions are estimated from checkpoint count.</summary>
    public static ImportResult ToImportResult(ParsedPlan parsed, bool linearDeps = true)
    {
        var result = new ImportResult();
        string? prev = null;
        foreach (var s in parsed.Stages)
        {
            result.Stages.Add(new StageConfig
            {
                Id = s.Id,
                Title = s.Title,
                Notes = s.Notes,
                Sessions = Math.Max(2, s.Checkpoints.Count),
                Kind = "deliver",
                DependsOn = linearDeps && prev != null ? [prev] : null,
            });
            prev = s.Id;
        }
        return result;
    }
}
