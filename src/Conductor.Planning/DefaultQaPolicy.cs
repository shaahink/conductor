namespace Conductor.Planning;

/// <summary>The default QA dial projection (P2). The workflows already ARE the modes, so the dial
/// just names them: off → deliver-verify with verification skipped (the existing override machinery:
/// deliver-only runs, checkpoints auto-confirm); everySession → deliver-verify; phaseGate →
/// big-dev-then-big-audit. Exactly what a plan author would pick by hand.</summary>
public sealed class DefaultQaPolicy : IQaPolicy
{
    public const string ModeOff = "off";
    public const string ModeEverySession = "everySession";
    public const string ModePhaseGate = "phaseGate";

    /// <summary>Whole-rule precedence: a stage dial replaces the plan dial for that stage. Shared by
    /// every consumer of the dial (the projection, the prompt's threshold var) so precedence is
    /// defined once.</summary>
    public static QaRule? EffectiveRule(QaRule? planRule, QaRule? stageRule) => stageRule ?? planRule;

    /// <summary>Modes come from user JSON — compared case-insensitively everywhere.</summary>
    public static bool IsValidMode(string? mode) =>
        Is(mode, ModeOff) || Is(mode, ModeEverySession) || Is(mode, ModePhaseGate);

    public QaProjection Project(QaRule? planRule, QaRule? stageRule)
    {
        var rule = EffectiveRule(planRule, stageRule);
        if (rule is null) return QaProjection.Classic;

        // An unknown mode projects to classic; plan validation rejects it before a plan can load,
        // so a typo'd dial can never silently no-op a live run.
        (string workflow, bool skip)? mode =
            Is(rule.Mode, ModeOff) ? ("deliver-verify", true)
            : Is(rule.Mode, ModeEverySession) ? ("deliver-verify", false)
            : Is(rule.Mode, ModePhaseGate) ? ("big-dev-then-big-audit", false)
            : null;
        if (mode is not { } m) return QaProjection.Classic;

        return new QaProjection
        {
            WorkflowName = m.workflow,
            SkipVerification = m.skip,
            VerifierThreshold = rule.VerifierThreshold,
            AuditCoversPriorSessions = rule.AuditCoversPriorSessions,
        };
    }

    private static bool Is(string? mode, string candidate) =>
        string.Equals(mode, candidate, StringComparison.OrdinalIgnoreCase);
}
