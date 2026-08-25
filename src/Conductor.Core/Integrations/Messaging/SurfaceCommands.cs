namespace Conductor.Core.Integrations.Messaging;

/// <summary>What a verb DOES, which is what decides who may use it.
///
/// <para>CHAPAR CH-3 defines the observer surface as a closed list, so the useful question about a
/// verb is not "is it dangerous" but "is it in the list" — and, when it is not, what kind of refusal
/// says so honestly.</para></summary>
public enum SurfaceScope
{
    /// <summary>Reads the run and answers. CH-3's closed observer set is exactly the browse verbs.</summary>
    Browse = 0,

    /// <summary>Points the run somewhere — steering, or an admin's own console affordance. Not
    /// control, not browsing, and not open to an observer.</summary>
    Steer = 1,

    /// <summary>Moves the run: pause, resume, approve, skip, abort, kill. Admin only, and only when
    /// the channel is wired two-way at all.</summary>
    Control = 2,
}

/// <param name="Verb">The slash verb exactly as a reader types it.</param>
/// <param name="Implemented">False while a verb is named by CH-3 but has no handler yet. Both
/// profiles get today's behaviour for it — silence — so an unimplemented browse verb is never
/// REFUSED to an observer, which is the distinction the matrix test turns on.</param>
public sealed record SurfaceCommand(string Verb, SurfaceScope Scope, bool Implemented = true)
{
    /// <summary>CH-3 in one line: admin keeps the whole surface, an observer gets the browse verbs
    /// and nothing else.</summary>
    public bool AllowedFor(ChatProfile profile) =>
        profile == ChatProfile.Admin || Scope == SurfaceScope.Browse;
}

/// <summary>KS11.2 / CHAPAR CH-3 — every verb the command surface knows, in one list.
///
/// <para>This exists so "the observer surface is closed" can be MEASURED rather than asserted about
/// a handful of samples. <c>KS11_2CommandMatrixTests</c> walks <see cref="All"/> against both
/// profiles, and a second test scans <c>CommandRouter.cs</c> for slash literals and fails if one is
/// missing here — so a verb added to the router without a decision about who may use it fails the
/// build rather than defaulting open.</para></summary>
public static class SurfaceCommands
{
    /// <summary>The whole surface. Adding a verb to <see cref="CommandRouter"/> means adding it here
    /// too; the source-scan test is what makes that non-optional.</summary>
    public static readonly IReadOnlyList<SurfaceCommand> All =
    [
        new("/status", SurfaceScope.Browse),
        new("/tasks", SurfaceScope.Browse),
        new("/daily", SurfaceScope.Browse),
        new("/start", SurfaceScope.Browse),
        new("/progress", SurfaceScope.Browse),                       // KS11.5
        new("/evidence", SurfaceScope.Browse),                       // KS11.4
        new("/money", SurfaceScope.Browse),                          // KS11.5
        new("/tokens", SurfaceScope.Browse),                         // KS11.5
        new("/project", SurfaceScope.Steer),                         // DV3.4
        new("/inject", SurfaceScope.Steer),
        new("/chat", SurfaceScope.Steer),
        new("/pause", SurfaceScope.Control),
        new("/resume", SurfaceScope.Control),
        new("/approve", SurfaceScope.Control),
        new("/skip", SurfaceScope.Control),
        new("/abort", SurfaceScope.Control),
        new("/kill", SurfaceScope.Control),
    ];

    /// <summary>What an observer may ask for, written the way a refusal and an onboarding message
    /// should list it.
    ///
    /// <para>KS11.3: only verbs that ANSWER. A list that promises <c>/evidence</c> before KS11.4
    /// builds it is a bot that ignores the reader the first time they take it at its word — so the
    /// list is derived from <see cref="SurfaceCommand.Implemented"/> and grows by itself as the
    /// handlers land. <c>/start</c> is left out because it is how a reader GOT this list.</para></summary>
    public static string BrowseList =>
        string.Join(", ", All.Where(c => c.Scope == SurfaceScope.Browse && c.Implemented && c.Verb != "/start")
                             .Select(c => c.Verb));

    /// <summary>The whole surface a profile may use, as one sentence for an onboarding message. The
    /// admin version says what the chat can do about the run; the observer version is
    /// <see cref="BrowseList"/> and stops there, because that is the point of the profile.</summary>
    public static string AskLine(ChatProfile profile, bool twoWay)
    {
        if (profile != ChatProfile.Admin)
            return BrowseList + " — reading only. Nothing this chat types can move the run.";

        var line = BrowseList + ", /inject &lt;text&gt; to steer the next session";
        var control = string.Join(", ", All.Where(c => c.Scope == SurfaceScope.Control).Select(c => c.Verb));
        return twoWay
            ? line + $", and {control} to control it — the destructive ones ask first."
            : line + $". Control ({control}) is off: set telegram.enableTwoWay to turn it on.";
    }

    /// <summary>The verb a message begins with, or null. Matches the router exactly: the whole text
    /// is the verb, or the verb followed by an argument.</summary>
    public static SurfaceCommand? Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        foreach (var cmd in All)
        {
            if (trimmed.Equals(cmd.Verb, StringComparison.OrdinalIgnoreCase)) return cmd;
            if (trimmed.StartsWith(cmd.Verb + " ", StringComparison.OrdinalIgnoreCase)) return cmd;
        }

        return null;
    }

    /// <summary>CH-3's one-line named refusal. It names the verb, says what kind of verb it is, says
    /// what this chat is, and ends with what it CAN ask — a refusal that leaves the reader knowing
    /// less than before is a worse answer than silence.</summary>
    public static string Refusal(SurfaceCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var kind = command.Scope == SurfaceScope.Control
            ? "is a control command"
            : "is not part of the observer surface";
        return $"{command.Verb} {kind} and this chat is an observer. Observers can ask: {BrowseList}.";
    }
}
