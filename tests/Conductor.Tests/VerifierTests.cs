using Conductor.Core;

namespace Conductor.Tests;

public class VerifierTests
{
    [Fact]
    public void Parse_returns_verdict_from_valid_json()
    {
        var json = """{"score":85,"findings":["missing null check","race condition"],"verdict":"PASS"}""";

        var result = Verifier.Parse(json);

        Assert.NotNull(result);
        Assert.Equal(85, result.Score);
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains("missing null check", result.Findings);
        Assert.Contains("race condition", result.Findings);
        Assert.Equal("PASS", result.Verdict);
    }

    [Fact]
    public void Parse_handles_missing_findings_array()
    {
        var json = """{"score":72,"verdict":"FAIL"}""";

        var result = Verifier.Parse(json);

        Assert.NotNull(result);
        Assert.Equal(72, result.Score);
        Assert.Empty(result.Findings);
        Assert.Equal("FAIL", result.Verdict);
    }

    [Fact]
    public void Parse_returns_null_for_empty_input()
    {
        var result = Verifier.Parse("");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_returns_null_for_null_input()
    {
        var result = Verifier.Parse(null!);
        Assert.Null(result);
    }

    [Fact]
    public void Parse_returns_null_for_non_json_text()
    {
        var result = Verifier.Parse("SESSION-RESULT: everything passed, score 90");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_returns_null_for_score_out_of_range()
    {
        var result = Verifier.Parse("""{"score":150,"findings":[],"verdict":"PASS"}""");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_returns_null_for_negative_score()
    {
        var result = Verifier.Parse("""{"score":-5,"findings":[],"verdict":"FAIL"}""");
        Assert.Null(result);
    }

    [Fact]
    public void Parse_handles_score_0()
    {
        var result = Verifier.Parse("""{"score":0,"findings":["everything broken"],"verdict":"FAIL"}""");

        Assert.NotNull(result);
        Assert.Equal(0, result.Score);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void Parse_handles_score_100()
    {
        var result = Verifier.Parse("""{"score":100,"findings":[],"verdict":"PASS"}""");

        Assert.NotNull(result);
        Assert.Equal(100, result.Score);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Parse_defaults_verdict_when_missing()
    {
        var result = Verifier.Parse("""{"score":85,"findings":[]}""");

        Assert.NotNull(result);
        Assert.Equal("PASS", result.Verdict);
    }

    [Fact]
    public void Parse_defaults_verdict_to_fail_when_score_low_and_missing()
    {
        var result = Verifier.Parse("""{"score":50}""");

        Assert.NotNull(result);
        Assert.Equal("FAIL", result.Verdict);
    }

    [Theory]
    [InlineData(80, 80, true)]
    [InlineData(85, 80, true)]
    [InlineData(100, 80, true)]
    [InlineData(79, 80, false)]
    [InlineData(50, 80, false)]
    [InlineData(0, 80, false)]
    [InlineData(90, 90, true)]
    [InlineData(89, 90, false)]
    public void Passes_respects_threshold(int score, int threshold, bool expected)
    {
        var verdict = new VerifierVerdict(score, Array.Empty<string>(), score >= threshold ? "PASS" : "FAIL");
        Assert.Equal(expected, verdict.Passes(threshold));
    }

    [Fact]
    public void Parse_extracts_json_from_surrounding_text()
    {
        var text = """
            SESSION-RESULT: verification complete
            {"score":95,"findings":["minor formatting issue"],"verdict":"PASS"}
            Delivered by verifier session.
            """;

        var result = Verifier.Parse(text);

        Assert.NotNull(result);
        Assert.Equal(95, result.Score);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void Parse_ignores_non_string_findings()
    {
        var json = """{"score":80,"findings":["valid item", 42, true, "another valid"],"verdict":"PASS"}""";

        var result = Verifier.Parse(json);

        Assert.NotNull(result);
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains("valid item", result.Findings);
        Assert.Contains("another valid", result.Findings);
    }
}
