using System.Globalization;
using System.Text.Json;

using Conductor.Core.Http;

namespace Conductor.Core.Fleet;

/// <summary>
/// SF5.4 — who else is running on this machine.
///
/// <para>The websites are plural, so the runs are plural: two repos, two engines, two ports, and until
/// now no way to see the second one without remembering it existed. Concurrent runs already coexist
/// (the control plane scans forward from 4317 precisely so they never collide); this makes them
/// visible.</para>
///
/// <para><b>Why a port probe and not a walk of discovery files.</b> The obvious implementation is to
/// find every <c>.conductor/control-plane.json</c> on disk — but there is no machine-wide index of
/// state dirs to walk, and worse, the file is not reliably there. Measured, not assumed: at the time
/// this was written the run driving this session was serving <c>127.0.0.1:4317</c> with a live engine
/// and <b>no</b> <c>control-plane.json</c> in its state dir (the file is deleted on control-plane
/// dispose, and a second short-lived server instance in the same state dir takes the first one's file
/// with it). A discovery-file scan would have missed the very run it was running inside. So the probe
/// is the primary source and the discovery file is the enrichment: every live plane answers
/// <c>GET /state</c>, and that answer already carries the run's identity — plan name, run id, repo,
/// state dir, status — because the Face needs exactly those fields.</para>
///
/// <para>Nothing here writes. <c>ps</c> is a read of other people's runs, including runs this machine's
/// owner did not start, so it may never do anything a stranger's engine would notice: loopback GETs
/// with a short timeout, no token, no POST.</para>
/// </summary>
public static class FleetScan
{
    /// <summary>The port the control plane prefers; it scans forward from here when taken.</summary>
    public const int FirstPort = 4317;

    /// <summary>How many ports forward the server itself will try — so the fleet lives in exactly this
    /// window and scanning wider only finds strangers.</summary>
    public const int PortSpan = 20;

    /// <summary>Default per-port probe budget. Generous enough for a busy engine folding a long event
    /// log, short enough that twenty ports of silence do not read as a hang.</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(2500);

    public static IReadOnlyList<int> DefaultPorts { get; } =
        Enumerable.Range(FirstPort, PortSpan).ToArray();

    /// <summary>Asks one port for its <c>/state</c>. Returns the body, or null for "nobody, or not a
    /// conductor". Injected so the scan is testable without a socket.</summary>
    public delegate Task<string?> StateProbe(int port, CancellationToken ct);

    /// <summary>Probes every port concurrently and returns the runs that answered, port order. Twenty
    /// sequential probes against a machine where most ports are dead would cost twenty timeouts.</summary>
    public static async Task<IReadOnlyList<FleetRun>> ScanAsync(
        StateProbe probe, IEnumerable<int> ports, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(ports);

        var portList = ports.ToArray();
        var bodies = await Task.WhenAll(portList.Select(async p =>
        {
            try { return (Port: p, Body: await probe(p, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return (Port: p, Body: (string?)null); }
        })).ConfigureAwait(false);

        return bodies
            .Select(b => FromStateJson(b.Port, b.Body))
            .Where(r => r is not null)
            .Select(r => r!)
            .OrderBy(r => r.Port)
            .ToArray();
    }

    /// <summary>The default probe: a loopback GET with its own timeout, quiet on every failure.</summary>
    public static StateProbe HttpProbe(HttpClient client, TimeSpan timeout) => async (port, ct) =>
    {
        ArgumentNullException.ThrowIfNull(client);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var resp = await client.GetAsync(
                new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/state"),
                cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;   // 4321 and 4331 on this box answer 404 — strangers
            return await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return null;    // closed, foreign, or too slow to be a healthy plane
        }
    };

    /// <summary>Turns one <c>/state</c> body into a fleet row. Null when the body is not a conductor's:
    /// a stranger serving JSON of another shape deserializes into an all-default record, which is why
    /// the run id and plan name are the identity test rather than a bare "did it parse".</summary>
    public static FleetRun? FromStateJson(int port, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        StateDto? dto;
        try { dto = JsonSerializer.Deserialize(json, ControlPlaneJsonContext.Default.StateDto); }
        catch (JsonException) { return null; }
        catch (NotSupportedException) { return null; }
        if (dto is null) return null;
        if (string.IsNullOrWhiteSpace(dto.RunId) && string.IsNullOrWhiteSpace(dto.PlanName)) return null;

        return new FleetRun(
            Port: port,
            BaseUrl: $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}",
            PlanName: dto.PlanName ?? "",
            RunId: dto.RunId ?? "",
            Repo: dto.Repo ?? "",
            StateDir: dto.StateDir ?? "",
            Status: string.IsNullOrWhiteSpace(dto.Status) ? "unknown" : dto.Status,
            StageId: dto.StageId ?? "",
            StageTitle: dto.StageTitle ?? "",
            AttentionReason: string.IsNullOrWhiteSpace(dto.AttentionReason) ? null : dto.AttentionReason,
            Done: dto.DoneCount,
            Total: dto.TotalCount,
            CostUsd: dto.TotalCostUsd);
    }

    /// <summary>Fills in what only the run's own state dir knows — the engine pid and when it started.
    /// Pure: the caller supplies the two file bodies, so the merge is testable without a filesystem.
    /// The discovery file wins on pid because it names the process that BOUND the port; the engine lock
    /// is the fallback for a run whose discovery file has gone missing, which is not hypothetical.</summary>
    public static FleetRun Enrich(FleetRun run, string? discoveryJson, string? lockText)
    {
        ArgumentNullException.ThrowIfNull(run);

        ControlPlaneInfo? info = null;
        if (!string.IsNullOrWhiteSpace(discoveryJson))
        {
            try { info = JsonSerializer.Deserialize(discoveryJson, ControlPlaneJsonContext.Default.ControlPlaneInfo); }
            catch (JsonException) { info = null; }
        }
        // A discovery file from a DIFFERENT plane in the same state dir (a stale one left by an engine
        // that has since restarted on another port) must not lend its pid to this row.
        if (info is not null && info.Port != run.Port) info = null;

        var holder = EngineLock.Parse(lockText);

        var pid = info?.Pid ?? holder?.Pid ?? 0;
        var started = info?.StartedUtc ?? holder?.StartedUtc;
        return run with
        {
            Pid = pid,
            StartedUtc = started,
            HasDiscoveryFile = info is not null,
        };
    }

    /// <summary>Reads the two files for a row's state dir and merges them. Best effort by design: a run
    /// in a directory this user cannot read still lists, just without a pid.</summary>
    public static async Task<FleetRun> EnrichFromDiskAsync(FleetRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(run.StateDir)) return run;
        return Enrich(run,
            await TryReadAsync(ControlPlaneDiscovery.PathFor(run.StateDir)).ConfigureAwait(false),
            await TryReadAsync(EngineLock.PathFor(run.StateDir)).ConfigureAwait(false));
    }

