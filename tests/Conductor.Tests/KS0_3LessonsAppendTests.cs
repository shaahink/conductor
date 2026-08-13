using System.Text;

using Conductor.Core;

namespace Conductor.Tests;

/// <summary>
/// KS0.3 — <c>lessons.md</c> never says the same thing twice.
///
/// <para>The regression this pins had one shape and cost two eras of prompt budget: the trimmer
/// re-parsed the content it had ALREADY prepended the new entry to, then emitted that entry again, so
/// every append that crossed the byte cap duplicated itself. It shipped a file where <c>K7-32</c>
/// appeared twice, and because <c>LessonsBattery</c> pastes the newest rules into every following
/// prompt, a duplicated line is rent charged on every session after it.</para>
///
/// <para>K1.3 rewrote the writer, and nothing pinned the property — which is how a regression comes
/// back. These tests are the pin: they drive the writer THROUGH the caps rather than under them,
/// because the bug only ever appeared on the append that had to trim.</para>
/// </summary>
public sealed class KS0_3LessonsAppendTests : IDisposable
{
    private readonly string _dir;

    public KS0_3LessonsAppendTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conductor-ks03-lessons-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) TestTemp.DeleteTree(_dir); }
        catch (IOException) { }
    }

    private string FilePath => Path.Combine(_dir, "lessons.md");

    private IReadOnlyList<string> RuleBodies()
    {
        if (!File.Exists(FilePath)) return [];
        return File.ReadAllLines(FilePath, Encoding.UTF8)
            .Where(l => l.StartsWith("- [", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(']', StringComparison.Ordinal) + 1)..].Trim())
            .ToList();
    }

    /// <summary>One rule per session, each long enough that the byte cap has to trim — the exact
    /// condition the old trimmer got wrong.</summary>
    private static string Result(int n) =>
        $"SESSION-RESULT: session {n} landed something.\n" +
        $"- Never let rule number {n} out of your sight, because the ratchet does not forgive a " +
        $"missing measurement and the next session pays for it twice over in wasted context.\n";

    [Fact]
    public void AnAppendThatCrossesTheByteCapDoesNotDuplicateItself()
    {
        var lessons = new LessonsManager(_dir, maxBytes: 1024, maxRules: 20);

        for (var i = 1; i <= 12; i++)
        {
            lessons.Append("KS0", i, Result(i));

            var bodies = RuleBodies();
            Assert.Equal(bodies.Distinct(StringComparer.Ordinal).Count(), bodies.Count);
        }

        // And it really did have to trim — otherwise this test proves nothing.
        Assert.True(new FileInfo(FilePath).Length <= 1024 + 256);
        Assert.True(RuleBodies().Count < 12, "the byte cap never engaged; the test is not exercising the bug");
    }

    [Fact]
    public void AnAppendThatCrossesTheRuleCountCapDoesNotDuplicateItself()
    {
        var lessons = new LessonsManager(_dir, maxBytes: 64 * 1024, maxRules: 4);

        for (var i = 1; i <= 10; i++) lessons.Append("KS0", i, Result(i));

        var bodies = RuleBodies();
        Assert.Equal(4, bodies.Count);
        Assert.Equal(bodies.Distinct(StringComparer.Ordinal).Count(), bodies.Count);
    }

    [Fact]
    public void TheSameLessonLearnedTwiceIsOneLesson_WhicheverSessionSaidIt()
    {
        var lessons = new LessonsManager(_dir, maxBytes: 64 * 1024, maxRules: 20);

        lessons.Append("KS0", 1, Result(7));
        lessons.Append("KS9", 2, Result(7));

        Assert.Single(RuleBodies());
    }

    [Fact]
    public void NewestFirstSurvivesTheTrim()
    {
        var lessons = new LessonsManager(_dir, maxBytes: 1024, maxRules: 20);

        for (var i = 1; i <= 12; i++) lessons.Append("KS0", i, Result(i));

        var bodies = RuleBodies();
        Assert.Contains("rule number 12", bodies[0], StringComparison.Ordinal);
        Assert.DoesNotContain(bodies, b => b.Contains("rule number 1,", StringComparison.Ordinal));
    }

    [Fact]
    public void ASessionWithNothingRuleShapedLeavesTheFileAlone()
    {
        var lessons = new LessonsManager(_dir, maxBytes: 64 * 1024, maxRules: 20);
        lessons.Append("KS0", 1, Result(1));
        var before = File.ReadAllText(FilePath, Encoding.UTF8);

        lessons.Append("KS0", 2, "SESSION-RESULT: landed three commits and pushed.\nartefacts: a.cs, b.cs\n");

        Assert.Equal(before, File.ReadAllText(FilePath, Encoding.UTF8));
    }
}
