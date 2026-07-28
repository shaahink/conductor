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

    /// <summary>W4.4: the item's own dial sits above the stage's. An item says only whether it wants
    /// verification, so it maps onto the existing modes (verify → everySession, off → off) and keeps
    /// whatever threshold/audit shape it inherits — the item changes QA FREQUENCY for its own
    /// session, not the shape of the stage around it.</summary>
    public static QaRule? EffectiveRule(QaRule? planRule, QaRule? stageRule, string? itemQa)
    {
        var inherited = EffectiveRule(planRule, stageRule);
        if (string.IsNullOrWhiteSpace(itemQa) || Is(itemQa, "inherit")) return inherited;
        if (!Is(itemQa, ModeOff) && !Is(itemQa, "verify")) return inherited;

        return new QaRule
        {
            Mode = Is(itemQa, ModeOff) ? ModeOff : ModeEverySession,
            VerifierThreshold = inherited?.VerifierThreshold,
            AuditCoversPriorSessions = inherited?.AuditCoversPriorSessions ?? true,
        };
    }

    /// <summary>Modes come from user JSON — compared case-insensitively everywhere.</summary>
    public static bool IsValidMode(string? mode) =>
        Is(mode, ModeOff) || Is(mode, ModeEverySession) || Is(mode, ModePhaseGate);

    public QaProjection Project(QaRule? planRule, QaRule? stageRule) => Project(planRule, stageRule, itemQa: null);

    public QaProjection Project(QaRule? planRule, QaRule? stageRule, string? itemQa)
    {
        var rule = EffectiveRule(planRule, stageRule, itemQa);
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
