using Conductor.Models;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>What the surface should DO about one inbound message. A decision, not an effect: the
/// router composes the answer and names the action, and <see cref="RemoteSurface"/> is what touches
/// the store, the control file and the wire. That split is what lets the whole command surface be
/// asserted without a channel, and it is where CH-3's profile enforcement lands in KS11.2 — one
/// place that decides, rather than a refusal bolted onto each handler.</summary>
public enum SurfaceAction
{
    /// <summary>Nothing to say. An unknown command from an admin chat has always been silent, and
    /// staying silent is what keeps a bot in a busy group from answering traffic meant for others.</summary>
    None = 0,

    /// <summary>Send <see cref="CommandOutcome.Text"/> back to the asking chat.</summary>
    Reply = 1,

    /// <summary>Write the control file, then acknowledge.</summary>
    Control = 2,

    /// <summary>Ask first — a destructive verb gets a confirmation keyboard and nothing happens
    /// until a button comes back.</summary>
    ConfirmControl = 3,

    /// <summary>Record an instruction for the next session, then acknowledge.</summary>
    Inject = 4,

    /// <summary>Remember that this chat's NEXT plain message is an injection, and say so.</summary>
    ArmInjection = 5,

    /// <summary>KS11.3 / CH-4 — send this chat its onboarding message. A separate action because
    /// the onboarding body is composed asynchronously (it reads the tracker and the plan) and
    /// because it is the one reply whose content depends on the asking chat's profile.</summary>
    Onboard = 7,

    /// <summary>KS11.2 / CH-3 — the verb exists and this chat may not use it. Delivered exactly like
    /// <see cref="Reply"/>; it is a separate action so that "an observer was refused" is a fact the
    /// command-by-profile matrix can assert, rather than a string it has to pattern-match.</summary>
    Refuse = 6,

    /// <summary>KS11.4 / CH-6 — send this chat the artifact a checkpoint claimed, named by
    /// <see cref="CommandOutcome.Text"/>. A separate action because a pull is the one answer with an
    /// EFFECT behind it: it reads the disk, it charges the chat's rate-limit budget, and it can leave
    /// as an upload rather than as text — none of which a router that only decides may do.</summary>
    Evidence = 8,

    /// <summary>DV3.4 / findings 1.5 (2) - set or show which project this chat's notes are about.
    /// A separate action because the selection lives on DISK, under the machine's state home, and
    /// the router that decides has no business writing files.</summary>
    Project = 9,

    /// <summary>DV4.4 / findings §1.7 — turn the filed note named by <see cref="CommandOutcome.Text"/>
    /// into a followups.md row. A separate action for the same reason as <see cref="Project"/>: it
    /// writes a file. It is also the rung the note stops at — promotion reaches
    /// <c>NotePromoter</c> and nothing else, and there is no action here that would let a note
    /// become an <see cref="Inject"/>.</summary>
    Promote = 10,

    /// <summary>DV5.1 / findings §2.3 CL-2 and §6.8 — talk to a cloud session about this chat's
    /// project. A separate action because it READS THE REPO before it does anything: a cloud session
    /// clones from the remote, so the git state has to be measured before the verb can honestly say
    /// what a session would see, and a router that only decides cannot shell out to git.</summary>
    Cloud = 11,
}

/// <param name="Text">The reply body, or the instruction for <see cref="SurfaceAction.Inject"/>.</param>
/// <param name="ControlAction">pause / resume / approve / skip / abort / kill.</param>
/// <param name="IntentId">Ties a confirmation button back to the request that raised it.</param>
public sealed record CommandOutcome(
    SurfaceAction Action,
    string? Text = null,
    string? ControlAction = null,
    bool Confirmed = false,
    string? IntentId = null,
    IReadOnlyList<MessageButton>? Buttons = null)
{
    public static readonly CommandOutcome Nothing = new(SurfaceAction.None);

    public static CommandOutcome Reply(string text) => new(SurfaceAction.Reply, text);

    /// <summary>CH-3's named refusal — one line, naming the verb and the profile.</summary>
    public static CommandOutcome Refuse(string text) => new(SurfaceAction.Refuse, text);
}

