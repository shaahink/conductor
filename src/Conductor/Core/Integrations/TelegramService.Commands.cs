using System.Globalization;
using System.Text.Json;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Integrations;

public sealed partial class TelegramService
{
    private async Task HandleMessageAsync(string chatId, TgMessage msg, CancellationToken ct)
    {
        var text = (msg.Text ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (_pendingInjections.TryGetValue(chatId, out var pending) && pending
            && !text.StartsWith('/'))
        {
            _pendingInjections.Remove(chatId);
            await HandleInjectAsync(chatId, text, ct).ConfigureAwait(false);
            return;
        }

        if (text.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            var status = BuildStatusText();
            await SendAsync(chatId, status, ct).ConfigureAwait(false);
        }
        else if (text.Equals("/tasks", StringComparison.OrdinalIgnoreCase))
        {
            var tasks = BuildTasksText();
            await SendAsync(chatId, tasks, ct).ConfigureAwait(false);
        }
        else if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
        {
            await SendAsync(chatId, "Conductor bot is running. Use /status to see the current state.", ct)
                .ConfigureAwait(false);
        }
        else if (text.Equals("/daily", StringComparison.OrdinalIgnoreCase))
        {
            await SendDailyDigestAsync(chatId, ct).ConfigureAwait(false);
        }
        else if (text.StartsWith("/inject ", StringComparison.OrdinalIgnoreCase))
        {
            var instruction = text[8..].Trim();
            await HandleInjectAsync(chatId, instruction, ct).ConfigureAwait(false);
        }
        else if (text.Equals("/chat", StringComparison.OrdinalIgnoreCase))
        {
            var planName = _plan.PlanFilePath != null ? Path.GetFileName(_plan.PlanFilePath) : "conductor.plan.json";
            await SendAsync(chatId,
                $"Use `conductor chat \"your question\"` from the terminal to ask questions about this run.\n\nExample: `conductor chat -p {planName} \"how did session 9 die?\"`",
                ct).ConfigureAwait(false);
        }
        else if (_cfg?.EnableTwoWay == true && text.StartsWith('/'))
        {
            await HandleTwoWayCommandAsync(chatId, text, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleTwoWayCommandAsync(string chatId, string command, CancellationToken ct)
    {
        string? controlAction;
        bool destructive;
        (controlAction, destructive) = command.ToLowerInvariant() switch
        {
            "/pause" => ("pause", false),
            "/resume" => ("resume", false),
            "/approve" => ("approve", false),
            "/skip" => ("skip", true),
            "/abort" => ("abort", true),
            "/kill" => ("kill", true),
            _ => (null, false),
        };

        if (controlAction == null) return;

        if (destructive)
        {
            var intentId = Guid.NewGuid().ToString("N")[..8];
            var kb = BuildInlineKeyboard(
            [
                ($"Yes, {controlAction}", $"{controlAction}:{intentId}:confirmed"),
                ("Cancel", $"cancel:{intentId}"),
            ]);
            await SendAsync(chatId, $"Confirm {controlAction}? This cannot be undone.", ct, kb)
                .ConfigureAwait(false);
        }
        else
        {
            WriteControlFile(controlAction);
            await SendAsync(chatId, $"{controlAction} command sent to Conductor.", ct)
                .ConfigureAwait(false);
        }
    }

    private async Task HandleInjectAsync(string chatId, string instruction, CancellationToken ct)
    {
        if (_store == null)
        {
            await SendAsync(chatId, "Cannot inject: store is not available.", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var runId = _state.RunId ?? Guid.NewGuid().ToString("N");
            _store.WriteInjection(runId, "telegram", null, _state.CurrentStage, instruction);
            await SendAsync(chatId, $"Instruction injected for the next session: <i>{EscapeHtml(instruction)}</i>", ct)
                .ConfigureAwait(false);
            _log.LogInformation("Telegram /inject: {Instruction} (stage={Stage})", instruction, _state.CurrentStage);
        }
        catch (Exception ex)
        {
            await SendAsync(chatId, $"Failed to inject: {EscapeHtml(ex.Message)}", ct).ConfigureAwait(false);
        }
    }
}
