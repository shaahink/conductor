namespace Conductor.Core;

/// <summary>
/// SF0.1 / bug 2: an <c>IHostedService</c> whose <c>StartAsync</c> may legitimately decline to start
/// anything, and can say so.
///
/// <para><see cref="ConductorHost.StartRunServicesAsync"/> logged <c>Run services started: X</c> from
/// the list of services it had CALLED StartAsync on — which is not the same list. Telegram's start is
/// an early return whenever the plan has no telegram block (the ordinary case), so the run announced a
/// notifier it had not started, on the same line, in the same words, as one it had. That is the exact
/// shape of the bug SC1.1 was written to kill — a surface reporting a prefix of the truth — surviving
/// one layer out from the service it was about.</para>
///
/// <para>Implement this on any run service that can no-op its own start. A service that does not
/// implement it is taken at its word, which is correct for one that either starts or throws.</para>
/// </summary>
public interface IReportsStartOutcome
{
    /// <summary>True only when the service is actually running after <c>StartAsync</c> returned.</summary>
    bool IsStarted { get; }

    /// <summary>One sentence saying why not, when <see cref="IsStarted"/> is false. Null when started.</summary>
    string? NotStartedReason { get; }
}
