using System.Text.Json;

using Conductor.Core.Fleet;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Commands;

/// <summary>
/// KS2.5 — what is on this machine, gathered once for everything that asks.
///
/// <para>Two surfaces answer "what is this machine doing": the hub (KS2.1), and <c>status</c> standing
/// in a directory that names no plan. They must answer it identically, and the way to guarantee that is
/// not discipline — it is having one gatherer. A board with two implementations ends up with the one
/// nobody watches quietly going stale, which is the exact failure KS1 spent six checkpoints removing
/// from the read side.</para>
///
/// <para><b>Best effort, everywhere.</b> Every step here can fail on a real machine — a catalogue
/// written by a newer engine, a plan file being edited right now, a state dir another user owns — and
/// none of those is a reason to refuse to say what else is running. A board that throws is a board that
/// tells you nothing on precisely the day something is wrong.</para>
///
/// <para><b>It never resolves "the" plan.</b> Plans are DISCOVERED and listed, however many there are;
/// zero is a normal outcome and so is eleven. <c>PlanSettings.ResolvePlanPath</c> prompts on the second
/// case and throws on the first, and neither belongs anywhere near a board.</para>
/// </summary>
public static class MachineBoard
{
    /// <summary>The plans discoverable from where the caller is standing, quietly.</summary>
    public static IReadOnlyList<PlanDiscovery.Candidate> Discover(string cwd)
    {
        try { return PlanDiscovery.Discover(cwd); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>The whole board: the fleet probe, the catalogue, and the plans here, composed into the
    /// model both surfaces render. The fleet list comes back beside the model because an attach needs
    /// the raw rows (a state dir and a port) that the display model deliberately flattens.</summary>
    /// <param name="probeTimeout">Per-port budget. Bounded by construction — twenty ports of silence
    /// must never read as a hang, which is why this is the only clock the scan consults.</param>
    public static async Task<(HubModel Model, IReadOnlyList<FleetRun> Fleet)> GatherAsync(
        string cwd, IReadOnlyList<PlanDiscovery.Candidate> plans, TimeSpan probeTimeout, DateTime nowUtc)
    {
        var fleet = await FleetAsync(probeTimeout, plans).ConfigureAwait(false);
        var root = StateHome.Root;
        var past = Past(root, fleet);
        return (HubModel.Compose(root, cwd, fleet, past.Rows, plans, nowUtc, past.Total), fleet);
    }

    /// <summary>What is answering, plus any engine holding one of this directory's plans with no
    /// control plane — "nothing here" and "something here I cannot talk to" are different facts.</summary>
    public static async Task<IReadOnlyList<FleetRun>> FleetAsync(
        TimeSpan probeTimeout, IReadOnlyList<PlanDiscovery.Candidate> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // the per-probe CTS owns the clock
        var answered = await FleetScan.ScanAsync(FleetScan.HttpProbe(http, probeTimeout), FleetScan.DefaultPorts)
            .ConfigureAwait(false);

        var runs = new List<FleetRun>();
        foreach (var r in answered) runs.Add(await FleetScan.EnrichFromDiskAsync(r).ConfigureAwait(false));

        foreach (var (name, stateDir) in StateDirs(plans))
            if (await FleetScan.UnattachedRunAsync(stateDir, name, runs).ConfigureAwait(false) is { } orphan)
                runs.Add(orphan);

        return runs;
    }

    /// <summary>The catalogue's half, best effort. A history that cannot be read must never stop
    /// someone attaching to a run that is right there.</summary>
    public static FacePastRunPage Past(string root, IReadOnlyList<FleetRun> fleet)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        try { return FacePastRuns.Read(root, fleet.Select(r => r.RunId)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return FacePastRunPage.Empty;
        }
    }

    /// <summary>Each discovered plan's state dir, quietly. A plan that will not load is skipped, not
    /// reported: this is a board, and a malformed plan file is <c>doctor</c>'s subject.</summary>
    private static IEnumerable<(string Name, string StateDir)> StateDirs(IReadOnlyList<PlanDiscovery.Candidate> plans)
    {
        foreach (var c in plans)
        {
            string? dir = null;
            var name = c.Name;
            try
            {
                var plan = PlanConfig.Load(c.Path);
                name = plan.Name;
                dir = plan.StateDir;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                          or InvalidOperationException or ArgumentException)
            {
                dir = null;
            }
            if (!string.IsNullOrWhiteSpace(dir)) yield return (name, dir);
        }
    }
}
