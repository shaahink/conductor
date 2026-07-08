using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>
/// The inline <see cref="IProgressProvider"/> (F-1, D-2): checkpoints are declared directly in the plan
/// JSON under <c>progress.checkpoints</c>, so a plan can carry its own progress contract with no separate
/// tracker file. Useful for small/self-contained plans and for tests. A handoff block isn't modelled
/// inline (there's no doc to carry it), so it's empty.
/// </summary>
public sealed class PlanCheckpointProvider(IReadOnlyList<PlanCheckpoint> checkpoints) : IProgressProvider
{
    private readonly IReadOnlyList<PlanCheckpoint> _checkpoints = checkpoints;

    public string Name => "plan-checkpoints";

    public TrackerSnapshot Read(PlanConfig plan)
    {
        var rows = new List<CheckpointRow>(_checkpoints.Count);
        foreach (var c in _checkpoints)
        {
            rows.Add(new CheckpointRow(
                c.Id.Trim(), c.Title.Trim(), c.Status.Trim(), c.Commit.Trim(), c.Evidence.Trim()));
        }

        return new TrackerSnapshot { Checkpoints = rows, HandoffBlock = "", RawText = "" };
    }
}