    /// <summary>The write token for a run, out of its discovery file. Null when there is no file, no
    /// token in it, or — the case that matters — the file names a DIFFERENT port than the plane that
    /// answered: a stale token from a previous engine in the same state dir would 401 every write, and
    /// silently, since reads never need one.</summary>
    public static string? TokenFrom(string? discoveryJson, int port)
    {
        if (string.IsNullOrWhiteSpace(discoveryJson)) return null;
        ControlPlaneInfo? info;
        try { info = JsonSerializer.Deserialize(discoveryJson, ControlPlaneJsonContext.Default.ControlPlaneInfo); }
        catch (JsonException) { return null; }
        if (info is null || info.Port != port) return null;
        return string.IsNullOrWhiteSpace(info.Token) ? null : info.Token;
    }

    /// <summary>Reads one run's write token off disk. Best effort: a run whose state dir this user
    /// cannot read still lists, read-only.</summary>
    public static async Task<string?> ReadTokenAsync(FleetRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (string.IsNullOrWhiteSpace(run.StateDir)) return null;
        return TokenFrom(await TryReadAsync(ControlPlaneDiscovery.PathFor(run.StateDir)).ConfigureAwait(false), run.Port);
    }

    private static async Task<string?> TryReadAsync(string path)
    {
        try { return File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>A run holding a live engine lock in <paramref name="stateDir"/> that no plane on the
    /// scanned ports claims — headless (the control plane is opt-in), bound outside the window, or
    /// wedged. It belongs in the listing: "no run here" and "a run here I cannot talk to" are different
    /// facts, and only one of them means it is safe to start another engine.</summary>
    public static async Task<FleetRun?> UnattachedRunAsync(string stateDir, string planName, IReadOnlyList<FleetRun> answered)
    {
        ArgumentNullException.ThrowIfNull(answered);
        if (string.IsNullOrWhiteSpace(stateDir)) return null;
        if (answered.Any(r => SameDir(r.StateDir, stateDir))) return null;

        var holder = EngineLock.Parse(await TryReadAsync(EngineLock.PathFor(stateDir)).ConfigureAwait(false));
        if (holder is null || !EngineLock.IsLive(holder)) return null;

        // No plane to ask, so the repo is inferred from where the state dir sits — PlanConfig roots
        // .conductor at the repo, so its parent is the repo and the row reads like every other one.
        var repo = Path.GetDirectoryName(stateDir.Replace('\\', '/').TrimEnd('/')) ?? "";

        return new FleetRun(
            Port: 0, BaseUrl: "", PlanName: planName, RunId: "", Repo: repo, StateDir: stateDir,
            Status: "no control plane", StageId: "", StageTitle: "", AttentionReason: null,
            Done: 0, Total: 0, CostUsd: 0m)
        {
            Pid = holder.Pid,
            StartedUtc = holder.StartedUtc,
            HasDiscoveryFile = false,
        };
    }

    /// <summary>Path comparison for state dirs coming from two sources (one off the wire, one off the
    /// local plan) — separators and case differ on Windows, and the run must not list twice.</summary>
    public static bool SameDir(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string Norm(string p) => p.Replace('\\', '/').TrimEnd('/');
        return string.Equals(Norm(a), Norm(b), OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
