using Conductor.Models;

namespace Conductor.Core.Providers;

/// <summary>
/// KS7.1 — everything the permission posture decides, as pure functions over config and args, so the
/// posture can be asserted without launching a process. Three decisions live here and nowhere else:
/// what the settings file says, what flag the command line gains, and what flag it loses.
/// </summary>
/// <remarks>
/// The measured behaviour these functions encode (claude 2.1.235, print mode — the probes are in the
/// KS7.1 evidence file, not inferred from docs):
/// <list type="bullet">
///   <item>a bare tool name in <c>permissions.deny</c> removes the tool from the advertised set;</item>
///   <item>a specifier (<c>Bash(git:*)</c>) refuses the call and emits <c>{"type":"system",
///   "subtype":"permission_denied"}</c> — parsed by <see cref="ClaudeProvider"/>;</item>
///   <item>deny is enforced <b>with the bypass flag on</b>, so containment does not depend on
///   dropping it — which is why <see cref="StripBypass"/> is driven by an explicit mode and never
///   inferred from the presence of a deny list;</item>
///   <item><c>permissions.allow</c> pre-approves; it does not gate. Print mode runs what is not denied.</item>
/// </list>
/// </remarks>
public static class PermissionPosture
{
    /// <summary>The bypass spellings the installed CLI accepts. Both are stripped when a posture asks
    /// for a non-bypass mode: <c>--allow-dangerously-skip-permissions</c> only OFFERS the bypass
    /// rather than enabling it, but leaving it in a restricted profile's command line still advertises
    /// an escape hatch the posture says is closed.</summary>
    public static readonly IReadOnlyList<string> BypassFlags =
        ["--dangerously-skip-permissions", "--allow-dangerously-skip-permissions"];

    /// <summary>The <c>permissions</c> object for the settings file conductor writes, or null when
    /// the block asks for nothing. Only non-empty members are emitted: a settings file carrying
    /// <c>"deny": []</c> reads, to anyone auditing it, like a posture that was considered and left
    /// empty, which is not what an unset field means.</summary>
    public static Dictionary<string, object>? SettingsFragment(PermissionsConfig? p)
    {
        if (p is null || !p.IsConfigured) return null;
        var frag = new Dictionary<string, object>(StringComparer.Ordinal);
        // `defaultMode` is the settings-file spelling of the same idea `--permission-mode` carries on
        // the command line. Both are set when a mode is configured: the flag wins for the session, and
        // the file makes the profile self-describing when it is read back as evidence.
        if (!string.IsNullOrWhiteSpace(p.Mode)) frag["defaultMode"] = p.Mode!.Trim();
        if (p.Allow.Count > 0) frag["allow"] = p.Allow.ToArray();
        if (p.Deny.Count > 0) frag["deny"] = p.Deny.ToArray();
        return frag.Count > 0 ? frag : null;
    }

    /// <summary>The flags the orchestrator appends for this posture. Empty when no mode is
    /// configured, and empty when the plan already passes a permission flag of its own — a plan that
    /// hand-wires its command line keeps full control rather than receiving a second, conflicting
    /// mode, which is the same rule <c>--mcp-config</c> and <c>--settings</c> already follow.</summary>
    public static IReadOnlyList<string> ExtraArgs(PermissionsConfig? p, IReadOnlyList<string>? plannedArgs)
    {
        var mode = p?.Mode?.Trim();
        if (string.IsNullOrEmpty(mode)) return [];
        if (plannedArgs != null && plannedArgs.Any(a => a.StartsWith("--permission-mode", StringComparison.Ordinal)))
            return [];
        return ["--permission-mode", mode];
    }

    /// <summary>Removes the bypass flags from resolved args when the posture asks for a restricted
    /// mode. Returns the SAME list instance when nothing is stripped, so the overwhelmingly common
    /// no-posture path allocates nothing and cannot reorder a plan's carefully positioned args.</summary>
    public static List<string> StripBypass(List<string> args, PermissionsConfig? p)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (p is null || !p.WantsRestrictedMode) return args;
        if (!args.Any(IsBypassFlag)) return args;
        return args.Where(a => !IsBypassFlag(a)).ToList();
    }

    /// <summary>How many bypass flags <see cref="StripBypass"/> would remove — the number a run log
    /// line quotes, so "the posture changed the command line" is a measurement and not a claim.</summary>
    public static int BypassFlagCount(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count(IsBypassFlag);
    }

    private static bool IsBypassFlag(string a) =>
        BypassFlags.Contains(a, StringComparer.Ordinal);

    /// <summary>One line for the run log describing the posture actually applied — mode, rule counts,
    /// and whether a bypass flag was taken off the command line. Callers log it verbatim; there is no
    /// second phrasing of the posture anywhere.</summary>
    public static string Describe(PermissionsConfig? p, int strippedBypassFlags)
    {
        if (p is null || !p.IsConfigured) return "KS7.1: no permission posture configured — the plan's own args decide";
        var mode = string.IsNullOrWhiteSpace(p.Mode) ? "(plan's own)" : p.Mode!.Trim();
        var bypass = strippedBypassFlags > 0 ? $", {strippedBypassFlags} bypass flag(s) stripped" : "";
        return $"KS7.1: permission posture — mode {mode}, {p.Deny.Count} deny rule(s), {p.Allow.Count} allow rule(s){bypass}";
    }
}
