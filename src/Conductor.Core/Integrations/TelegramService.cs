using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Conductor.Core.Planning;
using Conductor.Core.Courier;
using Conductor.Core.Events;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Integrations;

/// <summary>Pushes status messages to a Telegram bot; handles /status and (B6.2) inline-keyboard
/// callbacks that write control.json. Registered as an <see cref="IHostedService"/> so long-polling
/// starts with the host and stops on disposal. If no <see cref="TelegramConfig"/> is configured
/// or the bot token env-var is missing, the service is a no-op.</summary>
public interface IRunNotifier
{
    /// <summary>SF0.1 / FU-OWNER-12: <c>null</c> when a push from this run will actually be
    /// delivered, else the missing half in <see cref="TelegramReadiness"/>' own words — the same
    /// sentence <c>doctor</c> and <c>GET /telegram/status</c> print, so the surfaces cannot drift
    /// (SC1.2's same-words requirement). Exists because a run said NOTHING about notifications at
    /// startup: <c>grep -ci telegram .conductor/conductor.log</c> returned 0 on a live run, so an
    /// operator watching a silent chat could not tell "nothing happened" from "nothing can be
    /// delivered". The run log now answers that once, unasked.</summary>
    string? DeliveryBlocker { get; }

    /// <summary>K5.4: <paramref name="severity"/> is how a push that the owner must ACT on gets to
    /// buzz while a progress line does not. It is chosen by the caller — the one place that knows
    /// what happened — rather than sniffed out of the message text here.</summary>
    Task PushAsync(string message, Messaging.PushSeverity severity = Messaging.PushSeverity.Quiet,
        CancellationToken ct = default);
    Task PushWithKeyboardAsync(string message, IReadOnlyList<(string Text, string CallbackData)> buttons,
        CancellationToken ct = default);
    Task PushSessionEndAsync(SessionEndPush push, CancellationToken ct = default);

    /// <summary>KS11.3 / CHAPAR CH-4: tell every configured chat what this run is, what will arrive
    /// and what it may ask, before the run's first word. Idempotent per chat, so the run loop can
    /// also call it after a live plan reload and only a newly-added chat hears anything.
    /// <para>Defaulted rather than required: a notifier that reaches nobody (the no-op, a dry run)
    /// has nobody to introduce, and making every implementer say so would be noise.</para></summary>
    Task PushOnboardingAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>K5.4: the run-end push, composed from facts rather than assembled as prose at the
    /// call site — which is how it came to name the plan twice and give the engine build string more
    /// room than anything the run had delivered.</summary>
    Task PushRunCompleteAsync(RunCompletePush push, CancellationToken ct = default);

