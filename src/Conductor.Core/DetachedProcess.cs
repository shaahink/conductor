using System.Diagnostics;
using System.Text;

namespace Conductor.Core;

/// <summary>SC5.2: the outcome of one detach spawn.</summary>
/// <param name="Pid">The new process id, or 0 when the spawn failed.</param>
/// <param name="BrokeAwayFromJob">Windows only: the child left any job object this process belongs to,
/// so a KILL_ON_JOB_CLOSE teardown of the launching harness cannot reach it. False either because the
/// job forbade breakaway (we retried inside it) or because there was no job to leave.</param>
/// <param name="Error">Null on success; otherwise why nothing was started.</param>
public readonly record struct DetachSpawn(int Pid, bool BrokeAwayFromJob, string? Error)
{
    public bool Ok => Pid > 0 && Error is null;

    public static DetachSpawn Failed(string error) => new(0, false, error);
}

/// <summary>
/// SC5.2: start a process that OUTLIVES this one and the shell that launched it.
///
/// <para>devcontext #16: a run died to an unrelated harness cleanup. The engine was a child of the
/// shell that typed <c>conductor run</c>, sharing its console and (on Windows) usually its job
/// object — so closing that window, logging off, or a supervisor tearing down the job took a
/// perfectly healthy multi-hour run with it. <see cref="Process.Start(ProcessStartInfo)"/> cannot
/// express "not my child": .NET exposes no creation flags. This does.</para>
///
/// <para>Windows: <c>CreateProcessW</c> with <c>DETACHED_PROCESS</c> (no console at all, so the
/// launching console's CTRL_CLOSE_EVENT is never delivered), <c>CREATE_NEW_PROCESS_GROUP</c> (a
/// Ctrl+C in the old group is not the child's) and <c>CREATE_BREAKAWAY_FROM_JOB</c>. A job that
/// forbids breakaway fails the call with ERROR_ACCESS_DENIED rather than starting anything, so we
/// retry once without that flag: a new process group still survives the shell closing, which is the
/// common case, and <see cref="DetachSpawn.BrokeAwayFromJob"/> reports the weaker guarantee honestly
/// instead of claiming immunity it does not have.</para>
///
/// <para>POSIX: <c>setsid(1)</c> puts the child in a new session and process group, so the shell's
/// exit-time SIGHUP cannot reach it. Where setsid is absent the child is started normally and the
/// weaker guarantee is reported the same way.</para>
/// </summary>
public static partial class DetachedProcess
{
    /// <summary>Spawn <paramref name="fileName"/> detached. <paramref name="redirectAllOutputTo"/>,
    /// when given, receives the child's stdout and stderr — a detached process has no console, so
    /// without it a child that dies before its own logging comes up dies silently.</summary>
    public static DetachSpawn Start(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        string? redirectAllOutputTo = null)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return DetachSpawn.Failed("no executable to start");
        return OperatingSystem.IsWindows()
            ? StartWindows(fileName, args, workingDirectory, redirectAllOutputTo)
            : StartPosix(fileName, args, workingDirectory, redirectAllOutputTo);
    }

    /// <summary>
    /// Build one Windows command line from a program and its arguments, quoted so that
    /// <c>CommandLineToArgvW</c> — which is what the child's own runtime parses it with — recovers
    /// exactly the strings passed in. CreateProcessW takes a single string, not an argv array, so
    /// this is not cosmetic: an unquoted <c>C:\Program Files\...</c> silently becomes two arguments.
    /// </summary>
    public static string CommandLine(string fileName, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        AppendQuoted(sb, fileName);
        foreach (var a in args)
        {
            sb.Append(' ');
            AppendQuoted(sb, a);
        }
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            sb.Append(arg);
            return;
        }
        sb.Append('"');
        for (var i = 0; i < arg.Length; i++)
        {
            var slashes = 0;
            while (i < arg.Length && arg[i] == '\\') { slashes++; i++; }
            if (i == arg.Length)
            {
                // Trailing backslashes precede the closing quote: each must be doubled or the quote
                // is escaped and the argument swallows everything after it.
                sb.Append('\\', slashes * 2);
                break;
            }
            if (arg[i] == '"')
            {
                sb.Append('\\', (slashes * 2) + 1).Append('"');
            }
            else
            {
                sb.Append('\\', slashes).Append(arg[i]);
            }
        }
        sb.Append('"');
    }

    /// <summary>Where setsid lives, if it does. Checked as files rather than shelled through a PATH
    /// lookup so the absent case costs nothing.</summary>
    private static string? FindSetsid()
    {
        foreach (var p in new[] { "/usr/bin/setsid", "/bin/setsid" })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>The shell fragment that re-points both streams at the log and then gets out of the
    /// way. <c>exec</c> matters: without it the shell lingers as the parent and owns the pid.</summary>
    internal const string PosixRedirectScript = "exec \"$0\" \"$@\" >> \"$CONDUCTOR_DETACH_LOG\" 2>&1";

    private static DetachSpawn StartPosix(string fileName, IReadOnlyList<string> args, string wd, string? logPath)
    {
        var setsid = FindSetsid();
        var psi = new ProcessStartInfo { UseShellExecute = false, WorkingDirectory = wd };

        // Two optional wrappers, outermost first: setsid for the new session, sh for the redirect.
        // Whichever comes first is the program; the rest become its leading arguments.
        var argv = new List<string>();
        if (setsid is not null) argv.Add("/bin/sh");
        if (logPath is not null)
        {
            argv.Add("-c");
            argv.Add(PosixRedirectScript);
            psi.Environment["CONDUCTOR_DETACH_LOG"] = logPath;
        }
        else if (setsid is not null)
        {
            argv.RemoveAt(argv.Count - 1); // no redirect wanted: setsid runs the program directly
        }
        var wrapped = setsid is not null || logPath is not null;
        psi.FileName = setsid ?? (logPath is not null ? "/bin/sh" : fileName);
        if (wrapped) argv.Add(fileName);
        argv.AddRange(args);
        foreach (var a in argv) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return DetachSpawn.Failed($"could not start {fileName}");
            // Under setsid the pid we get back is the wrapper's; it execs straight through on Linux
            // (setsid without -f), so the id is the engine's. Without setsid it is the engine's too.
            return new DetachSpawn(proc.Id, setsid is not null, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return DetachSpawn.Failed($"could not start {fileName}: {ex.Message}");
        }
    }
}
