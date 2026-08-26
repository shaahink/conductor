using System.Globalization;

using Conductor.Core.Http;
using Conductor.Core.Publishing;

namespace Conductor.Tests;

/// <summary>
/// CH1.1 — the rendered board page is ONE document, whatever the checkout did to the source.
///
/// <para><b>What was wrong.</b> <c>BoardSnapshotHtml</c>'s inline CSS is a C# raw string literal, and
/// a raw string literal inherits the line endings of its SOURCE FILE. Every other line of that
/// renderer appends an explicit LF, so on a CRLF checkout — which is every Windows clone, CI
/// included — the CSS block alone carried CRLF and <c>Render()</c> emitted a mixed document.
/// <c>DV6_3BoardPageTests.Publishing_writes_one_file_atomically_and_hands_back_what_it_rendered</c>
/// reads the published file back through a CRLF→LF normalisation and compares it to <c>Render()</c>,
/// so it failed on every CRLF checkout and passed on none of them.</para>
///
/// <para><b>Why the property and not the symptom.</b> Fixing the one constant fixes the one bug and
/// leaves the class open: the next raw string literal to arrive in that file reintroduces it, and the
/// only test that notices is a byte-comparison in another checkpoint's class whose failure message
/// says nothing about line endings. So the bar asserted here is the property — <b>the rendered
/// document contains no carriage return at all</b> — over both shapes of the page: the full board and
/// the empty one, which between them take both sides of every "is there anything here" branch in the
/// renderer. A raw string added to either path fails this, by name, with the cause in the message.</para>
///
/// <para>Deliberately NOT done: normalising the finished document at the seam. It would fix the bug
/// too, and it would make this test unable to fail — a guard that cannot go red is not a guard.</para>
/// </summary>
public sealed class CH1_1BoardPageLineEndingsTests
{
    private static readonly DateTime RenderedAt = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_full_board_renders_without_a_single_carriage_return()
    {
        var html = BoardSnapshotHtml.Render(DV6_3BoardPageTests.Snapshot());

        AssertNoCarriageReturn(html, "the full board");
    }

    [Fact]
    public void The_empty_board_renders_without_a_single_carriage_return()
    {
        var html = BoardSnapshotHtml.Render(Empty());

        AssertNoCarriageReturn(html, "the empty board");
    }

    /// <summary>The renderer's own newlines are explicit LF, so a rendered document is exactly its
    /// LF-normalised self. This is the same equality the publish test makes across a file round-trip;
    /// asserted here directly it names the cause instead of reporting a byte mismatch.</summary>
    [Fact]
    public void Rendering_is_byte_identical_to_its_own_LF_normalisation()
    {
        var html = BoardSnapshotHtml.Render(DV6_3BoardPageTests.Snapshot());

        Assert.Equal(html.Replace("\r\n", "\n", StringComparison.Ordinal)
                         .Replace("\r", "\n", StringComparison.Ordinal),
                     html, StringComparer.Ordinal);
    }

    private static void AssertNoCarriageReturn(string html, string what)
    {
        var at = html.IndexOf('\r', StringComparison.Ordinal);
        if (at < 0) return;

        var from = Math.Max(0, at - 60);
        var around = html.Substring(from, Math.Min(120, html.Length - from))
                         .Replace("\r", "<CR>", StringComparison.Ordinal)
                         .Replace("\n", "<LF>", StringComparison.Ordinal);
        Assert.Fail(
            $"{what} carries a carriage return at offset {at} — the page is a mixed document and its "
          + "bytes now depend on how the repository was cloned. A raw string literal in "
          + "BoardSnapshotHtml.cs inherits that file's line endings; wrap it in Lf(\"\"\"...\"\"\") the "
          + $"way the CSS constant is. Around it: {around}");
    }

    /// <summary>A run with nothing on the board yet: the branches the full fixture never reaches.</summary>
    private static BoardSnapshot Empty() => new(
        State: DV6_3BoardPageTests.Snapshot().State,
        Tasks: new TasksDto([]),
        Owner: new OwnerQueueDto(0, RenderedAt.ToString("O", CultureInfo.InvariantCulture), []),
        Evidence: [],
        LedgerLine: "",
        Boundary: "session 1 end",
        RenderedUtc: RenderedAt);
}
