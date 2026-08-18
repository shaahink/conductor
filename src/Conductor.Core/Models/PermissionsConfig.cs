namespace Conductor.Models;

/// <summary>
/// KS7.1 — the permission posture an unattended session runs under, as plan config rather than as a
/// flag baked into every plan's <c>agent.args</c>.
///
/// <para>What this is NOT, measured rather than assumed (claude 2.1.235, <c>-p</c> print mode, four
/// live probes recorded in the KS7.1 evidence): <see cref="Allow"/> is <b>not</b> a whitelist. In
/// print mode the CLI auto-approves anything that is not explicitly denied, under every permission
/// mode tried — <c>acceptEdits</c>, <c>manual</c> and <c>dontAsk</c> all ran a Bash command that no
/// allow rule covered. So an allow list narrows nothing; it only pre-approves what would be asked
/// about in an interactive session, which an unattended run never has.</para>
///
/// <para>What this IS: <see cref="Deny"/> is a real, enforced boundary, and it is enforced
/// <b>independently of</b> <c>--dangerously-skip-permissions</c> — the same rule fired with the
/// bypass flag on. A bare tool name (<c>"WebFetch"</c>) removes the tool from the set the model is
/// even told about; a specifier (<c>"Bash(curl:*)"</c>) intercepts the call and emits a
/// <c>permission_denied</c> event on the stream, which is where <see cref="Conductor.Core.Events.ToolRefused"/>
/// comes from. That asymmetry is the whole posture story: the boundary is the deny list, not the
/// flag, and it is stated honestly in ARCHITECTURE.md rather than implied by a mode name.</para>
/// </summary>
public sealed class PermissionsConfig
{
    /// <summary>The <c>--permission-mode</c> the session runs under. Null means "do not pass one" —
    /// the plan's own args decide, exactly as before this config existed. Setting anything other
    /// than <see cref="ModeBypass"/> also STRIPS a bypass flag out of the resolved args, because a
    /// posture that says one thing while the command line says another is worse than no posture.</summary>
    public string? Mode { get; set; }

    /// <summary>Pre-approval rules (<c>permissions.allow</c>). Honest about its reach: this buys
    /// nothing in an unattended print-mode run — see the type remarks — and is carried so a plan can
    /// still express intent and so an interactive replay of the same profile behaves the same.</summary>
    public List<string> Allow { get; set; } = new();

    /// <summary>Refusal rules (<c>permissions.deny</c>) — the posture's only teeth. A bare tool name
    /// removes the tool; a specifier refuses the call and telemeters it.</summary>
    public List<string> Deny { get; set; } = new();

    // The six spellings `claude --permission-mode` accepts on 2.1.235, verified against the installed
    // CLI's own help output rather than a doc. Anything else is refused BY NAME at plan load: a typo
    // that silently falls back to the default posture is indistinguishable, from the outside, from a
    // posture that was applied and did nothing — which is the exact failure this checkpoint exists
    // to remove.
    public const string ModeAcceptEdits = "acceptEdits";
    public const string ModeAuto = "auto";
    public const string ModeBypass = "bypassPermissions";
    public const string ModeManual = "manual";
    public const string ModeDontAsk = "dontAsk";
    public const string ModePlan = "plan";

    public static readonly IReadOnlyList<string> KnownModes =
        [ModeAcceptEdits, ModeAuto, ModeBypass, ModeManual, ModeDontAsk, ModePlan];

    /// <summary>True when this block asks for anything at all. An empty block is not a posture and
    /// must not cause conductor to write a settings file or add a flag it would not otherwise add.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Mode) || Allow.Count > 0 || Deny.Count > 0;

    /// <summary>True when the posture asks for something other than "bypass everything". This is what
    /// makes the bypass flag get stripped; it is deliberately false for a null mode, so a plan that
    /// only sets a deny list keeps its own command line untouched.</summary>
    public bool WantsRestrictedMode =>
        !string.IsNullOrWhiteSpace(Mode) && !string.Equals(Mode!.Trim(), ModeBypass, StringComparison.Ordinal);

    /// <summary>KS7.1 — the config gate, as a sentence rather than a branch inside a command, so the
    /// bar "refuses BY NAME, never a silent fallback" is asserted against the text itself. null means
    /// the block is coherent. Absent is not wrong: a null mode is "the plan's args decide".</summary>
    public string? ModeRefusal()
    {
        var mode = Mode?.Trim();
        if (string.IsNullOrEmpty(mode)) return null;
        if (KnownModes.Contains(mode, StringComparer.Ordinal)) return null;
        return $"agent.permissions.mode '{mode}' is not a permission mode. it is one of: {string.Join(", ", KnownModes)}.";
    }

    /// <summary>Merge semantics matching the rest of <see cref="AgentConfig"/>: a stage-level block
    /// replaces the plan-level one field by field, and an unset field falls through rather than
    /// clearing what the plan said.</summary>
    public PermissionsConfig Merge(PermissionsConfig? o)
    {
        if (o == null) return this;
        return new PermissionsConfig
        {
            Mode = string.IsNullOrWhiteSpace(o.Mode) ? Mode : o.Mode,
            Allow = o.Allow.Count > 0 ? o.Allow : Allow,
            Deny = o.Deny.Count > 0 ? o.Deny : Deny,
        };
    }
}
