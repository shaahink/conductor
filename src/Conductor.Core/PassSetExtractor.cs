using System.Text.RegularExpressions;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// KS4.2 — reads the set of checks a <see cref="GateClass.Regression"/> gate reported PASSING out of
/// the run it just did. Pure: output text and a working directory in, a sorted distinct name set out.
/// </summary>
/// <remarks>
/// <para>Every format here is deliberately a <em>positive</em> reading — names that passed — never
/// "everything minus the failures". A suite that no longer runs a test does not print a failure for
/// it, so a subtractive reading would agree that nothing is wrong, which is the exact blindness this
/// class exists to remove.</para>
/// </remarks>
public static partial class PassSetExtractor
{
    /// <summary>The checks the gate reported passing. Empty means "nothing readable" — never
    /// "everything passed"; the caller treats an empty set from a passing gate as red.</summary>
    public static async Task<IReadOnlyList<string>> ExtractAsync(PassSetConfig cfg, string stdout, string cwd, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        IEnumerable<string> names =
            cfg.Is(PassSetConfig.Trx) ? await FromTrxAsync(ResolveTrx(cfg.Path, cwd), ct).ConfigureAwait(false)
            : cfg.Is(PassSetConfig.GoTest) ? FromGoTest(stdout ?? "")
            : cfg.Is(PassSetConfig.Lines) ? FromLines(stdout ?? "")
            : [];
        return names.Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>VSTest trx. Attribute order inside <c>UnitTestResult</c> is not stable across SDKs
    /// (testName and outcome swap places), so each element is matched whole and its two attributes
    /// read independently rather than pinned into one ordered pattern.</summary>
    private static async Task<IEnumerable<string>> FromTrxAsync(string? path, CancellationToken ct)
    {
        if (path is null || !File.Exists(path)) return [];
        string xml;
        try { xml = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }

        var names = new List<string>();
        foreach (Match m in UnitTestResultElement().Matches(xml))
        {
            var element = m.Value;
            var attributes = Attribute().Matches(element);
            var outcome = attributes.FirstOrDefault(a => a.Groups["key"].Value.Equals("outcome", StringComparison.Ordinal))?.Groups["value"].Value;
            if (!string.Equals(outcome, "Passed", StringComparison.Ordinal)) continue;
            var name = attributes.FirstOrDefault(a => a.Groups["key"].Value.Equals("testName", StringComparison.Ordinal))?.Groups["value"].Value;
            if (!string.IsNullOrWhiteSpace(name)) names.Add(System.Net.WebUtility.HtmlDecode(name).Trim());
        }
        return names;
    }

    /// <summary><c>go test -v</c>. Subtests are indented and carry a <c>Parent/Child</c> name; both
    /// levels are kept, because a deleted subtest is a deleted check.</summary>
    private static IEnumerable<string> FromGoTest(string stdout)
        => GoPassLine().Matches(stdout).Select(m => m.Groups["name"].Value.Trim());

    private static IEnumerable<string> FromLines(string stdout)
        => stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim());

    /// <summary>The trx the gate actually wrote. <c>dotnet test</c> names its file after the machine
    /// and the clock unless the plan pins <c>LogFileName</c>, so a <c>*</c> in the declared path
    /// resolves to the newest match — and "newest" is the one this run just produced.</summary>
    private static string? ResolveTrx(string? declared, string cwd)
    {
        if (string.IsNullOrWhiteSpace(declared)) return null;
        var full = Path.IsPathRooted(declared) ? declared : Path.Combine(cwd, declared);
        if (!full.Contains('*', StringComparison.Ordinal)) return full;
        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        try
        {
            return Directory.EnumerateFiles(dir, Path.GetFileName(full), SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex("""<UnitTestResult\b[^>]*>""", RegexOptions.None, 5_000)]
    private static partial Regex UnitTestResultElement();

    [GeneratedRegex(@"\b(?<key>[A-Za-z]+)\s*=\s*""(?<value>[^""]*)""", RegexOptions.ExplicitCapture, 5_000)]
    private static partial Regex Attribute();

    [GeneratedRegex("""^\s*--- PASS: (?<name>.+?) \(""", RegexOptions.Multiline | RegexOptions.ExplicitCapture, 5_000)]
    private static partial Regex GoPassLine();
}
