using Conductor.Models;
using Conductor.Core.Evidence;
using Conductor.Core.Store;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>KS11.1 / CHAPAR CH-1 — the remote surface: everything the run says and everything it
/// answers, over whatever channel it is handed.
///
/// <para>This is the seam's working half. It composes through <see cref="MessageComposer"/>, decides
/// through <see cref="CommandRouter"/>, and hands the result to an <see cref="IMessageChannel"/>
/// that knows nothing about runs. <c>TelegramService</c> is now one implementation of that channel
/// and nothing more; a fake one drives this entire class in tests, which is the whole point — until
/// KS11.1 there was no way to ask what the surface would do without an HTTP listener answering for
/// api.telegram.org.</para>
///
/// <para>Behaviour is deliberately unchanged here. KS11.1's goldens pin every byte this class now
/// produces against what the un-extracted engine produced, and KS11.2–11.5 are what change what it
/// says.</para></summary>
public sealed class RemoteSurface
{
    private readonly IMessageChannel _channel;
    private readonly MessageComposer _composer;
    private readonly CommandRouter _router;
    private readonly IRunStore? _store;
    private readonly RunState _state;
    private readonly Func<string, bool, string?, Task> _writeControl;
    private readonly Action<string, string?> _log;

    /// <summary>Chats that have been asked for injection text and whose next plain message is
    /// therefore an instruction rather than a command.</summary>
    private readonly Dictionary<string, bool> _injectionArmed = new(StringComparer.Ordinal);

    /// <summary>KS11.3 / CH-4 — chats that have already been told the rules, so that the run-start
    /// call and the after-a-reload call can both be made unconditionally and only the chats that
    /// need it hear anything.</summary>
    private readonly HashSet<string> _onboarded = new(StringComparer.Ordinal);

    /// <param name="writeControl">(action, confirmed, intentId) → the control file. An engine
    /// concern, handed in rather than reached for, so this class has no state directory of its own.</param>
    /// <param name="log">(message, argument) for the one line the inject path has always written.</param>
    public RemoteSurface(IMessageChannel channel, MessageComposer composer, CommandRouter router,
        RunState state, IRunStore? store, Func<string, bool, string?, Task> writeControl,
        Action<string, string?> log)
    {
        _channel = channel;
        _composer = composer;
        _router = router;
        _state = state;
        _store = store;
        _writeControl = writeControl;
        _log = log;
    }

    public MessageComposer Composer => _composer;

    // ────────────────────────────── outbound ──────────────────────────────

    /// <summary>K5.2: where the run is, on every engine push — fifteen messages of the owner's own
    /// run carried no checkpoint count, no stage progress and no ETA between them. Appended, never
    /// substituted, and empty when there is no tracker to read.</summary>
    public Task PushAsync(string message, PushSeverity severity, CancellationToken ct)
    {
        // Nothing to read the tracker for if the push cannot be delivered.
        if (!_channel.IsLive || _channel.Targets.Count == 0) return Task.CompletedTask;

        var progress = _composer.ProgressLine(null);
        return FanOutAsync(progress.Length > 0
            ? message + "\n" + MessageComposer.EscapeHtml(progress)
            : message, null, severity, null, ct);
    }

    public async Task PushSessionEndAsync(SessionEndPush push, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(push);
        if (!_channel.IsLive) return;

        var body = await _composer.SessionEndAsync(push).ConfigureAwait(false);

        // K5.4: a session that advanced is informational; one that ended blocked or needing the
        // owner is the whole reason a silent push exists.
        await FanOutAsync(body, push.Number, MessageComposer.SessionSeverity(push.Outcome), null, ct, push.Stage)
            .ConfigureAwait(false);
    }

    public async Task PushRunCompleteAsync(RunCompletePush push, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(push);
        if (!_channel.IsLive) return;

        var body = await _composer.RunCompleteAsync(push).ConfigureAwait(false);

        // A finished run is one of the two things worth a buzz — the other is a run that has parked.
        await FanOutAsync(body, null, PushSeverity.Alert, null, ct).ConfigureAwait(false);
    }

