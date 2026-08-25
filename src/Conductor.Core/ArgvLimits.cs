using System.Text;

namespace Conductor.Core;

/// <summary>
/// The command-line ceilings a spawned agent actually hits, the measurement of the argv that will
/// hit them, and the resolution of which ceiling applies on THIS machine.
///
/// <para>DV2.2, bug #15. All three of these lived in <c>DoctorCommand</c>, in the CLI project, where
/// the spawn seam could not reach them — so the diagnostic knew the wall was there and the engine
/// walked into it anyway. An argv over the ceiling does not fail loudly: a <c>.cmd</c>/<c>.bat</c>
/// shim (which is what an npm-installed agent CLI is on Windows) truncates or refuses the command
/// line, the agent does nothing, and the run scores the session as if it had read everything and
/// chosen to be brief. Moving the arithmetic here is what lets <see cref="AgentSession.Start"/>
/// refuse before spawning, and doctor now delegates to it so a diagnostic and a launch cannot
/// disagree about where the wall is.</para>
/// </summary>
public static class ArgvLimits
{
    /// <summary>Windows' CreateProcess limit. The engine spawns with <c>UseShellExecute=false</c>,
    /// so this is the wall it hits when the command is a real executable.</summary>
    public const int CreateProcessCommandLine = 32767;

    /// <summary>cmd.exe's much lower ceiling. It applies whenever the agent command resolves to a
    /// <c>.cmd</c>/<c>.bat</c> shim, because Windows runs the shim through the command
    /// interpreter — bug #15's "silently stops a cmd.exe-based agent".</summary>
    public const int CmdExeCommandLine = 8191;

    /// <summary>The ceiling this command will actually hit here, and why, in words fit for an error
    /// message. A command that resolves to nothing gets the CreateProcess ceiling: guessing the
    /// lower one for a program we cannot find would refuse launches that are fine.</summary>
    public static (int Ceiling, string Why) CeilingFor(string command, string cwd)
    {
        var resolved = ResolveProgram(command ?? "", cwd ?? "");
        var ext = resolved is null ? "" : Path.GetExtension(resolved);
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            ? (CmdExeCommandLine, $"{Path.GetFileName(resolved)} is a command-interpreter shim")
            : (CreateProcessCommandLine, "CreateProcess");
    }

    /// <summary>The length of the command line <c>ProcessStartInfo.ArgumentList</c> would build,
    /// quoting each argument by the same rules the runtime uses. Length, not the string: the point
    /// is the measurement, and a prompt does not belong in memory twice.</summary>
    public static int CommandLineLength(string fileName, IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var sb = new StringBuilder();
        AppendArgument(sb, fileName ?? "");
        foreach (var a in args)
        {
            sb.Append(' ');
            AppendArgument(sb, a);
        }
        return sb.Length;
    }

    /// <summary>The file a spawn would actually run for this command token — an explicit path if it
    /// names one, otherwise the first PATH hit with PATHEXT applied.</summary>
    public static string? ResolveProgram(string token, string cwd)
    {
        if (token.Length == 0) return null;
        if (!IsPathLike(token)) return ResolveOnPath(token);
        var full = Path.IsPathRooted(token) ? token : Path.Combine(cwd, token);
        if (File.Exists(full)) return full;
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var ext in PathExt())
            if (File.Exists(full + ext)) return full + ext;
        return null;
    }

    /// <summary>The first file PATH would spawn for a bare command name, PATHEXT included.</summary>
    public static string? ResolveOnPath(string command)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var exts = OperatingSystem.IsWindows() ? PathExt().Prepend("").ToArray() : [""];
        foreach (var dir in dirs)
        foreach (var ext in exts)
        {
            var candidate = Path.Combine(dir, command + ext);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string[] PathExt()
        => (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

    private static bool IsPathLike(string cmd)
        => cmd.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || cmd.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
        || Path.IsPathRooted(cmd);

    private static void AppendArgument(StringBuilder sb, string arg)
    {
        if (arg.Length != 0 && !arg.AsSpan().ContainsAny(' ', '\t', '"'))
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        for (var i = 0; i < arg.Length;)
        {
            var c = arg[i++];
            if (c == '\\')
            {
                var slashes = 1;
                while (i < arg.Length && arg[i] == '\\') { i++; slashes++; }
                if (i == arg.Length) sb.Append('\\', slashes * 2);
                else if (arg[i] == '"') { sb.Append('\\', (slashes * 2) + 1).Append('"'); i++; }
                else sb.Append('\\', slashes);
            }
            else if (c == '"') sb.Append('\\').Append('"');
            else sb.Append(c);
        }
        sb.Append('"');
    }
}
