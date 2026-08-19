using System.Text.Json;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>KS4.3 — one surviving mutant, as the fix brief needs to name it.</summary>
public sealed record MutantRow(string File, int Line, string Mutator, string Status)
{
    public override string ToString() => $"{File}:{Line} — {Mutator} ({Status})";
}

/// <summary>
/// KS4.3 — the arithmetic of a mutation gate, over exactly the files that were scored.
/// </summary>
/// <remarks><see cref="Percent"/> counts NO-COVERAGE mutants in the denominator on purpose. Stryker
/// also publishes a "score based on covered code" that omits them, and that number is the one a
/// checkpoint adding untested code would rather be judged by — it rises when a mutant is never
/// executed at all. The whole point of the class is that it cannot be raised by not testing.
/// </remarks>
public sealed record MutationScore(
    int Killed, int Timeout, int Survived, int NoCoverage, int Ignored, int CompileError,
    IReadOnlyList<string> ScoredFiles, IReadOnlyList<MutantRow> Survivors)
{
    /// <summary>Mutants the score is computed over. Ignored and compile-error mutants are excluded
    /// from both halves — Stryker does the same, and a mutant that never compiled measured nothing.
    /// </summary>
    public int Counted => Killed + Timeout + Survived + NoCoverage;

    /// <summary>Percent, or null when nothing was counted — which is NOT zero and NOT a hundred, and
    /// the caller has to decide what an unmeasurable file means rather than being handed a number.
    /// </summary>
    public double? Percent => Counted == 0 ? null : Math.Round((Killed + Timeout) * 100.0 / Counted, 2);

    public static readonly MutationScore Empty = new(0, 0, 0, 0, 0, 0, [], []);
}

/// <summary>
/// KS4.3 — reads a Stryker.NET <c>mutation-report.json</c> and scores it over a given set of files.
/// Pure: report text and a changed-file set in, counts out. No process, no git, no clock.
/// </summary>
public static class MutationReportReader
{
    /// <summary>Statuses that mean the test suite noticed the mutant.</summary>
    private const string Killed = "Killed", Timeout = "Timeout", Survived = "Survived",
        NoCoverage = "NoCoverage", Ignored = "Ignored", CompileError = "CompileError";

    /// <summary>
    /// Scores <paramref name="json"/> over <paramref name="scopeFiles"/>.
    /// </summary>
    /// <param name="json">A mutation-testing-elements report.</param>
    /// <param name="scopeFiles">Repo-relative paths to score, or null to score the whole report.
    /// A report entry counts when its key and a scope path agree on their trailing segments — Stryker
    /// has written both absolute and project-relative keys across versions, and the gate's cwd need
    /// not be the repo root, so neither side can be assumed to be the longer one.</param>
    /// <returns>Null when the text is not a readable report at all — distinct from a report that is
    /// readable and scores nothing, which is <see cref="MutationScore.Empty"/>.</returns>
    public static MutationScore? Read(string? json, IReadOnlyCollection<string>? scopeFiles)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                return null;

            var scope = scopeFiles?.Select(Norm).Where(s => s.Length > 0).ToList();
            int killed = 0, timeout = 0, survived = 0, noCoverage = 0, ignored = 0, compileError = 0;
            var scored = new List<string>();
            var survivors = new List<MutantRow>();

            foreach (var file in files.EnumerateObject())
            {
                if (scope is not null && !scope.Any(s => SamePath(file.Name, s))) continue;
                scored.Add(file.Name);
                if (!file.Value.TryGetProperty("mutants", out var mutants) || mutants.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var m in mutants.EnumerateArray())
                {
                    var status = Str(m, "status");
                    switch (status)
                    {
                        case Killed: killed++; break;
                        case Timeout: timeout++; break;
                        case Ignored: ignored++; break;
                        case CompileError: compileError++; break;
                        case Survived: survived++; survivors.Add(Row(file.Name, m, Survived)); break;
                        case NoCoverage: noCoverage++; survivors.Add(Row(file.Name, m, NoCoverage)); break;
                        case null: break;
                        // Anything else — Pending, or a status a later schema adds — is deliberately
                        // NOT counted as a kill. An unknown status has not proved the suite noticed.
                        default: survived++; survivors.Add(Row(file.Name, m, status)); break;
                    }
                }
            }

            return new MutationScore(killed, timeout, survived, noCoverage, ignored, compileError,
                scored.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(),
                survivors.OrderBy(s => s.File, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Line).ToList());
        }
    }

    /// <summary>Reads the report the gate wrote, resolving a wildcard to the newest match.</summary>
    public static async Task<MutationScore?> ReadFileAsync(
        MutationConfig cfg, string cwd, IReadOnlyCollection<string>? scopeFiles, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var path = Locate(cfg, cwd);
        if (path is null) return null;
        try { return Read(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), scopeFiles); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The resolved report path, so the caller can name it in a failure.</summary>
    public static string? Locate(MutationConfig cfg, string cwd)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        var path = ReportPath.ResolveNewest(cfg.Path, cwd);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>Two paths name the same file when one's segments are the tail of the other's. Both
    /// sides are normalised to forward slashes first.</summary>
    internal static bool SamePath(string reportKey, string normalisedScopePath)
    {
        var a = Norm(reportKey);
        var b = normalisedScopePath;
        if (a.Length == 0 || b.Length == 0) return false;
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase)
            || b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase);
    }

    private static MutantRow Row(string file, JsonElement m, string status)
        => new(file, Line(m), Str(m, "mutatorName") ?? "?", status);

    private static string Norm(string? p)
    {
        var s = (p ?? "").Replace('\\', '/').Trim();
        while (s.StartsWith("./", StringComparison.Ordinal)) s = s[2..];
        return s.TrimStart('/');
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int Line(JsonElement m)
        => m.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Object
           && loc.TryGetProperty("start", out var start) && start.ValueKind == JsonValueKind.Object
           && start.TryGetProperty("line", out var line) && line.TryGetInt32(out var n) ? n : 0;
}
