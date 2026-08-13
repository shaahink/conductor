namespace Conductor.Core.Planning;

/// <summary>
/// KS0.3, bug #20 — which plan a verb without <c>-p</c> is talking about.
/// <para>The old order was <c>-p</c> → <c>CONDUCTOR_PLAN</c> → discovery, and the middle step is the
/// bug: a session's environment carries <c>CONDUCTOR_PLAN</c> pointing at the plan that spawned it
/// (<c>SessionRunner</c> sets it deliberately), so a scratch rig launched from inside that session —
/// its own repo, its own single plan file, its own state dir — resolved to the DRIVING run's plan and
/// wrote into it. The F0–R0 phantom stages in <c>plans/karvan/CORE-TRACKER.md</c> are what that costs:
/// a throwaway rig edited a live run's plan and nobody noticed for an era.</para>
/// <para>The new order puts the directory you are standing in ahead of an inherited variable, but only
/// when the directory answers the question UNAMBIGUOUSLY — exactly one <c>*.plan.json</c> candidate.
/// Ambiguity is what the environment variable is for, so a tree with several plans (this repo has
/// eleven under <c>plans/</c>) still resolves through it, and every in-session <c>conductor task</c>
/// keeps hitting the run that spawned it. An override is never silent: it names both files.</para>
/// <para>Pure — no console, no throwing, no filesystem beyond what the caller already scanned — so the
/// rule is unit-testable and <see cref="Conductor.Commands.PlanSettings"/> stays the thin shell.</para>
/// </summary>
public static class PlanResolution
{
    /// <summary>A resolved plan path, plus whatever the operator needs told about it. <paramref
    /// name="Path"/> is null when the candidates alone cannot decide (zero, or several with no
    /// environment variable) — the shell then prompts or fails, as it always did.</summary>
    /// <param name="Path">The chosen plan file, or null when the caller must decide.</param>
    /// <param name="Note">Informational: which file was picked when nothing was specified.</param>
    /// <param name="Warning">An override happened and the operator gets to see it.</param>
    public sealed record Choice(string? Path, string? Note = null, string? Warning = null);

    public static Choice Decide(string? explicitPlan, string? envPlan,
                                IReadOnlyList<PlanDiscovery.Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // -p is someone saying it out loud; nothing outranks that.
        if (!string.IsNullOrWhiteSpace(explicitPlan)) return new Choice(explicitPlan);

        var env = string.IsNullOrWhiteSpace(envPlan) ? null : envPlan.Trim();
        var here = candidates.Count == 1 ? candidates[0].Path : null;

        if (here != null)
        {
            if (env == null) return new Choice(here, Note: $"using {here}");
            if (SamePath(here, env)) return new Choice(env);
            return new Choice(here, Warning:
                $"CONDUCTOR_PLAN points at {env}, but this directory has its own plan: {here}. " +
                "Using the one in this directory, so a rig cannot silently drive the run that " +
                "spawned it. Pass -p <path> to choose deliberately.");
        }

        // Zero candidates, or several: the variable is the tie-breaker it was always meant to be.
        return env != null ? new Choice(env) : new Choice(null);
    }

    /// <summary>Same file by path, without touching the disk — the two spellings that actually turn up
    /// are a relative <c>CONDUCTOR_PLAN</c> against an absolute discovery hit, and a case difference on
    /// Windows.</summary>
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or NotSupportedException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
