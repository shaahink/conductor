namespace Conductor.Planning;

/// <summary>One resolution hop of a post-session advance (P4): the step the walk landed on and
/// whether it was consumed as a skipped-as-passed verification. The engine effects each hop
/// (logging; confirming checkpoints for skips) — the decision itself lives here.</summary>
public sealed record WorkflowHop(int FromIndex, int ToIndex, WorkflowStep Step, bool SkippedAsPassed);
