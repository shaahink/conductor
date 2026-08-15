using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>KS3.5 — the spec-kit bridge. GitHub's spec-kit writes a <c>tasks.md</c> of phase headings
/// and numbered task lines (<c>## Phase 3.1: Setup</c>, <c>- [ ] T001 Create the project skeleton</c>,
/// <c>- [x] T004 [P] Contract test …</c>); this turns one into stages and checkpoints with NO model
/// call, which is the whole point — a plan you already wrote should not cost a model round trip to
/// import.
/// <para>The ids it mints are not cosmetic. A phase becomes <c>P31</c> and a task becomes
/// <c>P31.T001</c> because those are the shapes the engine's own readers require: stage ids match
/// <c>[A-Za-z]{1,4}\d+</c> and checkpoint ids <c>[A-Za-z]{1,4}\d+\.[A-Za-z0-9]+</c> (the tracker
/// table regex, the work graph's prefix link, and <c>FakeAgentCommand</c>'s row picker). A verbatim
/// <c>T001</c> checkpoint id has no stage prefix, so nothing would ever claim it.</para></summary>
public static class SpecKitImporter
{
    // "- [ ] T001 Create project structure" / "- [x] T004 [P] Contract test POST /api/users"
    private static readonly Regex TaskRegex = new(
        @"^\s*[-*]\s*\[(?<done>[ xX])\]\s*(?<id>T\d{1,4})\b[.:]?\s*(?<title>.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    // "## Phase 3.1: Setup" / "## Phase 1 - Setup" / "## Implementation"
    private static readonly Regex HeadingRegex = new(
        @"^\s{0,3}#{2,4}\s+(?<text>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    private static readonly Regex PhaseNumberRegex = new(
        @"^Phase\s+(?<n>\d+(?:\.\d+)*)\s*[:\-–—]?\s*(?<rest>.*)$",
        RegexOptions.ExplicitCapture | RegexOptions.IgnoreCase, ProgressConventions.RegexTimeout);

    /// <summary>Content detection, never the filename: two or more spec-kit task lines. One is an
    /// accident in a design doc; two is the format.</summary>
    public static bool Looks(string text)
        => !string.IsNullOrWhiteSpace(text) && TaskRegex.Matches(text).Count >= 2;

    /// <summary>Phases in document order, each carrying the tasks that follow it. Tasks that appear
    /// before any heading get a synthetic first phase — a tasks.md with no headings at all is still a
    /// plan, and refusing it would push it to the advisor for no reason.</summary>
    public static ImportResult? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var stages = new List<(string Id, string Title, List<ImportedCheckpoint> Rows)>();
        var headings = HeadingRegex.Matches(text)
            .Select(m => (Index: m.Index, Text: m.Groups["text"].Value.Trim()))
            .ToList();

        // One id per heading TEXT, minted the first time that heading owns a task — so two unnumbered
        // headings cannot collide on the same ordinal, and a heading that owns nothing costs nothing.
        var idByHeading = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match t in TaskRegex.Matches(text))
        {
            var heading = headings.LastOrDefault(h => h.Index < t.Index);
            var headingText = heading.Text is { Length: > 0 } ? heading.Text : "Tasks";
            if (!idByHeading.TryGetValue(headingText, out var id))
            {
                var (minted, title) = StageFrom(headingText, stages.Count + 1);
                while (stages.Exists(s => string.Equals(s.Id, minted, StringComparison.Ordinal)))
                    minted = $"P{stages.Count + 1}{stages.Count}";
                id = minted;
                idByHeading[headingText] = id;
                stages.Add((id, title, []));
            }
            var stage = stages.First(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            var done = !string.IsNullOrWhiteSpace(t.Groups["done"].Value);
            var rowId = $"{id}.{t.Groups["id"].Value}";
            if (stage.Rows.Exists(r => string.Equals(r.Id, rowId, StringComparison.OrdinalIgnoreCase))) continue;
            stage.Rows.Add(new ImportedCheckpoint
            {
                Id = rowId,
                Title = ImportBridge.CleanTitle(t.Groups["title"].Value),
                Status = done ? "DONE" : null,
            });
        }

        // A "## Dependencies" or "## Notes" section carries no tasks, so it never becomes a stage —
        // only headings that actually own work do. Stages with no rows cannot be driven.
        var live = stages.Where(s => s.Rows.Count > 0).ToList();
        return live.Count == 0 ? null : ImportBridge.Build(live);
    }

    /// <summary>"Phase 3.1: Setup" → (P31, "Setup"); "Implementation" → (P&lt;n&gt;, "Implementation").
    /// The number is squashed rather than dotted because a stage id may not contain a dot — the dot is
    /// what separates a stage from its checkpoint everywhere in this engine.</summary>
    private static (string Id, string Title) StageFrom(string heading, int ordinal)
    {
        var m = PhaseNumberRegex.Match(heading);
        if (!m.Success)
            return ($"P{ordinal}", ImportBridge.CleanTitle(heading));
        var digits = m.Groups["n"].Value.Replace(".", "", StringComparison.Ordinal);
        var rest = ImportBridge.CleanTitle(m.Groups["rest"].Value);
        return ($"P{digits}", rest.Length > 0 ? rest : $"Phase {m.Groups["n"].Value}");
    }
}