    /// <summary>DV1.2: the owner queue, pushed when it changes. One message per obligation, to
    /// admin chats only, in CH-5's grammar. Defaulted for the same reason
    /// <see cref="PushOnboardingAsync"/> is: a notifier that reaches nobody has nobody to tell.</summary>
    Task PushOwnerQueueAsync(IReadOnlyList<OwnerQueueItem> items, DateTime nowUtc,
        CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>K5.3: the notification path can CARRY an evidence artifact. This announces them as
    /// text — K5.4 is what actually sends a photo or a document, and it replaces the body of this
    /// method rather than adding a second path. The owner's case is a screenshot nobody forwards.</summary>
    Task PushEvidenceAsync(IReadOnlyList<Evidence.EvidenceArtifact> artifacts, CancellationToken ct = default);

    /// <summary>DV6.3: the board page goes OUT — one self-contained HTML file, as a document, at
    /// every boundary. Publish, not serve: ADR-0005 keeps the read view off any inbound route, and a
    /// file that has already left the machine needs none.
    /// <para>Defaulted like the two above: a notifier that reaches nobody has nobody to send a page
    /// to, and the boundary must not care which kind it is holding.</para></summary>
    /// <param name="path">The rendered file, on the engine's disk.</param>
    /// <param name="snapshot">What was rendered — the caption is composed from it, so the caption and
    /// the page can never state two different boards.</param>
    Task PushBoardSnapshotAsync(string path, Publishing.BoardSnapshot snapshot,
        CancellationToken ct = default) => Task.CompletedTask;
}

public sealed partial class TelegramService
    : IHostedService, IRunNotifier, IMessageChannel, IReportsStartOutcome, IDisposable
{
    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>SC1.3: config, token and API base are re-resolvable at runtime, not snapshots taken
    /// in the constructor. A token typed into the Face and a telegram block added by a plan edit both
    /// arrive AFTER this object exists; while these were readonly the only way to pick either up was
    /// to restart the engine, and no surface said so. Written only under <see cref="_gate"/>.</summary>
    private TelegramConfig? _cfg;
    private string? _token;
    internal PlanConfig _plan;
    internal readonly RunState _state;
    internal readonly ILogger<TelegramService> _log;

    /// <summary>KS11.1 — what this run SAYS and what it ANSWERS, neither of which is this class's
    /// business any more. Rebuilt on every plan adoption for the same reason the plan itself is
    /// re-derived there (SC1.3): a reload can rename the plan, move the tracker and change the stage
    /// map under a composer that had snapshotted all three.</summary>
    private RemoteSurface _surface;
    private MessageComposer _composer;
    /// <summary>SC1.2: the ack is how <c>POST /telegram/test</c> can route through the REAL queue and
    /// still answer its HTTP caller — the send loop completes it with null on success or the error
    /// text on failure. Every ordinary push leaves it null and stays fire-and-forget.
    /// SC1.3: recreated on every start, because <see cref="StopAsync"/> completes the writer and a
    /// completed channel can never carry another message — a restart on a reloaded token would
    /// otherwise come up with a queue that silently drops everything.</summary>
    private Channel<OutboundMessage> _sendQueue;
    private readonly HttpClient _http;
    private CancellationTokenSource _cts = new();
    private Task? _pollTask;
    private Task? _sendTask;
    private int _offset;
    internal bool _started;
    /// <summary>Serialises start / stop / reload against each other: a reload arriving from an HTTP
    /// thread while the run loop's plan swap is mid-restart would otherwise interleave two stops and
    /// two starts and leave orphaned loops behind.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);
    internal readonly IRunStore? _store;

    /// <summary>M8.2: last time getUpdates succeeded, and the last poll/send error message (if
    /// any) — surfaced by the /telegram/status endpoint so the Face can show live connection
    /// health, not just "configured or not".</summary>
    internal DateTime? _lastPollUtc;
    internal string? _lastError;
    internal string? _botUsername;

    internal const string DefaultApiRoot = "https://api.telegram.org";

    /// <summary>Bot API prefix up to and including <c>/bot</c>; the token is appended per call.</summary>
    private string _apiBase;

    /// <summary>DV4.3 / findings 6.9 — set when a courier owns the token on this machine, and null
    /// on a courier-less one. Every branch it guards is skipped when it is null, which is what makes
    /// the golden replay of an old-shape plan byte-identical.</summary>
    private CourierChannel? _courier;

    /// <summary>Why this run is not polling, or null. Read by the health probe and by /telegram/status
    /// so an operator is told which process took the phone rather than that nothing is wrong.</summary>
    private string? _pollingRefusedBy;

    /// <summary>The state home the courier is looked for in. Null - always, outside a rig - means the
    /// resolved machine state home. A rig sets it so a test can put a courier.json somewhere that is
    /// not the operator's real one.</summary>
    internal string? CourierStateHome { get; set; }

    /// <summary>What this run pushes through, or null when it pushes itself. Test seam.</summary>
    internal CourierChannel? Courier => _courier;

    /// <summary>Why in-run polling did not start, or null. Never a bare false — DV1.1's rule.</summary>
    internal string? PollingRefusedBy => _pollingRefusedBy;

    /// <summary>Whether the long-poll loop is actually running. The claim findings 6.9 makes is
    /// about POLLING specifically, so the test asserts the loop and not a flag that stands for it.</summary>
    internal bool Polling => _pollTask is not null;

    public TelegramService(
        PlanConfig plan,
        RunState state,
        ILogger<TelegramService> logger,
        IRunStore? store = null)
    {
        _state = state;
        _log = logger;
        _store = store;
        AdoptPlan(plan);

        _sendQueue = NewSendQueue();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(65) };
    }

