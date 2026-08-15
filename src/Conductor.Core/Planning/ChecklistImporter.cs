using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>KS3.5 — the plainest bridge of the three: a markdown checklist. Headings become stages
/// (<c>C1</c>, <c>C2</c>, …) and checkbox items become their checkpoints (<c>C1.1</c>), with no model
/// call. This is the format nobody standardised — a README section, a hand-written TODO — and it is
/// the one most people actually have.
/// <para>Deliberately last in <see cref="ImportBridge.Read"/>: spec-kit's task lines ARE checkbox
/// items, so this reader would happily claim a spec-kit document and mint worse ids for it.</para></summary>
public static class ChecklistImporter
{
    // "- [ ] Do the thing" / "* [x] Done thing"
    private static readonly Regex ItemRegex = new(
        @"^\s*[-*]\s*\[(?<done>[ xX])\]\s*(?<title>.*?)\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    private static readonly Regex HeadingRegex = new(
        @"^\s{0,3}#{1,4}\s+(?<text>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture, ProgressConventions.RegexTimeout);

    /// <summary>Two or more checkbox items. One is a sentence with brackets in it.</summary>
    public static bool Looks(string text)
        => !string.IsNullOrWhiteSpace(text) && ItemRegex.Matches(text).Count >= 2;

    public static ImportResult? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var headings = HeadingRegex.Matches(text)
            .Select(m => (Index: m.Index, Text: ImportBridge.CleanTitle(m.Groups["text"].Value)))
            .ToList();

        var stages = new List<(string Id, string Title, List<ImportedCheckpoint> Rows)>();
        var idByHeading = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match item in ItemRegex.Matches(text))
        {
            var heading = headings.LastOrDefault(h => h.Index < item.Index);
            var headingText = heading.Text is { Length: > 0 } ? heading.Text : "Checklist";
            if (!idByHeading.TryGetValue(headingText, out var id))
            {
                id = $"C{stages.Count + 1}";
                idByHeading[headingText] = id;
                stages.Add((id, headingText, []));
            }
            var stage = stages.First(s => string.Equals(s.Id, id, StringComparison.Ordinal));
            stage.Rows.Add(new ImportedCheckpoint
            {
                Id = $"{id}.{stage.Rows.Count + 1}",
                Title = ImportBridge.CleanTitle(item.Groups["title"].Value),
                // "[x]" is delivered work; "[ ]" is the default TODO the tracker writes.
                Status = string.IsNullOrWhiteSpace(item.Groups["done"].Value) ? null : "DONE",
            });
        }

        var live = stages.Where(s => s.Rows.Count > 0).ToList();
        return live.Count == 0 ? null : ImportBridge.Build(live);
    }
}
