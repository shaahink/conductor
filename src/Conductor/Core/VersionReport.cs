using System.Text.Json.Serialization;

namespace Conductor.Core;

/// <summary>
/// SC8.1 — the build identity as data. ONE shape, served by both <c>conductor version --json</c> and
/// <c>GET /version</c> on the control plane: two records for the same three facts is two chances for
/// the CLI and the wire to disagree about which engine is running, which is the exact confusion this
/// stage exists to end.
/// </summary>
/// <param name="Version">Semver without build metadata, e.g. <c>2.0.0</c>.</param>
/// <param name="Full">Semver with build metadata, e.g. <c>2.0.0+abc123def456.dirty</c>.</param>
/// <param name="Commit">Short git sha the binary was built from, or <c>unknown</c>.</param>
/// <param name="Dirty">True when the working tree carried uncommitted changes at build time.</param>
/// <param name="BuildDate">ISO-8601 UTC build timestamp, or null on an unstamped binary.</param>
/// <param name="Runtime">The .NET runtime the process is on.</param>
/// <param name="Os">The OS the process is on.</param>
/// <param name="Binary">The file that is executing — which conductor answered.</param>
public sealed record VersionReport(
    string Version,
    string Full,
    string Commit,
    bool Dirty,
    string? BuildDate,
    string Runtime,
    string Os,
    string Binary)
{
    /// <summary>Projects the running engine's own stamp.</summary>
    public static VersionReport Current() => new(
        Version: BuildInfo.Current.Version,
        Full: BuildInfo.Current.Full,
        Commit: BuildInfo.Current.CommitSha,
        Dirty: BuildInfo.Current.Dirty,
        BuildDate: BuildInfo.Current.BuildDateIso,
        Runtime: BuildInfo.Runtime,
        Os: BuildInfo.Os,
        Binary: BuildInfo.BinaryPath);
}

/// <summary>camelCase source-gen context, matching the control plane's convention so the CLI's
/// <c>--json</c> and the wire's <c>/version</c> are byte-identical in shape.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(VersionReport))]
public sealed partial class VersionJsonContext : JsonSerializerContext;
