using System.Diagnostics;
using Conductor.Core.Store;

namespace Conductor.Core;

/// <summary>
/// W3.3 (bug #2): how a background child's output actually reaches a file.
///
/// `conductor bg start` attached .NET's async read pump to the child and then returned — killing
/// the pump it had just installed. Every log for anything slower than ~300ms was a three-byte BOM
/// while looking perfectly healthy, which inverts the whole "use bg for commands over 3 minutes"
/// instruction the agents are given.
///
/// The fix is to stop pumping. The child is spawned through the platform shell with its stdout and
/// stderr redirected by the OS, so the bytes land in the file whether or not the launcher is still
/// alive — which it is not, by design.
///
/// One consequence: the log name cannot contain the child's pid, because the redirect target has to
/// exist in the command line before the pid does. Names are <c>{purpose}-{startedUtc}.log</c>, and
/// the same instant is written to the pids row, so pid → log is still an exact lookup rather than a
/// guess (<see cref="Resolve"/>). Legacy <c>{purpose}-{pid}.log</c> files still resolve.
/// </summary>
public static partial class BgLogs
{
    /// <summary>Compact, sortable, filename-safe UTC stamp.</summary>
    public const string StampFormat = "yyyyMMdd-HHmmssfff";

    /// <summary>The purpose prefix <see cref="AgentSession"/> tracks a live agent under. An agent is
    /// the one tracked pid that has no bg-log at all — its output goes to the session stream.</summary>
    public const string AgentPurposePrefix = "agent:";

