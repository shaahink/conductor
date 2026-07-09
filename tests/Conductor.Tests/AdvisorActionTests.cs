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
}
