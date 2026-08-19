namespace Conductor.Models;

/// <summary>
/// KS4.1 — the gate <b>visibility</b> class. A <c>holdout</c> gate is one the coding agent cannot
/// see, name, discover or run: it is excluded from the prompt, from every verb and MCP tool the
/// agent can call, and from every log, report and store row the agent can read. Only the engine
/// runs it, at verdict time, and its result reaches the verdict as the same one bit every other
/// gate contributes (<see cref="Conductor.Core.Orchestration.SessionEvidence.GatesGreen"/>).
/// </summary>
/// <remarks>
/// <para><b>Why a class and not a convention.</b> The 2026 reward-hacking literature's finding is
/// that an agent optimises against the measurement it can read. Every other gate in this engine is
/// readable — by design, because a fix session that cannot see why the battery went red cannot fix
/// it. A holdout gate is the deliberate exception: the one measurement that stays outside the
/// agent's world, so that "the visible gates are green" can never be the whole of the truth.</para>
/// <para><b>The guarantee is structural, not a list of redaction call sites.</b> A holdout gate's
/// name, command, exit code and output never leave <see cref="Conductor.Core.GateRunner"/>. The
/// <see cref="Conductor.Core.GateResult"/> it returns is already anonymous — named
/// <see cref="RedactedName"/>, carrying <see cref="FailureNotice"/> instead of the command's tail —
/// so no downstream renderer, store row, spill file, report or MCP payload has the secret to leak
/// in the first place. That is why this is proved by asserting on the runner's OUTPUT rather than
/// by auditing the thirty-odd surfaces the map in KS4.1's evidence lists.</para>
/// <para><b>Fail closed on the one thing the runner cannot redact: the plan file.</b> A gate's
/// command lives in the plan, and the plan usually lives in the repo the agent is editing — so a
/// holdout gate declared there would be one <c>cat</c> away. <see cref="HoldoutGateSource"/>
/// refuses that at load: a holdout gate's command must come from outside the repo working tree.</para>
/// </remarks>
public static class GateVisibility
{
    /// <summary>The default: the agent may see this gate, run it, and read its output.</summary>
    public const string Visible = "visible";

    /// <summary>Engine-only. Never composed into a prompt, never listed by a verb the agent can
    /// call, never named in a log, report or store row the agent can read.</summary>
    public const string Holdout = "holdout";

    /// <summary>What a holdout gate is called EVERYWHERE outside the runner — its result, its store
    /// row, its spill file, its summary glyph. Deliberately identical for every holdout gate in a
    /// plan: two holdouts must be indistinguishable, or the pair leaks a bit of the answer.</summary>
    public const string RedactedName = "holdout";

    /// <summary>The whole of what a session is told when a holdout fails. It names no gate, no
    /// command, no file and no assertion — but it does say a holdout exists and that it failed,
    /// because a fix session told nothing at all thrashes, and knowing that the CLASS exists is not
    /// what lets an agent optimise against it. Knowing its content is.</summary>
    public const string FailureNotice =
        "A holdout verification failed. Which one, what it runs and what it printed are withheld " +
        "from this session by design (KS4.1): holdout gates exist so that passing the visible " +
        "gates cannot be the whole of the work. Fix the delivery, not the gate.";

    /// <summary>A passing holdout says nothing at all beyond the fact that it ran.</summary>
    public const string PassNotice = "holdout verification passed";

    /// <summary>The two accepted spellings, for the plan-load refusal message.</summary>
    public static readonly string[] Known = [Visible, Holdout];

    public static bool IsKnown(string? value)
        => value is null || Known.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The gate list every surface OUTSIDE the runner is allowed to enumerate: prompts,
    /// <c>conductor journey</c>, doctor's lints, the plan-architect brief, the control-plane DTO.
    /// Reading <c>plan.Gates</c> directly in one of those is the leak this exists to prevent.</summary>
    public static IEnumerable<GateConfig> VisibleOnly(IEnumerable<GateConfig> gates)
    {
        ArgumentNullException.ThrowIfNull(gates);
        return gates.Where(g => !g.IsHoldout);
    }
}
