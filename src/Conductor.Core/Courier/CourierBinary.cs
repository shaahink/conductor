namespace Conductor.Core.Courier;

/// <summary>PK1.1 / D1 - where the courier's own executable is. The engine never hosts the daemon: it
/// starts, registers and stops <c>conductor-courier</c>. One name, in core, because the alias
/// (<c>conductor courier run</c>), <c>courier install</c>, the installer and the release preflight all
/// have to agree on it.
///
/// <para><b>Two layouts, measured at PK1.2.</b> A build puts the courier beside the engine (the engine
/// project references it, and the SDK copies a referenced executable). An install puts it in its OWN
/// directory, <c>&lt;install&gt;\courier\</c>, and that is not tidiness: a courier running beside the
/// engine loads the same <c>Conductor.Core.dll</c> and <c>Microsoft.Extensions.*.dll</c> files, and
/// the next engine publish failed on exactly those locks. A separate executable in a shared directory
/// does not remove D1's coupling; a separate directory does.</para></summary>
public static class CourierBinary
{
    /// <summary>The file name without an extension - the courier project's AssemblyName.</summary>
    public const string Stem = "conductor-courier";

    /// <summary>The courier's own directory under an install.</summary>
    public const string DirName = "courier";

    /// <summary>The file name on this OS.</summary>
    public static string FileName => OperatingSystem.IsWindows() ? Stem + ".exe" : Stem;

    /// <summary>The courier binary directly in <paramref name="directory"/> - the build layout.</summary>
    public static string Beside(string directory) => Path.Combine(directory, FileName);

    /// <summary>The courier binary in the install layout under <paramref name="engineDirectory"/>.</summary>
    public static string InOwnDirectory(string engineDirectory) =>
        Path.Combine(engineDirectory, DirName, FileName);

    /// <summary>The courier an engine in <paramref name="engineDirectory"/> manages: its own directory
    /// when there is one (an install), otherwise the copy beside the engine (a build). The own
    /// directory wins, because an install carries both and only that one leaves the engine's files
    /// free to be replaced.</summary>
    public static string Resolve(string engineDirectory) =>
        File.Exists(InOwnDirectory(engineDirectory)) ? InOwnDirectory(engineDirectory) : Beside(engineDirectory);

    /// <summary>Is <paramref name="exe"/> the courier's own binary, rather than an engine?</summary>
    public static bool IsCourier(string? exe) =>
        exe is { Length: > 0 }
        && string.Equals(Path.GetFileNameWithoutExtension(exe), Stem, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a running courier is, as the reinstall sees it.</summary>
    public static CourierShape ShapeOf(string? exe)
    {
        if (exe is not { Length: > 0 }) return CourierShape.NotRunning;
        if (!IsCourier(exe)) return CourierShape.InsideEngine;
        var dir = Path.GetFileName(Path.GetDirectoryName(exe)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(dir, DirName, StringComparison.OrdinalIgnoreCase)
            ? CourierShape.OwnDirectory
            : CourierShape.BesideEngine;
    }
}

/// <summary>PK1.2 - where a live courier runs from, which decides what a reinstall has to do to it.</summary>
public enum CourierShape
{
    /// <summary>Nothing is polling.</summary>
    NotRunning,

    /// <summary><c>conductor-courier</c> in its own directory: the engine installs around it.</summary>
    OwnDirectory,

    /// <summary><c>conductor-courier</c> beside an engine: it holds that directory's shared files.</summary>
    BesideEngine,

    /// <summary>An engine binary running <c>courier run</c> itself (before D1): it holds the engine.</summary>
    InsideEngine,
}