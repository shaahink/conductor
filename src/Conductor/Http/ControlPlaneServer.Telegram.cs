using Conductor.Core;
using Conductor.Core.Http;
using System.Net;
using System.Text.Json;
using Conductor.Core.Integrations;
using Microsoft.Extensions.Logging;

namespace Conductor.Http;

/// <summary>M8.2: Telegram setup/status/test for the Face's guided onboarding tab — "configure it
/// through the app" instead of hand-editing plan.json/env vars. Status/test read the live
/// <see cref="Integrations.IRunNotifier"/> instance, pattern-matched to the concrete
/// <see cref="TelegramService"/> for its extra internal status surface; a
/// <see cref="NoOpRunNotifier"/> (or no <c>Telegram</c> block on the plan) means Telegram
/// simply isn't configured on this plan yet — not an error. The bot token itself is never
/// round-tripped through the versioned plan file; it is saved to the local secrets store
/// (<see cref="SecretsStore"/>), the same file the token env var it complements would otherwise
/// need to be set in. Non-secret settings (allowed chat ids, poll interval, two-way toggle) reuse
/// the existing <c>/plan/edit</c> mechanism via a new "telegram" edit target — see
/// <see cref="ApplyTelegramEdit"/> in <c>ControlPlaneServer.Plan.cs</c>.</summary>
public sealed partial class ControlPlaneServer
{
    private async Task WriteTelegramStatusAsync(HttpListenerContext ctx)
    {
        await WriteJsonAsync(ctx, BuildTelegramStatus(), ControlPlaneJsonContext.Default.TelegramStatusDto).ConfigureAwait(false);
    }

    /// <summary>SC1.2: the four older booleans each described a precondition; none of them answered
    /// the only question an operator asks — will this run actually reach my phone? WillDeliver is
    /// that answer, derived (never stored), and when it is false WillDeliverReason names the missing
    /// half in the same words doctor uses, from the same helper.</summary>
    /// <summary>FU-OWNER-13: <see cref="TelegramReadiness.NoBlock"/> is the right answer about the LIVE
    /// plan and the wrong thing to print when the plan on disk already has the block and the loop
    /// simply has not swapped it in yet — it names a cause that no longer exists and gives an
    /// instruction that would be a no-op. Rewritten only in that exact case: a reload is queued AND
    /// the plan that reload will install carries a telegram block. Every other blocker (no token, no
    /// chat ids, not started) is untouched, because a pending reload does not fix any of them.</summary>
    private string? ReloadAware(string? blocker) =>
        blocker == TelegramReadiness.NoBlock && ReloadPending && _queuedReloadPlan?.Telegram is not null
            ? TelegramReadiness.ReloadQueued
            : blocker;

    /// <summary>The same rewrite for the token endpoint, whose reply is a whole sentence ending in the
    /// blocker rather than the bare constant. "saved, but this run still will not deliver: not
    /// configured — add a telegram block" becomes "saved, and a plan reload is queued": both halves
    /// true, neither of them an instruction to redo work already accepted.</summary>
    private string ReloadAwareMessage(string message) =>
        message.EndsWith(TelegramReadiness.NoBlock, StringComparison.Ordinal)
        && ReloadPending && _queuedReloadPlan?.Telegram is not null
            ? "saved, and " + TelegramReadiness.ReloadQueued
            : message;

