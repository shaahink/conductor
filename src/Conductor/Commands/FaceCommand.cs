using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Conductor.Core.Face;
using Conductor.Core.Fleet;
using Conductor.Core.Http;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// Attaches a Face TUI to a run that is already going — a second terminal, or a reattach after the Face
/// was closed.
///
/// <para>SF5.4: the target is found by PROBING the control-plane ports, not by reading this directory's
/// <c>control-plane.json</c>. The old way was wrong twice. It could only ever reach the run in this
/// repo, which is no answer at all on the machine this ships to (several websites, several engines);
/// and it was wrong about that too, because the discovery file is deleted on control-plane dispose — a
/// live engine can be serving 4317 with no file, and <c>conductor face</c> said "no live run" at a live
/// run. See <see cref="FleetScan"/> for why the probe leads.</para>
///
/// <para>The run in THIS directory still wins without a prompt: standing in a repo is an unambiguous
/// answer. When it is not there — or <c>--pick</c> asks — the fleet goes to the Face in
/// <c>CONDUCTOR_FLEET</c> and its picker asks. That variable carries write tokens, which is why it is
/// an environment variable and not an argument.</para>
/// </summary>
public sealed partial class FaceCommand : AsyncCommand<FaceCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandOption("--demo")]
        [Description("Run the TUI against synthetic data — no conductor process needed.")]
        public bool Demo { get; init; }

        [CommandOption("--pick")]
        [Description("Always show the run picker, even when the run in this directory is live.")]
        public bool Pick { get; init; }

        [CommandOption("--timeout <MS>")]
        [Description("Per-port probe budget in milliseconds (default 2500). Raise it if a busy engine is missed.")]
        public int? TimeoutMs { get; init; }

        [CommandOption("--archive <RUN>")]
        [Description("Open a FINISHED run read-only: a run id, an id prefix, a catalogue slug, a repo name, or a run.db path.")]
        public string? Archive { get; init; }

        [CommandOption("--serve")]
        [Description("With --archive: serve the read-only plane and print its url instead of opening a face.")]
        public bool Serve { get; init; }

        [CommandOption("--port <N>")]
        [Description("With --archive: first port to bind (default 4400 — deliberately outside the fleet window).")]
        public int? Port { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // KS2.2 — a finished run is served, not probed for. Answered before the port scan because
        // there is nothing on a port to find.
        if (!string.IsNullOrWhiteSpace(settings.Archive))
            return await ArchiveAsync(settings.Archive, settings.Serve, settings.Port).ConfigureAwait(false);

        var psi = FaceProcess();
        if (psi is null) return 1;

        if (settings.Demo)
        {
            psi.ArgumentList.Add("--demo");
            return await LaunchAsync(psi).ConfigureAwait(false);
        }

        var localStateDir = LocalStateDir(settings, out var localPlanName);

        var timeout = TimeSpan.FromMilliseconds(settings.TimeoutMs is > 0 ? settings.TimeoutMs.Value : FleetScan.DefaultProbeTimeout.TotalMilliseconds);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // the per-probe CTS owns the clock
        var answered = await FleetScan.ScanAsync(FleetScan.HttpProbe(http, timeout), FleetScan.DefaultPorts).ConfigureAwait(false);

        var runs = new List<FleetRun>();
        foreach (var r in answered) runs.Add(await FleetScan.EnrichFromDiskAsync(r).ConfigureAwait(false));

        var decision = FaceTarget.Choose(runs, localStateDir, settings.Pick);
        switch (decision.Kind)
        {
            case FaceTarget.Kind.Single when decision.Run is { } run:
                // Say which run was chosen. It used to be unambiguous — this directory's, or nothing —
                // and now it can be another repo's, so the one line before the TUI takes the terminal
                // is the only chance the user has to notice they are looking at the wrong website.
                AnsiConsole.MarkupLine($"[grey]attaching to[/] [white]{Markup.Escape(string.IsNullOrWhiteSpace(run.RepoLabel) ? run.PlanName : run.RepoLabel)}[/] [grey]{Markup.Escape(run.StageId)} · {Markup.Escape(run.BaseUrl)}[/]");
                // The write token goes via env, never argv — it must not show in a process listing.
                return await AttachAsync(run.BaseUrl, await FleetScan.ReadTokenAsync(run).ConfigureAwait(false))
                    .ConfigureAwait(false);

            case FaceTarget.Kind.Picker:
                // K3.2: the picker also lists what this machine remembers. Best-effort — a catalogue
                // that cannot be read must not stop someone attaching to a live run.
                IReadOnlyList<FacePastRun> past = [];
                try
                {
                    past = FacePastRuns.Read(StateHome.Root, decision.Fleet.Select(r => r.RunId));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // no history offered; the live runs still are
                }
                psi.Environment[FaceTarget.FleetEnvVar] =
                    FaceTarget.Serialize(decision.Fleet, await TokensForAsync(decision.Fleet).ConfigureAwait(false),
                        localStateDir, past);
                // KS2.2: the picker can now answer with a FINISHED run, which has no url to attach to.
                // It writes that run's id here and exits; we open the read-only archive over it.
                return await LaunchThenMaybeArchiveAsync(psi).ConfigureAwait(false);

            default:
                return await NothingToAttachToAsync(psi, localStateDir, localPlanName).ConfigureAwait(false);
        }
    }

    /// <summary>Every reachable run's write token, keyed by state dir. Read once here rather than
    /// inside the serializer so the envelope stays a pure transformation.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> TokensForAsync(IReadOnlyList<FleetRun> runs)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in runs)
            if (await FleetScan.ReadTokenAsync(r).ConfigureAwait(false) is { Length: > 0 } t)
                tokens[r.StateDir] = t;
        return tokens;
    }

    /// <summary>No plane answered the scan. Two things can still be true, and they need different
    /// sentences: a discovery file here naming a port outside the window (a race with a starting
    /// engine — attach to it and say nothing), or an engine holding this plan's lock with no control
    /// plane at all, which is a run the Face genuinely cannot reach and must not pretend to.</summary>
    private static async Task<int> NothingToAttachToAsync(ProcessStartInfo psi, string? localStateDir, string localPlanName)
    {
        if (localStateDir is not null)
        {
            var discovery = ControlPlaneDiscovery.PathFor(localStateDir);
            if (File.Exists(discovery))
            {
                try
                {
                    var info = JsonSerializer.Deserialize(await File.ReadAllTextAsync(discovery).ConfigureAwait(false),
                        ControlPlaneJsonContext.Default.ControlPlaneInfo);
                    if (info is not null && !string.IsNullOrWhiteSpace(info.BaseUrl))
                    {
                        psi.ArgumentList.Add("--url");
                        psi.ArgumentList.Add(info.BaseUrl);
                        if (!string.IsNullOrEmpty(info.Token)) psi.Environment["CONDUCTOR_TOKEN"] = info.Token;
                        return await LaunchAsync(psi).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* fall through to the message */ }
            }

            if (await FleetScan.UnattachedRunAsync(localStateDir, localPlanName, []).ConfigureAwait(false) is { } orphan)
            {
                AnsiConsole.MarkupLine($"[red]error:[/] an engine is running here (pid [yellow]{orphan.Pid.ToString(CultureInfo.InvariantCulture)}[/]) with no control plane, so there is nothing to attach to.");
                AnsiConsole.MarkupLine("[grey]Restart it with [/][yellow]conductor run --control-plane[/][grey], or watch it with [/][yellow]conductor watch[/][grey].[/]");
                return 1;
            }
        }

        AnsiConsole.MarkupLine($"[red]error:[/] no conductor run answering on ports [yellow]{FleetScan.FirstPort.ToString(CultureInfo.InvariantCulture)}-{(FleetScan.FirstPort + FleetScan.PortSpan - 1).ToString(CultureInfo.InvariantCulture)}[/].");
        AnsiConsole.MarkupLine("[grey]Start one with [/][yellow]conductor run --control-plane[/][grey], see what is live with [/][yellow]conductor ps[/][grey], or explore offline with [/][yellow]conductor face --demo[/][grey].[/]");
        return 1;
    }

    /// <summary>KS2.1: the hub attaches through this, so the front door and the verb share one
    /// launcher and one token rule. The token goes via the environment, never argv — a process
    /// listing is readable by every process on the machine.</summary>
    internal static async Task<int> AttachAsync(string baseUrl, string? token)
    {
        var psi = FaceProcess();
        if (psi is null) return 1;
        psi.ArgumentList.Add("--url");
        psi.ArgumentList.Add(baseUrl);
        if (!string.IsNullOrEmpty(token)) psi.Environment["CONDUCTOR_TOKEN"] = token;
        return await LaunchAsync(psi).ConfigureAwait(false);
    }

    /// <summary>The Face binary, or the one sentence that says how to build it. Null means it is not
    /// there — every caller turns that into exit 1 rather than launching nothing quietly.</summary>
    private static ProcessStartInfo? FaceProcess()
    {
        var entry = FaceLauncher.ResolveEntrypoint();
        if (entry is not null) return new ProcessStartInfo(entry) { UseShellExecute = false };
        AnsiConsole.MarkupLine($"[red]error:[/] no built Face found. Run [yellow]go build -o bin/{FaceLauncher.BinaryName} ./cmd/conductor-face/[/] in [yellow]face-go/[/].");
        return null;
    }

    private static async Task<int> LaunchAsync(ProcessStartInfo psi)
    {
        using var proc = Process.Start(psi);
        if (proc is null) return 1;
        await proc.WaitForExitAsync().ConfigureAwait(false);
        return proc.ExitCode;
    }

    /// <summary>The state dir of the plan in this directory, quietly. <c>face</c> now works from a
    /// directory with no plan at all — it can still find and attach to a run elsewhere on the machine —
    /// so a missing or ambiguous plan is a normal outcome here, never an error.</summary>
    private static string? LocalStateDir(Settings settings, out string planName)
    {
        planName = "";
        try
        {
            // Deliberately NOT ResolvePlanPath(): that one prompts on an ambiguous directory and throws
            // on an empty one, both of which are fine for `run` and wrong here — `face` in a directory
            // with no plan should quietly go looking at the machine instead of interrogating the user.
            var path = settings.Plan ?? Environment.GetEnvironmentVariable("CONDUCTOR_PLAN");
            if (string.IsNullOrWhiteSpace(path))
            {
                var candidates = PlanDiscovery.Discover(Directory.GetCurrentDirectory());
                if (candidates.Count != 1) return null;
                path = candidates[0].Path;
            }
            if (!File.Exists(path)) return null;
            var plan = PlanConfig.Load(path);
            planName = plan.Name;
            return plan.StateDir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
