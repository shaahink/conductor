using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Http;
using Conductor.Models;

using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// SC5.2: <c>conductor run --detach</c>. The parent spawns the engine into its own process group
/// (see <see cref="DetachedProcess"/>), waits for the child to publish its own control-plane
/// discovery file, prints the pid and the URL it actually bound, and returns.
///
/// <para>The URL is READ BACK from the child, never predicted. The preferred port is only a
/// preference — <c>ControlPlaneServer.Start</c> scans forward when it is taken, so a second
/// concurrent run lands elsewhere. Printing <c>--port</c> back at the operator would be a plausible
/// lie exactly when it matters, which is this era's whole complaint.</para>
/// </summary>
public static class RunDetach
{
    /// <summary>How long the parent waits for the child to publish its bound port before giving up
    /// on the handshake. Generous: a cold start pays JIT, plan load and store migrations first.</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long a published control plane must SURVIVE before the banner will vouch for it.
    ///
    /// <para>The engine binds and publishes its discovery file before it takes the plan lock, so a
    /// second <c>run --detach</c> onto a plan that is already running publishes a perfectly real
    /// URL and then exits with "another conductor already holds this plan's lock". Without this
    /// settle the handshake races that exit: it wins sometimes, and when it wins the operator is
    /// handed a pid and a URL belonging to a process that no longer exists.</para>
    /// </summary>
    public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The child's argv. Everything the parent was asked to do, minus the flags that only mean
    /// something with a terminal attached: a detached process has no console, so the Face cannot
    /// live in it (attach one later with <c>conductor face</c>) and the TUI must not be tried.
    /// <c>--detach</c> itself is dropped — a child that re-detached would fork forever.
    /// </summary>
    public static List<string> ChildArgs(RunCommand.Settings s, string planPath)
    {
        var a = new List<string> { "run", "-p", planPath, "--headless", "--no-face" };
        if (s.Once) a.Add("--once");
        if (s.MaxSessions > 0)
        {
            a.Add("--max-sessions");
            a.Add(s.MaxSessions.ToString(CultureInfo.InvariantCulture));
        }
        if (s.Paused) a.Add("--paused");
        if (s.NoControlPlane) a.Add("--no-control-plane");
        a.Add("--port");
        a.Add(s.ControlPlanePort.ToString(CultureInfo.InvariantCulture));
        return a;
    }

    /// <summary>How to re-launch this same engine. Normally the apphost itself; under
    /// <c>dotnet Conductor.dll</c> the host exe alone would start a bare runtime, so the entry
    /// assembly is put back in front of the arguments.</summary>
    public static (string Exe, IReadOnlyList<string> Prefix, string? Error) ResolveSelf()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return ("", [], "cannot determine this executable's own path");

        var stem = Path.GetFileNameWithoutExtension(exe);
        if (!string.Equals(stem, "dotnet", StringComparison.OrdinalIgnoreCase))
            return (exe, [], null);

