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

    public bool DetectsAuthFailure(string evidence) => ProviderText.DetectsAuthFailure(evidence);

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
                var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() ?? "system" : "system";
                // W3.2: the system envelope carries the API's own verdict on a failed call
                // (`{"subtype":"api_retry","error_status":401,"error":"authentication_failed"}`).
                // Flattening it to the bare subtype threw that away — session #13 retried a dead
                // OAuth token ten times and the run only ever saw the word "api_retry".
                var errStatus = root.TryGetProperty("error_status", out var es) && es.ValueKind == JsonValueKind.Number
                    ? es.GetInt32() : (int?)null;
                var errText = root.TryGetProperty("error", out var er) && er.ValueKind == JsonValueKind.String
                    ? er.GetString() : null;
                if (errStatus is null && errText is null) { state.Emit("system", subtype); break; }

                var detail = $"{subtype} — {(errStatus is { } code ? $"HTTP {code}" : "error")}" +
                             (string.IsNullOrEmpty(errText) ? "" : $" {errText}");
                state.Emit("system", detail);
                if (errStatus == 401 || ProviderText.DetectsAuthFailure(errText ?? ""))
                    state.AuthFailure ??= detail;
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
                ReadUsage(root, state);
                state.Emit("result", state.ResultIsError ? "ERROR result: " + ProviderText.Trunc(state.ResultText ?? "", 160) : "result received");
                break;
            default:
                state.Emit("raw", ProviderText.Trunc(line, 180));
                break;
        }
    }

    /// <summary>
    /// Reads the session's token usage off the terminal <c>result</c> envelope — the same place
    /// <c>total_cost_usd</c> comes from, and the CLI's own authoritative session total.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT summed from <c>assistant</c> messages: claude re-emits one message once per
    /// content block (a thinking block and a text block of the same <c>message.id</c> arrive as two
    /// lines carrying the SAME usage), so accumulating per line overcounts by 3-4x. The result
    /// envelope is emitted exactly once.
    /// <para><c>cache_creation_input_tokens</c> folds into <see cref="AgentStreamState.TokensInput"/>:
    /// the state has four buckets and no cache-write bucket, and
    /// <c>SessionRecord.TokensTotal</c> sums all four to gate <c>limits.maxSessionTokens</c>. Dropping
    /// cache-creation would understate the total the rollover cap is measured against. Both are input
    /// billed at write/fresh rates, as distinct from <c>cache_read_input_tokens</c>.</para>
    /// <para>Reasoning tokens are left unset: claude's usage has no thinking/reasoning field — that
    /// spend is already inside <c>output_tokens</c>. Synthesising one would double-count it in
    /// TokensTotal and invent a number the wire never reported.</para>
    /// </remarks>
    private static void ReadUsage(JsonElement root, AgentStreamState state)
    {
        if (!root.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return;

        var input = Num(u, "input_tokens") + Num(u, "cache_creation_input_tokens");
        if (input > 0) state.TokensInput = input;
        if (Num(u, "output_tokens") is var output && output > 0) state.TokensOutput = output;
        if (Num(u, "cache_read_input_tokens") is var cacheRead && cacheRead > 0) state.TokensCacheRead = cacheRead;
    }

    private static long Num(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
