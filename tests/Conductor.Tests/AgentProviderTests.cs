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
}
