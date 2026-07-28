namespace Conductor.Planning;

/// <summary>Runtime variables available to WorkflowStep.RunIf / SkipIf expressions. A plain POCO of
/// facts — the ONLY runtime shape the planning library ever sees. The engine adapts its own records
/// (SessionRecord etc.) into this; the library never reaches back for more.</summary>
public sealed class WorkflowRuntimeVars
{
    public int? VerifierScore { get; set; }
    public bool VerifierPassed { get; set; }
    public bool CircuitBroken { get; set; }
    public int StageAttempts { get; set; }
    public bool GatesGreen { get; set; }
    public bool HasCommits { get; set; }
    public bool Stalled { get; set; }
    public int NewlyDoneCount { get; set; }
    public bool StageComplete { get; set; }
}
