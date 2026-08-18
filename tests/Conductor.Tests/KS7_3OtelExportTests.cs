using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.Providers;
using Conductor.Core.Telemetry;
using Xunit;

namespace Conductor.Tests;

/// <summary>
/// KS7.3 — the cost/usage half and the OTel half, measured rather than described.
/// </summary>
/// <remarks>
/// Two claims are load-bearing and both are asserted here rather than argued in a doc comment:
/// <list type="number">
/// <item>The wire reports a FOUR-way split and conductor now keeps all four. Before this checkpoint
/// <c>cache_creation_input_tokens</c> was added into <c>TokensInput</c> and lost its name there.</item>
/// <item>The per-turn context curve the exporter puts on a session span is K4.1's derivation — the same
/// number <c>LiveMetrics.ContextForSession</c> produces, not a second implementation that agrees today
/// and drifts tomorrow.</item>
/// </list>
/// </remarks>
public class KS7_3OtelExportTests
{
    // The usage shape is lifted verbatim from a live claude 2.1.235 stream captured for this checkpoint
    // (see the evidence file): input 2, cache_creation 9306, cache_read 20673, output 4.
    private const string LiveShapedTurn =
        """{"type":"assistant","message":{"id":"msg_live","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":9306,"cache_read_input_tokens":20673,"output_tokens":4}}}""";

    // ─────────────────────────── the cache split, per turn ───────────────────────────

    [Fact]
    public void ClaudeStreamKeepsAllFourUsageBucketsAndTheTotalIsUnchanged()
    {
        var deltas = new List<(long Input, long Output, long Reasoning, long CacheRead, long CacheWrite)>();
        var state = new AgentStreamState((_, _) => { },
            (i, o, r, c, cw, _) => deltas.Add((i, o, r, c, cw)));

        new ClaudeProvider().ParseLine(LiveShapedTurn, state);

        var d = Assert.Single(deltas);
        Assert.Equal(9306, d.CacheWrite);                 // the half that used to vanish into Input
        Assert.Equal(20673, d.CacheRead);
        Assert.Equal(4, d.Output);
        // Input KEEPS its shipped meaning: fresh + cache-creation. Every archived total and the rollover
        // cap were written against it, so the split is recovered by naming the part, not by moving it.
        Assert.Equal(2 + 9306, d.Input);
        Assert.True(d.CacheWrite <= d.Input, "CacheWrite is a SUBSET of Input — adding both double-counts.");
        Assert.Equal(9306, state.TokensCacheWrite);
    }

    [Fact]
    public void AProviderThatReportsNoCacheWriteEmitsZeroRatherThanGuessing()
    {
        var deltas = new List<long>();
        var state = new AgentStreamState((_, _) => { }, (_, _, _, _, cw, _) => deltas.Add(cw));

        new OpencodeProvider().ParseLine(
            """{"type":"step_finish","part":{"tokens":{"input":800,"output":200,"cache":{"read":1500}},"cost":0.005}}""",
            state);

        Assert.Equal(0, Assert.Single(deltas));
        Assert.Null(state.TokensCacheWrite);
    }

    // ─────────────────────────── the trace shape ───────────────────────────

    [Fact]
    public void ARunBecomesARunStageSessionGateTreeUnderOneTrace()
    {
        var spans = OtelTrace.Build(Corpus());

        var root = Assert.Single(spans, s => s.Name == "conductor.run");
        Assert.Null(root.ParentSpanId);
        Assert.All(spans, s => Assert.Equal(root.TraceId, s.TraceId));

        var stage = Assert.Single(spans, s => s.Name == "stage KS7");
        Assert.Equal(root.SpanId, stage.ParentSpanId);

        var session = Assert.Single(spans, s => s.Name.StartsWith("chat ", StringComparison.Ordinal));
        Assert.Equal(stage.SpanId, session.ParentSpanId);
        Assert.Equal(3, session.Kind);                                  // CLIENT — it calls a provider

        var gate = Assert.Single(spans, s => s.Name == "gate build");
        Assert.Equal(session.SpanId, gate.ParentSpanId);                // gates hang off the session
        Assert.Equal(OtelStatus.Error, gate.Status);                    // this one failed

        var tool = Assert.Single(spans, s => s.Name == "execute_tool task_update");
        Assert.Equal(session.SpanId, tool.ParentSpanId);
    }

