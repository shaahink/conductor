namespace Conductor.Core.Courier;

/// <summary>PK1.1 / D1 - where the courier's own executable is. The engine never hosts the daemon: it
/// starts, registers and stops <c>conductor-courier</c>, which is built and published beside it. One
/// name, in core, because the alias (<c>conductor courier run</c>), the installer and the scheduled
/// task all have to agree on it.</summary>
public static class CourierBinary
{
    /// <summary>The file name without an extension - the courier project's AssemblyName.</summary>
    public const string Stem = "conductor-courier";

    /// <summary>The file name on this OS.</summary>
    public static string FileName => OperatingSystem.IsWindows() ? Stem + ".exe" : Stem;

    /// <summary>The courier binary in <paramref name="directory"/> - the engine's own directory, in a
    /// build and in an install alike.</summary>
    public static string Beside(string directory) => Path.Combine(directory, FileName);
}