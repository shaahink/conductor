using Conductor.Core.Orchestration;
using Conductor.Planning;

namespace Conductor.Tests;

/// <summary>Regression coverage for the session #3 (U-series, 2026-07-17) verify failure: a
/// verifier's real, valid JSON verdict — 2682 chars, all prose findings, no code fences dropped —
/// was cropped to 700 chars by the same helper Deliver/Fix sessions use for their one-paragraph
/// SESSION-RESULT: summary, chopping the closing brace off and leaving <see cref="Verifier.Parse"/>
/// nothing to match. The session was recorded AgentError ("verifier produced no parseable score")
/// even though the agent had produced a perfectly good verdict.</summary>
public class SessionRunnerExtractSessionResultTests
{
    private static string LongText(int length, char pad = 'x') => new(pad, length);

    [Fact]
    public void Verify_kind_does_not_truncate_at_700_chars()
    {
        var json = $$"""{"score":66,"findings":["{{LongText(900)}}"],"verdict":"WARN"}""";
        Assert.True(json.Length > 700, "the fixture must reproduce a payload longer than the old cap");

        var result = SessionRunner.ExtractSessionResult(json, SessionKind.Verify);

        Assert.Equal(json, result);
        Assert.EndsWith("}", result);
    }

    [Fact]
    public void Verify_kind_does_not_search_for_SESSION_RESULT_marker()
    {
        // Verify sessions are never asked to print SESSION-RESULT: (only Deliver/Fix are) — a
        // verdict JSON that happens to mention the phrase in a finding must not have everything
        // before it silently dropped.
        var json = """{"score":80,"findings":["unrelated to SESSION-RESULT: conventions"],"verdict":"PASS"}""";

        var result = SessionRunner.ExtractSessionResult(json, SessionKind.Verify);

        Assert.Equal(json, result);
    }

    [Fact]
    public void Verify_kind_still_bounded_by_a_generous_cap()
    {
        var huge = LongText(SessionRunner.VerifyResultMaxChars + 500);

        var result = SessionRunner.ExtractSessionResult(huge, SessionKind.Verify);

        Assert.Equal(SessionRunner.VerifyResultMaxChars + 1, result.Length); // +1 for the ellipsis
    }

    [Fact]
    public void Deliver_kind_keeps_the_700_char_SESSION_RESULT_convention()
    {
        var text = "SESSION-RESULT: " + LongText(900);

        var result = SessionRunner.ExtractSessionResult(text, SessionKind.Deliver);

        Assert.Equal(701, result.Length); // Trunc(…, 700) + ellipsis — unchanged behavior
        Assert.StartsWith("SESSION-RESULT:", result);
    }

    [Fact]
    public void Deliver_kind_finds_the_marker_mid_text()
    {
        var text = "some preamble the agent printed\nSESSION-RESULT: delivered fine.";

        var result = SessionRunner.ExtractSessionResult(text, SessionKind.Deliver);

        Assert.Equal("SESSION-RESULT: delivered fine.", result);
    }

    [Fact]
    public void Null_or_blank_input_returns_empty_regardless_of_kind()
    {
        Assert.Equal("", SessionRunner.ExtractSessionResult(null, SessionKind.Verify));
        Assert.Equal("", SessionRunner.ExtractSessionResult("   ", SessionKind.Deliver));
    }

    [Fact]
    public void Real_session_003_payload_survives_untruncated()
    {
        // The exact shape (length + trailing content) of the agent output that session #3
        // produced — a ```json fence, a 5-item findings array (one finding long enough alone to
        // blow past 700 chars), verdict WARN. This is what got cropped to 709 bytes of garbage.
        var payload = "```json\n{\"score\":66,\"findings\":[\"" + LongText(2500) + "\"],\"verdict\":\"WARN\"}\n```";
        Assert.True(payload.Length > 2000);

        var result = SessionRunner.ExtractSessionResult(payload, SessionKind.Verify);

        Assert.Equal(payload, result);
        Assert.Contains("\"verdict\":\"WARN\"", result);
    }
}
