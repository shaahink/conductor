using System.Reflection;
using Conductor.Core;

namespace Conductor.Tests;

/// <summary>
/// SC8.1 — the build stamp. Two halves, and the second is the one that matters: the parse is
/// exercised against every shape the build can emit, and then the REAL assembly is asserted to
/// actually carry a stamp, so a broken MSBuild target fails a test instead of silently shipping a
/// binary that answers "unknown" forever.
/// </summary>
public sealed class BuildInfoTests
{
    private static readonly Dictionary<string, string> None = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Parse_SplitsSemverFromBuildMetadata()
    {
        var info = BuildInfo.Parse("2.0.0+abc123def456", None);
        Assert.Equal("2.0.0", info.Version);
        Assert.Equal("abc123def456", info.CommitSha);
        Assert.False(info.Dirty);
        Assert.Equal("2.0.0+abc123def456", info.Full);
    }

    [Fact]
    public void Parse_ReadsTheDirtyMarkerOffTheSuffix()
    {
        // The suffix is the single-source read: a binary stamped only through SourceRevisionId (no
        // metadata attributes) must still report both facts.
        var info = BuildInfo.Parse("2.0.0+abc123def456.dirty", None);
        Assert.Equal("abc123def456", info.CommitSha);
        Assert.True(info.Dirty);
        Assert.Equal("2.0.0+abc123def456.dirty", info.Full);
    }

    [Fact]
    public void Parse_MetadataAttributesWinOverTheSuffix()
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CommitSha"] = "999999999999",
            ["CommitDirty"] = "false",
        };
        var info = BuildInfo.Parse("2.0.0+abc123def456.dirty", meta);
        Assert.Equal("999999999999", info.CommitSha);
        Assert.False(info.Dirty);
    }

    [Fact]
    public void Parse_ReadsTheBuildDateAsUtc()
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BuildDate"] = "2026-07-31T15:47:20Z",
        };
        var info = BuildInfo.Parse("2.0.0+abc123def456", meta);
        Assert.Equal(new DateTimeOffset(2026, 7, 31, 15, 47, 20, TimeSpan.Zero), info.BuildDate);
        Assert.Equal("2026-07-31T15:47:20Z", info.BuildDateIso);
    }

    [Fact]
    public void Parse_UnparseableBuildDateIsNullNotAnException()
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["BuildDate"] = "whenever" };
        var info = BuildInfo.Parse("2.0.0+abc", meta);
        Assert.Null(info.BuildDate);
        Assert.Null(info.BuildDateIso);
    }

    [Fact]
    public void Parse_UnstampedAssemblyStillReportsAVersion()
    {
        // Someone builds with the target disabled, or off a source archive with no git. The binary
        // must still answer — "unknown" is an answer, a crash on `conductor version` is not.
        var info = BuildInfo.Parse(informational: null, None, fallbackVersion: "2.0.0");
        Assert.Equal("2.0.0", info.Version);
        Assert.Equal(BuildInfo.UnknownCommit, info.CommitSha);
        Assert.False(info.Dirty);
        Assert.Null(info.BuildDate);
    }

    [Fact]
    public void Parse_EmptyEverythingFallsBackToZeroes()
    {
        var info = BuildInfo.Parse("", None);
        Assert.Equal("0.0.0", info.Version);
        Assert.Equal(BuildInfo.UnknownCommit, info.CommitSha);
    }

    // The load-bearing one. Everything above tests a parser; this tests that the BUILD did its job.
    // If Conductor.csproj's StampBuildInfo target stops running, stops finding git, or stops writing
    // its attributes, this is what says so — instead of the operator discovering months later that
    // every binary claims to be "unknown".
    [Fact]
    public void TheEngineAssemblyIsActuallyStamped()
    {
        var engine = typeof(BuildInfo).Assembly;
        var info = BuildInfo.FromAssembly(engine);

        Assert.Matches(@"^\d+\.\d+\.\d+$", info.Version);
        Assert.NotEqual(BuildInfo.UnknownCommit, info.CommitSha);
        Assert.Matches("^[0-9a-f]{7,40}$", info.CommitSha);
        Assert.NotNull(info.BuildDate);
        // A stamp from the future or from before this stage was written is not a stamp, it is a
        // leftover constant.
        Assert.True(info.BuildDate!.Value > new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            $"build date {info.BuildDateIso} predates SC8 — the stamp is not coming from the build");
        Assert.True(info.BuildDate!.Value < DateTimeOffset.UtcNow.AddDays(1),
            $"build date {info.BuildDateIso} is in the future");

        // And the informational version the compiler wrote must agree with what we render.
        var informational = engine.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.Equal(informational, info.Full);
    }

    [Fact]
    public void CurrentAndReportAgree()
    {
        // One shape, two surfaces (CLI --json and GET /version). If these ever drift, the two
        // surfaces are answering the "which engine is running" question differently.
        var report = VersionReport.Current();
        Assert.Equal(BuildInfo.Current.Version, report.Version);
        Assert.Equal(BuildInfo.Current.Full, report.Full);
        Assert.Equal(BuildInfo.Current.CommitSha, report.Commit);
        Assert.Equal(BuildInfo.Current.Dirty, report.Dirty);
        Assert.Equal(BuildInfo.Current.BuildDateIso, report.BuildDate);
        Assert.False(string.IsNullOrWhiteSpace(report.Binary));
        Assert.False(string.IsNullOrWhiteSpace(report.Runtime));
    }
}
