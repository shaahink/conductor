using System.Globalization;

using Conductor.Models;

namespace Conductor.Core.Watch;

/// <summary>One line of <c>conductor watches</c>: a run this machine can see, and what — if
/// anything — is watching it. Every field is a sentence rather than a flag, because "armed" with no
/// detail is the answer that made the owner go and read the plan file anyway.</summary>
/// <param name="Supervisor">The local babysitter: its source and whether it would run.</param>
/// <param name="Fuse">Fires in the last rolling hour against the cap that bounds them.</param>
/// <param name="Remote">Where a wake travels when the supervisor is not on this box.</param>
/// <param name="Pushes">The park-notification cap this run is running under (KS2.6).</param>
public sealed record WatchRosterEntry(
    string Repo,
    string PlanName,
    string RunId,
    string Status,
    int Port,
    int Pid,
    string Supervisor,
    string Fuse,
    string Remote,
    string Pushes)
{
    /// <summary>Nothing would wake anybody for this run: no local supervisor, no remote.</summary>
    public bool Unwatched { get; init; }

    /// <summary>First eight of the run id — the form every other surface prints.</summary>
    public string ShortRunId => RunId.Length >= 8 ? RunId[..8] : RunId;
}

/// <summary>
/// KS2.6 — what is ARMED on this machine, as facts rather than as a promise.
///
/// <para><c>conductor watch</c> blocks on one run and <c>supervisor</c> blocks in a plan name who
/// gets woken, but nothing ever answered "is anything actually watching?". The owner's 14-hour
/// silent park and the 200-push flood are the same question from opposite ends: one run nobody was
/// told about, one run everybody was told about two hundred times. This is the read-only surface
/// that answers it — the supervisor block each live run is running under, how much of its hourly
/// fuse it has burnt, where its remote wake goes, and the park-push cap in force.</para>
///
/// <para>Pure and side-effect free: it reads the plan and the two fire ledgers
/// (<see cref="SupervisorPolicy.FiresFile"/>, <see cref="SupervisorPolicy.RemoteFiresFile"/>) and
/// writes nothing. A run whose plan cannot be found is still listed, saying so — an unreadable plan
/// is exactly the case where "nothing is armed" must not be silently assumed.</para>
/// </summary>
public static class WatchRoster
{
    /// <summary>What is watching one run. <paramref name="plan"/> null = the plan file could not be
    /// found or read from here, which is reported rather than treated as "no supervisor".</summary>
    public static WatchRosterEntry Describe(
        string repo, string planName, string runId, string status, int port, int pid,
        PlanConfig? plan, string? stateDir, DateTimeOffset nowUtc)
    {
        var sup = plan?.Supervisor;
        var remote = sup?.Remote;
        var entry = new WatchRosterEntry(
            repo, planName, runId, status, port, pid,
            plan is null ? "plan not readable from here" : SupervisorText(sup),
            FuseText(sup, stateDir, nowUtc),
            plan is null ? "?" : RemoteText(remote, stateDir, nowUtc),
            plan is null ? "?" : PushesText(plan.Limits));
        return entry with
        {
            Unwatched = plan is not null && !Runs(sup) && !Delivers(remote),
        };
    }

    /// <summary>Would the local supervisor command run at all on the next wake?</summary>
    public static bool Runs(SupervisorConfig? sup)
        => sup is { Enabled: true } && !string.IsNullOrWhiteSpace(sup.Command);

    /// <summary>Would a remote wake actually leave the box?</summary>
    public static bool Delivers(SupervisorRemote? remote)
        => remote is { Enabled: true } && (remote.Telegram || !string.IsNullOrWhiteSpace(remote.WebhookUrl));

    internal static string SupervisorText(SupervisorConfig? sup)
    {
        if (sup is null) return "none";
        if (!sup.Enabled) return "disabled in the plan";
        if (string.IsNullOrWhiteSpace(sup.Command)) return "declared, no command";
        var head = sup.Command.Trim();
        if (head.Length > 40) head = head[..39] + "…";
        return FormattableString.Invariant($"{head} ({sup.TimeoutMinutes}m)");
    }

    internal static string FuseText(SupervisorConfig? sup, string? stateDir, DateTimeOffset nowUtc)
    {
        if (!Runs(sup) || string.IsNullOrWhiteSpace(stateDir)) return "-";
        var fires = SupervisorPolicy.CountRecentFires(stateDir, TimeSpan.FromHours(1), nowUtc);
        if (sup!.MaxPerHour <= 0) return fires.ToString(CultureInfo.InvariantCulture) + "/hr (uncapped)";
        var burnt = fires >= sup.MaxPerHour ? " BURNT" : "";
        return FormattableString.Invariant($"{fires}/{sup.MaxPerHour} this hour{burnt}");
    }

    internal static string RemoteText(SupervisorRemote? remote, string? stateDir, DateTimeOffset nowUtc)
    {
        if (remote is null) return "none";
        if (!remote.Enabled) return "disabled in the plan";
        var targets = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(remote.WebhookUrl)) targets.Add("webhook");
        if (remote.Telegram) targets.Add("telegram");
        if (targets.Count == 0) return "declared, no target";
        var fired = string.IsNullOrWhiteSpace(stateDir)
            ? ""
            : FormattableString.Invariant($" · {SupervisorPolicy.CountRecentFires(stateDir, TimeSpan.FromHours(1), nowUtc, SupervisorPolicy.RemoteFiresFile)}") +
              (remote.MaxPerHour > 0 ? FormattableString.Invariant($"/{remote.MaxPerHour}") : "") + " this hour";
        return string.Join("+", targets) + fired;
    }

    internal static string PushesText(LimitsConfig? limits)
    {
        var cap = limits?.MaxPushesPerIncident ?? Integrations.ParkNotifier.DefaultMaxPerIncident;
        return cap <= 0 ? "uncapped" : FormattableString.Invariant($"{cap}/incident");
    }
}
