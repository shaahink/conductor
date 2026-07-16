namespace Conductor.Planning;

/// <summary>The full decision for "what comes after this session" (P4): the resolved next step
/// (null = workflow exhausted, stage complete), every hop the walk took — including verification
/// steps consumed as skipped-as-passed — and, on exhaustion, the index the workflow ran out at
/// (the recorded index the walk started from, which the resolver removes on exhaustion).</summary>
public sealed class WorkflowAdvance
{
    public WorkflowStep? Next { get; init; }
    public IReadOnlyList<WorkflowHop> Hops { get; init; } = [];
    public int? ExhaustedFromIndex { get; init; }
}
