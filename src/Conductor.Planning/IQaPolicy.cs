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

    /// <summary>W4.4: the same projection with the CLAIMED work item's override on top —
    /// <c>verify</c> or <c>off</c> beats the stage dial, which beats the plan dial. Null/empty/
    /// <c>inherit</c> is the absence of an override and must project identically to the two-argument
    /// form. Default implementation ignores the item, so an older policy keeps compiling and behaves
    /// exactly as before.</summary>
    QaProjection Project(QaRule? planRule, QaRule? stageRule, string? itemQa) => Project(planRule, stageRule);
}
