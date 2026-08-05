using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Store;

/// <summary>
/// K3.1: <c>&lt;state-home&gt;/catalogue.json</c> — one index, keyed by repo path plus plan name,
/// of every run store this machine knows about. It is what makes "show me that run from July"
/// answerable (K3.2) without walking the disk for <c>.conductor</c> directories, and what lets a
/// second working tree find the primary tree's store.
/// <para><b>It is an index, not the truth.</b> The databases are the truth. Every write here is
/// best-effort and every read tolerates a missing or corrupt file, because losing the index must
/// cost a rebuild, never a run.</para>
/// </summary>
public static class StateCatalogue
{
    public const string FileName = "catalogue.json";
    private const string LockFileName = "catalogue.lock";
    private const int LockAttempts = 25;
    private const int LockRetryMs = 20;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Every catalogued entry, newest activity first. Empty when the catalogue is absent or
    /// unreadable.</summary>
    public static IReadOnlyList<StateCatalogueEntry> Read(string root)
    {
        try
        {
            var p = StateHome.CataloguePathFor(root);
            if (!File.Exists(p)) return [];
            var doc = JsonSerializer.Deserialize<StateCatalogueFile>(File.ReadAllText(p), Opts);
            return (doc?.Entries ?? []).OrderByDescending(e => e.LastSeenUtc).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>The entry for a (repo, plan) pair, or null.</summary>
    public static StateCatalogueEntry? Find(string root, string repo, string plan)
    {
        var key = StateHome.KeyFor(repo, plan);
        return Read(root).FirstOrDefault(e => e.Key == key);
    }

    /// <summary>
    /// Records (or refreshes) a (repo, plan) → database mapping. First write stamps
    /// <c>firstSeenUtc</c>; every write stamps <c>lastSeenUtc</c>. An import is recorded once and
    /// never overwritten by a later resolution that imported nothing.
    /// </summary>
    public static bool Upsert(string root, string repo, string? plan, string runDb, StateImport? import = null)
    {
        // Everything below runs inline rather than through private helpers on purpose: the blocking
        // file I/O belongs to this synchronous public boundary (a plan load), and splitting it into
        // private helpers only hides that from the analyzer without making it async.
        FileStream? gate = null;
        try
        {
            Directory.CreateDirectory(root);
            var lockPath = Path.Combine(root, LockFileName);
            // A cross-process advisory lock. This machine runs two engines at once by design and both
            // upsert on startup; a read-modify-write race would drop one of their entries. Bounded
            // retry, then proceed unlocked — an index is not worth blocking a command for.
            for (var attempt = 0; attempt < LockAttempts && gate is null; attempt++)
            {
                try
                {
                    gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
                }
                catch (IOException) { Thread.Sleep(LockRetryMs); }
                catch (UnauthorizedAccessException) { break; }
            }

            var path = StateHome.CataloguePathFor(root);
            StateCatalogueFile doc;
            try
            {
                doc = File.Exists(path)
                    ? JsonSerializer.Deserialize<StateCatalogueFile>(File.ReadAllText(path), Opts) ?? new StateCatalogueFile()
                    : new StateCatalogueFile();
            }
            catch (JsonException)
            {
                // A corrupt index is rebuilt, not repaired: the databases it points at are all still
                // on disk under their slugs, and every resolution re-adds its own entry.
                doc = new StateCatalogueFile();
            }

            var key = StateHome.KeyFor(repo, plan);
            var now = DateTimeOffset.UtcNow;
            var existing = doc.Entries.FirstOrDefault(e => e.Key == key);
            if (existing is null)
            {
                doc.Entries.Add(new StateCatalogueEntry
                {
                    Key = key,
                    Repo = Path.GetFullPath(repo),
                    Plan = plan ?? "",
                    Slug = StateHome.SlugFor(repo, plan),
                    RunDb = Path.GetFullPath(runDb),
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    ImportedFrom = import?.From,
                    ImportedAtUtc = import?.ImportedAtUtc,
                });
            }
            else
            {
                existing.Repo = Path.GetFullPath(repo);
                existing.Plan = plan ?? existing.Plan;
                existing.Slug = StateHome.SlugFor(repo, plan);
                existing.RunDb = Path.GetFullPath(runDb);
                existing.LastSeenUtc = now;
                if (import is not null)
                {
                    existing.ImportedFrom = import.From;
                    existing.ImportedAtUtc = import.ImportedAtUtc;
                }
            }
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(doc, Opts));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
        finally
        {
            gate?.Dispose();
        }
    }

    private sealed class StateCatalogueFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("entries")] public List<StateCatalogueEntry> Entries { get; set; } = [];
    }
}

/// <summary>One catalogued run store. <see cref="Key"/> is <see cref="StateHome.KeyFor"/> — the
/// normalised repo path and plan name — so re-spelling a path's case does not fork the history.</summary>
public sealed class StateCatalogueEntry
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("repo")] public string Repo { get; set; } = "";
    [JsonPropertyName("plan")] public string Plan { get; set; } = "";
    [JsonPropertyName("slug")] public string Slug { get; set; } = "";
    [JsonPropertyName("runDb")] public string RunDb { get; set; } = "";
    [JsonPropertyName("firstSeenUtc")] public DateTimeOffset FirstSeenUtc { get; set; }
    [JsonPropertyName("lastSeenUtc")] public DateTimeOffset LastSeenUtc { get; set; }
    /// <summary>Set once, when this entry's database was imported from a pre-K3.1
    /// <c>.conductor/run.db</c>. The source file still exists — the import copies.</summary>
    [JsonPropertyName("importedFrom")] public string? ImportedFrom { get; set; }
    [JsonPropertyName("importedAtUtc")] public DateTimeOffset? ImportedAtUtc { get; set; }
}
