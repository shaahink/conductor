using Conductor.Core.Planning;

namespace Conductor.Core;

public sealed record CheckpointRow(string Id, string Title, string Status, string Commit, string Evidence)
{
    public string StageId => Id.Split('.')[0];
    public bool IsDone => Status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase);
    public bool IsBlocked => Status.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase);
    public bool IsInProgress => Status.StartsWith("IN", StringComparison.OrdinalIgnoreCase);
}

public sealed class TrackerSnapshot
{
    public List<CheckpointRow> Checkpoints { get; init; } = new();
    public string HandoffBlock { get; init; } = "";
    public string RawText { get; init; } = "";

    public bool AllDone => Checkpoints.Count > 0 && Checkpoints.All(c => c.IsDone);

    public IEnumerable<CheckpointRow> ForStage(string stageId)
        => Checkpoints.Where(c => c.StageId.Equals(stageId, StringComparison.OrdinalIgnoreCase));

    public bool StageDone(string stageId)
    {
        var rows = ForStage(stageId).ToList();
        return rows.Count > 0 && rows.All(r => r.IsDone);
    }

    public CheckpointRow? ById(string id)
        => Checkpoints.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Back-compat facade over the default <see cref="MarkdownTableProvider"/>. Existing call sites and
/// tests keep using <c>TrackerParser.Parse/ParseFile</c>; the engine's new seam is
/// <see cref="IProgressProvider"/>. Both share the exact same parsing code, so behaviour is identical.
/// </summary>
public static class TrackerParser
{
    public static TrackerSnapshot Parse(string trackerText) => MarkdownTableProvider.Parse(trackerText);

    public static TrackerSnapshot ParseFile(string path) => MarkdownTableProvider.ParseFile(path);
}
