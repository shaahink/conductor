using Conductor.Core.Update;

namespace Conductor.Tests;

/// <summary>
/// SC8.3 — semver precedence, and one case in particular. Since SC8.2 the engine's own version is
/// almost always a tag-height PRERELEASE (<c>2.1.1-alpha.0.7</c>), so "is the release newer than me?"
/// runs straight through the rule most hand-rolled comparators get wrong. A comparator that ordered
/// <c>2.1.1-alpha.0.7</c> ABOVE <c>2.1.1</c> would make `conductor update` refuse every real upgrade;
/// one that ordered it BELOW <c>2.1.0</c> would offer a downgrade as an upgrade.
/// </summary>
public sealed class SemVerTests
{
    [Theory]
    [InlineData("2.1.0", 2, 1, 0, "", "")]
    [InlineData("v2.1.0", 2, 1, 0, "", "")]                       // git tags carry the v
    [InlineData("2.1.1-alpha.0.7", 2, 1, 1, "alpha.0.7", "")]
    [InlineData("2.1.1-alpha.0.7+abc123def456", 2, 1, 1, "alpha.0.7", "abc123def456")]
    [InlineData("0.0.0-alpha.0", 0, 0, 0, "alpha.0", "")]         // what a tagless shallow clone builds
    public void Parse_SplitsEveryPart(string text, int major, int minor, int patch, string pre, string build)
    {
        Assert.True(SemVer.TryParse(text, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(pre, v.Prerelease);
        Assert.Equal(build, v.BuildMetadata);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2.1")]
    [InlineData("2.1.0.4")]
    [InlineData("latest")]
    [InlineData("v")]
    [InlineData("2.1.x")]
    [InlineData("-1.0.0")]
    public void Parse_RefusesWhatIsNotASemanticVersion(string text) =>
        Assert.False(SemVer.TryParse(text, out _));

    [Fact]
    public void Prerelease_SortsBelowTheReleaseItPrecedes()
    {
        // The whole reason this type exists: an engine seven commits past v2.1.0 reports 2.1.1-alpha.0.7,
        // which is BELOW 2.1.1 and ABOVE 2.1.0. Both halves matter to `conductor update`.
        Assert.True(SemVer.Parse("2.1.1-alpha.0.7") < SemVer.Parse("2.1.1"));
        Assert.True(SemVer.Parse("2.1.1-alpha.0.7") > SemVer.Parse("2.1.0"));
    }

    [Fact]
    public void NumericPrereleaseIdentifiers_CompareAsNumbers_NotAsText()
    {
        // Ordinal string comparison puts alpha.0.10 BELOW alpha.0.7 ('1' < '7'), which would make the
        // engine think it had gone backwards three commits into a stage.
        Assert.True(SemVer.Parse("2.1.1-alpha.0.7") < SemVer.Parse("2.1.1-alpha.0.10"));
        Assert.True(SemVer.Parse("2.1.1-alpha.0.9") < SemVer.Parse("2.1.1-alpha.0.100"));
    }

    [Fact]
    public void PrereleasePrecedence_FollowsTheSpecsWorkedExample()
    {
        // semver.org 11.4, verbatim.
        var ordered = new[]
        {
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0",
        }.Select(SemVer.Parse).ToArray();

        for (var i = 1; i < ordered.Length; i++)
            Assert.True(ordered[i - 1] < ordered[i], $"{ordered[i - 1]} should sort below {ordered[i]}");
    }

    [Fact]
    public void BuildMetadata_IsIgnoredForPrecedence()
    {
        // Two binaries built from the same version and different commits are not newer than one
        // another — that question is what the commit sha in `conductor version` answers.
        Assert.Equal(0, SemVer.Parse("2.1.0+abc123").CompareTo(SemVer.Parse("2.1.0+def456")));
        Assert.Equal(0, SemVer.Parse("2.1.0").CompareTo(SemVer.Parse("2.1.0+abc123.dirty")));
    }

    [Theory]
    [InlineData("2.2.0", "2.1.0")]
    [InlineData("2.1.1", "2.1.0")]
    [InlineData("3.0.0", "2.99.99")]
    [InlineData("2.1.0", "2.0.99")]
    public void CoreNumbers_CompareLeftToRight(string bigger, string smaller) =>
        Assert.True(SemVer.Parse(bigger) > SemVer.Parse(smaller));

    [Fact]
    public void ToString_RoundTrips()
    {
        Assert.Equal("2.1.1-alpha.0.7+abc123", SemVer.Parse("2.1.1-alpha.0.7+abc123").ToString());
        Assert.Equal("2.1.0", SemVer.Parse("v2.1.0").ToString());
    }
}
