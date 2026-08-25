using Conductor.Core.Integrations.Github;
using Conductor.Models;

namespace Conductor.Core.Integrations;

/// <summary>What one outbound channel is actually doing, right now.</summary>
public enum ChannelState
{
    /// <summary>The plan never asked for this channel. Not a fault, and never loud.</summary>
    Off,

    /// <summary>Configured, and every half this probe can check is present.</summary>
    Ready,

    /// <summary>Configured and delivering, but a part of what the plan asked for is not running —
    /// the issue mirror without its project board, say. Loud, because the plan says otherwise.</summary>
    Degraded,

    /// <summary>Configured and CANNOT deliver. This is the edge run's failure and the reason this
    /// type exists: the plan says the channel is on, the operator believes the plan, and nothing is
    /// reaching it.</summary>
    Dead,
}

/// <summary>
/// DV1.1 — one outbound channel's live health, in the words the surfaces print.
/// </summary>
/// <param name="Channel">Stable machine name — <c>telegram</c>, <c>github</c>, and (DV4)
/// <c>courier</c>. Used as an owner-queue id, so it carries no spaces or punctuation.</param>
/// <param name="Detail">Why, in one sentence, in the vocabulary the channel's own refusal already
/// uses — <see cref="TelegramReadiness"/> for telegram, <c>GithubIdentity.MissingTokenRefusal</c>
/// for github. Drawn from those rather than re-worded here so doctor, the report, <c>/status</c> and
/// the owner queue cannot drift apart about what "dead" means.</param>
/// <param name="Fix">What the owner has to do, as prose, including the case where the honest answer
/// is "edit the plan" rather than "type this".</param>
/// <param name="FixCommand">The literal command that clears it, or <c>""</c> when nothing typed at a
/// prompt does — the same convention <see cref="OwnerQueueItem.Command"/> already carries, so the
/// queue hands this straight through instead of inventing a command that does not work.</param>
public sealed record ChannelHealth(
    string Channel,
    ChannelState State,
    string Detail,
    string Fix,
    string FixCommand)
{
    /// <summary>A dead or degraded channel is LOUD: it reaches the REPORT.md header, <c>/status</c>
    /// and the owner queue. Off and Ready are quiet — they appear in the one-line roll-up and nowhere
    /// else, because a surface that shouts about a healthy channel is one an operator learns to
    /// skip.</summary>
    public bool IsLoud => State is ChannelState.Dead or ChannelState.Degraded;

    /// <summary>The state as a word, upper-cased for the two that matter so a reader scanning a
    /// header block sees them without reading it.</summary>
    public string Word => State switch
    {
        ChannelState.Dead => "DEAD",
        ChannelState.Degraded => "DEGRADED",
        ChannelState.Ready => "ready",
        _ => "off",
    };

    /// <summary><c>github DEAD</c> — the token for the one-line roll-up.</summary>
    public string Summary => Channel + " " + Word;

    /// <summary><c>github DEAD — enabled in the plan but no token …</c> — the loud form.</summary>
    public string Line => Detail.Length == 0 ? Summary : Summary + " - " + Detail;
}

/// <summary>
/// DV1.1 — the health of every outbound channel this plan configured, derived from the plan and the
/// environment at the moment it is asked.
///
/// <para><b>The failure this exists for.</b> The Karvansara edge run's plan set
/// <c>github.enabled</c>, <c>liveMirror</c> and <c>runHistoryIssue</c>; no token was present;
/// <c>GithubMirror.TryCreate</c> wrote two lines to <c>.conductor/conductor.log</c> and returned
/// null. Twenty-four checkpoints, twenty-three sessions and $324 of work produced ZERO issues on the
/// board the plan asked for, and nothing said so — not the report, not <c>/status</c>, not the owner
/// queue, not the exit status (docs/dev/OBSERVABILITY-AND-MARKET-2026-08-22.md §2.2 cause 1). A
/// channel that fails invisibly is worse than one that was never configured, because the plan says
/// it is on and the operator believes the plan.</para>
///
/// <para><b>Derived, never stored</b> — the rule <see cref="OwnerQueue"/> is built on, for the same
/// reason. A stored health record can outlive the condition that raised it and then has to be
/// garbage-collected; a derived one clears itself the moment the token appears. It also makes the
/// verdict identical whether the engine is running or an operator is reading <c>conductor report</c>
/// afterwards — which is exactly the reading the edge run lost.</para>
///
/// <para><b>The seam DV4 uses.</b> A channel is one entry appended by <see cref="Collect"/>; the
/// courier lands here as a third <c>Probe*</c> method and reaches all three surfaces without any of
/// them changing, because none of them knows the list's length.</para>
/// </summary>
public static class ChannelHealthProbe
{
    /// <summary>Stable channel names. The owner queue keys on these, so they are constants.</summary>
    public const string TelegramChannel = "telegram";

    /// <inheritdoc cref="TelegramChannel"/>
    public const string GithubChannel = "github";

    /// <summary>Every configured outbound channel, in a stable order.</summary>
    /// <param name="telegramStarted">The one condition only a live engine process can answer.
    /// <c>null</c> — the default, and what <c>doctor</c>, the report and <c>/status</c> all pass —
    /// means "not knowable here", and a probe that cannot know does not claim.</param>
    public static IReadOnlyList<ChannelHealth> Collect(PlanConfig plan, bool? telegramStarted = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [ProbeTelegram(plan, telegramStarted), ProbeGithub(plan)];
    }

