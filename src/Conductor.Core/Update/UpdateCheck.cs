using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Update;

/// <summary>SC8.3 — the answer to "is there a newer engine?", as data. <paramref name="Latest"/> is
/// null when the question could not be answered, and <paramref name="Detail"/> then says why.</summary>
/// <param name="Current">The running engine's version, prerelease and all.</param>
/// <param name="Latest">The latest published release's version, or null when unknown.</param>
/// <param name="Tag">The release tag verbatim (<c>v2.1.0</c>), or null.</param>
/// <param name="Available">True only when <paramref name="Latest"/> is strictly newer.</param>
/// <param name="Detail">One sentence a human can act on — the reason, the URL, or the failure.</param>
public sealed record UpdateStatus(
    SemVer Current,
    SemVer? Latest,
    string? Tag,
    bool Available,
    string Detail)
{
    /// <summary>Was the question answerable at all? A machine with no network is not "up to date".</summary>
    public bool Known => Latest is not null;

    /// <summary>Decides. Split from the fetch so precedence is testable without a socket, and so the
    /// prerelease rule is asserted rather than assumed: a build seven commits past v2.1.0 reports
    /// <c>2.1.1-alpha.0.7</c>, which is NEWER than the v2.1.0 release it came from — offering to
    /// "update" it back down to 2.1.0 would be a downgrade wearing an upgrade's clothes.</summary>
    public static UpdateStatus Decide(SemVer current, GithubRelease? release, string? error)
    {
        if (release is null)
            return new UpdateStatus(current, null, null, false, error ?? "could not reach the release feed");
        if (!SemVer.TryParse(release.TagName, out var latest))
            return new UpdateStatus(current, null, release.TagName, false,
                $"the latest release is tagged '{release.TagName}', which is not a semantic version");

        if (latest > current)
            return new UpdateStatus(current, latest, release.TagName, true,
                $"{release.TagName} is available" + (release.HtmlUrl is { Length: > 0 } u ? $" — {u}" : ""));

        return new UpdateStatus(current, latest, release.TagName, false,
            latest.CompareTo(current) == 0
                ? "running the latest release"
                : $"running {current} — newer than the latest release {release.TagName} (a local or prerelease build)");
    }
}

/// <summary>
/// SC8.3 — a user-level memo of the last successful check, so <c>doctor</c> can report
/// update-available without paying a network round trip on every invocation.
///
/// <para>It lives beside the user's other caches, NOT in a run's <c>.conductor/</c>: doctor is
/// documented as never writing run state, and a per-repo cache would also re-ask GitHub once per
/// repository for an answer that is identical everywhere.</para>
/// </summary>
public sealed record UpdateCheckCache(
    [property: JsonPropertyName("checkedUtc")] DateTimeOffset CheckedUtc,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("feed")] string Feed)
{
    /// <summary>Environment variable that switches every automatic check off — for an air-gapped
    /// machine, or an operator who does not want a CLI phoning home.</summary>
    public const string DisableEnvVar = "CONDUCTOR_NO_UPDATE_CHECK";

    /// <summary>How long a cached answer is trusted. Six hours: long enough that doctor is instant
    /// through a working day, short enough that "update available" appears the day it is true.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(6);

    public static bool Disabled =>
        Environment.GetEnvironmentVariable(DisableEnvVar) is { Length: > 0 } v
        && !string.Equals(v, "0", StringComparison.Ordinal)
        && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>%LOCALAPPDATA%\conductor\update-check.json</c>, or the XDG cache equivalent.</summary>
    public static string Path
    {
        get
        {
            var dir = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg
                    ? xdg
                    : System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return System.IO.Path.Combine(dir, "conductor", "update-check.json");
        }
    }

    /// <summary>The cached answer if it is fresh AND was taken from the feed currently in force —
    /// a cache written against an override must never be served as the real repo's answer.</summary>
    public static UpdateCheckCache? ReadFresh(DateTimeOffset now)
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var cached = JsonSerializer.Deserialize(File.ReadAllText(Path), UpdateJsonContext.Default.UpdateCheckCache);
            if (cached is null) return null;
            if (!string.Equals(cached.Feed, ReleaseClient.FeedUrl, StringComparison.Ordinal)) return null;
            return now - cached.CheckedUtc <= Ttl && now >= cached.CheckedUtc ? cached : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;   // a corrupt cache is a cache miss, never a failure
        }
    }

    /// <summary>Best effort — a cache that cannot be written costs one round trip, nothing more.</summary>
    public static void Write(GithubRelease release, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(release);
        try
        {
            var path = Path;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var body = new UpdateCheckCache(now, release.TagName, release.HtmlUrl, ReleaseClient.FeedUrl);
            File.WriteAllText(path, JsonSerializer.Serialize(body, UpdateJsonContext.Default.UpdateCheckCache));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no cache, no problem
        }
    }

    /// <summary>Rebuilds a release document from the memo, so the decision runs through exactly one
    /// code path whether the answer came off the wire or off the disk.</summary>
    public GithubRelease AsRelease() => new() { TagName = Tag, HtmlUrl = Url };

    /// <summary>How stale this answer is, in words, for the surface that reports it.</summary>
    public string AgeText(DateTimeOffset now)
    {
        var age = now - CheckedUtc;
        return age < TimeSpan.FromMinutes(1)
            ? "just now"
            : age < TimeSpan.FromHours(1)
                ? string.Create(CultureInfo.InvariantCulture, $"{age.TotalMinutes:0}m ago")
                : string.Create(CultureInfo.InvariantCulture, $"{age.TotalHours:0.#}h ago");
    }
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(UpdateCheckCache))]
public sealed partial class UpdateJsonContext : JsonSerializerContext;
