using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations.Github;

/// <summary>One remembered mapping in a <see cref="GithubMapFile"/>.</summary>
public sealed record GithubMapFileEntry(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("issue")] int Issue);

/// <summary>
/// Bug #79 — the backfill's memory, kept BESIDE the archive rather than in it.
///
/// <para>KS9.2 fixed the duplicate-board failure for the live mirror by persisting <c>github_map</c>
/// rows in <c>run.db</c>. The read-only backfill could not use that: it must not write the archive it
/// is reading, so it ran with <see cref="GithubMap.Transient"/> and forgot everything at exit — and
/// a second pass inside GitHub's replica lag listed the repository, saw none of what the first pass
/// created seconds earlier, and created it all again. Measured live at DV6.1: 8 issues, then 4 more.</para>
///
/// <para>So the map lives in a file of its own under the state home — one per (run, repository) —
/// which the archive's read-only promise does not cover and a rerun can read. Seeded from the
/// archive's own <c>github_map</c> rows too, so a backfill of a run the live mirror already pushed
/// starts knowing what that mirror made. The file is written once the pass is over, from a
/// <c>finally</c> — so a pass that threw halfway still remembers what it did create — and a crash
/// harder than that costs a rebuild from the markers in the issue bodies, never a duplicate.</para>
/// </summary>
public static class GithubMapFile
{
    public const string DirName = "github-maps";

    /// <summary>Where the map for one (run, repo) pair lives under a state home root.</summary>
    public static string PathFor(string stateHomeRoot, string runId, string repo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateHomeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        return Path.Combine(stateHomeRoot, DirName, $"{runId}.{Slug(repo)}.json");
    }

    /// <summary>A map remembering what the file holds and what <paramref name="seed"/> says. The
    /// seed is read first and the file second, so the file's word wins for a key both name — the
    /// file is what the last backfill actually saw GitHub answer.</summary>
    public static GithubMap Load(string path, IEnumerable<GithubMapFileEntry>? seed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var map = new GithubMap(null);
        foreach (var e in seed ?? []) map.Seed(e.Key, e.Kind, e.Issue);
        foreach (var e in ReadEntries(path)) map.Seed(e.Key, e.Kind, e.Issue);
        return map;
    }

    /// <summary>Write everything the map now knows. Called once the pass is over — and from a
    /// <c>finally</c>, so a pass that threw halfway still remembers the issues it did create.</summary>
    public static async Task<bool> SaveAsync(string path, GithubMap map, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(map);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var sb = new StringBuilder();
            foreach (var (key, kind, issue) in map.Entries)
                sb.Append(JsonSerializer.Serialize(new GithubMapFileEntry(key, kind, issue), GithubJsonContext.Default.GithubMapFileEntry)).Append('\n');
            await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The creates already happened; the marker in each body is the fallback memory.
            return false;
        }
    }

    /// <summary>The file's entries, oldest first. A missing or torn file is an empty map — the marker
    /// in every issue body rebuilds what a lost map knew, at the cost of one listing.</summary>
    public static IReadOnlyList<GithubMapFileEntry> ReadEntries(string path)
    {
        if (!File.Exists(path)) return [];
        var entries = new List<GithubMapFileEntry>();
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize(line, GithubJsonContext.Default.GithubMapFileEntry) is { } e
                        && e.Key.Length > 0 && e.Kind.Length > 0)
                        entries.Add(e);
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return entries;
    }

    private static string Slug(string repo)
    {
        var sb = new StringBuilder(repo.Length);
        foreach (var c in repo.Trim())
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        return sb.ToString().Trim('-');
    }
}