    private static Channel<OutboundMessage> NewSendQueue() =>
        Channel.CreateUnbounded<OutboundMessage>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>SC1.3: take everything this service derives from the plan, from THIS plan — used by
    /// the constructor and again by every reload, so there is one derivation and the two cannot
    /// drift. The token is re-resolved here too: it lives outside the plan (env var or secrets file)
    /// and can appear at any moment, which is exactly the case that used to need a restart.</summary>
    [MemberNotNull(nameof(_plan), nameof(_apiBase), nameof(_composer), nameof(_surface))]
    private void AdoptPlan(PlanConfig plan)
    {
        _plan = plan;
        _composer = new MessageComposer(plan, _state, ProgressProviderFactory.Create(plan), _store,
            m => _log.LogWarning("{Message}", m));
        _surface = new RemoteSurface(this, _composer, new CommandRouter(_composer, plan), _state, _store,
            WriteControlFileAsync,
            (instruction, stage) => _log.LogInformation(
                "Telegram /inject: {Instruction} (stage={Stage})", instruction, stage),
            // DV3.2: the note's durable home. Rooted on the plan's state dir, so a note filed here
            // is read by the next session of THIS project - and by one three weeks from now.
            inbox: new Inbox.InboxStore(plan.StateDir),
            // DV3.3: speech to text, from the plan's courier block. Constructed unconditionally -
            // with no command configured it answers "not configured" instantly and the note files
            // untranscribed with its audio, which is a supported state and not a broken one.
            transcriber: new Inbox.LocalCommandTranscriber(
                plan.Courier?.Transcribe, m => _log.LogInformation("{Message}", m)),
            // DV3.4: which project a note is about. The local run is always routable - a fresh run
            // whose catalogue entry has not been written yet must still be able to receive a note
            // about itself - and everything else comes from the machine catalogue (findings 1.5).
            notes: new Inbox.NoteRouter(
                new Inbox.ProjectDirectory(local: LocalProject(plan)),
                new Inbox.ChatRoutes(Store.StateHome.Root)),
            // DV5.1: the cloud verb. Constructed unconditionally and it costs nothing until asked -
            // every path it has starts by MEASURING the repo, and none of them starts a cloud
            // session, because starting one is interactive-only on the CLI this engine drives.
            cloud: new Cloud.CloudVerb());
        _cfg = plan.Telegram;
        _token = ResolveToken(plan);

        var root = string.IsNullOrWhiteSpace(_cfg?.ApiBaseUrl) ? DefaultApiRoot : _cfg!.ApiBaseUrl!.Trim();
        _apiBase = root.TrimEnd('/') + "/bot";
    }

    /// <summary>DV3.4 — this run's own project, as the router sees it. Built from the plan rather
    /// than looked up, so a run whose catalogue entry has not been written yet is still a project a
    /// note can be filed against.</summary>
    internal static Inbox.ProjectRef LocalProject(PlanConfig plan) => new(
        Plan: plan.Name ?? "",
        Repo: plan.Repo ?? "",
        Slug: Store.StateHome.SlugFor(plan.Repo ?? "", plan.Name),
        StateDir: plan.StateDir,
        Present: Directory.Exists(plan.Repo ?? ""));

    /// <summary>Env var wins (unchanged, existing behavior); falls back to the M8.2 local secrets
    /// file (SecretsStore) so the token can also be typed into the Face's guided setup instead of
    /// set as an environment variable.</summary>
    internal static string? ResolveToken(PlanConfig plan)
    {
        var fromEnv = Environment.GetEnvironmentVariable("CONDUCTOR_TELEGRAM_TOKEN")?.Trim();
        if (fromEnv is { Length: > 0 }) return fromEnv;
        return SecretsStore.TryReadTelegramToken(plan.StateDir);
    }

    internal bool IsConfigured => _cfg != null && _token != null;

    /// <inheritdoc />
    /// <remarks>Derived, never stored, and read from the service's OWN block rather than the plan's —
    /// after a live reload only one of those describes what the next push will do. Read outside
    /// <c>_gate</c> like <c>GET /telegram/status</c> does: this answers one log line and one HTTP
    /// field, and taking the gate for either would let a wedged start block a status read.</remarks>
    public string? DeliveryBlocker => TelegramReadiness.MissingHalf(
        hasBlock: _cfg is not null, hasToken: IsConfigured,
        allowedChatIds: _cfg?.ChatCount ?? 0, started: _started);

    /// <summary>SF0.1 / bug 2: whether <see cref="StartAsync"/> actually started the loops. It very
    /// often does not — no telegram block is the ordinary case — and until this existed the host
    /// announced <c>Run services started: TelegramService</c> either way, because it named every
    /// service it had called StartAsync on rather than every service that had started.</summary>
    public bool IsStarted => _started;

