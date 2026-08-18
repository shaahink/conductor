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
