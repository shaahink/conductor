namespace Conductor.Planning;

/// <summary>The QA-frequency seam (P2): projects the friendly dial (off / everySession / phaseGate)
/// onto the EXISTING workflow + override machinery — no parallel scheduler, no new engine concept.
/// Implementations must stay pure (a deterministic function of the arguments, no IO, no engine
/// types), same contract as <see cref="IWorkflowResolver"/> and <see cref="IAssignmentPolicy"/>.
/// Resolving a dial value must produce exactly the same run as picking the corresponding workflow
/// by hand — pinned by a unit test comparing resolved definitions.</summary>
public interface IQaPolicy
{
    /// <summary>Project the effective QA rule onto a workflow name + overrides. The stage rule
    /// replaces the plan rule whole for that stage (no field merge — a dial is either set or not).
    /// Both null = <see cref="QaProjection.Classic"/>, exactly today's behavior.</summary>
    QaProjection Project(QaRule? planRule, QaRule? stageRule);
}