/// <summary>KS11.1 / CHAPAR CH-1 — inbound dispatch, channel-agnostic. It takes the text a reader
/// typed and the profile of the chat they typed it in, and answers with a decision.
///
/// <para>KS11.2 makes the profile load-bearing. Every inbound path — a typed verb, a plain message
/// while injection is armed, and a button press — passes through <see cref="Gate"/> before anything
/// else happens, so the closed observer surface of CH-3 is enforced in ONE place rather than as a
/// refusal bolted onto each handler. An admin routes exactly as it always has, which is what the
/// KS11.1 goldens pin.</para></summary>
public sealed class CommandRouter
{
    private readonly MessageComposer _composer;
    private readonly PlanConfig _plan;

    public CommandRouter(MessageComposer composer, PlanConfig plan)
    {
        _composer = composer;
        _plan = plan;
    }

    /// <param name="profile">What this chat is allowed to be, read from the plan's
    /// <c>telegram.chats</c> block (or Admin, for an old-shape <c>allowedChatIds</c> plan).</param>
    /// <param name="twoWay">Whether the channel is wired for control verbs at all.</param>
    /// <param name="injectionArmed">Whether this chat's last exchange asked it for injection text.</param>
    public CommandOutcome Route(string text, ChatProfile profile, bool twoWay, bool injectionArmed)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length == 0) return CommandOutcome.Nothing;

        if (Gate(text, profile) is { } refusal) return refusal;

        if (injectionArmed && !text.StartsWith('/'))
        {
            // An observer can never arm injection (the callback that arms it is gated too), so this
            // is belt and braces — and it is the branch that would silently turn a plain sentence
            // into a steering instruction if the gate above were ever moved below it.
            if (profile != ChatProfile.Admin) return CommandOutcome.Nothing;
            return new CommandOutcome(SurfaceAction.Inject, text);
        }

        if (text.Equals("/status", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(_composer.StatusText());

        if (text.Equals("/tasks", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(_composer.TasksText());

        // KS11.3 / CH-4: /start answered one static sentence — "Conductor bot is running" — which
        // told a new reader nothing about what run this is, what will arrive, or what they may ask.
        if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
            return new CommandOutcome(SurfaceAction.Onboard);

        if (text.Equals("/daily", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(_composer.DailyDigestText());

        // KS11.5 / CH-6: the figures tier. Three verbs rather than one because they are three
        // questions — where is it, what has it cost, what is it burning — and a reader who wanted
        // one of them had, until now, to read the last push and hope it was recent.
        if (text.Equals("/progress", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(_composer.ProgressText());

        if (text.Equals("/money", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(_composer.MoneyText());

        if (text.Equals("/tokens", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(_composer.TokensText());

        // KS11.4 / CH-6: the bare verb is a list, and a list is just text — but naming a checkpoint
        // is a PULL, and a pull is an effect. Both arrive here; only the second leaves as an action.
        if (text.Equals("/evidence", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(_composer.EvidenceListText());

        // Verb-then-space, exactly as SurfaceCommands.Find matches it: "/evidencex" is not this verb,
        // and a gate that does not recognise it must not be followed by a router that does.
        if (text.StartsWith("/evidence ", StringComparison.OrdinalIgnoreCase))
        {
            var id = text["/evidence ".Length..].Trim();
            return id.Length == 0
                ? CommandOutcome.Reply(_composer.EvidenceListText())
                : new CommandOutcome(SurfaceAction.Evidence, id);
        }

        // DV3.4: the sticky selection. Bare shows what is in force and what this machine has; with
        // an argument it sets it. Both leave here as one action - what a chat is SET to is disk
        // state, and this router only decides.
        if (text.Equals("/project", StringComparison.OrdinalIgnoreCase))
            return new CommandOutcome(SurfaceAction.Project, "");

        if (text.StartsWith("/project ", StringComparison.OrdinalIgnoreCase))
            return new CommandOutcome(SurfaceAction.Project, text["/project ".Length..].Trim());

        // DV5.1: bare is usage plus the git state (the useful question from a phone is "is this repo
        // ready for one"), and with an argument it is either a message to a session or a task.
        // Which of those it is, is the verb's decision and not the router's - it turns on whether the
        // first token parses as a session id, and this router does not own that grammar.
        if (text.Equals("/cloud", StringComparison.OrdinalIgnoreCase))
            return new CommandOutcome(SurfaceAction.Cloud, "");

        if (text.StartsWith("/cloud ", StringComparison.OrdinalIgnoreCase))
            return new CommandOutcome(SurfaceAction.Cloud, text["/cloud ".Length..].Trim());

        if (text.StartsWith("/inject ", StringComparison.OrdinalIgnoreCase))
            return new CommandOutcome(SurfaceAction.Inject, text[8..].Trim());

        if (text.Equals("/chat", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(
                $"Use `conductor chat \"your question\"` from the terminal to ask questions about this run.\n\nExample: `conductor chat -p {PlanFileName()} \"how did session 9 die?\"`");

        if (twoWay && text.StartsWith('/')) return RouteControl(text);

        return CommandOutcome.Nothing;
    }

    private static CommandOutcome RouteControl(string command)
    {
        var (action, destructive) = command.ToLowerInvariant() switch
        {
            "/pause" => ("pause", false),
            "/resume" => ("resume", false),
            "/approve" => ("approve", false),
            "/skip" => ("skip", true),
            "/abort" => ("abort", true),
            "/kill" => ("kill", true),
            _ => (null, false),
        };

        if (action == null) return CommandOutcome.Nothing;
        if (!destructive)
            return new CommandOutcome(SurfaceAction.Control, $"{action} command sent to Conductor.", action);

        var intentId = Guid.NewGuid().ToString("N")[..8];
        return new CommandOutcome(SurfaceAction.ConfirmControl,
            $"Confirm {action}? This cannot be undone.", action, IntentId: intentId,
            Buttons:
            [
                new MessageButton($"Yes, {action}", $"{action}:{intentId}:confirmed"),
                new MessageButton("Cancel", $"cancel:{intentId}"),
            ]);
    }

    /// <summary>KS11.2 / CH-3 — the one gate. Answers a refusal when this chat may not use the verb
    /// it typed, and null when routing should carry on as normal.
    ///
    /// <para>A verb NOT in <see cref="SurfaceCommands.All"/> is not refused: an unknown command has
    /// always been met with silence, and a bot in a busy group that answers every stray slash is a
    /// bot that gets removed from the group. Silence is also what an unimplemented browse verb gets,
    /// for both profiles — which is why <see cref="SurfaceCommand.Implemented"/> exists.</para></summary>
    private static CommandOutcome? Gate(string text, ChatProfile profile)
    {
        if (profile == ChatProfile.Admin) return null;
        if (!text.StartsWith('/')) return CommandOutcome.Nothing;

        var command = SurfaceCommands.Find(text);
        if (command == null) return CommandOutcome.Nothing;

        return command.AllowedFor(profile) ? null : CommandOutcome.Refuse(SurfaceCommands.Refusal(command));
    }

    /// <summary>A button press, routed by the same rules and with no channel type in sight — the
    /// callback payload is a string this side of the seam owns, because this side is what put it on
    /// the button.
    ///
    /// <para>KS11.2 gave this the profile it was missing. Pushes fan out to every configured chat,
    /// so a confirmation keyboard raised by the owner lands in the observer's chat too; without the
    /// check below, pressing it wrote control.json. Nothing on a callback is a browse verb, so an
    /// observer is refused the whole callback surface by name.</para></summary>
    public CommandOutcome RouteCallback(string data, ChatProfile profile)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (profile != ChatProfile.Admin)
            return CommandOutcome.Refuse(
                $"That button is not part of the observer surface. Observers can ask: {SurfaceCommands.BrowseList}.");

        if (data.StartsWith("cancel:", StringComparison.Ordinal))
            return CommandOutcome.Reply("Cancelled.");

        if (data.StartsWith("inject:", StringComparison.Ordinal))
            return new CommandOutcome(SurfaceAction.ArmInjection,
                "Reply to this message with the text you want to inject into the next session.");

        // DV4.4 — before the generic action:intent:confirmed split below, which would otherwise read
        // a note id as an intent id and answer Nothing. The payload travels whole: this class decides,
        // and the surface is what owns an inbox to look the note up in.
        if (data.StartsWith(Inbox.NotePromoter.CallbackPrefix, StringComparison.Ordinal))
            return new CommandOutcome(SurfaceAction.Promote, data);

        if (data.StartsWith("chat:", StringComparison.Ordinal))
            return CommandOutcome.Reply(
                $"Use `conductor chat -p {PlanFileName()} \"your question\"` from the terminal.");

        var parts = data.Split(':');
        if (parts.Length < 2) return CommandOutcome.Nothing;
        var action = parts[0];
        if (parts.Length <= 2 || parts[2] != "confirmed") return CommandOutcome.Nothing;

        return new CommandOutcome(SurfaceAction.Control, $"{action} confirmed and sent to Conductor.",
            action, Confirmed: true, IntentId: parts[1]);
    }

    private string PlanFileName() =>
        _plan.PlanFilePath != null ? Path.GetFileName(_plan.PlanFilePath) : "conductor.plan.json";
}
