using Conductor.Core.Providers;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// B2.4 — proves each <see cref="IAgentProvider"/> adapter parses its backend's real wire format into
/// the same <see cref="AgentStreamState"/> the Orchestrator reads back, and that adapter selection
/// (explicit name or legacy <c>output</c> inference) resolves correctly. The opencode/claude line
/// samples below are trimmed from actual captured <c>session-*.jsonl</c> streams, so a regression in
/// parsing (the F-2 coupling this checkpoint removes) is caught without spawning a real agent.
/// </summary>
public class AgentProviderTests
{
    private static (AgentStreamState State, List<(string Kind, string Text)> Events) NewState()
    {
        var events = new List<(string, string)>();
        var state = new AgentStreamState((k, t) => events.Add((k, t)));
        return (state, events);
    }

    [Fact]
    public void OpencodeAdapterFoldsTextThinkingToolAndTokenDeltas()
    {
        var provider = new OpencodeProvider();
        var (state, events) = NewState();

        // Trimmed from a real opencode `run --format json` stream (session-013.jsonl shape):
        // payloads nest under `part`; step_finish carries tokens; text feeds the result buffer.
        string[] lines =
        [
            """{"type":"step_start","part":{"id":"prt_1"}}""",
            """{"type":"reasoning","part":{"text":"Let me read the tracker first."}}""",
            """{"type":"tool_use","part":{"type":"tool","tool":"read","state":{"status":"completed","title":"CONDUCTOR-START.md"}}}""",
            """{"type":"step_finish","part":{"tokens":{"input":7850,"output":157,"reasoning":86,"cache":{"read":1200}},"cost":0.0123}}""",
            """{"type":"text","part":{"text":"SESSION-RESULT: delivered."}}""",
        ];
        foreach (var line in lines) provider.ParseLine(line, state);

        Assert.Equal("opencode", provider.Name);
        Assert.Contains(events, e => e.Kind == "thinking" && e.Text == "Let me read the tracker first.");
        Assert.Contains(events, e => e.Kind == "tool" && e.Text == "read CONDUCTOR-START.md");
        Assert.Contains(events, e => e.Kind == "text" && e.Text.Contains("SESSION-RESULT", StringComparison.Ordinal));
        Assert.Equal(7850, state.TokensInput);
        Assert.Equal(157, state.TokensOutput);
        Assert.Equal(86, state.TokensReasoning);
        Assert.Equal(1200, state.TokensCacheRead);
        Assert.Equal(0.0123m, state.CostUsd);
        Assert.Equal(1, state.NumTurns);
        Assert.Contains("SESSION-RESULT: delivered.", state.ResultText);
    }

    [Fact]
    public void OpencodeAdapterAccumulatesTokenDeltasAcrossSteps()
    {
        var provider = new OpencodeProvider();
        var (state, _) = NewState();

        provider.ParseLine("""{"type":"step_finish","part":{"tokens":{"input":100,"output":50},"cost":0.001}}""", state);
        provider.ParseLine("""{"type":"step_finish","part":{"tokens":{"input":40,"output":10},"cost":0.002}}""", state);

        Assert.Equal(140, state.TokensInput);   // deltas summed, not overwritten
        Assert.Equal(60, state.TokensOutput);
        Assert.Equal(0.003m, state.CostUsd);
        Assert.Equal(2, state.NumTurns);
    }

    [Fact]
    public void OpencodeAdapterFlagsErrorEvent()
    {
        var provider = new OpencodeProvider();
        var (state, events) = NewState();

        provider.ParseLine("""{"type":"error","part":{"text":"usage limit reached"}}""", state);

        Assert.True(state.ResultIsError);
        Assert.Contains(events, e => e.Kind == "result" && e.Text.StartsWith("ERROR:", StringComparison.Ordinal));
    }

    [Fact]
    public void ClaudeAdapterParsesAssistantBlocksAndResultEnvelope()
    {
        var provider = new ClaudeProvider();
        var (state, events) = NewState();

        // Trimmed from a claude `-p --output-format stream-json` stream.
        string[] lines =
        [
            """{"type":"system","subtype":"init"}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Working on it."},{"type":"tool_use","name":"Edit","input":{"path":"a.cs"}}]}}""",
            """{"type":"result","subtype":"success","result":"done","total_cost_usd":0.25,"num_turns":3}""",
        ];
        foreach (var line in lines) provider.ParseLine(line, state);

        Assert.Equal("claude", provider.Name);
        Assert.Contains(events, e => e.Kind == "system" && e.Text == "init");
        Assert.Contains(events, e => e.Kind == "text" && e.Text == "Working on it.");
        Assert.Contains(events, e => e.Kind == "tool" && e.Text.StartsWith("Edit", StringComparison.Ordinal));
        Assert.False(state.ResultIsError);
        Assert.Equal("done", state.ResultText);
        Assert.Equal(0.25m, state.CostUsd);
        Assert.Equal(3, state.NumTurns);
    }

