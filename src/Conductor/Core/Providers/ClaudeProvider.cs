using System.Text.Json;

namespace Conductor.Core.Providers;

/// <summary>
/// Adapter for claude <c>-p --output-format stream-json</c> (today's <c>stream-json</c> mode). Parses
/// <c>system</c> / <c>assistant</c> (text + tool_use blocks) / <c>result</c> envelopes, reading cost +
/// turns from the terminal <c>result</c> event. Extracted verbatim from <c>AgentSession.ParseClaude</c>
/// (B2.4); behaviour is unchanged.
/// </summary>
public sealed class ClaudeProvider : IAgentProvider
{
    public string Name => "claude";

    public bool DetectsUsageLimit(string evidence) => ProviderText.DetectsUsageLimit(evidence);

    public void ParseLine(string line, AgentStreamState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var t = (line ?? "").TrimStart();
        if (!t.StartsWith('{')) { state.Emit("raw", ProviderText.Trunc(line ?? "", 220)); return; }

        try
        {
            using var doc = JsonDocument.Parse(t);
            Parse(doc.RootElement, line!, state);
        }
        catch (JsonException)
        {
            state.Emit("raw", ProviderText.Trunc(line ?? "", 180));
        }
    }

    private static void Parse(JsonElement root, string line, AgentStreamState state)
    {
        var type = root.TryGetProperty("type", out var ty) ? ty.GetString() : null;
        switch (type)
        {
            case "system":
                state.Emit("system", root.TryGetProperty("subtype", out var st) ? st.GetString() ?? "system" : "system");
                break;
            case "assistant":
                if (root.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in content.EnumerateArray())
                    {
                        var bt = block.TryGetProperty("type", out var b) ? b.GetString() : null;
                        if (bt == "text" && block.TryGetProperty("text", out var txt))
                        {
                            var s = (txt.GetString() ?? "").Trim();
                            if (s.Length > 0) state.Emit("text", ProviderText.Trunc(s, 220));
                        }
                        else if (bt == "tool_use")
                        {
                            var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "tool" : "tool";
                            var input = block.TryGetProperty("input", out var inp) ? ProviderText.Trunc(inp.GetRawText(), 150) : "";
                            state.Emit("tool", $"{name} {input}");
                        }
                    }
                }
                break;
            case "result":
                if (root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True) state.ResultIsError = true;
                if (root.TryGetProperty("subtype", out var sub) && (sub.GetString() ?? "").StartsWith("error", StringComparison.Ordinal)) state.ResultIsError = true;
                if (root.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.String) state.ResultText = res.GetString();
                if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) state.CostUsd = c.GetDecimal();
                if (root.TryGetProperty("num_turns", out var nt) && nt.ValueKind == JsonValueKind.Number) state.NumTurns = nt.GetInt32();
                state.Emit("result", state.ResultIsError ? "ERROR result: " + ProviderText.Trunc(state.ResultText ?? "", 160) : "result received");
                break;
            default:
                state.Emit("raw", ProviderText.Trunc(line, 180));
                break;
        }
    }
}
