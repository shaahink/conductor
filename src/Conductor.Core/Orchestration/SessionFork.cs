using Conductor.Models;

namespace Conductor.Core.Orchestration;

/// <summary>
/// KS7.4 — the rule that decides whether a session forks an earlier one, and which one.
/// </summary>
/// <remarks>
/// A fix or an audit session is a session ABOUT work another session just did. Started cold it pays
/// fresh-input rates to rediscover that work: re-read the files, re-derive what changed, re-read the
/// gate output it is reacting to. Forked, the same context arrives as a cache READ — measured at
/// 30,098 read against 0 write on a 30k base, 0.15% larger and a hair cheaper than resuming the same
/// conversation (see <see cref="AgentConfig.ForkArgs"/>).
/// <para><b>Fork, not resume.</b> Resuming would append the fix onto the delivery session's own
/// transcript, so a second fix attempt would inherit the first attempt's failure and the delivery
/// session's record would no longer be what it was when it was confirmed. A fork branches: the base is
/// left exactly as it ended, and every attempt branches from the same clean point.</para>
/// <para>Deliberately a pure function over the history list rather than a method on the runner: the
/// interesting part is the CHOICE, and a choice that needs a live run to test is a choice nobody
/// tests.</para>
/// </remarks>
public static class SessionFork
{
    /// <summary>The session <paramref name="kind"/> should fork in <paramref name="stageId"/>, or null
    /// to start cold. <paramref name="history"/> is the run's session records in order.</summary>
    public static string? BaseFor(
        IReadOnlyList<SessionRecord> history, string stageId, SessionKind kind, AgentConfig agent)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(agent);

        // Two gates, both opt-in, because only the plan knows whether its agent CLI can fork at all.
        if (agent.ForkArgs is not { Count: > 0 }) return null;
        if (!Forks(agent.ForkKinds, kind)) return null;

        for (var i = history.Count - 1; i >= 0; i--)
        {
            var s = history[i];
            if (!string.Equals(s.Stage, stageId, StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(s.ClaudeSessionId)) continue;

            // Fork the most recent session of this stage that actually held a conversation. A session
            // that never reached the agent has an id but no transcript behind it, and forking it would
            // fail at the CLI rather than here, where the reason can still be logged.
            if (s.EndedUtc is null) continue;
            return s.ClaudeSessionId;
        }

        // Nothing to fork from — the first session of a stage always starts cold, by definition.
        return null;
    }

    /// <summary>Whether <paramref name="kind"/> is named in the plan's fork list. Unset or empty means
    /// nothing forks: an existing plan does not change behaviour by upgrading.</summary>
    public static bool Forks(IReadOnlyList<string>? forkKinds, SessionKind kind)
    {
        if (forkKinds is not { Count: > 0 }) return false;
        foreach (var name in forkKinds)
            if (string.Equals(name, kind.ToString(), StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
