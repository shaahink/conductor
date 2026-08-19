namespace Conductor.Models;

/// <summary>
/// The rules a plan's <see cref="GateConfig"/> list must satisfy, gathered in one place rather than
/// scattered down <see cref="PlanConfig.CollectErrors"/>. Both callers of that method — the fail-fast
/// <see cref="PlanConfig.Load"/> and the host-start options validator — get these for free.
/// </summary>
public static class GateRules
{
    public static IEnumerable<string> CollectErrors(IReadOnlyList<GateConfig> gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        return Enumerate(gates);
    }

    private static IEnumerable<string> Enumerate(IReadOnlyList<GateConfig> gates)
    {
        if (gates.Any(g => string.IsNullOrWhiteSpace(g.Command)))
            yield return "a gate is missing its command — every gate needs a shell command to run";

        // KS4.1: a typo'd visibility must never project to "visible" in silence — that is P2's QA-dial
        // shape again, and here it would quietly publish a gate the plan meant to hide.
        foreach (var g in gates.Where(g => !GateVisibility.IsKnown(g.Visibility)))
            yield return $"gate '{g.Name}' has visibility '{g.Visibility}' — only {string.Join(" or ", GateVisibility.Known)} are accepted";

        // KS4.1: the redacted name is what every holdout is CALLED once it leaves the runner. A visible
        // gate wearing it is indistinguishable from a holdout's result in the summary, the spill
        // filename and the store row — the one place the anonymity could be read backwards.
        if (gates.Any(g => !g.IsHoldout && g.Name.Equals(GateVisibility.RedactedName, StringComparison.OrdinalIgnoreCase)))
            yield return $"a visible gate is named '{GateVisibility.RedactedName}' — that name is reserved for redacted holdout results; rename the gate";

        // KS4.2, same reasoning as the visibility rule above: an unknown class must not project to
        // "standard" in silence, or a plan that asked for PASS-TO-PASS gets an ordinary exit-code
        // gate and no one is told the difference.
        foreach (var g in gates.Where(g => !GateClass.IsKnown(g.Class)))
            yield return $"gate '{g.Name}' has class '{g.Class}' — only {string.Join(" or ", GateClass.Known)} are accepted";

        // A regression gate that cannot say how to read its passing checks has no baseline to
        // compare, so it would be an ordinary gate wearing the name of a stronger one.
        foreach (var g in gates.Where(g => g.IsRegression && !PassSetConfig.IsKnownFormat(g.PassSet?.Format)))
            yield return $"gate '{g.Name}' is class '{GateClass.Regression}' but its passSet.format is " +
                         $"'{g.PassSet?.Format ?? "(unset)"}' — a regression gate must say how to read the set of " +
                         $"checks that passed ({string.Join(", ", PassSetConfig.Formats)})";

        foreach (var g in gates.Where(g => g.IsRegression && g.PassSet is { } p && p.Is(PassSetConfig.Trx) && string.IsNullOrWhiteSpace(p.Path)))
            yield return $"gate '{g.Name}' reads its pass set from a trx file but declares no passSet.path";

        // Refused rather than half-supported. A holdout's whole contract is that its name, its
        // command and its output never reach the agent (KS4.1) — and a regression's whole value is
        // naming the checks that stopped passing. One of the two has to lose, so the plan is told to
        // choose instead of silently getting the weaker of them.
        foreach (var g in gates.Where(g => g.IsRegression && g.IsHoldout))
            yield return $"gate '{g.Name}' is both '{GateVisibility.Holdout}' and '{GateClass.Regression}' — a regression " +
                         "reports the checks that stopped passing by name, which is exactly what a holdout may not do";

        // KS4.3, the same shape as the regression rules above and for the same reason: a mutation
        // gate whose report the engine cannot find or whose bar is unset is an ordinary gate wearing
        // the name of a stronger one, and the exit code would never say so.
        foreach (var g in gates.Where(g => g.IsMutation && !MutationConfig.IsKnownFormat(g.Mutation?.Format)))
            yield return $"gate '{g.Name}' is class '{GateClass.Mutation}' but its mutation.format is " +
                         $"'{g.Mutation?.Format ?? "(unset)"}' — a mutation gate must say how its report is read " +
                         $"({string.Join(", ", MutationConfig.Formats)})";

        foreach (var g in gates.Where(g => g.IsMutation && string.IsNullOrWhiteSpace(g.Mutation?.Path)))
            yield return $"gate '{g.Name}' is class '{GateClass.Mutation}' but declares no mutation.path — the engine " +
                         "scores the report the gate wrote, so it has to be told where the gate wrote it";

        // A zero threshold is the switch-it-off value and reads in a plan diff as a number rather
        // than as a removal, so it is refused rather than honoured. Above 100 is unreachable, which
        // is a gate that can only ever be red.
        foreach (var g in gates.Where(g => g.IsMutation && g.Mutation is { } m && (m.Threshold <= 0 || m.Threshold > 100)))
            yield return $"gate '{g.Name}' has mutation.threshold {g.Mutation!.Threshold} — a mutation score is a " +
                         "percentage, so the bar has to be above 0 and at most 100";

        // Refused for the reason the regression/holdout pair is: the fix brief for this class NAMES
        // the surviving mutants, file and line, and that is the one thing a holdout may not say.
        foreach (var g in gates.Where(g => g.IsMutation && g.IsHoldout))
            yield return $"gate '{g.Name}' is both '{GateVisibility.Holdout}' and '{GateClass.Mutation}' — a mutation " +
                         "gate reports the surviving mutants by file and line, which is exactly what a holdout may not do";
    }
}
