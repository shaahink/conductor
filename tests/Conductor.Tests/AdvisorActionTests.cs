using Conductor.Core;

namespace Conductor.Tests;

public class AdvisorActionTests
{
    [Theory]
    [InlineData("blockretry", AdvisorAction.BlockRetry)]
    [InlineData("block_retry", AdvisorAction.BlockRetry)]
    [InlineData("resetbudget", AdvisorAction.ResetBudget)]
    [InlineData("reset_budget", AdvisorAction.ResetBudget)]
    [InlineData("needshuman", AdvisorAction.NeedsHuman)]
    [InlineData("needs_human", AdvisorAction.NeedsHuman)]
    [InlineData("human", AdvisorAction.NeedsHuman)]
    [InlineData("applyfix", AdvisorAction.ApplyFix)]
    [InlineData("apply_fix", AdvisorAction.ApplyFix)]
    [InlineData("rerungates", AdvisorAction.RerunGates)]
    [InlineData("rerun_gates", AdvisorAction.RerunGates)]
    [InlineData("retry", AdvisorAction.Retry)]
    [InlineData("resume", AdvisorAction.Resume)]
    [InlineData("skip", AdvisorAction.Skip)]
    public void TryParseAction_parses_known_actions(string input, AdvisorAction expected)
    {
        var result = Advisor.TryParseAction(input);
        Assert.NotNull(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("invalid_action")]
    [InlineData("blocked")]
    [InlineData("fix")]
    public void TryParseAction_returns_null_for_unknown(string input)
    {
        var result = Advisor.TryParseAction(input);
        Assert.Null(result);
    }

    [Fact]
    public void AdvisorVerdict_uses_enum()
    {
        var verdict = new AdvisorVerdict(AdvisorAction.BlockRetry, "stall pattern detected");
        Assert.Equal(AdvisorAction.BlockRetry, verdict.Action);
        Assert.Equal("stall pattern detected", verdict.Reason);
    }

    [Fact]
    public void All_enum_values_are_parseable()
    {
        foreach (AdvisorAction val in Enum.GetValues<AdvisorAction>())
        {
            var name = val.ToString().ToLowerInvariant();
            var result = Advisor.TryParseAction(name);
            Assert.NotNull(result);
            Assert.Equal(val, result);
        }
    }

    // --- G1.1: envelope unwrapping shared by verdict consults and plan-import asks ---

    [Fact]
    public void UnwrapEnvelope_json_unwraps_claude_result_wrapper()
    {
        var text = """{"result":"the actual answer","total_cost_usd":0.01}""";
        Assert.Equal("the actual answer", Advisor.UnwrapEnvelope(text, "json"));
    }

    [Fact]
    public void UnwrapEnvelope_stream_json_takes_the_result_line()
    {
        var ndjson =
            """{"type":"system","subtype":"init"}""" + "\n" +
            """{"type":"assistant","message":{}}""" + "\n" +
            """{"type":"result","subtype":"success","result":"{\"stages\":[],\"gates\":[]}"}""";
        Assert.Equal("""{"stages":[],"gates":[]}""", Advisor.UnwrapEnvelope(ndjson, "stream-json"));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("json")]       // malformed envelope falls through raw
    [InlineData("stream-json")] // no result line falls through raw
    public void UnwrapEnvelope_passes_through_raw_text(string kind)
    {
        Assert.Equal("plain answer", Advisor.UnwrapEnvelope("plain answer", kind));
    }
}
