using System.Text.Json;
using Conductor.Core.Events;

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

    /// <summary>Property lookup that tolerates a non-object element. <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/>
    /// throws <see cref="InvalidOperationException"/> on any element that isn't an object — including the
    /// <c>default</c> (<see cref="JsonValueKind.Undefined"/>) element produced when an expected property is
    /// absent. An agent line is untrusted input, so a shape we don't expect must degrade to "no value here",
    /// never throw: this parser runs on the process's stdout callback thread, where an exception is fatal.</summary>
    private static bool TryProp(JsonElement el, string name, out JsonElement value)
    {
        if (el.ValueKind != JsonValueKind.Object) { value = default; return false; }
        return el.TryGetProperty(name, out value);
    }

    private static void Parse(JsonElement root, string line, AgentStreamState state)
    {
        var type = TryProp(root, "type", out var ty) ? ty.GetString() : null;
        // opencode nests payload under "part". Flat variants (and hand-rolled/fake agents) put the
        // fields on the root instead — fall back to root so those parse rather than silently drop.
        var part = TryProp(root, "part", out var p) ? p : root;
        switch (type)
        {
            case "text":
                if (TryProp(part, "text", out var txt))
                {
                    var s = (txt.GetString() ?? "").Trim();
                    if (s.Length > 0) { state.AppendResultLine(s); state.Emit("text", ProviderText.Trunc(s, 220)); }
                }
                break;
            case "reasoning":
                if (TryProp(part, "text", out var rtxt))
                {
                    // Push full reasoning text (no truncation) — the buffer dedups growing snapshots
                    // and the pop-out pager shows it in full; the live panel clips for display.
                    var s = (rtxt.GetString() ?? "").Trim();
                    if (s.Length > 0) state.Emit("thinking", s);
                }
                break;
            case "tool_use":
                // SC7.1: opencode's `state.input` is the same shape as claude's `tool_use.input` — an
                // object of arguments — so it goes through the SAME extractor. One vocabulary for both
                // wires, or the Face and the verdict would learn two.
                var tool = TryProp(part, "tool", out var tn) ? tn.GetString() ?? "tool" : "tool";
                var hasState = TryProp(part, "state", out var stt);
                if (hasState && TryProp(stt, "input", out var inp) && inp.ValueKind == JsonValueKind.Object)
                {
                    var call = ToolEventExtractor.Extract(tool, inp);
                    state.EmitTool(call, ToolEventExtractor.Render(call));
                    break;
                }
                // No argument object on the wire: opencode's own rendered `title` is the best structure
                // available, kept as a purpose field rather than thrown away.
                var title = hasState && TryProp(stt, "title", out var tt) && tt.ValueKind == JsonValueKind.String
                    ? (tt.GetString() ?? "").Trim() : "";
                var titled = new ToolCall(tool, new Dictionary<string, string>(StringComparer.Ordinal));
                if (title.Length > 0) titled.Fields["purpose"] = ProviderText.Trunc(title, ToolEventExtractor.MaxFieldChars);
                state.EmitTool(titled, $"{tool} {title}".Trim());
                break;
            case "step_finish":
                long di = 0, dout = 0, dr = 0, dc = 0;
                decimal dcost = 0;
                if (TryProp(part, "cost", out var c) && c.ValueKind == JsonValueKind.Number)
                {
                    dcost = c.GetDecimal();
                    state.CostUsd = (state.CostUsd ?? 0m) + dcost;
                }
                if (TryProp(part, "tokens", out var tk))
                {
                    if (TryProp(tk, "input", out var ti) && ti.ValueKind == JsonValueKind.Number) { di = ti.GetInt64(); state.TokensInput = (state.TokensInput ?? 0) + di; }
                    if (TryProp(tk, "output", out var to) && to.ValueKind == JsonValueKind.Number) { dout = to.GetInt64(); state.TokensOutput = (state.TokensOutput ?? 0) + dout; }
                    if (TryProp(tk, "reasoning", out var tr) && tr.ValueKind == JsonValueKind.Number) { dr = tr.GetInt64(); state.TokensReasoning = (state.TokensReasoning ?? 0) + dr; }
                    if (TryProp(tk, "cache", out var ca) && TryProp(ca, "read", out var cr) && cr.ValueKind == JsonValueKind.Number) { dc = cr.GetInt64(); state.TokensCacheRead = (state.TokensCacheRead ?? 0) + dc; }
                }
                state.NumTurns = (state.NumTurns ?? 0) + 1;
                state.EmitTokenDelta(di, dout, dr, dc, dcost);
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
