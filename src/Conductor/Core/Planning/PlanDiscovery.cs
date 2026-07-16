using System.Text.Json;

namespace Conductor.Core.Planning;

/// <summary>
/// U0.1 — plan-file discovery for <see cref="Conductor.Commands.PlanSettings.ResolvePlanPath"/>. Pure:
/// no console I/O, no throwing, no side effects — the caller (which knows whether the session is
/// interactive) decides what to do with zero/one/many candidates. Search order: files matching
/// <c>*.plan.json</c> directly in <paramref name="cwd"/>; if none, the same glob under
/// <c>&lt;cwd&gt;/plans/</c>. A directory with any matches wins outright (even if there's more than
/// one) — the fallback to <c>plans/</c> only happens when cwd has zero matches.
/// </summary>
public static class PlanDiscovery
{
    public sealed record Candidate(string Name, string Path);

    public static IReadOnlyList<Candidate> Discover(string cwd)
    {
        var inCwd = FindPlanFiles(cwd);
        var pool = inCwd.Count > 0 ? inCwd : FindPlanFiles(Path.Combine(cwd, "plans"));
        return pool
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new Candidate(ReadPlanName(p) ?? Path.GetFileNameWithoutExtension(p), p))
            .ToList();
    }

    private static List<string> FindPlanFiles(string dir)
        => Directory.Exists(dir) ? Directory.GetFiles(dir, "*.plan.json").ToList() : [];

    /// <summary>Cheap peek at the plan's <c>name</c> field for the picker label — best-effort, never
    /// throws (an unreadable/malformed candidate just falls back to its filename).</summary>
#pragma warning disable MA0045 // sync by design: called from Spectre.Cli settings resolution, a sync-only seam
    private static string? ReadPlanName(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
#pragma warning restore MA0045
}