    // Bug #5: ClaudeProvider read total_cost_usd but never the usage object, so every claude-native
    // session recorded 0 tokens (costs table all-zero while cost_usd was populated). That silently
    // disabled limits.maxSessionTokens — SessionRecord.TokensTotal was always 0, so a token cap could
    // never trigger — as well as HealthMetrics context saturation and the Face's token surfaces.
    // Shape and numbers are lifted from a real session log (.conductor/logs/session-006.jsonl).
    [Fact]
    public void ClaudeAdapterReadsSessionTokenUsageFromResultEnvelope()
    {
        var provider = new ClaudeProvider();
        var (state, _) = NewState();

        provider.ParseLine(
            """
            {"type":"result","subtype":"success","result":"done","total_cost_usd":2.6979,"num_turns":43,
             "usage":{"input_tokens":641,"cache_creation_input_tokens":96444,
             "cache_read_input_tokens":2327709,"output_tokens":22543,"service_tier":"standard"}}
            """, state);

        // input_tokens + cache_creation_input_tokens: TokensTotal sums the four buckets to gate
        // maxSessionTokens, and there is no cache-write bucket to put creation in.
        Assert.Equal(641 + 96444, state.TokensInput);
        Assert.Equal(22543, state.TokensOutput);
        Assert.Equal(2327709, state.TokensCacheRead);
        // claude reports no reasoning/thinking bucket — it is inside output_tokens. Don't invent one.
        Assert.Null(state.TokensReasoning);
    }

    // The reason usage is read from `result` and not accumulated per line: claude emits the SAME
    // message once per content block (thinking block + text block = two lines, one message.id, one
    // identical usage). Summing per assistant line overcounted by 3-4x on real logs.
    [Fact]
    public void ClaudeAdapterDoesNotAccumulateRepeatedAssistantUsage()
    {
        var provider = new ClaudeProvider();
        var (state, _) = NewState();

        const string assistant =
            """{"type":"assistant","message":{"id":"msg_01","content":[{"type":"text","text":"hi"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":11374,"cache_read_input_tokens":12528,"output_tokens":1}}}""";
        provider.ParseLine(assistant, state);
        provider.ParseLine(assistant, state); // same message id, re-emitted for its next content block
        provider.ParseLine(assistant, state);

        Assert.Null(state.TokensInput);
        Assert.Null(state.TokensOutput);
        Assert.Null(state.TokensCacheRead);

        provider.ParseLine(
            """{"type":"result","subtype":"success","usage":{"input_tokens":2,"cache_creation_input_tokens":11374,"cache_read_input_tokens":12528,"output_tokens":1}}""",
            state);

        Assert.Equal(2 + 11374, state.TokensInput);
        Assert.Equal(1, state.TokensOutput);
        Assert.Equal(12528, state.TokensCacheRead);
    }

    // A result envelope with no usage (older CLI, or an error result) must not fabricate zeros —
    // null means "not reported", which is what RecordCost's `?? 0` and the Face both key off.
    [Fact]
    public void ClaudeAdapterLeavesTokensUnsetWhenResultHasNoUsage()
    {
        var provider = new ClaudeProvider();
        var (state, _) = NewState();

        provider.ParseLine("""{"type":"result","subtype":"success","result":"done","total_cost_usd":0.25}""", state);

        Assert.Equal(0.25m, state.CostUsd);
        Assert.Null(state.TokensInput);
        Assert.Null(state.TokensOutput);
        Assert.Null(state.TokensCacheRead);
    }

    [Fact]
    public void ClaudeAdapterFlagsErrorResult()
    {
        var provider = new ClaudeProvider();
        var (state, _) = NewState();

        provider.ParseLine("""{"type":"result","subtype":"error_during_execution","is_error":true,"result":"boom"}""", state);

        Assert.True(state.ResultIsError);
        Assert.Equal("boom", state.ResultText);
    }

