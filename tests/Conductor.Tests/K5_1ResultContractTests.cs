using Conductor.Core;
using Conductor.Core.Orchestration;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// K5.1 — the session result contract.
///
/// <para>Before this checkpoint the same paragraph was cut four different ways by four consumers that
/// each knew nothing about where a field ended, because there were no fields: 700 characters on the
/// session record, 700 again on the way to Telegram, 1200 for the advisor, 600 bytes into the next
/// prompt. Every one of those cuts landed mid-word, and the fields an agent puts LAST — evidence and
/// gaps, the two a reviewer actually reads — were the first to be lost.</para>
///
/// <para>These pin both halves of the contract: a structured result survives whole, and anything that
/// is not structured is treated EXACTLY as it was before, byte for byte. The engine cannot make an
/// agent obey a format; it can only prefer one, and it must not punish the sessions that do not.</para>
/// </summary>
public sealed class K5_1ResultContractTests
{
    private const string WellFormed = """
        SESSION-RESULT: K5.1 landed the result contract and moved every consumer onto it
        - SessionResult parses headline, bullets, artefacts, evidence and gaps
        - the four blind cuts now drop whole fields instead of half a word
        - legacy prose degrades to the pre-K5.1 behaviour
        artefacts: src/Conductor.Core/SessionResult.cs, 1b4e87c
        evidence: .conductor/evidence/K5/K5-1-result-contract.md
        gaps: K5.2 still renders the stage as a bare letter
        """;

    // ── the parse ──

    [Fact]
    public void A_well_formed_result_parses_into_fields()
    {
        var r = SessionResult.Parse(WellFormed);

        Assert.True(r.IsStructured);
        Assert.True(r.HasMarker);
        Assert.Equal("K5.1 landed the result contract and moved every consumer onto it", r.Headline);
        Assert.Equal(3, r.Outcomes.Count);
        Assert.StartsWith("SessionResult parses headline", r.Outcomes[0], StringComparison.Ordinal);
        Assert.Equal(["src/Conductor.Core/SessionResult.cs", "1b4e87c"], r.Artefacts);
        Assert.Equal([".conductor/evidence/K5/K5-1-result-contract.md"], r.Evidence);
        Assert.Equal("K5.2 still renders the stage as a bare letter", r.Gaps);
    }

