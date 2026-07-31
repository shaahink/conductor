using System.Net;
using System.Text.Json;
using Conductor.Core.Integrations;

namespace Conductor.Core.Http;

/// <summary>M8.2: Telegram setup/status/test for the Face's guided onboarding tab — "configure it
/// through the app" instead of hand-editing plan.json/env vars. Status/test read the live
/// <see cref="Integrations.ITelegramService"/> instance, pattern-matched to the concrete
/// <see cref="TelegramService"/> for its extra internal status surface; a
/// <see cref="NoOpTelegramService"/> (or no <c>Telegram</c> block on the plan) means Telegram
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
    private TelegramStatusDto BuildTelegramStatus()
    {
        var cfg = _plan.Telegram;
        if (cfg is null || _telegram is not TelegramService svc)
            return new TelegramStatusDto(
                Configured: false, Started: false, HasToken: false, AllowedChatIds: [],
                PollIntervalSeconds: 4, EnableTwoWay: false, BotUsername: null, LastError: null, LastPollUtc: null,
                WillDeliver: false, WillDeliverReason: TelegramReadiness.NoBlock);

        var missing = TelegramReadiness.MissingHalf(
            hasBlock: true, hasToken: svc.IsConfigured,
            allowedChatIds: cfg.AllowedChatIds.Count, started: svc._started);

        return new TelegramStatusDto(
            Configured: true,
            Started: svc._started,
            HasToken: svc.IsConfigured,
            AllowedChatIds: cfg.AllowedChatIds,
            PollIntervalSeconds: cfg.PollIntervalSeconds,
            EnableTwoWay: cfg.EnableTwoWay,
            BotUsername: svc._botUsername,
            LastError: svc._lastError,
            LastPollUtc: svc._lastPollUtc?.ToString("O"),
            WillDeliver: missing is null,
            WillDeliverReason: missing);
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
            await WriteJsonAsync(ctx, new TelegramSetTokenResultDto(false, "malformed JSON body"),
                ControlPlaneJsonContext.Default.TelegramSetTokenResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(req?.Token))
        {
            await WriteJsonAsync(ctx, new TelegramSetTokenResultDto(false, "token is empty"),
                ControlPlaneJsonContext.Default.TelegramSetTokenResultDto, HttpStatusCode.BadRequest).ConfigureAwait(false);
            return;
        }

        SecretsStore.WriteTelegramToken(_plan.StateDir, req.Token);
        await WriteJsonAsync(ctx, new TelegramSetTokenResultDto(true, "saved — restart conductor to connect with the new token"),
            ControlPlaneJsonContext.Default.TelegramSetTokenResultDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }
}