    /// <summary>The channels worth shouting about — dead or degraded, most broken first.</summary>
    public static IReadOnlyList<ChannelHealth> Loud(IReadOnlyList<ChannelHealth> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return [.. channels.Where(c => c.IsLoud).OrderBy(c => c.State == ChannelState.Dead ? 0 : 1)];
    }

    /// <summary>The one-line roll-up: <c>telegram ready · github DEAD</c>. Every channel appears,
    /// including the ones that are off, because "github is not in this list" and "github is fine"
    /// must not look the same.</summary>
    public static string SummaryLine(IReadOnlyList<ChannelHealth> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return channels.Count == 0 ? "none configured" : string.Join(" · ", channels.Select(c => c.Summary));
    }

    // -- the channels ----------------------------------------------------------------------------

    /// <summary>Telegram, in <see cref="TelegramReadiness"/>' own words — the class that already owns
    /// the question "will a push actually be delivered".
    /// <para>The severity is the DV1.1 part: <see cref="TelegramReadiness.NoBlock"/> is
    /// <see cref="ChannelState.Off"/> (a plan that never asked for a bot has nothing wrong with it),
    /// and every OTHER missing half is <see cref="ChannelState.Dead"/> — configured, and not
    /// delivering. <c>doctor</c> called all of them a warning, which is how a run starts with a
    /// channel the plan claims is on and nobody notices.</para></summary>
    private static ChannelHealth ProbeTelegram(PlanConfig plan, bool? started)
    {
        var cfg = plan.Telegram;
        if (cfg is null)
            return new ChannelHealth(TelegramChannel, ChannelState.Off, TelegramReadiness.NoBlock, "", "");

        var hasToken = TelegramService.ResolveToken(plan) is { Length: > 0 };
        var missing = TelegramReadiness.MissingHalf(
            hasBlock: true, hasToken: hasToken, allowedChatIds: cfg.ChatCount, started: started);

        if (missing is null)
            return new ChannelHealth(TelegramChannel, ChannelState.Ready,
                FormattableString.Invariant($"token present, {cfg.ChatCount} chat(s)"), "", "");

        var (fix, command) = missing switch
        {
            TelegramReadiness.NoToken => (
                "set CONDUCTOR_TELEGRAM_TOKEN in the engine's environment (or save one from the Face's "
                + "Telegram tab), or take the telegram block out of the plan",
                "setx CONDUCTOR_TELEGRAM_TOKEN <token>"),
            TelegramReadiness.NoChatIds => (
                "add at least one chat to telegram.chats (or telegram.allowedChatIds) in the plan",
                ""),
            _ => ("the engine process is not running the Telegram service - restart the run", ""),
        };

        return new ChannelHealth(TelegramChannel, ChannelState.Dead, missing, fix, command);
    }

    /// <summary>The github mirror, asking the SAME questions <c>GithubMirror.TryCreate</c> asks, in
    /// the same order, from the same helpers — so this cannot report "ready" about a mirror that will
    /// refuse to be created, which would be the original defect wearing a new coat.</summary>
    private static ChannelHealth ProbeGithub(PlanConfig plan)
    {
        var cfg = plan.Github;
        if (cfg is not { Enabled: true })
            return new ChannelHealth(GithubChannel, ChannelState.Off,
                "not configured - optional; add a github block with enabled: true to mirror the run", "", "");

        var repo = GithubIdentity.Resolve(plan);
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/', StringComparison.Ordinal))
            return new ChannelHealth(GithubChannel, ChannelState.Dead,
                "github.enabled is set but no owner/name destination could be resolved",
                "set github.repo to owner/name in the plan, or give the repo an origin remote that names one",
                "");

        var (token, _) = GithubIdentity.ResolveToken(plan);
        if (token is null)
        {
            // The SAME sentence the mirror's own refusal writes to the log. The edge run proved that
            // sentence was correct and that its only reader was a log file; DV1.1 changes where it
            // goes, not what it says.
            var refusal = GithubIdentity.MissingTokenRefusal(plan);
            return new ChannelHealth(GithubChannel, ChannelState.Dead,
                "enabled in the plan but no token - " + refusal[0] + " " + refusal[1],
                "set " + GithubIdentity.TokenEnvVar + " in the engine's environment, or put a githubToken in "
                    + GithubIdentity.SecretsPath(plan) + ", or set github.enabled to false",
                "setx " + GithubIdentity.TokenEnvVar + " <token>");
        }

        if (cfg.BoardRefusal() is { } boardRefusal)
            return new ChannelHealth(GithubChannel, ChannelState.Degraded,
                FormattableString.Invariant($"mirroring issues to {repo}, but the project board is off: {boardRefusal}"),
                "fix the github.board / github.projectNumber pair in the plan, or accept the issue board alone",
                "");

        if (cfg.WantsProjectBoard)
            return new ChannelHealth(GithubChannel, ChannelState.Degraded,
                FormattableString.Invariant(
                    $"mirroring issues to {repo}, but the project board is off: {GithubProjects.NotImplementedLine}"),
                "set github.board to 'issues' to stop asking for a board this engine cannot write",
                "");

        return new ChannelHealth(GithubChannel, ChannelState.Ready,
            FormattableString.Invariant(
                $"mirroring to {repo}{(cfg.LiveMirror ? "" : " (liveMirror off - backfill only)")}"),
            "", "");
    }
}