    /// <summary>The reason the loops are not running, in <see cref="TelegramReadiness"/>' words;
    /// null once started.</summary>
    public string? NotStartedReason => _started ? null : DeliveryBlocker;

    /// <summary>SC1.3: the telegram block this service is actually running on, which after a live
    /// reload is not necessarily the one any other holder of a plan reference has. Status must report
    /// what the service will do, not what the plan file says it should.</summary>
    internal TelegramConfig? LiveConfig => _cfg;



    public void Dispose()
    {
        _cts.Dispose();
        _http.Dispose();
    }

    // ── IRunNotifier: every one of these belongs to the seam now ──

    public Task PushAsync(string message, PushSeverity severity = PushSeverity.Quiet,
        CancellationToken ct = default) => _surface.PushAsync(message, severity, ct);

    public Task PushWithKeyboardAsync(string message,
        IReadOnlyList<(string Text, string CallbackData)> buttons, CancellationToken ct = default) =>
        _surface.PushWithKeyboardAsync(message,
            [.. buttons.Select(b => new MessageButton(b.Text, b.CallbackData))], ct);

    public Task PushSessionEndAsync(SessionEndPush push, CancellationToken ct = default) =>
        _surface.PushSessionEndAsync(push, ct);

    public Task PushOnboardingAsync(CancellationToken ct = default) => _surface.PushOnboardingAsync(ct);

    public Task PushRunCompleteAsync(RunCompletePush push, CancellationToken ct = default) =>
        _surface.PushRunCompleteAsync(push, ct);

    public Task PushEvidenceAsync(IReadOnlyList<Evidence.EvidenceArtifact> artifacts,
        CancellationToken ct = default) => _surface.PushEvidenceAsync(artifacts, ct);

    public Task PushBoardSnapshotAsync(string path, Publishing.BoardSnapshot snapshot,
        CancellationToken ct = default) => _surface.PushBoardSnapshotAsync(path, snapshot, ct);

    /// <summary>KS11.1 — the adapter's whole inbound job: unwrap the Bot API envelope, check the
    /// chat is one of ours, and hand plain text to the seam. What the text MEANS is
    /// <see cref="CommandRouter"/>'s, and what to do about it is <see cref="RemoteSurface"/>'s.</summary>
    private async Task HandleUpdateAsync(TgUpdate upd, CancellationToken ct)
    {
        if (upd.Message is { } msg)
        {
            var chatId = msg.Chat?.Id.ToString(CultureInfo.InvariantCulture);
            if (!IsAllowed(chatId)) return;

            // DV3.1: text is text and takes the path it has always taken, byte for byte. A message
            // carrying a file goes the inbound route instead (TelegramService.Inbound.cs), which
            // fetches the bytes — or refuses them by name — before the surface sees the note.
            if (KindOf(msg) is { } kind)
                await HandleMediaMessageAsync(chatId!, ProfileFor(chatId!), msg, kind, upd.UpdateId, ct)
                    .ConfigureAwait(false);
            else
                await _surface.HandleMessageAsync(chatId!, ProfileFor(chatId!), msg.Text ?? "", ct,
                        msg.MessageThreadId)
                    .ConfigureAwait(false);
        }
        if (upd.CallbackQuery is { } cb)
        {
            var chatId = cb.Message?.Chat?.Id.ToString(CultureInfo.InvariantCulture)
                         ?? cb.From?.Id.ToString(CultureInfo.InvariantCulture);
            if (!IsAllowed(chatId)) return;

            // Answering the query is what stops the client spinning. It is a Bot API obligation and
            // has nothing to do with what the press MEANT, so it stays on this side of the seam.
            await AnswerCallbackAsync(cb.Id, ct).ConfigureAwait(false);

            // The answer goes to whoever PRESSED it, which is not always the chat the keyboard was
            // posted in — a group keyboard pressed by the owner is answered to the owner.
            if (cb.From is not { } from) return;
            var to = from.Id.ToString(CultureInfo.InvariantCulture);
            await _surface.HandleCallbackAsync(to, ProfileFor(to), cb.Data ?? "", ct).ConfigureAwait(false);
        }
    }