    /// <summary>SC5.4: true for the run's own agent sessions, which <see cref="Resolve"/> can never
    /// answer for — there is no file under <c>bg-logs/</c> with their name on it.</summary>
    public static bool IsAgentRow(PidRow row) =>
        row is not null && row.Purpose.StartsWith(AgentPurposePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The session an agent pid row belongs to. The column is authoritative; the
    /// <c>session#N</c> tail of the purpose is the fallback that keeps rows written before SC5.4
    /// (stage_id and session_number both NULL for every agent row) resolvable.</summary>
    public static int? SessionNumberFor(PidRow row)
    {
        if (!IsAgentRow(row)) return null;
        if (row!.SessionNumber is > 0) return row.SessionNumber;
        var hash = row.Purpose.LastIndexOf("session#", StringComparison.OrdinalIgnoreCase);
        if (hash < 0) return null;
        var digits = new string(row.Purpose[(hash + "session#".Length)..].TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>The raw agent stream for session <paramref name="number"/>, written live by
    /// <see cref="AgentSession"/>. Kept next to <see cref="PromptName"/> under <c>logs/</c>.</summary>
    public static string StreamName(int number) => $"session-{number:000}.jsonl";

    /// <summary>The composed prompt that started session <paramref name="number"/>.</summary>
    public static string PromptName(int number) => $"session-{number:000}.prompt.md";

    /// <summary>
    /// SC5.4 (round-four #4): where `bg logs` on an AGENT row has to look. `bg status` lists the live
    /// agent, so `bg logs &lt;that pid&gt;` is the obvious way to watch a session — and it answered
    /// "No log file found" and then printed 67 unrelated bg log names, because an agent's output never
    /// goes to <c>bg-logs/</c>. It goes here.
    /// </summary>
    /// <returns>The stream path, or null when the row is not an agent row or the file is not there.</returns>
    public static string? ResolveAgentStream(string stateDir, PidRow row)
    {
        if (SessionNumberFor(row) is not { } number) return null;
        var path = Path.Combine(stateDir, "logs", StreamName(number));
        return File.Exists(path) ? path : null;
    }

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "bg-process" : result;
    }

    /// <summary>The log file name for a bg child. <paramref name="startedUtc"/> MUST be the same
    /// instant recorded in the pids row — that identity is what makes <see cref="Resolve"/> exact.</summary>
    public static string NameFor(string purpose, DateTime startedUtc) =>
        $"{Sanitize(purpose)}-{startedUtc.ToUniversalTime().ToString(StampFormat, System.Globalization.CultureInfo.InvariantCulture)}.log";

    /// <summary>The pids row for <paramref name="pid"/> in this run, or null when the store cannot be
    /// asked. Best-effort by design: every caller has a fuzzier path to fall back to.</summary>
    public static PidRow? FindRow(IRunStore? store, string? runId, int pid)
    {
        if (store == null || string.IsNullOrEmpty(runId)) return null;
        try { return store.GetAllPids(runId).FirstOrDefault(p => p.Pid == pid); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Find a bg child's log by pid: the legacy pid-suffixed name first, then the exact
    /// name reconstructed from its pids row. Null when there is nothing to read.</summary>
    public static string? Resolve(string logDir, int pid, IRunStore? store, string? runId)
    {
        if (!Directory.Exists(logDir)) return null;

        var legacy = Directory.EnumerateFiles(logDir, $"*-{pid}.log").FirstOrDefault();
        if (legacy != null) return legacy;

        if (store == null || string.IsNullOrEmpty(runId)) return null;
        try
        {
            var row = store.GetAllPids(runId).FirstOrDefault(p => p.Pid == pid);
            if (row == null) return null;
            var purpose = row.Purpose.StartsWith(StallDetector.BgPurposePrefix, StringComparison.OrdinalIgnoreCase)
                ? row.Purpose[StallDetector.BgPurposePrefix.Length..]
                : row.Purpose;
            var path = Path.Combine(logDir, NameFor(purpose, row.StartedUtc));
            return File.Exists(path) ? path : null;
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// A spawn whose output the OS writes to <paramref name="logPath"/> — no in-process pump, so it
    /// survives the launcher exiting a millisecond later. The tracked pid is the shell's; every
    /// kill path already tree-kills, so the real child dies with it.
    ///
    /// <para>SF0.3 (bug #12): the three standard streams are redirected to handles THIS process owns,
    /// and <see cref="DetachStandardStreams"/> closes them the moment the child is up. Not to capture
    /// anything — the log is written by the shell's own <c>&gt;</c>, which is the whole point of
    /// W3.3's fix and is unchanged — but to stop the launcher's console handles being INHERITED by a
    /// process that outlives it. A detached child holding the caller's stdout means a pipe on
    /// <c>conductor bg start</c> gets no EOF until the background job finishes, so the one command
    /// whose promise is "this returns immediately" blocked for the full length of the work. It also
    /// stops a detached child eating keystrokes from the caller's stdin.</para>
    /// </summary>
    public static ProcessStartInfo RedirectedSpawn(string exe, IReadOnlyList<string> args, string workingDir, string logPath)
    {
        ArgumentNullException.ThrowIfNull(args);
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            // /d skips AutoRun, /s makes cmd strip exactly the outer quotes and take the rest
            // verbatim — the only reliable way to hand it a fully quoted command line.
            psi.Arguments = $"/d /s /c \"{WindowsCommandLine(exe, args)} > {Quote(logPath)} 2>&1\"";
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"exec {PosixCommandLine(exe, args)} > {PosixQuote(logPath)} 2>&1");
        }
        return psi;
    }

    /// <summary>
    /// SF0.3 (bug #12): let go of the pipes <see cref="RedirectedSpawn"/> created. Every caller starts
    /// a child it deliberately does not wait for, so nothing here ever reads them — leaving them open
    /// would only swap an inherited-handle leak for a pipe-buffer one. Closing our ends leaves the
    /// child writing into a broken pipe, which is harmless: the shell redirects the real command's
    /// stdout and stderr to the log file, and even the shell's own "not recognized" error goes there
    /// (measured), so nothing an operator needs is on these streams. Stdin closes to EOF, which is the
    /// honest answer to a detached child that asks the console a question nobody will see.
    /// </summary>
    public static void DetachStandardStreams(Process proc)
    {
        ArgumentNullException.ThrowIfNull(proc);
        try { proc.StandardInput.Close(); } catch (InvalidOperationException) { }
        try { proc.StandardOutput.Close(); } catch (InvalidOperationException) { }
        try { proc.StandardError.Close(); } catch (InvalidOperationException) { }
    }

    internal static string WindowsCommandLine(string exe, IReadOnlyList<string> args) =>
        string.Join(" ", new[] { exe }.Concat(args).Select(Quote));

    internal static string PosixCommandLine(string exe, IReadOnlyList<string> args) =>
        string.Join(" ", new[] { exe }.Concat(args).Select(PosixQuote));

    /// <summary>Quote for cmd.exe: always quote, and double any embedded quote.</summary>
    private static string Quote(string s) => $"\"{s.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>Quote for sh: single quotes, with the standard '\'' escape.</summary>
    private static string PosixQuote(string s) => $"'{s.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
