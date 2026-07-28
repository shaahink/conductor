using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Core;

public sealed record CheckpointRow(string Id, string Title, string Status, string Commit, string Evidence)
{
    /// <summary>Owning stage id. The parameterless default is Loom's split-on-first-dot; providers with
    /// configured conventions set it via <see cref="Create"/> (B1.4).</summary>
    public string StageId { get; init; } = Id.Split('.')[0];
    public bool IsDone { get; init; } = Status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase);
    public bool IsBlocked { get; init; } = Status.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase);
    public bool IsInProgress { get; init; } = Status.StartsWith("IN", StringComparison.OrdinalIgnoreCase);

    /// <summary>Build a row whose stage id and status flags honour the given conventions (B1.4). All
    /// fields are trimmed, matching the original parser.</summary>
    public static CheckpointRow Create(
        ProgressConventions conventions, string id, string title, string status, string commit, string evidence)
    {
        id = id.Trim();
        status = status.Trim();
        return new CheckpointRow(id, title.Trim(), status, commit.Trim(), evidence.Trim())
        {
            StageId = conventions.DeriveStageId(id),
            IsDone = conventions.IsDone(status),
            IsBlocked = conventions.IsBlocked(status),
            IsInProgress = conventions.IsInProgress(status),
        };
    }
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