    /// <summary>K5.4 — evidence ARRIVES. Every artifact is sent as itself, up to the batch budget,
    /// with the text line it used to push as the caption; the rest are announced as text, which is
    /// exactly what they were before.</summary>
    public async Task PushEvidenceAsync(IReadOnlyList<EvidenceArtifact> artifacts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        if (!_channel.IsLive || artifacts.Count == 0) return;

        var sendable = artifacts.Take(MessageComposer.EvidenceFilesPerPush).ToList();
        foreach (var a in sendable)
        {
            var absolute = _composer.ResolveArtifact(a.Path);
            var caption = await _composer.EvidenceCaptionAsync(a, artifacts.Count).ConfigureAwait(false);
            if (absolute is null)
            {
                await FanOutAsync(caption + "\n<i>not attached — the path did not resolve to a file</i>",
                    a.SessionNumber, PushSeverity.Quiet, null, ct).ConfigureAwait(false);
                continue;
            }

            await FanOutAsync(caption, a.SessionNumber, PushSeverity.Quiet,
                new OutboundAttachment(absolute, EvidenceKinds.IsVisual(a.Kind), caption), ct, a.StageId)
                .ConfigureAwait(false);
        }

        var rest = artifacts.Skip(MessageComposer.EvidenceFilesPerPush).ToList();
        if (rest.Count == 0) return;

        var body = await _composer.EvidenceOverflowAsync(rest).ConfigureAwait(false);
        await FanOutAsync(body, null, PushSeverity.Quiet, null, ct).ConfigureAwait(false);
    }

    /// <summary>A keyboard means the engine is ASKING the owner for something — that is the
    /// definition of a push that should buzz.</summary>
    public async Task PushWithKeyboardAsync(string message, IReadOnlyList<MessageButton> buttons,
        CancellationToken ct)
    {
        if (!_channel.IsLive || !_channel.AllowsControl || _channel.Targets.Count == 0) return;

        // KS11.3 / CH-3: a keyboard is the engine ASKING for a decision, so it goes only to chats
        // that can make one. KS11.2 closed the callback an observer could press; this stops the
        // button being offered at all, which is the difference between a refusal and a surface that
        // never pretended. The observer still gets the news — the text half rides PushAsync.
        foreach (var target in _channel.Targets.Where(t => t.Profile == ChatProfile.Admin))
            await _channel.EnqueueAsync(
                new OutboundMessage(target.ChatId, message, buttons, Severity: PushSeverity.Alert), ct)
                .ConfigureAwait(false);
    }

    /// <summary>The one write path onto the channel's queue: one copy per configured chat.</summary>
    private async Task FanOutAsync(string message, int? sessionNumber, PushSeverity severity,
        OutboundAttachment? attachment, CancellationToken ct, string? stageId = null)
    {
        foreach (var target in _channel.Targets)
            await _channel.EnqueueAsync(
                new OutboundMessage(target.ChatId, message, null, null, sessionNumber, severity, attachment, stageId),
                ct).ConfigureAwait(false);
    }

    /// <summary>KS11.3 / CHAPAR CH-4 — every configured chat that has not been told the rules gets
    /// told them, in its own profile's voice.
    ///
    /// <para>Called at run start, before the run's first word, and again after a live plan reload —
    /// which is the case CH-4 actually names: a chat added MID-RUN used to start receiving
    /// session-end pushes with no frame at all. The set below is what makes the second call cost
    /// nothing for chats that were already here.</para></summary>
    public async Task PushOnboardingAsync(CancellationToken ct)
    {
        if (!_channel.IsLive) return;

        foreach (var target in _channel.Targets)
        {
            if (!_onboarded.Add(target.ChatId)) continue;
            await SendOnboardingAsync(target.ChatId, target.Profile, ct).ConfigureAwait(false);
        }
    }

    /// <summary>What <c>/start</c> does: the rules again, on request, whether or not this chat has
    /// already been told them. It leaves by <see cref="ReplyAsync"/> — the door every other answer
    /// to a typed command uses — because an ANSWER that queues behind the run's pushes is an answer
    /// that arrives minutes after the question.</summary>
    private async Task ForceOnboardAsync(string chatId, ChatProfile profile, CancellationToken ct)
    {
        _onboarded.Add(chatId);
        var body = await _composer.OnboardingAsync(profile, _channel.AllowsControl).ConfigureAwait(false);
        await ReplyAsync(chatId, body, null, ct).ConfigureAwait(false);
    }

    /// <summary>The unprompted half: onboarding as a push, on the run's own queue with everything
    /// else the run says.</summary>
    private async Task SendOnboardingAsync(string chatId, ChatProfile profile, CancellationToken ct)
    {
        var body = await _composer.OnboardingAsync(profile, _channel.AllowsControl).ConfigureAwait(false);
        await _channel.EnqueueAsync(
            new OutboundMessage(chatId, body, null, null, null, PushSeverity.Quiet), ct).ConfigureAwait(false);
    }

