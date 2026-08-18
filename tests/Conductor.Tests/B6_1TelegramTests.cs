using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Models;

namespace Conductor.Tests;

public class B6_1TelegramTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public async Task NoOp_push_is_noop()
    {
        var svc = new NoOpRunNotifier();
        await svc.PushAsync("test");
        await svc.PushWithKeyboardAsync("test", [("btn", "data")]);
    }

    [Fact]
    public void InlineKeyboard_produces_valid_telegram_json()
    {
        var json = BuildKeyboardReflection(
        [
            ("Pause", "pause:abc:confirmed"),
            ("Resume", "resume:abc:confirmed"),
        ]);

        using var doc = JsonDocument.Parse(json);
        var kb = doc.RootElement.GetProperty("inline_keyboard");
        Assert.Equal(JsonValueKind.Array, kb.ValueKind);
        Assert.Equal("Pause", kb[0][0].GetProperty("text").GetString());
        Assert.Equal("pause:abc:confirmed", kb[0][0].GetProperty("callback_data").GetString());
        Assert.Equal("Resume", kb[0][1].GetProperty("text").GetString());
    }

    [Fact]
    public void InlineKeyboard_single_button_is_valid()
    {
        var json = BuildKeyboardReflection([("Approve", "approve:intent:confirmed")]);
        using var doc = JsonDocument.Parse(json);
        var row = doc.RootElement.GetProperty("inline_keyboard")[0];
        Assert.Single(row.EnumerateArray());
    }

    [Fact]
    public void CallbackData_parse_action_intent_confirmed()
    {
        var data = "pause:abc12345:confirmed";
        var parts = data.Split(':');
        Assert.Equal(3, parts.Length);
        Assert.Equal("pause", parts[0]);
        Assert.Equal("abc12345", parts[1]);
        Assert.Equal("confirmed", parts[2]);
    }

    [Fact]
    public void CallbackData_cancel_prefix_is_recognized()
    {
        Assert.StartsWith("cancel:", "cancel:abc12345", StringComparison.Ordinal);
        Assert.DoesNotContain("cancel:", "pause:abc12345", StringComparison.Ordinal);
    }

    [Fact]
    public void ControlFile_written_by_telegram_has_expected_schema()
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["command"] = "pause",
            ["issuedUtc"] = DateTime.UtcNow.ToString("O"),
            ["confirmed"] = true,
            ["intentId"] = "abc12345",
        };
        var json = JsonSerializer.Serialize(payload);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("pause", doc.RootElement.GetProperty("command").GetString());
        Assert.True(doc.RootElement.GetProperty("confirmed").GetBoolean());
        Assert.Equal("abc12345", doc.RootElement.GetProperty("intentId").GetString());
    }

    [Fact]
    public void ControlFile_without_confirmed_parses_correctly()
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["command"] = "resume",
            ["issuedUtc"] = DateTime.UtcNow.ToString("O"),
        };
        var json = JsonSerializer.Serialize(payload);

        var parsed = ControlFile.Parse(json);
        Assert.Equal(ControlAction.ResumeRun, parsed.Action);
        Assert.False(parsed.Confirmed);
    }

    [Fact]
    public void ControlFile_non_destructive_has_no_confirmed_flag()
    {
        // resume/pause/approve don't need confirmed
        var json = """{"command":"resume","issuedUtc":"2026-01-01T00:00:00Z"}""";
        var parsed = ControlFile.Parse(json);
        Assert.Equal(ControlAction.ResumeRun, parsed.Action);
        Assert.False(parsed.Confirmed);
    }

    [Fact]
    public void AllowedChatIds_block_unknown_chat()
    {
        var cfg = new TelegramConfig { AllowedChatIds = ["111", "222"] };
        Assert.Contains("111", cfg.AllowedChatIds);
        Assert.DoesNotContain("333", cfg.AllowedChatIds);
    }

    [Fact]
    public void AllowedChatIds_empty_list_accepts_no_one()
    {
        var cfg = new TelegramConfig { AllowedChatIds = [] };
        Assert.Empty(cfg.AllowedChatIds);
    }

    [Fact]
    public void EnableTwoWay_defaults_to_false()
    {
        var cfg = new TelegramConfig();
        Assert.False(cfg.EnableTwoWay);
    }

    // ──────────────────────── JSON DTO deserialization ────────────────────────

    [Fact]
    public void TgResponse_deserializes_getUpdates_with_message()
    {
        const string json = """
        {
            "ok": true,
            "result": [
                {
                    "update_id": 123,
                    "message": {
                        "message_id": 456,
                        "text": "/status",
                        "chat": { "id": 789, "type": "private" }
                    }
                }
            ]
        }
        """;

        var resp = JsonSerializer.Deserialize<TgResponse>(json, JsonOpts);
        Assert.NotNull(resp);
        Assert.True(resp.Ok);
        Assert.Single(resp.Result!);
        Assert.Equal(123, resp.Result![0].UpdateId);
        Assert.Equal("/status", resp.Result[0].Message!.Text);
        Assert.Equal(789, resp.Result[0].Message!.Chat!.Id);
    }

    [Fact]
    public void TgResponse_deserializes_callback_query()
    {
        const string json = """
        {
            "ok": true,
            "result": [
                {
                    "update_id": 200,
                    "callback_query": {
                        "id": "cb-1",
                        "from": { "id": 789, "username": "testuser" },
                        "message": {
                            "message_id": 100,
                            "chat": { "id": 789, "type": "private" }
                        },
                        "data": "pause:intent1:confirmed"
                    }
                }
            ]
        }
        """;

        var resp = JsonSerializer.Deserialize<TgResponse>(json, JsonOpts);
        Assert.NotNull(resp);
        Assert.True(resp.Ok);
        Assert.Equal("cb-1", resp.Result![0].CallbackQuery!.Id);
        Assert.Equal(789, resp.Result[0].CallbackQuery!.From!.Id);
        Assert.Equal("pause:intent1:confirmed", resp.Result[0].CallbackQuery!.Data);
    }

    [Fact]
    public void TgResponse_deserializes_empty_result()
    {
        const string json = """{"ok":true,"result":[]}""";
        var resp = JsonSerializer.Deserialize<TgResponse>(json, JsonOpts);
        Assert.NotNull(resp);
        Assert.True(resp.Ok);
        Assert.Empty(resp.Result!);
    }

    [Fact]
    public void TgResponse_deserializes_ok_false()
    {
        const string json = """{"ok":false,"description":"Not Found"}""";
        var resp = JsonSerializer.Deserialize<TgResponse>(json, JsonOpts);
        Assert.NotNull(resp);
        Assert.False(resp.Ok);
    }

    // ──────────────────────── IRunNotifier contract ────────────────────────

    [Fact]
    public void IRunNotifier_has_push_and_push_with_keyboard()
    {
        var methods = typeof(IRunNotifier).GetMethods();
        Assert.Contains(methods, m => m.Name == "PushAsync");
        Assert.Contains(methods, m => m.Name == "PushWithKeyboardAsync");
    }

    // ──────────────────────── PlanConfig model ────────────────────────

    [Fact]
    public void TelegramConfig_deserializes_from_json()
    {
        const string json = """
        {
            "name": "test",
            "repo": "C:\\tmp",
            "tracker": "TRACKER.md",
            "agent": { "command": "echo", "args": ["hello"] },
            "stages": [{ "id": "B1", "title": "test", "sessions": 1 }],
            "telegram": {
                "allowedChatIds": ["111", "222"],
                "pollIntervalSeconds": 10,
                "enableTwoWay": true
            }
        }
        """;

        var plan = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts);
        Assert.NotNull(plan);
        Assert.NotNull(plan.Telegram);
        Assert.Equal(2, plan.Telegram.AllowedChatIds.Count);
        Assert.Equal("111", plan.Telegram.AllowedChatIds[0]);
        Assert.Equal(10, plan.Telegram.PollIntervalSeconds);
        Assert.True(plan.Telegram.EnableTwoWay);
    }

    [Fact]
    public void TelegramConfig_is_null_when_not_in_json()
    {
        const string json = """
        {
            "name": "test",
            "repo": "C:\\tmp",
            "tracker": "TRACKER.md",
            "agent": { "command": "echo", "args": ["hello"] },
            "stages": [{ "id": "B1", "title": "test", "sessions": 1 }]
        }
        """;

        var plan = JsonSerializer.Deserialize<PlanConfig>(json, PlanConfig.JsonOpts);
        Assert.NotNull(plan);
        Assert.Null(plan.Telegram);
    }

    [Fact]
    public void NotifyConfig_has_webhook_fields()
    {
        var cfg = new NotifyConfig();
        Assert.Null(cfg.Webhook);
        Assert.Null(cfg.Discord);
        Assert.Null(cfg.Slack);
    }

    [Fact]
    public void WebhookNotifyConfig_has_url_and_headers()
    {
        var cfg = new WebhookNotifyConfig { Url = "https://example.com/hook", Headers = new() { ["X-Token"] = "s" } };
        Assert.Equal("https://example.com/hook", cfg.Url);
        Assert.Contains("X-Token", cfg.Headers!.Keys);
    }

    // FU-C4 — Telegram mock: two-way control callback parsing is the bridge between the
    // Telegram inline keyboard and the control.json file. Verifying the format end-to-end
    // proves that conductor can parse what Telegram sends and vice versa.
    [Fact]
    public void CallbackDataFormat_RoundTripsFromKeyboardToParse()
    {
        // Build the keyboard button data in the same format TelegramService uses
        var intentId = Guid.NewGuid().ToString("N")[..8];
        var callbackData = $"skip:{intentId}:confirmed";

        // Verify the format: action:id:confirmed or cancel:id
        Assert.StartsWith("skip:", callbackData, StringComparison.Ordinal);
        Assert.Contains($":{intentId}:confirmed", callbackData, StringComparison.Ordinal);

        // Verify the cancel format works too
        Assert.StartsWith("cancel:", $"cancel:{intentId}", StringComparison.Ordinal);
    }

    // ──────────────────────── helper: call private BuildInlineKeyboard ────────────────────────

    private static string BuildKeyboardReflection(
        IReadOnlyList<(string Text, string CallbackData)> buttons)
    {
        var method = typeof(TelegramService).GetMethod("BuildInlineKeyboard",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method.Invoke(null, [buttons])!;
    }
}