    [Fact]
    public void The_headline_is_clipped_to_fifteen_words()
    {
        var words = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"w{i}"));

        var r = SessionResult.Parse($"SESSION-RESULT: {words}\n- one\ngaps: none");

        Assert.True(r.IsStructured);
        Assert.EndsWith("w15…", r.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("w16", r.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_three_outcome_bullets_survive_and_the_rest_are_counted()
    {
        var r = SessionResult.Parse(
            "SESSION-RESULT: five bullets\n- a\n- b\n- c\n- d\n- e\ngaps: none");

        Assert.Equal(3, r.Outcomes.Count);
        Assert.Equal(2, r.OutcomeOverflow);
        Assert.Contains("+2 more", r.ToCanonical(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("artefacts")]
    [InlineData("artifacts")]
    [InlineData("Artefacts")]
    public void The_artefact_label_is_spelled_either_way(string label)
    {
        var r = SessionResult.Parse($"SESSION-RESULT: h\n- one\n{label}: a.cs, b.cs");

        Assert.Equal(["a.cs", "b.cs"], r.Artefacts);
    }

    [Fact]
    public void Prose_after_the_fields_stays_out_of_the_structured_view_but_not_out_of_raw()
    {
        var r = SessionResult.Parse(
            "SESSION-RESULT: headline here\n- did a thing\ngaps: none\nThen a whole paragraph of narrative.");

        Assert.Equal("headline here", r.Headline);
        Assert.DoesNotContain("narrative", r.ToCanonical(), StringComparison.Ordinal);
        Assert.Contains("narrative", r.Raw, StringComparison.Ordinal);
    }

    // ── degrading: the half of the contract that must not break anyone ──

    [Fact]
    public void Legacy_prose_is_not_structured_and_keeps_the_exact_pre_K5_1_cut()
    {
        var prose = "SESSION-RESULT: " + new string('x', 900);

        var r = SessionResult.Parse(prose);

        Assert.False(r.IsStructured);
        Assert.Equal(Old(prose), r.ToCanonical());
        Assert.Equal(701, r.ToCanonical().Length);
    }

    [Fact]
    public void A_narrative_that_merely_contains_a_markdown_list_is_still_prose()
    {
        // No marker: whatever it looks like, it is not a result and must not be filleted.
        var text = "Here is what happened.\n- a thing\n- another thing\nevidence: nowhere";

        var r = SessionResult.Parse(text);

        Assert.False(r.IsStructured);
        Assert.Equal(text, r.ToCanonical());
    }

    [Fact]
    public void A_marker_with_bullets_but_no_headline_degrades_rather_than_losing_the_bullets()
    {
        var text = "SESSION-RESULT:\n- did a thing\n- did another";

        var r = SessionResult.Parse(text);

        Assert.False(r.IsStructured);
        Assert.Contains("did another", r.ToCanonical(), StringComparison.Ordinal);
    }

    [Fact]
    public void Null_blank_and_junk_all_parse_without_throwing()
    {
        Assert.False(SessionResult.Parse(null).HasMarker);
        Assert.Equal("", SessionResult.Parse("   ").ToCanonical());
        Assert.Equal("", SessionResult.Parse("").ToCompact(700));

        var json = "```json\n{\"score\":66,\"findings\":[\"a\"],\"verdict\":\"WARN\"}\n```";
        var r = SessionResult.Parse(json);
        Assert.False(r.IsStructured);
        Assert.Contains("\"verdict\":\"WARN\"", r.ToCanonical(), StringComparison.Ordinal);
    }

    // ── the renderers ──

    [Fact]
    public void The_stored_form_keeps_the_last_field_that_the_700_char_cut_used_to_eat()
    {
        // The defect, exactly: a structured result whose gaps line sits past character 700.
        var padded = WellFormed.Replace(
            "- legacy prose degrades to the pre-K5.1 behaviour",
            "- " + new string('p', 600),
            StringComparison.Ordinal);
        Assert.True(padded.IndexOf("gaps:", StringComparison.Ordinal) > 700);

        var stored = SessionRunner.ExtractSessionResult(padded, SessionKind.Deliver);

        Assert.Contains("gaps: K5.2 still renders the stage as a bare letter", stored, StringComparison.Ordinal);
        Assert.Contains("evidence: .conductor/evidence/K5/", stored, StringComparison.Ordinal);
        Assert.True(stored.Length < padded.Length, "the long bullet is clipped on its own, not the record");
        Assert.StartsWith("SESSION-RESULT:", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_drops_whole_fields_from_the_bottom_instead_of_cutting_a_word()
    {
        var r = SessionResult.Parse(WellFormed);

        var wide = r.ToCompact(4000);
        Assert.Contains(r.Headline, wide, StringComparison.Ordinal);
        Assert.Contains("gaps:", wide, StringComparison.Ordinal);
        Assert.Contains("artefacts:", wide, StringComparison.Ordinal);

        var tight = r.ToCompact(150);
        Assert.True(tight.Length <= 150, $"compact({150}) produced {tight.Length}");
        Assert.Contains(r.Headline, tight, StringComparison.Ordinal);
        Assert.DoesNotContain("artefacts:", tight, StringComparison.Ordinal);
        // Whatever survived, it survived as whole lines.
        foreach (var line in tight.TrimEnd('…').Split('\n'))
            Assert.Contains(line, wide, StringComparison.Ordinal);
    }

    [Fact]
    public void Compact_of_an_unstructured_result_is_the_old_blind_cut()
    {
        var prose = "SESSION-RESULT: " + new string('y', 900);

        Assert.Equal(Old(prose), SessionResult.Parse(prose).ToCompact(700));
    }

    [Fact]
    public void The_lessons_feed_gets_the_rule_shaped_parts_and_not_the_status_headline()
    {
        var r = SessionResult.Parse(WellFormed);

        var forLessons = r.ForLessons();

        Assert.DoesNotContain("K5.1 landed the result contract", forLessons, StringComparison.Ordinal);
        Assert.Contains("legacy prose degrades", forLessons, StringComparison.Ordinal);
        Assert.Contains("K5.2 still renders", forLessons, StringComparison.Ordinal);
    }

    [Fact]
    public void The_report_renders_fields_for_a_structured_result_and_the_old_blockquote_otherwise()
    {
        var md = SessionResult.Parse(WellFormed).ToMarkdown();
        Assert.StartsWith("> **K5.1 landed", md, StringComparison.Ordinal);
        Assert.Contains("> - the four blind cuts", md, StringComparison.Ordinal);
        Assert.Contains("> evidence: ", md, StringComparison.Ordinal);

        var prose = "line one\nline two";
        Assert.Equal("> line one\n> line two", SessionResult.Parse(prose).ToMarkdown());
    }

    // ── the record ──

    [Fact]
    public void A_verify_payload_never_goes_near_the_contract()
    {
        var payload = "```json\n{\"score\":66,\"findings\":[\"" + new string('f', 2500) + "\"],\"verdict\":\"WARN\"}\n```";

        var stored = SessionRunner.ExtractSessionResult(payload, SessionKind.Verify);

        Assert.Equal(payload, stored);
    }

    /// <summary>The pre-K5.1 formula, spelled out so parity is asserted against the code that was
    /// there and not against a memory of it: find the marker, trim, cut at 700, add an ellipsis.</summary>
    private static string Old(string text)
    {
        var idx = text.IndexOf("SESSION-RESULT:", StringComparison.OrdinalIgnoreCase);
        var s = (idx >= 0 ? text[idx..] : text).Trim();
        return s.Length <= 700 ? s : s[..700] + "…";
    }
}
