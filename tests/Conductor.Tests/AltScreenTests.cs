using Conductor.Ui;

namespace Conductor.Tests;

public class AltScreenTests
{
    [Fact]
    public void EmitsEnterAndLeaveSequencesAroundAnInteractiveSession()
    {
        using var writer = new StringWriter();

        using (var alt = AltScreen.Enter(writer, enabled: true))
        {
            Assert.True(alt.IsActive);
            // Enter must switch to the alt buffer and hide the cursor before any drawing.
            var afterEnter = writer.ToString();
            Assert.Contains(AltScreen.EnterAlt, afterEnter, StringComparison.Ordinal);
            Assert.Contains(AltScreen.HideCursor, afterEnter, StringComparison.Ordinal);
            Assert.DoesNotContain(AltScreen.LeaveAlt, afterEnter, StringComparison.Ordinal);
        }

        // Dispose must restore: leave the alt buffer AND show the cursor — or the terminal wedges.
        var full = writer.ToString();
        Assert.Contains(AltScreen.LeaveAlt, full, StringComparison.Ordinal);
        Assert.Contains(AltScreen.ShowCursor, full, StringComparison.Ordinal);
        // Ordering: enter precedes leave (we didn't leave before we entered).
        Assert.True(full.IndexOf(AltScreen.EnterAlt, StringComparison.Ordinal)
                    < full.IndexOf(AltScreen.LeaveAlt, StringComparison.Ordinal));
    }

    [Fact]
    public void RestoreIsIdempotent_ExplicitLeaveThenDisposeEmitsLeaveOnce()
    {
        using var writer = new StringWriter();
        var alt = AltScreen.Enter(writer, enabled: true);

        alt.Leave();      // e.g. a signal handler already un-wedged the terminal
        alt.Leave();      // second call is a no-op
        alt.Dispose();    // and the using/finally path is also a no-op

        var full = writer.ToString();
        Assert.Equal(1, CountOccurrences(full, AltScreen.LeaveAlt));
        Assert.Equal(1, CountOccurrences(full, AltScreen.EnterAlt));
    }

    [Fact]
    public void RedirectedOutput_EmitsNothing_SoInlineFallbackIsClean()
    {
        // `conductor preview` piped / CI: no TTY, so the guard is inert and the caller renders inline.
        using var writer = new StringWriter();

        using (var alt = AltScreen.Enter(writer, enabled: false))
        {
            Assert.False(alt.IsActive);
            alt.Leave();
        }

        Assert.Equal("", writer.ToString());
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
