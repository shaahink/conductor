namespace Conductor.Core.Integrations.Messaging;

/// <summary>CHAPAR CH-2 — what a chat is FOR. Permissions were all-or-nothing per chat: any chat the
/// bot served got <c>/inject</c> and, under two-way, the control verbs, so there was no way to put a
/// stakeholder in a chat without also handing them the steering wheel.
///
/// <para>KS11.1 introduces the vocabulary and routes every chat as <see cref="Admin"/>, which is
/// exactly what every chat is today — the profile is not yet readable from a plan and nothing
/// enforces the observer set. KS11.2 is what makes the distinction load-bearing; naming it here is
/// what lets the seam be shaped for it instead of retrofitted around it.</para></summary>
public enum ChatProfile
{
    /// <summary>The owner's chat: the full surface, including control and steering. The only profile
    /// any chat has had until now, and the one an old-shape <c>allowedChatIds</c> plan reads as.</summary>
    Admin = 0,

    /// <summary>A reader: the run's story, and a closed set of browsing verbs. Never control, never
    /// steering.</summary>
    Observer = 1,
}

/// <summary>One chat a channel will deliver to, and the profile that decides what it may see and
/// ask.</summary>
public readonly record struct ChatTarget(string ChatId, ChatProfile Profile);

/// <summary>KS11.2 / CHAPAR CH-2 — the profile names as a plan spells them, and the one place a
/// string becomes a <see cref="ChatProfile"/>.
///
/// <para>There is deliberately no fallback. An unknown profile string is a refusal by name at plan
/// load, never a quiet read as admin: the whole point of the observer role is that a chat cannot
/// end up with more surface than the plan asked for, and "unrecognised means default" is exactly
/// how that happens. This is the <c>GithubConfig.BoardRefusal</c> rule, reused.</para></summary>
public static class ChatProfiles
{
    /// <summary>The plan spelling of <see cref="ChatProfile.Admin"/>.</summary>
    public const string AdminName = "admin";

    /// <summary>The plan spelling of <see cref="ChatProfile.Observer"/>.</summary>
    public const string ObserverName = "observer";

    /// <summary>Every legal spelling, in the order a refusal message should list them.</summary>
    public static readonly IReadOnlyList<string> Names = [AdminName, ObserverName];

    /// <summary>Parses a plan's profile string. Case- and whitespace-insensitive, because a plan is
    /// hand-written JSON; anything else answers null and the caller refuses by name.</summary>
    public static ChatProfile? TryParse(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        AdminName => ChatProfile.Admin,
        ObserverName => ChatProfile.Observer,
        _ => null,
    };

    /// <summary>How a profile is written back — into a refusal, a status payload or a log line.</summary>
    public static string Name(ChatProfile profile) =>
        profile == ChatProfile.Observer ? ObserverName : AdminName;
}