    [Fact]
    public void GenericTextAdapterEmitsEachLineAsRaw()
    {
        var provider = new GenericTextProvider();
        var (state, events) = NewState();

        provider.ParseLine("plain line of output", state);
        provider.ParseLine("""{"looks":"like json but text mode ignores structure"}""", state);

        Assert.Equal("text", provider.Name);
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("raw", e.Kind));
        Assert.Null(state.TokensInput); // no structured usage in text mode
    }

    [Fact]
    public void NonJsonLineFallsBackToRawInJsonAdapters()
    {
        var (s1, e1) = NewState();
        new OpencodeProvider().ParseLine("[claude-mem] plugin loading", s1);
        Assert.Contains(e1, e => e.Kind == "raw");

        var (s2, e2) = NewState();
        new ClaudeProvider().ParseLine("not json at all", s2);
        Assert.Contains(e2, e => e.Kind == "raw");
    }

    [Theory]
    [InlineData("opencode", null, "opencode")]
    [InlineData("claude", null, "claude")]
    [InlineData("text", null, "text")]
    [InlineData(null, "opencode-json", "opencode")]   // legacy output inference
    [InlineData(null, "stream-json", "claude")]
    [InlineData(null, "anything-else", "text")]
    [InlineData("", "opencode-json", "opencode")]     // empty provider → infer from output
    public void FactorySelectsAdapterByProviderThenOutput(string? provider, string? output, string expected)
    {
        var cfg = new AgentConfig { Provider = provider, Output = output ?? "stream-json" };
        Assert.Equal(expected, AgentProviderFactory.Create(cfg).Name);
    }

    [Fact]
    public void UsageLimitDetectionMatchesKnownBackendPhrasing()
    {
        var provider = new OpencodeProvider();
        Assert.True(provider.DetectsUsageLimit("Error: usage limit reached, try later"));
        Assert.True(provider.DetectsUsageLimit("HTTP 429 too many requests"));
        Assert.False(provider.DetectsUsageLimit("compilation succeeded, all green"));
    }

    // ----------------------------------------------- B2.6 token-delta emission

    [Fact]
    public void TokenDeltaEmittedOnStepFinishViaDelegate()
    {
        var deltas = new List<(long Inp, long Out, long R, long C, decimal Cost)>();
        var state = new AgentStreamState((k, t) => { }, (i, o, r, c, cost) => deltas.Add((i, o, r, c, cost)));
        var provider = new OpencodeProvider();

        provider.ParseLine("""{"type":"step_finish","part":{"tokens":{"input":800,"output":200,"reasoning":50,"cache":{"read":1500}},"cost":0.005}}""", state);

        Assert.Single(deltas);
        Assert.Equal((800, 200, 50, 1500, 0.005m), deltas[0]);
    }

    [Fact]
    public void OpencodeAdapterEmitsTokenDeltaPerStepFinish()
    {
        var deltas = new List<(long Inp, long Out, long R, long C, decimal Cost)>();
        var state = new AgentStreamState((k, t) => { }, (i, o, r, c, cost) => deltas.Add((i, o, r, c, cost)));
        var provider = new OpencodeProvider();

        provider.ParseLine("""{"type":"step_finish","part":{"tokens":{"input":100,"output":50},"cost":0.001}}""", state);
        provider.ParseLine("""{"type":"step_finish","part":{"tokens":{"input":40,"output":10},"cost":0.002}}""", state);

        Assert.Equal(2, deltas.Count);                                // one per step_finish
        Assert.Equal((100, 50, 0, 0, 0.001m), deltas[0]);
        Assert.Equal((40, 10, 0, 0, 0.002m), deltas[1]);
        Assert.Equal(140, state.TokensInput);                         // accumulated totals still hold
        Assert.Equal(0.003m, state.CostUsd);
    }

    /// <summary>A JSON object whose payload is NOT nested under <c>part</c> used to leave <c>part</c> as the
    /// default (Undefined) JsonElement, and <c>TryGetProperty</c> on a non-object element throws
    /// <see cref="InvalidOperationException"/> — which the parser's <c>catch (JsonException)</c> did not
    /// catch. Because ParseLine runs on the agent's stdout callback thread, that escaped as an unhandled
    /// exception and killed the whole conductor process. Any agent line shape must parse or degrade, never throw.</summary>
    [Theory]
    [InlineData("""{"type":"text","text":"flat, no part wrapper"}""")]
    [InlineData("""{"type":"reasoning","text":"flat reasoning"}""")]
    [InlineData("""{"type":"tool_use","tool":"read"}""")]
    [InlineData("""{"type":"step_finish"}""")]
    [InlineData("""{"type":"text","part":"a string, not an object"}""")]
    [InlineData("""{"type":"step_finish","part":{"tokens":42}}""")]
    [InlineData("""{"type":"tool_use","part":{"state":"running"}}""")]
    [InlineData("""{"type":"error"}""")]
    [InlineData("""{"no_type":true}""")]
    [InlineData("""{}""")]
    public void OpencodeAdapterNeverThrowsOnUnexpectedLineShape(string line)
    {
        var provider = new OpencodeProvider();
        var (state, _) = NewState();

        var ex = Record.Exception(() => provider.ParseLine(line, state));

        Assert.Null(ex);
    }

    /// <summary>The flat (un-nested) shape must still yield its text rather than being silently dropped —
    /// the crash guard falls back to the root element when there is no <c>part</c> wrapper.</summary>
    [Fact]
    public void OpencodeAdapterReadsTextFromRootWhenPartWrapperIsAbsent()
    {
        var provider = new OpencodeProvider();
        var (state, events) = NewState();

        provider.ParseLine("""{"type":"text","text":"SESSION-RESULT: landed T1.1"}""", state);

        Assert.Contains(events, e => e.Kind == "text" && e.Text.Contains("landed T1.1", StringComparison.Ordinal));
        Assert.Contains("landed T1.1", state.ResultText ?? "", StringComparison.Ordinal);
    }

    // --- U3.3: ResolveName, the provider's NAME for anything that has to report it ---------------

    [Theory]
    // Unset Provider is the common case — the plan says only `output`, and the provider is inferred.
    [InlineData(null, "stream-json", "claude")]
    [InlineData(null, "opencode-json", "opencode")]
    [InlineData(null, "text", "text")]
    [InlineData(null, "", "text")]
    // Explicit Provider wins over output, including when they disagree.
    [InlineData("opencode", "stream-json", "opencode")]
    [InlineData("claude", "text", "claude")]
    // Aliases and casing canonicalise, so the wire never carries two spellings of one provider.
    [InlineData("stream-json", "text", "claude")]
    [InlineData("opencode-json", "text", "opencode")]
    [InlineData("CLAUDE", "text", "claude")]
    [InlineData("  opencode  ", "text", "opencode")]
    // An unknown name degrades to the generic text adapter rather than throwing.
    [InlineData("dracula", "stream-json", "text")]
    public void ResolveName_canonicalises_provider_and_infers_from_output(string? provider, string output, string expected)
    {
        var cfg = new AgentConfig { Provider = provider, Output = output };
        Assert.Equal(expected, AgentProviderFactory.ResolveName(cfg));
    }

    /// <summary>ResolveName exists so the control plane can NAME what Create builds. If the two ever
    /// disagreed, the Face would render one provider's conventions over another's wire format — so
    /// pin them against each other rather than trusting that Create delegates.</summary>
    [Theory]
    [InlineData(null, "stream-json", typeof(ClaudeProvider))]
    [InlineData(null, "opencode-json", typeof(OpencodeProvider))]
    [InlineData(null, "text", typeof(GenericTextProvider))]
    [InlineData("opencode", "stream-json", typeof(OpencodeProvider))]
    [InlineData("stream-json", "text", typeof(ClaudeProvider))]
    [InlineData("dracula", "stream-json", typeof(GenericTextProvider))]
    public void ResolveName_always_names_the_adapter_Create_builds(string? provider, string output, Type expected)
    {
        var cfg = new AgentConfig { Provider = provider, Output = output };
        var built = AgentProviderFactory.Create(cfg);
        Assert.IsType(expected, built);

        var namedType = AgentProviderFactory.ResolveName(cfg) switch
        {
            "claude" => typeof(ClaudeProvider),
            "opencode" => typeof(OpencodeProvider),
            _ => typeof(GenericTextProvider),
        };
        Assert.Equal(built.GetType(), namedType);
    }

    [Fact]
    public void ResolveName_rejects_null_config()
        => Assert.Throws<ArgumentNullException>(() => AgentProviderFactory.ResolveName(null!));
}