    [Fact]
    public void SessionSpanCarriesGenAiUsageWithBothHalvesOfTheCache()
    {
        var session = Assert.Single(OtelTrace.Build(Corpus()), s => s.Name.StartsWith("chat ", StringComparison.Ordinal));
        var a = session.Attributes.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal);

        Assert.Equal("anthropic", a["gen_ai.system"]);
        Assert.Equal("chat", a["gen_ai.operation.name"]);
        Assert.Equal("claude-opus-5", a["gen_ai.request.model"]);
        Assert.Equal(2L + 9306L + 900L, a["gen_ai.usage.input_tokens"]);           // two turns, cache-creation included
        Assert.Equal(30L + 4L, a["gen_ai.usage.output_tokens"]);
        Assert.Equal(20673L + 50000L, a["gen_ai.usage.cache_read_input_tokens"]);
        Assert.Equal(9306L, a["gen_ai.usage.cache_creation_input_tokens"]);
    }

    /// <summary>The exit the checkpoint names: the curve a collector renders IS K4.1's derivation.</summary>
    [Fact]
    public void PerTurnContextCurveOnTheSpanReconcilesWithK41sDerivation()
    {
        var corpus = Corpus();
        var expected = LiveMetrics.ContextForSession(corpus, 4);
        var session = Assert.Single(OtelTrace.Build(corpus), s => s.Name.StartsWith("chat ", StringComparison.Ordinal));
        var a = session.Attributes.ToDictionary(k => k.Key, v => v.Value, StringComparer.Ordinal);

        Assert.Equal(expected.HighWaterTokens, a["conductor.context.high_water_tokens"]);
        Assert.Equal(expected.MeanTurnTokens, a["conductor.context.mean_turn_tokens"]);
        Assert.Equal((long)expected.Turns, a["conductor.context.turns"]);

        // And the span EVENTS are the curve itself: one point per API call, each carrying the prompt
        // that call re-sent. Summing them back must reproduce the mean K4.1 reports.
        Assert.Equal(expected.Turns, session.Events.Count);
        var prompts = session.Events
            .Select(e => (long)e.Attributes.First(x => x.Key == "conductor.context.prompt_tokens").Value)
            .ToList();
        Assert.Equal(expected.HighWaterTokens, prompts.Max());
        Assert.Equal(expected.MeanTurnTokens, prompts.Sum() / prompts.Count);
    }

    [Fact]
    public void ExportingTheSameRunTwiceIsTheSameTraceNotTwo()
    {
        var first = OtelTrace.Build(Corpus());
        var second = OtelTrace.Build(Corpus());

        Assert.Equal(first[0].TraceId, second[0].TraceId);
        Assert.Equal(first.Select(s => s.SpanId), second.Select(s => s.SpanId));
        Assert.NotEqual(first[0].TraceId, OtelTrace.Build(Corpus("other-run"))[0].TraceId);
    }

    [Fact]
    public void AnEmptyLogExportsNothingRatherThanAnEmptyTrace() =>
        Assert.Empty(OtelTrace.Build(Array.Empty<ConductorEvent>()));

    // ─────────────────────────── the wire body ───────────────────────────

    [Fact]
    public void OtlpBodyQuotesEveryInt64AndUsesBase16Ids()
    {
        var spans = OtelTrace.Build(Corpus());
        using var doc = JsonDocument.Parse(OtlpJson.Request(spans, "conductor", "0.4.1"));

        var scopeSpan = doc.RootElement
            .GetProperty("resourceSpans")[0].GetProperty("scopeSpans")[0];
        var first = scopeSpan.GetProperty("spans")[0];

        // Nanosecond timestamps and token counts are past 2^53; a JSON number would silently round.
        Assert.Equal(JsonValueKind.String, first.GetProperty("startTimeUnixNano").ValueKind);
        Assert.Equal(JsonValueKind.String, first.GetProperty("endTimeUnixNano").ValueKind);
        Assert.Equal(32, first.GetProperty("traceId").GetString()!.Length);   // 16 bytes of base16
        Assert.Equal(16, first.GetProperty("spanId").GetString()!.Length);
        Assert.Matches("^[0-9a-f]+$", first.GetProperty("traceId").GetString()!);

        var intAttr = first.GetProperty("attributes").EnumerateArray()
            .First(x => x.GetProperty("key").GetString() == "gen_ai.usage.cache_read_input_tokens");
        Assert.Equal(JsonValueKind.String, intAttr.GetProperty("value").GetProperty("intValue").ValueKind);
    }

    [Fact]
    public void ASpanWhoseReconstructedStartPrecedesNothingIsNeverEmittedBackwards()
    {
        // A gate anchors its start backwards from its finish by DurationMs. A duration longer than the
        // run cannot produce end < start on the wire, whatever the log says.
        var log = new List<ConductorEvent>
        {
            new RunStarted { Seq = 1, Ts = At(0), RunId = "r", Plan = "p", Repo = "c:/x" },
            new GateFinished { Seq = 2, Ts = At(1), RunId = "r", Name = "slow", Passed = true, DurationMs = 99_999_999 },
        };

        using var doc = JsonDocument.Parse(OtlpJson.Request(OtelTrace.Build(log), "conductor", "0"));
        foreach (var s in doc.RootElement.GetProperty("resourceSpans")[0]
                     .GetProperty("scopeSpans")[0].GetProperty("spans").EnumerateArray())
        {
            var start = long.Parse(s.GetProperty("startTimeUnixNano").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var end = long.Parse(s.GetProperty("endTimeUnixNano").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(end >= start, $"{s.GetProperty("name").GetString()} ends before it starts");
        }
    }

    // ─────────────────────────── the corpus ───────────────────────────

    private static DateTimeOffset At(int minutes) =>
        new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    /// <summary>A minimal but complete run: one stage, one session, two turns, a failed gate, one tool
    /// call. Token figures are the live-measured ones so the numbers in the assertions are real.</summary>
    private static List<ConductorEvent> Corpus(string runId = "run-ks73") =>
    [
        new RunStarted { Seq = 1, Ts = At(0), RunId = runId, Plan = "karvansara-edge", Repo = "C:/code/conductor", Branch = "feat/karvansara-edge" },
        new StageEntered { Seq = 2, Ts = At(0), RunId = runId, StageId = "KS7", Title = "Platform catch-up" },
        new SessionStarted { Seq = 3, Ts = At(0), RunId = runId, SessionId = "4", Number = 4, StageId = "KS7", Kind = "work", Attempt = 1, Model = "claude-opus-5" },
        new TokenDelta { Seq = 4, Ts = At(1), RunId = runId, SessionId = "4", Input = 2 + 9306, Output = 4, CacheRead = 20673, CacheWrite = 9306 },
        new McpCallFinished { Seq = 5, Ts = At(1), RunId = runId, SessionId = "4", ToolName = "task_update", DurationMs = 12, Success = true },
        new TokenDelta { Seq = 6, Ts = At(2), RunId = runId, SessionId = "4", Input = 900, Output = 30, CacheRead = 50000, CacheWrite = 0 },
        new SessionFinished { Seq = 7, Ts = At(3), RunId = runId, SessionId = "4", Number = 4, StageId = "KS7", Outcome = "success", CostUsd = 1.25m },
        new GateFinished { Seq = 8, Ts = At(3), RunId = runId, SessionId = "4", Name = "build", Passed = false, ExitCode = 1, DurationMs = 4000 },
        new RunFinished { Seq = 9, Ts = At(4), RunId = runId, Status = "completed", Sessions = 1 },
    ];
}