    // ────────────────────────────── inbound ──────────────────────────────

    /// <summary>One message from one chat, routed and acted on.</summary>
    public Task HandleMessageAsync(string chatId, ChatProfile profile, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Trim().Length == 0) return Task.CompletedTask;

        var armed = _injectionArmed.TryGetValue(chatId, out var pending) && pending;
        if (armed && !text.Trim().StartsWith('/')) _injectionArmed.Remove(chatId);

        return ApplyAsync(chatId, _router.Route(text, profile, _channel.AllowsControl, armed), ct, profile);
    }

    /// <summary>One button press, routed and acted on. <paramref name="chatId"/> is where the answer
    /// goes — for a callback that is the user who pressed it, which is not always the chat the
    /// keyboard was posted in.</summary>
    public Task HandleCallbackAsync(string chatId, ChatProfile profile, string data, CancellationToken ct)
        => ApplyAsync(chatId, _router.RouteCallback(data, profile), ct, profile);

    private async Task ApplyAsync(string chatId, CommandOutcome outcome, CancellationToken ct,
        ChatProfile profile = ChatProfile.Admin)
    {
        switch (outcome.Action)
        {
            case SurfaceAction.Onboard:
                await ForceOnboardAsync(chatId, profile, ct).ConfigureAwait(false);
                return;

            case SurfaceAction.None:
                return;

            case SurfaceAction.Reply:
                await ReplyAsync(chatId, outcome.Text!, null, ct).ConfigureAwait(false);
                return;

            case SurfaceAction.Refuse:
                // KS11.2 / CH-3: delivered exactly like a reply, and deliberately NOT written to the
                // engine log — the only log delegate this class holds is the inject path's, and a
                // refusal logged through it would read as an injection that never happened.
                await ReplyAsync(chatId, outcome.Text!, null, ct).ConfigureAwait(false);
                return;

            case SurfaceAction.ArmInjection:
                _injectionArmed[chatId] = true;
                await ReplyAsync(chatId, outcome.Text!, null, ct).ConfigureAwait(false);
                return;

            case SurfaceAction.Control:
                await _writeControl(outcome.ControlAction!, outcome.Confirmed, outcome.IntentId).ConfigureAwait(false);
                await ReplyAsync(chatId, outcome.Text!, null, ct).ConfigureAwait(false);
                return;

            case SurfaceAction.ConfirmControl:
                await ReplyAsync(chatId, outcome.Text!, outcome.Buttons, ct).ConfigureAwait(false);
                return;

            case SurfaceAction.Inject:
                await InjectAsync(chatId, outcome.Text!, ct).ConfigureAwait(false);
                return;

            default:
                return;
        }
    }

    private async Task InjectAsync(string chatId, string instruction, CancellationToken ct)
    {
        if (_store == null)
        {
            await ReplyAsync(chatId, "Cannot inject: store is not available.", null, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var runId = _state.RunId ?? Guid.NewGuid().ToString("N");
            _store.WriteInjection(runId, _channel.Name, null, _state.CurrentStage, instruction);
            await ReplyAsync(chatId,
                $"Instruction injected for the next session: <i>{MessageComposer.EscapeHtml(instruction)}</i>",
                null, ct).ConfigureAwait(false);
            _log(instruction, _state.CurrentStage);
        }
#pragma warning disable CA1031 // an inbound command must never take the poll loop down with it
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await ReplyAsync(chatId, $"Failed to inject: {MessageComposer.EscapeHtml(ex.Message)}", null, ct)
                .ConfigureAwait(false);
        }
    }

    private Task ReplyAsync(string chatId, string text, IReadOnlyList<MessageButton>? buttons, CancellationToken ct)
        => _channel.SendAsync(new OutboundMessage(chatId, text, buttons), ct);

    // ────────────────────────────── the digest ──────────────────────────────

    private DateTime _lastDigestUtc = DateTime.UtcNow;

    /// <summary>Once a day, unasked, to every configured chat.</summary>
    public async Task MaybeSendDailyDigestAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastDigestUtc < TimeSpan.FromHours(24) || _channel.Targets.Count == 0) return;

        _lastDigestUtc = DateTime.UtcNow;
        foreach (var target in _channel.Targets)
            await ReplyAsync(target.ChatId, _composer.DailyDigestText(), null, ct).ConfigureAwait(false);
    }
}
