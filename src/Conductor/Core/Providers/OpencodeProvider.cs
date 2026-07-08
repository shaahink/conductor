using System.Text.Json;

namespace Conductor.Core.Providers;

/// <summary>
/// Adapter for opencode <c>run --format json</c> nd-JSON (today's <c>opencode-json</c> mode). Parses
/// <c>text</c> / <c>reasoning</c> / <c>tool_use</c> / <c>step_finish</c> / <c>error</c> parts, folding
/// cost + token deltas per <c>step_finish</c>. Extracted verbatim from <c>AgentSession.ParseOpencode</c>
/// (B2.4) so behaviour is unchanged; the session core no longer knows this wire format.
/// </summary>
public sealed class OpencodeProvider : IAgentProvider
{
    public string Name => "opencode";

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
        var part = root.TryGetProperty("part", out var p) ? p : default;
        switch (type)
        {
            case "text":
                if (part.TryGetProperty("text", out var txt))
                {
                    var s = (txt.GetString() ?? "").Trim();
                    if (s.Length > 0) { state.AppendResultLine(s); state.Emit("text", ProviderText.Trunc(s, 220)); }
                }
                break;
            case "reasoning":
                if (part.TryGetProperty("text", out var rtxt))
                {
                    // Push full reasoning text (no truncation) — the buffer dedups growing snapshots
                    // and the pop-out pager shows it in full; the live panel clips for display.
                    var s = (rtxt.GetString() ?? "").Trim();
                    if (s.Length > 0) state.Emit("thinking", s);
                }
                break;
            case "tool_use":
                var tool = part.TryGetProperty("tool", out var tn) ? tn.GetString() ?? "tool" : "tool";
                var detail = "";
                if (part.TryGetProperty("state", out var stt))
                {
                    if (stt.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                        detail = title.GetString() ?? "";
                    else if (stt.TryGetProperty("input", out var inp))
                        detail = ProviderText.Trunc(inp.GetRawText(), 150);
                }
                state.Emit("tool", $"{tool} {detail}".Trim());
                break;
            case "step_finish":
                if (part.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number)
                    state.CostUsd = (state.CostUsd ?? 0m) + c.GetDecimal();
                if (part.TryGetProperty("tokens", out var tk))
                {
                    if (tk.TryGetProperty("input", out var ti) && ti.ValueKind == JsonValueKind.Number) state.TokensInput = (state.TokensInput ?? 0) + ti.GetInt64();
                    if (tk.TryGetProperty("output", out var to) && to.ValueKind == JsonValueKind.Number) state.TokensOutput = (state.TokensOutput ?? 0) + to.GetInt64();
                    if (tk.TryGetProperty("reasoning", out var tr) && tr.ValueKind == JsonValueKind.Number) state.TokensReasoning = (state.TokensReasoning ?? 0) + tr.GetInt64();
                    if (tk.TryGetProperty("cache", out var ca) && ca.TryGetProperty("read", out var cr) && cr.ValueKind == JsonValueKind.Number) state.TokensCacheRead = (state.TokensCacheRead ?? 0) + cr.GetInt64();
                }
                state.NumTurns = (state.NumTurns ?? 0) + 1;
                break;
            case "error":
                state.ResultIsError = true;
                state.Emit("result", "ERROR: " + ProviderText.Trunc(root.GetRawText(), 200));
                break;
            case "step_start":
                break;
            default:
                state.Emit("raw", ProviderText.Trunc(line, 180));
                break;
        }
        state.ResultText = state.ResultBufferSnapshot();
    }
}
