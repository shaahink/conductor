using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Conductor.Core;

/// <summary>
/// SC8.1 — what this binary actually is. Read off the assembly attributes that
/// <c>Conductor.csproj</c>'s <c>StampBuildInfo</c> target writes at compile time, never off a
/// constant a human maintains.
/// <para>The question this exists to answer is the one that burned three sessions and has no other
/// answer: <em>is the run using stale engine code?</em> Two binaries built from the same
/// <c>Version</c> are indistinguishable without the commit sha; a binary built from a dirty tree is
/// indistinguishable from the commit it claims without the dirty flag; and neither tells you WHICH
/// file on disk answered, which is why <see cref="BinaryPath"/> is part of the report.</para>
/// </summary>
public sealed record BuildInfo(
    string Version,
    string CommitSha,
    bool Dirty,
    DateTimeOffset? BuildDate)
{
    /// <summary>Placeholder commit for a build with no git available (source archive, no git on PATH).
    /// A word, not an empty string: "unknown" reads as an answer, blank reads as a bug.</summary>
    public const string UnknownCommit = "unknown";

    /// <summary>The running engine's own stamp. Computed once — assembly attributes cannot change.</summary>
    public static BuildInfo Current { get; } = FromAssembly(typeof(BuildInfo).Assembly);

    /// <summary>Full semver with build metadata: <c>2.0.0+abc123def456</c>, or
    /// <c>2.0.0+abc123def456.dirty</c> when the working tree carried uncommitted changes.</summary>
    public string Full => Dirty
        ? $"{Version}+{CommitSha}.dirty"
        : $"{Version}+{CommitSha}";

    /// <summary>The build date as an ISO-8601 UTC string, or null when the build did not stamp one.</summary>
    public string? BuildDateIso => BuildDate?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    /// <summary>The .NET runtime this process is on (e.g. <c>.NET 10.0.0</c>).</summary>
    public static string Runtime => RuntimeInformation.FrameworkDescription;

    /// <summary>The OS this process is on.</summary>
    public static string Os => RuntimeInformation.OSDescription;

    /// <summary>The file that is actually executing. Trap 3 of this repo's own trap list is
    /// "you exercised the published engine on PATH, not your fresh build" — this line is how the
    /// operator sees which one answered without guessing from behaviour.
    /// <para>SC8.3: the fallback deliberately does NOT read <c>Assembly.Location</c>. That property
    /// returns an empty string in a single-file app — which is exactly how the release archives are
    /// published — and reading it is an IL3000 error under the single-file analyzer, so the line
    /// added in SC8.1 made <c>release.yml</c> fail to compile on every platform. The documented
    /// single-file-safe pair is <see cref="AppContext.BaseDirectory"/> plus the assembly's simple
    /// name.</para></summary>
    public static string BinaryPath
    {
        get
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path)) return path;
            var name = typeof(BuildInfo).Assembly.GetName().Name;
            if (string.IsNullOrEmpty(name)) return "(unknown)";
            var guess = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            return File.Exists(guess) ? guess : "(unknown)";
        }
    }

    /// <summary>Reads the stamp off an assembly. Falls back through every layer rather than throwing:
    /// an un-stamped assembly (someone built with the target disabled) still reports a version.</summary>
    public static BuildInfo FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (!string.IsNullOrEmpty(m.Key) && m.Value != null)
                metadata[m.Key] = m.Value;
        }
        var fallback = assembly.GetName().Version?.ToString(3);
        return Parse(informational, metadata, fallback);
    }

    /// <summary>The parse, separated from reflection so it is testable against every shape the build
    /// can emit — stamped, half-stamped, and not stamped at all.</summary>
    /// <param name="informational">The <c>AssemblyInformationalVersion</c>, e.g. <c>2.0.0+abc123.dirty</c>.</param>
    /// <param name="metadata">The <c>AssemblyMetadata</c> pairs the build target writes.</param>
    /// <param name="fallbackVersion">Used only when <paramref name="informational"/> is absent.</param>
    public static BuildInfo Parse(
        string? informational,
        IReadOnlyDictionary<string, string> metadata,
        string? fallbackVersion = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // "2.0.0+abc123def456.dirty" -> version "2.0.0", metadata suffix "abc123def456.dirty".
        var text = informational?.Trim() ?? "";
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var version = plus >= 0 ? text[..plus] : text;
        var suffix = plus >= 0 ? text[(plus + 1)..] : "";
        if (string.IsNullOrEmpty(version)) version = fallbackVersion ?? "0.0.0";

        // The suffix carries the same two facts as the metadata attributes. The attributes win when
        // present (they are unambiguous); the suffix is the back-compat / single-source read, so a
        // binary stamped only via SourceRevisionId still reports its commit.
        var suffixDirty = suffix.EndsWith(".dirty", StringComparison.OrdinalIgnoreCase);
        var suffixSha = suffixDirty ? suffix[..^".dirty".Length] : suffix;

        var sha = metadata.TryGetValue("CommitSha", out var metaSha) && !string.IsNullOrWhiteSpace(metaSha)
            ? metaSha.Trim()
            : (string.IsNullOrWhiteSpace(suffixSha) ? UnknownCommit : suffixSha.Trim());

        var dirty = metadata.TryGetValue("CommitDirty", out var metaDirty)
            ? string.Equals(metaDirty.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            : suffixDirty;

        DateTimeOffset? built = null;
        if (metadata.TryGetValue("BuildDate", out var metaDate)
            && DateTimeOffset.TryParse(metaDate, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            built = parsed;
        }

        return new BuildInfo(version, sha, dirty, built);
    }
}
