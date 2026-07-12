using System.Globalization;

namespace Conductor.Core.Integrations;

public sealed partial class TelegramService
{
    private async Task HandleCallbackAsync(TgCallbackQuery cb, CancellationToken ct)
    {
        var data = cb.Data ?? "";
        await AnswerCallbackAsync(cb.Id, ct).ConfigureAwait(false);

        if (data.StartsWith("cancel:", StringComparison.Ordinal))
        {
            if (cb.From != null)
                await SendAsync(cb.From.Id.ToString(CultureInfo.InvariantCulture), "Cancelled.", ct)
                    .ConfigureAwait(false);
            return;
        }

        if (data.StartsWith("inject:", StringComparison.Ordinal))
        {
            if (cb.From != null)
            {
                var userId = cb.From.Id.ToString(CultureInfo.InvariantCulture);
                _pendingInjections[userId] = true;
                await SendAsync(userId, "Reply to this message with the text you want to inject into the next session.", ct)
                    .ConfigureAwait(false);
            }
            return;
        }

        if (data.StartsWith("chat:", StringComparison.Ordinal))
        {
            if (cb.From != null)
            {
                var planName = _plan.PlanFilePath != null ? Path.GetFileName(_plan.PlanFilePath) : "conductor.plan.json";
                await SendAsync(cb.From.Id.ToString(CultureInfo.InvariantCulture),
                    $"Use `conductor chat -p {planName} \"your question\"` from the terminal.", ct)
                    .ConfigureAwait(false);
            }
            return;
        }

        var parts = data.Split(':');
        if (parts.Length < 2) return;
        var action = parts[0];
        var confirmed = parts.Length > 2 && parts[2] == "confirmed";

        if (confirmed && cb.From != null)
        {
            WriteControlFile(action, confirmed: true, intentId: parts[1]);
            await SendAsync(cb.From.Id.ToString(CultureInfo.InvariantCulture),
                $"{action} confirmed and sent to Conductor.", ct).ConfigureAwait(false);
        }
    }
}
