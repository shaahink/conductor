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

    [Fact]
    public void Parse_survives_stray_braces_inside_a_finding_string()
    {
        // The old regex was `\{[^{}]*"score"[^{}]*\}` — ANY brace character between "score" and
        // the closing `}`, even one quoted inside a finding's string value, broke the match
        // outright. This project's own docs are full of `{model}`/`{planDoc}`/`{message}`
        // placeholders, so a verifier commenting on them is a completely ordinary case, not an
        // edge case.
        var json = """{"score":88,"findings":["the {model} placeholder resolves correctly","{planDoc} falls back to the tracker"],"verdict":"PASS"}""";

        var result = Verifier.Parse(json);

        Assert.NotNull(result);
        Assert.Equal(88, result.Score);
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.Contains("{model}"));
    }

    [Fact]
    public void Parse_prefers_the_last_valid_candidate_when_several_appear()
    {
        var text = """
            A draft verdict while thinking out loud: {"score":40,"findings":["draft"],"verdict":"FAIL"}
            On reflection, the final verdict is:
            {"score":92,"findings":["all good"],"verdict":"PASS"}
            """;

        var result = Verifier.Parse(text);

        Assert.NotNull(result);
        Assert.Equal(92, result.Score);
        Assert.Equal("PASS", result.Verdict);
    }

    [Fact]
    public void Parse_handles_the_real_session_003_verifier_output()
    {
        // Captured verbatim from .conductor/logs/session-003.jsonl's final "result" field
        // (2026-07-17) — a real verifier response, code-fenced, that VerdictEngine failed to
        // score because SessionRunner's 700-char SESSION-RESULT: cropping (fixed separately)
        // cut the JSON's closing brace off before Verifier.Parse ever saw it. Proves the parser
        // itself handles the full, untruncated real payload correctly.
        var raw = """
            ```json
            {"score":66,"findings":["CRITICAL: CONDUCTOR-UX-START.md's U0.1/U0.2/U0.3 checkpoint rows are unchanged from baseline — still Status=TODO, Commit and Evidence columns blank. Fix: edit the CONDUCTOR-UX-START.md checkpoint table rows to DONE with commit SHA + evidence path for U0.1 (199f2c8), U0.2 (66e6f57), U0.3 (ebd0eca/84fe84f) — this is the actual mechanism the engine and any future session trust, not the AGENTS.md handoff text.","Zero conductor-note ledger rows exist for run 1a7c1714 despite session #2 claiming to have root-caused the ratchet-gate 40>38 pragma regression. Fix: retroactively write the ledger note and file+close the bug so the finding survives a session kill next time.","Code substance for all three U0 checkpoints was independently verified correct against docs/CONDUCTOR-UX.md.","Full gate battery independently reproduced green: dotnet build 0 warnings/0 errors; dotnet test 878/878 passed.","Commit c829143 ('U0 CLOSED 3/3 — session handoff') and the AGENTS.md 'U0 delivered this session' section overstate completion relative to the system of record."],"verdict":"WARN"}
            ```
            """;

        var result = Verifier.Parse(raw);

        Assert.NotNull(result);
        Assert.Equal(66, result.Score);
        Assert.Equal("WARN", result.Verdict);
        Assert.Equal(5, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.StartsWith("CRITICAL:"));
    }
}