    private TelegramStatusDto BuildTelegramStatus()
    {
        // SC1.3: no service in this process is a state of its own — the plan can be fully configured
        // and nothing here can ever deliver, because there is nobody to hand the configuration to.
        // RestartRequired says that out loud instead of letting it read as "configured, not started".
        if (_telegram is not TelegramService svc)
        {
            var block = _plan.Telegram;
            return new TelegramStatusDto(
                Configured: block is not null, Started: false, HasToken: false,
                AllowedChatIds: block is null ? [] : [.. block.ResolvedChats().Select(c => c.ChatId)],
                PollIntervalSeconds: block?.PollIntervalSeconds ?? 4, EnableTwoWay: block?.EnableTwoWay ?? false,
                BotUsername: null, LastError: null, LastPollUtc: null,
                WillDeliver: false,
                WillDeliverReason: ReloadAware(block is null ? TelegramReadiness.NoBlock : TelegramReadiness.RestartRequired),
                RestartRequired: block is not null,
                ReloadPending: ReloadPending);
        }

        // The service's OWN block, not _plan's: after a live reload those can differ for a moment,
        // and only one of them describes what the next push will actually do.
        var cfg = svc.LiveConfig;
        if (cfg is null)
            return new TelegramStatusDto(
                Configured: false, Started: false, HasToken: false, AllowedChatIds: [],
                PollIntervalSeconds: 4, EnableTwoWay: false, BotUsername: null, LastError: null, LastPollUtc: null,
                WillDeliver: false, WillDeliverReason: ReloadAware(TelegramReadiness.NoBlock),
                RestartRequired: false, ReloadPending: ReloadPending);

        var missing = TelegramReadiness.MissingHalf(
            hasBlock: true, hasToken: svc.IsConfigured,
            allowedChatIds: cfg.ChatCount, started: svc._started);

        return new TelegramStatusDto(
            Configured: true,
            Started: svc._started,
            HasToken: svc.IsConfigured,
            // KS11.2: every chat served, old list and chats block merged - the field is what the
            // bot will talk to, and showing only half of it is how a reader concludes a configured
            // observer chat was never picked up.
            AllowedChatIds: [.. cfg.ResolvedChats().Select(c => c.ChatId)],
            PollIntervalSeconds: cfg.PollIntervalSeconds,
            EnableTwoWay: cfg.EnableTwoWay,
            BotUsername: svc._botUsername,
            LastError: svc._lastError,
            LastPollUtc: svc._lastPollUtc?.ToString("O"),
            WillDeliver: missing is null,
            WillDeliverReason: ReloadAware(missing),
            // A live service can take a new token or a new block without a restart — that is the
            // whole of SC1.3, and saying "restart required" here would be a lie in the other
            // direction.
            RestartRequired: false,
            ReloadPending: ReloadPending);
    }

    private async Task HandleTelegramTestAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        if (_telegram is not TelegramService svc)
        {
            await WriteJsonAsync(ctx, new TelegramTestResultDto(false, null,
                    "Telegram is not configured on this plan — add a Telegram block first", ViaQueue: false,
                    Detail: TelegramReadiness.NoBlock),
                ControlPlaneJsonContext.Default.TelegramTestResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }

        var r = await svc.TestConnectionAsync(ct).ConfigureAwait(false);
        await WriteJsonAsync(ctx, new TelegramTestResultDto(r.Ok, r.BotUsername, r.Error, r.ViaQueue, r.Detail),
            ControlPlaneJsonContext.Default.TelegramTestResultDto,
            r.Ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest).ConfigureAwait(false);
    }

    private async Task HandleTelegramTokenAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        TelegramSetTokenRequestDto? req;
        try { req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.TelegramSetTokenRequestDto); }
        catch (JsonException)
        {
            await WriteJsonAsync(ctx, new TelegramSetTokenResultDto(false, "malformed JSON body", WillDeliver: false),
                ControlPlaneJsonContext.Default.TelegramSetTokenResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(req?.Token))
        {
            await WriteJsonAsync(ctx, new TelegramSetTokenResultDto(false, "token is empty", WillDeliver: false),
                ControlPlaneJsonContext.Default.TelegramSetTokenResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }

        SecretsStore.WriteTelegramToken(_plan.StateDir, req.Token);

        // SC1.3: the token was resolved ONCE, in the service's constructor, so this endpoint's old
        // reply ("restart conductor to connect with the new token") was the honest description of a
        // feature that could not pick a token up — and the Face showed it as a plain green save.
        // Now the running service re-resolves and starts, and the reply says what actually happened.
        if (_telegram is TelegramService svc)
        {
            var outcome = await svc.ReloadAsync(ct: ct).ConfigureAwait(false);
            // FU-OWNER-13: this is the reply that actually burned the owner — "saved, but this run
            // still will not deliver: not configured, add a telegram block to the plan", seconds
            // after `POST /plan/edit` had accepted exactly that block. The reload carrying it is
            // queued, so say THAT; the token really is saved either way.
            var message = ReloadAwareMessage(outcome.Message);
            _logger.LogInformation("Telegram token saved from the control plane: {Message}", message);
            await WriteJsonAsync(ctx, new TelegramSetTokenResultDto(true, message, outcome.WillDeliver),
                ControlPlaneJsonContext.Default.TelegramSetTokenResultDto, HttpStatusCode.Accepted).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(ctx, new TelegramSetTokenResultDto(true,
                "saved, but " + TelegramReadiness.RestartRequired, WillDeliver: false),
            ControlPlaneJsonContext.Default.TelegramSetTokenResultDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }
}
