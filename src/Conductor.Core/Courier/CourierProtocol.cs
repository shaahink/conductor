namespace Conductor.Core.Courier;

/// <summary>DV4.2 / findings §6.4 — the version the run and the courier have to agree on.
///
/// <para>The courier is the one process on this machine DESIGNED to outlive everything else, and
/// that is exactly what makes version skew real here in a way it is not anywhere else in this
/// program. A reinstall replaces the published engine; every run started afterwards is the new
/// engine. The courier is not: it was started at logon, it holds the old binary open, and left
/// alone it keeps running yesterday's code indefinitely, answering the same phone.</para>
///
/// <para>So the courier states its version — at the loopback hello DV4.3 adds, and, before that
/// listener exists, in the presence record it writes at startup (<see cref="CourierPresence"/>).
/// A run that speaks a newer protocol REFUSES the stale courier by name and says which command
/// fixes it. It does not silently downgrade, and it does not guess that the old shape still
/// works.</para></summary>
public static class CourierProtocol
{
    /// <summary>The protocol this build speaks. Bump it when the run↔courier contract changes in a
    /// way an older courier would get wrong — a new field a run relies on, a changed meaning, a new
    /// verb. Never bump it for something a stale courier handles correctly anyway: every bump makes
    /// somebody restart a daemon.</summary>
    public const int Version = 1;

    /// <summary>The verb that fixes skew. Named in every refusal, because "restart the courier" with
    /// no command is how a person ends up killing a pid by hand.</summary>
    public const string RestartVerb = "conductor courier restart";

    /// <summary>Why this run will not talk to the courier that is running, or null when it will.
    ///
    /// <para>Older courier than the run: refused, by name, with the restart command. NEWER courier
    /// than the run is not an error — an old run talking to a fresh daemon is the ordinary state of
    /// a machine between a reinstall and the next courier restart, and the courier's contract is to
    /// keep understanding what older runs say.</para></summary>
    /// <param name="courier">The presence record the running courier wrote, or null if none.</param>
    /// <param name="speaking">The protocol version this run speaks. Defaults to <see cref="Version"/>.</param>
    public static string? RefuseStale(CourierPresence? courier, int speaking = Version)
    {
        if (courier is null || courier.Protocol >= speaking) return null;

        var name = string.IsNullOrWhiteSpace(courier.TaskName) ? CourierTask.DefaultName : courier.TaskName;
        var restart = CourierTask.IsDefaultName(name)
            ? RestartVerb
            : $"{RestartVerb} --task-name \"{name}\"";

        return $"the courier \"{name}\" (pid {courier.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)}) "
             + $"speaks protocol {courier.Protocol.ToString(System.Globalization.CultureInfo.InvariantCulture)}; "
             + $"this run speaks {speaking.ToString(System.Globalization.CultureInfo.InvariantCulture)}. "
             + $"It is still running the engine it was started with{Engine(courier)}. Restart it: {restart}";
    }

    private static string Engine(CourierPresence courier) =>
        string.IsNullOrWhiteSpace(courier.Engine) ? "" : $" ({courier.Engine})";
}