    /// <summary>KS11.2 is what makes this answer anything but <see cref="ChatProfile.Admin"/>.</summary>
    /// <summary>KS11.2 / CH-2: the profile the plan gave this chat. An id that is not a target
    /// cannot reach here — <see cref="IsAllowed"/> runs first — so the fallback is unreachable
    /// rather than a policy, and it is the SAFE profile, not the permissive one.</summary>
    private ChatProfile ProfileFor(string chatId)
    {
        foreach (var t in Targets)
            if (string.Equals(t.ChatId, chatId, StringComparison.Ordinal)) return t.Profile;
        return ChatProfile.Observer;
    }

    /// <summary>Reads the queue it was STARTED with, not the current field: a reload swaps in a new
    /// queue, and a loop that followed the field would end up as a second reader on it.</summary>
    private async Task SendLoopAsync(Channel<OutboundMessage> queue, CancellationToken ct)
    {
        // ReadAllAsync completes normally once the writer is closed AND the backlog is drained —
        // that is what lets StopAsync flush the final session-end push instead of dropping it.
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(item, ct).ConfigureAwait(false);
                    item.Ack?.TrySetResult(null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    item.Ack?.TrySetResult("the send queue was shutting down");
                    break;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    _log.LogWarning(ex, "Telegram send error");
                    item.Ack?.TrySetResult(ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (ChannelClosedException) { }
    }

    /// <summary>FU-OWNER-11 — the two facts a Telegram message cannot recover on its own: WHICH plan
    /// sent it and WHICH session it belongs to. One chat can receive two machines' runs, so an
    /// unattributed line is unreadable; and a message read hours later has no other way to be placed
    /// in the run's history. The observed failure was the mirror image — a hand-typed operator
    /// message was indistinguishable from an engine push, and quoted an engine version the run had
    /// already superseded.
    /// <para>Read off the LIVE plan and state rather than a constructor snapshot: a reload can rename
    /// the plan (SC1.3) and the session counter moves under every message.</para></summary>
    internal string IdentityLine => _composer.IdentityLine;

    /// <summary>KS11.1: composition moved to <see cref="MessageComposer"/>. These stay as the names
    /// the rest of the engine and its suites already call a conductor push by.</summary>
    internal string Stamp(int? sessionNumber, string? stageId = null) => _composer.Stamp(sessionNumber, stageId);

    internal string StageLabel(string stageId) => _composer.StageLabel(stageId);

    internal static string Elapsed(TimeSpan d) => MessageComposer.Elapsed(d);

    // K5.4: the wire path moved to TelegramService.Transport.cs — the identity stamp is still applied
    // at that single choke point (FU-OWNER-11), and it now also chunks at 4096, threads the run and
    // maps severity to disable_notification, for text and attachments alike.

    private async Task AnswerCallbackAsync(string callbackQueryId, CancellationToken ct)
    {
        try
        {
            var url = $"{_apiBase}{_token}/answerCallbackQuery?callback_query_id={Uri.EscapeDataString(callbackQueryId)}";
            await _http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { _log.LogWarning(ex, "answerCallbackQuery failed"); }
    }

    internal async Task WriteControlFileAsync(string action, bool confirmed = false, string? intentId = null)
    {
        try
        {
            var cts = _cts;   // SC1.3: the token of the loop this handler is running on
            var path = Path.Combine(_plan.StateDir, "control.json");
            var payload = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["command"] = action,
                ["issuedUtc"] = DateTime.UtcNow.ToString("O"),
                ["confirmed"] = confirmed,
            };
            if (intentId != null) payload["intentId"] = intentId;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, JsonOpts), cts.Token).ConfigureAwait(false);
            _log.LogInformation("Telegram wrote control.json: {Action} (confirmed={Confirmed})", action, confirmed);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to write control.json"); }
    }

    /// <summary>KS11.2: who the bot answers at all. Now read from the resolved target list rather
    /// than <c>AllowedChatIds</c> directly, so a chat named only in the new <c>chats</c> block is
    /// served — WHAT it may then ask is <see cref="Messaging.CommandRouter"/>'s decision, not this
    /// one, which is why this stayed a yes/no.</summary>
    private bool IsAllowed(string? chatId)
    {
        if (chatId == null) return false;
        foreach (var t in Targets)
            if (string.Equals(t.ChatId, chatId, StringComparison.Ordinal)) return true;
        return false;
    }
}
