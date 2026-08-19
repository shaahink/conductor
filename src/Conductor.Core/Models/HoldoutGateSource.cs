using System.Text.Json;

namespace Conductor.Models;

/// <summary>
/// KS4.1 — where a holdout gate's COMMAND is allowed to live, and the refusal when it lives
/// somewhere the agent can read.
/// </summary>
/// <remarks>
/// <para><see cref="Conductor.Core.GateRunner"/> can redact a holdout gate's name and output out of every result,
/// log and store row. It cannot redact the plan file: <c>conductor.plan.json</c> normally sits in
/// the repo the agent is editing, so a <c>"visibility": "holdout"</c> gate declared there is one
/// <c>cat</c> away from the session it is supposed to be invisible to. The redaction would still
/// work and the whole class would still be worthless.</para>
/// <para>So the rule is a location rule, checked at plan load and failing closed: <b>a holdout
/// gate's command must not be readable inside the repo working tree.</b> Two ways to satisfy it —
/// point <c>plan.holdoutGates</c> at a JSON file outside the repo, or run from a plan file that is
/// itself outside the repo. Anything else is refused BY NAME at load, which is this project's rule
/// for a misconfiguration that would otherwise look like it worked.</para>
/// </remarks>
public static class HoldoutGateSource
{
    /// <summary>Applied by <see cref="PlanConfig.Load"/> after deserialisation and before
    /// validation: enforces the location rule on inline holdout gates, then loads and appends the
    /// gates from <see cref="PlanConfig.HoldoutGates"/> if one is configured.</summary>
    /// <exception cref="InvalidOperationException">The location rule is broken, or the holdout file
    /// is missing or unparseable. Every message names the offending path.</exception>
    public static void Apply(PlanConfig cfg)
    {
        ArgumentNullException.ThrowIfNull(cfg);

        // Repo unset or missing is a different error, reported by PlanConfig.CollectErrors. Without
        // a repo there is no working tree to be inside, so the location rule cannot be evaluated —
        // and refusing here would replace a clear message with a confusing one.
        var repo = SafeFullPath(cfg.Repo);

        var inline = cfg.Gates.Where(g => g.IsHoldout).ToList();
        if (inline.Count > 0 && repo is not null && IsInside(repo, cfg.PlanFilePath))
            throw new InvalidOperationException(
                $"gate '{inline[0].Name}' is declared visibility=holdout inside a plan file that lives in the repo " +
                $"({cfg.PlanFilePath}). A holdout gate the agent can read in the plan is not a holdout — move it to a " +
                "JSON file outside the repo and point plan.holdoutGates at it, or run from a plan file outside the repo.");

        if (string.IsNullOrWhiteSpace(cfg.HoldoutGates)) return;

        var full = Path.IsPathRooted(cfg.HoldoutGates)
            ? Path.GetFullPath(cfg.HoldoutGates)
            : Path.GetFullPath(Path.Combine(PlanDir(cfg), cfg.HoldoutGates));

        if (repo is not null && IsInside(repo, full))
            throw new InvalidOperationException(
                $"plan.holdoutGates '{full}' is inside the repo working tree ({repo}) — the agent can read it, " +
                "so the gates in it would not be holdouts. Move the file outside the repo.");

        if (!File.Exists(full))
            throw new InvalidOperationException(
                $"plan.holdoutGates '{full}' does not exist. A missing holdout file is not an empty one: it would " +
                "silently drop every holdout gate and leave the run reporting green on the visible gates alone.");

        List<GateConfig>? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<List<GateConfig>>(File.ReadAllText(full), PlanConfig.JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"plan.holdoutGates '{full}' is not a JSON array of gates: {ex.Message}", ex);
        }

        if (loaded is null)
            throw new InvalidOperationException($"plan.holdoutGates '{full}' is empty — remove the key or put gates in the file.");

        foreach (var g in loaded)
        {
            // The file IS the declaration of holdout-ness; a gate in it is a holdout whatever it says
            // about itself, so a forgotten "visibility" key cannot quietly produce a visible gate.
            g.Visibility = GateVisibility.Holdout;
            cfg.Gates.Add(g);
        }
    }

    /// <summary>The directory a relative <c>holdoutGates</c> path is resolved against: the plan
    /// file's own directory, so a plan and its holdouts can travel together outside the repo.</summary>
    private static string PlanDir(PlanConfig cfg)
        => string.IsNullOrWhiteSpace(cfg.PlanFilePath)
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(Path.GetFullPath(cfg.PlanFilePath)) ?? Environment.CurrentDirectory;

    private static string? SafeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (PathTooLongException) { return null; }
    }

    /// <summary>Path containment, decided on normalised full paths with a trailing separator so
    /// <c>C:\repo-holdouts</c> is not read as being inside <c>C:\repo</c>.</summary>
    internal static bool IsInside(string root, string? candidate)
    {
        var full = SafeFullPath(candidate);
        if (full is null) return false;
        var rootWithSep = root + Path.DirectorySeparatorChar;
        return full.Equals(root, PathComparison) || full.StartsWith(rootWithSep, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
