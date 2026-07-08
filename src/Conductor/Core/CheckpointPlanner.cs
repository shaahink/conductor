namespace Conductor.Core;

/// <summary>
/// B9.2: deterministic decomposition that splits a checkpoint title on common separators
/// (arrow, plus, em-dash, semicolon). Each resulting segment becomes one ordered sub-task.
/// This is the seed — the real planner persona (agent session) refines tasks via MCP in B9.3.
/// </summary>
public sealed class CheckpointPlanner : IPlanner
{
    // Note: \u2192 is the rightwards arrow (→), \u2014 is the em-dash (—).
    // Encoded as Unicode escapes to survive text-file round-trips.
    private static readonly char[] Splitters = ['\u2192', '+', '\u2014', ';'];

    public IReadOnlyList<PlannedTask> Decompose(string checkpointId, string checkpointTitle, string stageNotes)
    {
        var segments = checkpointTitle
            .Split(Splitters, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select((title, i) => new PlannedTask(title, i + 1))
            .ToList();

        if (segments.Count == 0)
            segments.Add(new PlannedTask(checkpointTitle, 1));

        return segments;
    }
}