        // SC8.3: NOT Assembly.Location — it is empty in a single-file app and reading it is an
        // IL3000 error under the analyzer PublishSingleFile turns on, which made release.yml fail to
        // compile. This branch is unreachable in a single-file build anyway (the host would have to
        // be `dotnet`), but the compiler cannot know that. BaseDirectory + the simple name is the
        // documented replacement and resolves to the same file under `dotnet Conductor.dll`.
        var name = Assembly.GetEntryAssembly()?.GetName().Name;
        var dll = string.IsNullOrEmpty(name) ? null : Path.Combine(AppContext.BaseDirectory, name + ".dll");
        return string.IsNullOrEmpty(dll) || !File.Exists(dll)
            ? ("", [], "running under the dotnet host with no locatable entry assembly")
            : (exe, new[] { dll }, null);
    }

    /// <summary>Read the discovery file, tolerating the window where the child is mid-write.</summary>
    public static ControlPlaneInfo? ReadDiscovery(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return JsonSerializer.Deserialize(sr.ReadToEnd(), ControlPlaneJsonContext.Default.ControlPlaneInfo);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The run log the operator actually wants, sitting BESIDE <c>logs/</c> rather than in
    /// it — <c>logs/</c> holds the dated rotation and the per-session streams. Same path
    /// <c>RunCommand</c>'s epilogue prints, for the same reason.</summary>
    public static string RunLogPath(string stateDir) => Path.Combine(stateDir, "conductor.log");

    /// <summary>The handshake test, kept pure so it is testable without a process: only a discovery
    /// file naming OUR child answers. A stale file from a previous run of the same plan is the whole
    /// hazard here — it parses, it holds a plausible URL, and it belongs to a dead engine.</summary>
    public static bool IsOurs(ControlPlaneInfo? info, int childPid) => info is not null && info.Pid == childPid;

    public static async Task<int> LaunchAsync(RunCommand.Settings settings, string planPath, PlanConfig plan, CancellationToken ct)
    {
        if (settings.DryRun)
        {
            AnsiConsole.MarkupLine("[red]error:[/] --detach and --dry-run contradict each other — a dry run prints a prompt to THIS terminal and exits.");
            return 1;
        }

        var (exe, prefix, resolveError) = ResolveSelf();
        if (resolveError is not null)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] cannot detach: {Markup.Escape(resolveError)}.");
            return 1;
        }

        var stateDir = Path.GetFullPath(plan.StateDir);
        var logsDir = Path.Combine(stateDir, "logs");
        Directory.CreateDirectory(logsDir);
        var discovery = ControlPlaneDiscovery.PathFor(stateDir);
        // One capture file PER detach, not one shared file. A shared detach.log made the failure
        // report lie by omission: the tail printed for a child that died on the plan lock was full
        // of the LIVE run's session lines, appended by the engine that already held it.
        var detachLog = Path.Combine(logsDir, $"detach-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

        var args = new List<string>(prefix);
        args.AddRange(ChildArgs(settings, Path.GetFullPath(planPath)));

        var spawn = DetachedProcess.Start(exe, args, Directory.GetCurrentDirectory(), detachLog);
        if (!spawn.Ok)
        {
            AnsiConsole.MarkupLine($"[red]error:[/] {Markup.Escape(spawn.Error ?? "detach failed")}");
            return 1;
        }

        var info = await AwaitHandshakeAsync(discovery, spawn.Pid, ct).ConfigureAwait(false);
        if (info is not null && !await SurvivesSettleAsync(spawn.Pid, ct).ConfigureAwait(false)) info = null;
        await ReportAsync(spawn, info, plan, planPath, settings, detachLog, stateDir).ConfigureAwait(false);
        return info is null && !Alive(spawn.Pid) ? 1 : 0;
    }

    private static async Task<ControlPlaneInfo?> AwaitHandshakeAsync(string discovery, int childPid, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + HandshakeTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var info = ReadDiscovery(discovery);
            if (IsOurs(info, childPid)) return info;
            // A child that has already exited will never write one — stop waiting the full timeout
            // to tell the operator something they could have known in a second.
            if (!Alive(childPid)) return null;
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
        }
        return null;
    }

    /// <summary>Watch the child across <see cref="SettleWindow"/> and report whether it was still
    /// there at the end. A URL is only worth printing if something is still listening on it.</summary>
    private static async Task<bool> SurvivesSettleAsync(int pid, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + SettleWindow;
        while (DateTime.UtcNow < deadline)
        {
            if (!Alive(pid)) return false;
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        return Alive(pid);
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        // Access-denied on a foreign owner of a recycled id: not ours to judge, assume alive.
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    private static async Task ReportAsync(DetachSpawn spawn, ControlPlaneInfo? info, PlanConfig plan,
        string planPath, RunCommand.Settings settings, string detachLog, string stateDir)
    {
        var planArg = Markup.Escape(planPath);
        if (info is null && !Alive(spawn.Pid))
        {
            AnsiConsole.MarkupLine($"[red]detach failed:[/] the engine (pid {spawn.Pid}) exited before its control plane was usable. Its last output:");
            foreach (var line in await TailAsync(detachLog, 15).ConfigureAwait(false))
                AnsiConsole.MarkupLine($"[grey]  |[/] {Markup.Escape(line)}");
            AnsiConsole.MarkupLine($"[grey]full output: {Markup.Escape(detachLog)}[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]run detached[/] — pid [yellow]{spawn.Pid}[/] · plan {Markup.Escape(plan.Name)}");
        if (info is not null)
            AnsiConsole.MarkupLine($"  control plane: [yellow]{Markup.Escape(info.BaseUrl)}[/]");
        else if (settings.NoControlPlane)
            AnsiConsole.MarkupLine("  control plane: [grey]disabled by --no-control-plane — this run cannot be attached to[/]");
        else
            AnsiConsole.MarkupLine($"  control plane: [yellow]not yet published[/] after {HandshakeTimeout.TotalSeconds:0}s — the engine is alive; check {Markup.Escape(detachLog)}");

        AnsiConsole.MarkupLine($"  attach:        [yellow]conductor face -p {planArg}[/]");
        AnsiConsole.MarkupLine($"  watch:         [yellow]conductor status -p {planArg}[/]");
        // The run log, and then the console stream the detached engine can no longer show anyone.
        // Both paths are checked against the live rig — a banner pointing at a file that does not
        // exist is the same class of lie as a doc comment that does not match the code.
        AnsiConsole.MarkupLine($"  logs:          {Markup.Escape(RunLogPath(stateDir))}");
        AnsiConsole.MarkupLine($"  console:       {Markup.Escape(detachLog)}");
        AnsiConsole.MarkupLine($"  stop:          [yellow]conductor abort -p {planArg}[/]");
        AnsiConsole.MarkupLine(spawn.BrokeAwayFromJob
            ? "[grey]this shell can close, log off, or be torn down — the run is in its own process group and does not go with it.[/]"
            : "[grey]this shell can close — the run is in its own process group. Note: it could not leave this shell's job object, so a forced teardown of the whole job would still reach it.[/]");
    }

    private static async Task<IReadOnlyList<string>> TailAsync(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return [];
            // The child still holds this file open for writing, so the share flags are not optional.
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            await using (fs.ConfigureAwait(false))
            {
                using var sr = new StreamReader(fs);
                var all = (await sr.ReadToEndAsync().ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                return all.Length <= lines ? all : all[^lines..];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
