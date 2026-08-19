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
    }
}
