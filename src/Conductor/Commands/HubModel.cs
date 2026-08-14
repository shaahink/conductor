using Conductor.Core.Fleet;
using Conductor.Core.History;
using Conductor.Core.Planning;

namespace Conductor.Commands;

/// <summary>One run as the hub lists it — live or remembered, in one shape.
/// <para><b>Why one shape.</b> The hub's whole claim is "here is what is on this machine". A screen
/// that keeps live runs and past runs in two incompatible records ends up with two status vocabularies
/// and two age formats, and the reader has to know which half they are looking at to know what a word
/// means. <see cref="Live"/> is the only difference, and it is the one difference that matters:
/// something is answering, or nothing is.</para>
/// <para><see cref="Status"/> is the word to PRINT and it arrives reconciled — from the probe for a
/// live row (a plane that answers is a plane that is there) and from KS1.3's
/// <c>RunLiveness.Reconcile</c> by way of <see cref="FacePastRuns"/> for a past one. Nothing here
/// re-derives it: a second opinion about liveness is exactly the drift KS1.3 removed.</para></summary>
public sealed record HubRunRow(bool Live, string Repo, string PlanName, string RunId, string Status)
{
    /// <summary>The stage the run is on, when anything knows it.</summary>
    public string StageId { get; init; } = "";

    /// <summary>Why a live run is waiting on a human, when it is.</summary>
    public string? Attention { get; init; }

    public int Done { get; init; }

    public int Total { get; init; }

    public decimal CostUsd { get; init; }

    /// <summary>Where the Face would attach. Empty for a past run and for an engine holding a lock
    /// with no control plane — both are rows you can see and cannot talk to.</summary>
    public string BaseUrl { get; init; } = "";

    public int Port { get; init; }

    public int Pid { get; init; }

    /// <summary>The run's own state dir; how the hub finds its write token without carrying one.</summary>
    public string StateDir { get; init; } = "";

    /// <summary>Already formatted: uptime for a live row, the last-activity date for a past one. The
    /// composer owns the clock so the view stays a pure function of the model.</summary>
    public string When { get; init; } = "";

    /// <summary>First eight of the run id, the form every other surface prints.</summary>
    public string ShortRunId => RunId.Length >= 8 ? RunId[..8] : RunId;

    /// <summary>Trailing directory name of the repo, falling back to the plan name for a row whose
    /// repo nobody recorded — an unreadable catalogue row must still be nameable.</summary>
    public string Label
    {
        get
        {
            var repo = RunHistory.RepoLabel(Repo);
            return repo.Length > 0 ? repo : PlanName;
        }
    }
}

/// <summary>A plan file discoverable from where the user is standing. Zero of these is a normal
/// outcome, and so is five — the hub reports what it found and never interrogates anyone about it.</summary>
public sealed record HubPlanRow(string Name, string Path);

/// <summary>
/// KS2.1 — the caravanserai, as data.
///
/// <para>Bare <c>conductor</c> used to answer with forty-one verbs, which is a table of contents, not a
/// door. What a person standing at that prompt actually wants to know is: what is running on this
/// machine, what did it do before, is there a plan here, and what can I do about any of it. This
/// record is those four answers, composed from inputs the caller supplies, so the hub's arrangement is
/// testable without a socket, a catalogue, or a terminal.</para>
///
/// <para><b>What it deliberately does not do.</b> It never resolves "the" plan.
/// <c>PlanSettings.ResolvePlanPath</c> prompts on an ambiguous directory and throws on an empty one,
/// and the front door of a CLI may do neither: a directory with no plan is the most likely place a new
/// user types <c>conductor</c>, and a directory with eleven is this very repo. Zero and many are both
/// normal, and both are simply listed (<see cref="PlanDiscovery"/>).</para>
/// </summary>
public sealed record HubModel(
    string StateHomeRoot,
    string Cwd,
    IReadOnlyList<HubRunRow> Runs,
    IReadOnlyList<HubPlanRow> Plans)
{
    /// <summary>Runs something is answering for, port order.</summary>
    public IReadOnlyList<HubRunRow> LiveRuns => Runs.Where(r => r.Live).ToArray();

    /// <summary>Runs only the catalogue remembers, newest activity first.</summary>
    public IReadOnlyList<HubRunRow> PastRuns => Runs.Where(r => !r.Live).ToArray();

    /// <summary>Runs the Face can actually be pointed at.</summary>
    public IReadOnlyList<HubRunRow> Attachable =>
        Runs.Where(r => r.Live && r.BaseUrl.Length > 0).ToArray();

    /// <summary>
    /// Arranges what the machine knows. Pure: every input is passed in, including the clock.
    /// </summary>
    /// <param name="stateHomeRoot"><c>StateHome.Root</c> — the machine's state home, named on the
    /// board because "which database am I looking at" is the first question a wrong answer raises.</param>
    /// <param name="live">Rows from the fleet probe (and any engine holding a lock with no plane).</param>
    /// <param name="past">Rows from the catalogue, already reconciled and already excluding live ids.</param>
    /// <param name="plans">What <see cref="PlanDiscovery"/> found here. Zero and many are both fine.</param>
    public static HubModel Compose(
        string stateHomeRoot,
        string cwd,
        IReadOnlyList<FleetRun> live,
        IReadOnlyList<FacePastRun> past,
        IReadOnlyList<PlanDiscovery.Candidate> plans,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(past);
        ArgumentNullException.ThrowIfNull(plans);

        var rows = new List<HubRunRow>();
        foreach (var r in live.OrderBy(r => r.Port == 0).ThenBy(r => r.Port))
        {
            rows.Add(new HubRunRow(true, r.Repo, r.PlanName, r.RunId, r.Status)
            {
                StageId = r.StageId,
                Attention = r.AttentionReason,
                Done = r.Done,
                Total = r.Total,
                CostUsd = r.CostUsd,
                BaseUrl = r.BaseUrl,
                Port = r.Port,
                Pid = r.Pid,
                StateDir = r.StateDir,
                When = PsCommand.Age(r.StartedUtc, nowUtc),
            });
        }

        foreach (var p in past)
        {
            rows.Add(new HubRunRow(false, p.Repo, p.PlanName, p.RunId, p.Status)
            {
                Done = p.Done,
                Total = p.Total,
                CostUsd = p.CostUsd,
                When = Day(p.LastActivityUtc),
            });
        }

        return new HubModel(stateHomeRoot, cwd, rows,
            plans.Select(c => new HubPlanRow(c.Name, c.Path)).ToArray());
    }

    /// <summary>A past run's date, to the day. The hour it stopped is <c>conductor history</c>'s
    /// business; a board that printed full timestamps for eight rows would be a wall of digits.</summary>
    private static string Day(string? lastActivityUtc)
        => RunHistory.ParseUtc(lastActivityUtc) is { } d ? d.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : "?";
}
