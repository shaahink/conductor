using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Conductor.Core.Inbox;

/// <summary>How a note found its project. Carried into the acknowledgement, because a router that
/// is silent about WHY is one nobody can correct.</summary>
public enum RouteReason
{
    /// <summary>§1.5 (1), the headline interaction: it was a reply to a conductor push, and the push
    /// said which project it was about. No command typed.</summary>
    ReplyToPush,

    /// <summary>§1.5 (3): the forum topic it arrived in has a project selected.</summary>
    Topic,

    /// <summary>§1.5 (2): the chat has a project selected, and it stayed selected.</summary>
    Sticky,

    /// <summary>Nothing said otherwise, so it is about the run that received it.</summary>
    LocalRun,

    /// <summary>Nothing said otherwise and there is no local run either.</summary>
    Unknown,

    /// <summary>A project was named and this machine cannot file against it — the checkout has moved
    /// or gone (findings §6.10). The note is PARKED, never dropped.</summary>
    Unroutable,
}

/// <summary>Where a note goes, and why.</summary>
/// <param name="Project">The project it belongs to, or null when there is nothing to file against.</param>
/// <param name="Reason">Which rule decided it.</param>
/// <param name="Refusal">What to tell the sender when there is no project, naming what was asked for
/// and what this machine has. Null when <paramref name="Project"/> is set.</param>
public sealed record NoteRoute(ProjectRef? Project, RouteReason Reason, string? Refusal)
{
    /// <summary>Whether this note can actually be filed in a project inbox.</summary>
    public bool Routed => Project is not null;

    /// <summary>One clause for the acknowledgement: which project, and how it was chosen. The
    /// sender's only chance to notice a wrong route is being told what it was.</summary>
    public string Describe() => Project is not { } p ? "nowhere" : Reason switch
    {
        RouteReason.ReplyToPush => p.Name + " (the run you replied to)",
        RouteReason.Topic => p.Name + " (this topic's project)",
        RouteReason.Sticky => p.Name + " (this chat's project — /project to change it)",
        RouteReason.LocalRun => p.Name + " (the run on this machine)",
        _ => p.Name,
    };
}

/// <summary>DV3.4 / findings §1.5 — which project is this note about?
///
/// <para>Three mechanisms, in the order of how much typing they cost, and the cheapest one is the
/// one that matters: <b>a reply to a push files against that push's project with no command at
/// all</b>. Every message conductor sends already opens with an identity line naming the plan, so
/// the information is on the wire already and the owner types nothing.</para>
///
/// <para>Then the sticky selection (per topic, then per chat), then the run that received it. Every
/// step down that ladder is a weaker claim about what the owner meant, so each one is NAMED in the
/// acknowledgement rather than applied silently.</para>
///
/// <para>Nothing here guesses. An unknown name is refused with what this machine actually has, an
/// ambiguous one is refused with both candidates, and a project whose checkout has vanished is
/// <see cref="RouteReason.Unroutable"/> — which parks the note rather than losing it.</para></summary>
public sealed class NoteRouter
{
    private readonly ProjectDirectory _projects;
    private readonly ChatRoutes _routes;

    public NoteRouter(ProjectDirectory projects, ChatRoutes routes)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(routes);
        _projects = projects;
        _routes = routes;
    }

    public ProjectDirectory Projects => _projects;
    public ChatRoutes Routes => _routes;

    /// <param name="chatId">The chat the note came from.</param>
    /// <param name="threadId">The forum topic, where the chat is a supergroup with topics.</param>
    /// <param name="replyToText">The text of the message this one replies to — a conductor push, if
    /// the owner replied to one. Plain text: the wire strips the HTML the composer wrote.</param>
    public NoteRoute Route(string chatId, long? threadId, string? replyToText)
    {
        if (PlanNameIn(replyToText) is { Length: > 0 } plan)
        {
            var match = _projects.Resolve(plan);
            if (match.Project is { } replied) return Verify(replied, RouteReason.ReplyToPush);

            // The push named a project this machine no longer lists. Say so with the name the push
            // used - "unknown project" without it is unanswerable.
            return new NoteRoute(null, RouteReason.Unknown,
                $"That message is about \"{plan}\", which is not a project on this machine any more. "
                + $"It has: {_projects.Listed()}. Use /project to choose one.");
        }

        if (_routes.Current(chatId, threadId) is { Length: > 0 } sticky)
        {
            var match = _projects.Resolve(sticky);
            if (match.Project is { } chosen)
                return Verify(chosen, ThreadHasOwnSelection(chatId, threadId) ? RouteReason.Topic : RouteReason.Sticky);

            return new NoteRoute(null, RouteReason.Unknown,
                $"This chat is set to \"{sticky}\", which this machine no longer has. "
                + $"It has: {_projects.Listed()}. Use /project to choose one.");
        }

        if (_projects.Local is { } local) return Verify(local, RouteReason.LocalRun);

        return new NoteRoute(null, RouteReason.Unknown,
            $"No project is selected for this chat. This machine has: {_projects.Listed()}. "
            + "Use /project <name>.");
    }

    /// <summary>A selection resolves to a project; whether that project is still ON this disk is a
    /// separate question, and the answer decides between filing and parking (findings §6.10).</summary>
    private static NoteRoute Verify(ProjectRef project, RouteReason reason) =>
        project.Present
            ? new NoteRoute(project, reason, null)
            : new NoteRoute(null, RouteReason.Unroutable,
                $"\"{project.Name}\" is a project this machine knows about, but its checkout is gone "
                + $"({project.Repo}). Your note is kept — it is parked where nothing deletes it — but "
                + "it could not be filed against that project.");

    private bool ThreadHasOwnSelection(string chatId, long? threadId) =>
        threadId is not null && _routes.All().ContainsKey(
            chatId + ":" + threadId.Value.ToString(CultureInfo.InvariantCulture));

    /// <summary>The plan name out of a conductor push, or null when this is not one.
    ///
    /// <para>The shape is <c>MessageComposer.IdentityFor</c>'s: the first line of every outbound
    /// message is <c>&lt;plan&gt; · s&lt;n&gt;</c>, italicised on the wire and delivered back to us as
    /// plain text. Parsed rather than pattern-matched loosely: the separator AND the session marker
    /// both have to be there, so a person's own message that happens to contain a middle dot is not
    /// mistaken for a push.</para></summary>
    public static string? PlanNameIn(string? replyToText)
    {
        if (replyToText is not { Length: > 0 }) return null;

        var first = replyToText.Replace("\r", "", StringComparison.Ordinal)
                               .Split('\n', 2)[0].Trim();
        var cut = first.LastIndexOf(" · ", StringComparison.Ordinal);
        if (cut <= 0) return null;

        var tail = first[(cut + 3)..].Trim();
        if (tail.Length < 2 || tail[0] is not ('s' or 'S')) return null;
        if (!long.TryParse(tail[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return null;

        var name = first[..cut].Trim();
        return name.Length > 0 ? name : null;
    }
}
