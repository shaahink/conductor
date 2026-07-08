using System.Text;

namespace Conductor.Core.Events;

/// <summary>
/// B5.4: evidence-based confidence per checkpoint — how many distinct evidence items (tests, files,
/// docs) back each confirmed checkpoint, derived from the tracker row's <c>Evidence</c> field.
/// Replaces a bare "DONE" label with a count the human/ingestor can audit.
/// </summary>
public static class Confidence
{
    /// <summary>One checkpoint's confidence snapshot.</summary>
    public sealed record Entry(string CheckpointId, string StageId, int EvidenceCount, string EvidenceSummary);

    /// <summary>Parse comma- or semicolon-separated evidence paths from a tracker row's Evidence column.
    /// Empty/whitespace yields zero; each non-empty trimmed segment counts as one piece of evidence.</summary>
    public static int CountEvidence(string? evidenceField)
    {
        if (string.IsNullOrWhiteSpace(evidenceField)) return 0;
        return evidenceField.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(s => s.Length > 0);
    }

    /// <summary>Compute per-checkpoint confidence from the tracker snapshot. Only rows actually marked
    /// DONE are included (in-progress / blocked / TODO rows are N/A for "confirmed" confidence).</summary>
    public static List<Entry> Compute(TrackerSnapshot track)
    {
        return track.Checkpoints
            .Where(c => c.IsDone)
            .Select(c => new Entry(c.Id, c.StageId, CountEvidence(c.Evidence),
                string.IsNullOrWhiteSpace(c.Evidence) ? "(none)" : c.Evidence))
            .OrderBy(c => c.CheckpointId)
            .ToList();
    }

    /// <summary>Render one line per confirmed checkpoint suitable for a report section or TUI panel.</summary>
    public static IEnumerable<string> Format(IReadOnlyList<Entry> entries)
    {
        if (entries.Count == 0) { yield return "(no checkpoints confirmed yet)"; yield break; }
        yield return $"checkpoints confirmed: {entries.Count}   with evidence: {entries.Count(e => e.EvidenceCount > 0)}";
        yield return "";
        var wid = entries.Max(e => e.CheckpointId.Length);
        foreach (var e in entries)
        {
            var bar = e.EvidenceCount switch
            {
                0 => "",
                <= 2 => " ·",
                <= 4 => " ··",
                _ => " ···",
            };
            yield return $"  {e.CheckpointId.PadRight(wid)}  {e.EvidenceCount} evidence item(s){bar}  {e.EvidenceSummary}";
        }
    }
}
