using System.Globalization;

using Conductor.Models;

namespace Conductor.Core.Watch;

/// <summary>What the watch decided to do about the supervisor on this wake, and why.</summary>
/// <param name="Command">The command to run, or null when nothing should run.</param>
/// <param name="Timeout">How long it may run.</param>
/// <param name="Source">Where the command came from — <c>--hook</c> or <c>plan.supervisor</c>.</param>
/// <param name="Skipped">Null when the supervisor runs; otherwise the reason it did not, in the words
/// that go to stderr. A supervisor that stays quiet must always be able to say which of the several
/// reasons for quiet applied.</param>
public readonly record struct SupervisorDecision(
    string? Command, TimeSpan Timeout, string Source, string? Skipped)
{
    public bool ShouldRun => Command is not null;
}

/// <summary>
/// SF5.2 — resolve who supervises this wake, and hold the cost fuse.
///
/// <para>Separated from <see cref="WatchHook"/> (which only knows how to run a command) and from the
/// command class (which only knows how to print) because the two things worth being sure of here are
/// decisions, not I/O: which of <c>--hook</c> and the plan block wins, and whether this fire is inside
/// the hourly budget. Both are testable without starting a process, and both have a failure mode that
/// costs real money if it is wrong.</para>
/// </summary>
public static class SupervisorPolicy
{
    /// <summary>The rolling ledger of supervisor fires, one ISO-8601 instant per line, in the run's
    /// state dir. A file and not memory because the shipped shape is a shell loop: every wake is a
    /// FRESH <c>conductor watch</c> process, so an in-process counter would reset on exactly the event
    /// it exists to bound.</summary>
    public const string FiresFile = "supervisor-fires.log";

    /// <summary>SF5.3 — the same ledger, for remote dispatches. A separate file because the two fuses
    /// bound different spends and must not consume each other: the hour in which the local supervisor
    /// has hit its cap is precisely the hour the wake most needs to reach a human off the box.</summary>
    public const string RemoteFiresFile = "supervisor-remote-fires.log";

    /// <summary>Decide what runs on this wake. <paramref name="hookOverride"/> is the command line's
    /// <c>--hook</c>: it wins over the plan block, unconditionally and including the rate limit, because
    /// an operator typing a hook at a live run is making a deliberate one-off decision and the plan's
    /// fuse is not theirs to be bound by.</summary>
    public static SupervisorDecision Decide(
        PlanConfig plan, string? hookOverride, TimeSpan hookTimeout, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if (!string.IsNullOrWhiteSpace(hookOverride))
            return new SupervisorDecision(hookOverride, hookTimeout, "--hook", null);

        var sup = plan.Supervisor;
        if (sup is null)
            return new SupervisorDecision(null, hookTimeout, "none", null);
        if (!sup.Enabled)
            return new SupervisorDecision(null, hookTimeout, "plan.supervisor", "supervisor disabled in the plan");
        if (string.IsNullOrWhiteSpace(sup.Command))
            return new SupervisorDecision(null, hookTimeout, "plan.supervisor", "supervisor has no command");

        var timeout = TimeSpan.FromMinutes(Math.Clamp(sup.TimeoutMinutes, 1, 1440));
        if (sup.MaxPerHour > 0)
        {
            var recent = CountRecentFires(plan.StateDir, TimeSpan.FromHours(1), nowUtc);
            if (recent >= sup.MaxPerHour)
                return new SupervisorDecision(null, timeout, "plan.supervisor",
                    $"rate limited: {recent} supervisor fire(s) this hour, cap {sup.MaxPerHour} " +
                    "(raise supervisor.maxPerHour, or read the brief yourself — a supervisor hitting this is usually a run stuck on one cause)");
        }

        return new SupervisorDecision(sup.Command, timeout, "plan.supervisor", null);
    }

    /// <summary>Count fires inside the window. Unreadable ledger counts as zero fires: the fuse must
    /// never be the reason a run goes unsupervised.</summary>
    public static int CountRecentFires(string stateDir, TimeSpan window, DateTimeOffset nowUtc,
        string fileName = FiresFile)
    {
        try
        {
            var file = Path.Combine(stateDir, fileName);
            if (!File.Exists(file)) return 0;
            var cutoff = nowUtc - window;
            return File.ReadAllLines(file).Count(line =>
                DateTimeOffset.TryParse(line, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
                && at >= cutoff);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Stamp a fire. Called after the supervisor is STARTED, not after it succeeds — a
    /// babysitter that crashes on every wake has still cost an invocation, and the fuse is there to
    /// bound invocations.</summary>
    public static void RecordFire(string stateDir, DateTimeOffset nowUtc, string fileName = FiresFile)
    {
        try
        {
            Directory.CreateDirectory(stateDir);
            var file = Path.Combine(stateDir, fileName);
            File.AppendAllText(file, nowUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine);
            Trim(file, nowUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A ledger that cannot be written must not take the watch down with it.
        }
    }

    // Unbounded append over a long run turns an hourly count into a whole-run file read. Keep a day.
#pragma warning disable MA0045 // sync file I/O at the Spectre.Cli sync boundary (same pattern as StatusCommand)
    private static void Trim(string file, DateTimeOffset nowUtc)
    {
        var lines = File.ReadAllLines(file);
        if (lines.Length < 512) return;
        var cutoff = nowUtc - TimeSpan.FromDays(1);
        var keep = lines.Where(line =>
            DateTimeOffset.TryParse(line, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at)
            && at >= cutoff).ToArray();
        File.WriteAllLines(file, keep);
    }
#pragma warning restore MA0045
}
