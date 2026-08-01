using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Fleet;

/// <summary>
/// SF5.4 — what <c>conductor face</c> hands the Face when it cannot decide which run you meant.
///
/// <para>The Face used to find its run by walking up from the working directory for
/// <c>.conductor/control-plane.json</c>. That was wrong twice over. It answered only "the run in this
/// repo", so a machine running three websites had no way to reach the other two; and it answered even
/// that wrongly whenever the discovery file was absent, which is not hypothetical — measured while
/// SF5.4 was written, the run driving this very session was serving 4317 with a live engine and no
/// discovery file, and <c>conductor face</c> in this repo said "no live run" at a live run.</para>
///
/// <para>So the target is resolved by the same port probe <c>conductor ps</c> uses (see
/// <see cref="FleetScan"/>), and when the answer is ambiguous the whole fleet is handed to the Face,
/// which shows a picker. The envelope travels in an environment variable rather than argv because it
/// carries each run's write token, and a process listing is readable by every process on the machine.
/// <c>ps --json</c>, which anyone may run, carries no token at all — that is <see cref="FleetRunDto"/>,
/// deliberately a different shape from <see cref="FaceFleetRun"/>.</para>
/// </summary>
public static class FaceTarget
{
    /// <summary>The environment variable the Face reads its fleet from.</summary>
    public const string FleetEnvVar = "CONDUCTOR_FLEET";

    public enum Kind
    {
        /// <summary>One run is unambiguously the target — attach straight to it, no picker.</summary>
        Single,
        /// <summary>More than one plausible run (or <c>--pick</c>): the Face asks.</summary>
        Picker,
        /// <summary>Nothing answered. The caller falls back to the local discovery file, then errors.</summary>
        None,
    }

    public sealed record Decision(Kind Kind, FleetRun? Run, IReadOnlyList<FleetRun> Fleet);

    /// <summary>
    /// Which run should the Face attach to? Pure, so the rule is testable without sockets or a disk.
    ///
    /// <para>The precedence is "what did the person who typed this most likely mean": the run in THIS
    /// directory first — standing in a repo is itself an unambiguous answer, even on a busy machine —
    /// then the only run there is, then ask. <c>--pick</c> jumps the queue entirely, because the one
    /// case the directory rule cannot serve is "I am in repo A and want to look at repo B".</para>
    /// </summary>
    public static Decision Choose(IReadOnlyList<FleetRun> answered, string? localStateDir, bool pick)
    {
        ArgumentNullException.ThrowIfNull(answered);

        // Port 0 is an engine holding a lock with no control plane. It belongs in `ps` — "a run here I
        // cannot talk to" is a fact worth listing — but the Face has nothing to attach to.
        var reachable = answered.Where(r => r.Port > 0 && !string.IsNullOrWhiteSpace(r.BaseUrl)).ToArray();

        if (reachable.Length == 0) return new Decision(Kind.None, null, []);
        if (pick) return new Decision(Kind.Picker, null, reachable);

        var local = reachable.FirstOrDefault(r => FleetScan.SameDir(r.StateDir, localStateDir));
        if (local is not null) return new Decision(Kind.Single, local, reachable);

        return reachable.Length == 1
            ? new Decision(Kind.Single, reachable[0], reachable)
            : new Decision(Kind.Picker, null, reachable);
    }

    /// <summary>Serializes the fleet for the Face. <paramref name="tokens"/> maps a run's state dir to
    /// its write token; a run with no token still lists — the Face just marks it read-only rather than
    /// hiding a run the user can see in <c>ps</c>.</summary>
    public static string Serialize(IReadOnlyList<FleetRun> runs, IReadOnlyDictionary<string, string> tokens, string? localStateDir)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(tokens);

        var envelope = new FaceFleet(runs.Select(r => new FaceFleetRun(
            r.Repo, r.PlanName, r.RunId, r.Status, r.Port, r.Pid, r.StageId, r.StageTitle,
            r.AttentionReason, r.Done, r.Total, r.CostUsd, r.BaseUrl, r.StateDir,
            LookupToken(tokens, r.StateDir),
            FleetScan.SameDir(r.StateDir, localStateDir))).ToArray());

        return JsonSerializer.Serialize(envelope, FaceFleetJsonContext.Default.FaceFleet);
    }

    /// <summary>State dirs arrive from the wire and from a local plan, so they differ in separators and
    /// case on Windows — the same reason <see cref="FleetScan.SameDir"/> exists. A plain dictionary
    /// lookup would silently hand every run a null token on exactly the machine this ships to.</summary>
    private static string? LookupToken(IReadOnlyDictionary<string, string> tokens, string stateDir)
    {
        foreach (var (dir, token) in tokens)
            if (FleetScan.SameDir(dir, stateDir)) return token;
        return null;
    }
}

/// <summary>The <c>CONDUCTOR_FLEET</c> envelope. Mirrored by <c>tui.Fleet</c> in face-go.</summary>
public sealed record FaceFleet(IReadOnlyList<FaceFleetRun> Runs);

/// <summary>One attachable run as the Face sees it. Identical to <see cref="FleetRunDto"/> plus
/// <paramref name="Token"/> — and the token is the whole reason this is a separate type: the shape
/// that goes to stdout must not be able to grow one by accident.</summary>
public sealed record FaceFleetRun(
    string Repo, string PlanName, string RunId, string Status, int Port, int Pid,
    string StageId, string StageTitle, string? AttentionReason,
    int Done, int Total, decimal CostUsd, string BaseUrl, string StateDir,
    string? Token, bool Self);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(FaceFleet))]
public sealed partial class FaceFleetJsonContext : JsonSerializerContext;
