using System.Runtime.InteropServices;

namespace Conductor.Core.Update;

/// <summary>
/// SC8.3 — which archive on a release is THIS machine's. The names come from one place and one place
/// only: the matrix in <c>.github/workflows/release.yml</c>, which publishes
/// <c>conductor-&lt;moniker&gt;.zip</c> on Windows and <c>conductor-&lt;moniker&gt;.tar.gz</c>
/// everywhere else. If that matrix grows a target, this map grows the same row.
///
/// <para>An unsupported platform is a first-class answer, not an exception: a linux-arm32 user
/// running <c>conductor update</c> deserves "no build is published for linux-arm — build from
/// source", not a stack trace or a silent download of the wrong binary.</para>
/// </summary>
public sealed record UpdateTarget(string Moniker, string ArchiveExtension, string BinaryExtension)
{
    /// <summary>The engine's file name inside the archive, e.g. <c>conductor.exe</c>.</summary>
    public string EngineFileName => "conductor" + BinaryExtension;

    /// <summary>The face's file name inside the archive. Shipped beside the engine because that is
    /// where <c>FaceLauncher.ResolveEntrypoint</c> looks first.</summary>
    public string FaceFileName => "conductor-face" + BinaryExtension;

    /// <summary>The asset name this platform expects on a release.</summary>
    public string AssetName => $"conductor-{Moniker}.{ArchiveExtension}";

    /// <summary>This machine's target, or null when no release is published for it.</summary>
    public static UpdateTarget? ForThisMachine() => For(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : "",
        RuntimeInformation.OSArchitecture);

    /// <summary>Split out from <see cref="ForThisMachine"/> so every row of the matrix is testable on
    /// one machine — the whole point of the map is the four rows this host will never be.</summary>
    public static UpdateTarget? For(string os, Architecture arch)
    {
        var cpu = arch switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        if (cpu is null) return null;

        return (os, cpu) switch
        {
            ("windows", "x64") => new UpdateTarget("windows-x64", "zip", ".exe"),
            ("linux", "x64") => new UpdateTarget("linux-x64", "tar.gz", ""),
            ("linux", "arm64") => new UpdateTarget("linux-arm64", "tar.gz", ""),
            ("macos", "arm64") => new UpdateTarget("macos-arm64", "tar.gz", ""),
            ("macos", "x64") => new UpdateTarget("macos-x64", "tar.gz", ""),
            _ => null,
        };
    }

    /// <summary>How to describe this machine when there IS no target — the message has to name what
    /// it looked for, or "unsupported" is unactionable.</summary>
    public static string DescribeThisMachine() =>
        $"{RuntimeInformation.OSDescription.Split('\n')[0].Trim()} / {RuntimeInformation.OSArchitecture}";
}
