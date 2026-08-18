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
}

/// <summary>KS11.1 / CHAPAR CH-1 — inbound dispatch, channel-agnostic. It takes the text a reader
/// typed and the profile of the chat they typed it in, and answers with a decision.
///
/// <para>KS11.1 preserves today's behaviour exactly: every chat arrives as
/// <see cref="ChatProfile.Admin"/>, so every command routes as it always has. What has changed is
/// that the routing is now a function of (text, profile) in one readable place — which is the shape
/// CH-3's closed observer surface needs, and the shape its exhaustive command-by-profile matrix can
/// actually be written against.</para></summary>
public sealed class CommandRouter
{
    private readonly MessageComposer _composer;
    private readonly PlanConfig _plan;

    public CommandRouter(MessageComposer composer, PlanConfig plan)
    {
        _composer = composer;
        _plan = plan;
    }

    /// <param name="profile">What this chat is allowed to be. Carried through KS11.1 without
    /// branching on it — the value is Admin everywhere until KS11.2 can read it from a plan.</param>
    /// <param name="twoWay">Whether the channel is wired for control verbs at all.</param>
    /// <param name="injectionArmed">Whether this chat's last exchange asked it for injection text.</param>
    public CommandOutcome Route(string text, ChatProfile profile, bool twoWay, bool injectionArmed)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length == 0) return CommandOutcome.Nothing;

        if (injectionArmed && !text.StartsWith('/'))
            return new CommandOutcome(SurfaceAction.Inject, text);

        if (text.Equals("/status", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(_composer.StatusText());

        if (text.Equals("/tasks", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(_composer.TasksText());

        if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply("Conductor bot is running. Use /status to see the current state.");

        if (text.Equals("/daily", StringComparison.OrdinalIgnoreCase))
            return CommandOutcome.Reply(_composer.DailyDigestText());

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

    /// <summary>A button press, routed by the same rules and with no channel type in sight — the
    /// callback payload is a string this side of the seam owns, because this side is what put it on
    /// the button.</summary>
    public CommandOutcome RouteCallback(string data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.StartsWith("cancel:", StringComparison.Ordinal))
            return CommandOutcome.Reply("Cancelled.");

        if (data.StartsWith("inject:", StringComparison.Ordinal))
            return new CommandOutcome(SurfaceAction.ArmInjection,
                "Reply to this message with the text you want to inject into the next session.");

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
