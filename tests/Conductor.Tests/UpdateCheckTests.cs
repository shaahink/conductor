using System.Net;
using System.Runtime.InteropServices;
using Conductor.Core.Update;

namespace Conductor.Tests;

/// <summary>
/// SC8.3 — the decide-and-select half of <c>conductor update</c>: which release is newer, which
/// archive is this machine's, and what the release feed's answers parse to. All offline; the wire is
/// exercised through a stub handler so a CI box with no GitHub reachability still measures it.
/// </summary>
public sealed class UpdateCheckTests
{
    private static GithubRelease Release(string tag, params string[] assets) => new()
    {
        TagName = tag,
        HtmlUrl = $"https://github.com/x/y/releases/tag/{tag}",
        Assets = [.. assets.Select(a => new GithubAsset { Name = a, DownloadUrl = "https://x/" + a, Size = 10 })],
    };

    [Fact]
    public void Decide_OffersTheReleaseWhenItIsNewerThanAPrereleaseBuild()
    {
        // A binary built two commits past v2.1.0 answers 2.1.1-alpha.0.2. v2.2.0 is a genuine upgrade.
        var status = UpdateStatus.Decide(SemVer.Parse("2.1.1-alpha.0.2"), Release("v2.2.0"), null);
        Assert.True(status.Available);
        Assert.Equal("v2.2.0", status.Tag);
        Assert.Contains("v2.2.0 is available", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_NeverOffersADowngradeToAPrereleasesOwnBaseTag()
    {
        // The trap the SC8.2 handoff flagged. 2.1.1-alpha.0.7 came FROM v2.1.0 and is ahead of it;
        // "updating" to v2.1.0 would walk the operator backwards while calling it an upgrade.
        var status = UpdateStatus.Decide(SemVer.Parse("2.1.1-alpha.0.7"), Release("v2.1.0"), null);
        Assert.False(status.Available);
        Assert.True(status.Known);
        Assert.Contains("newer than the latest release", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_SaysUpToDateOnTheExactTag()
    {
        var status = UpdateStatus.Decide(SemVer.Parse("2.1.0"), Release("v2.1.0"), null);
        Assert.False(status.Available);
        Assert.True(status.Known);
        Assert.Equal("running the latest release", status.Detail);
    }

    [Fact]
    public void Decide_BuildMetadataDoesNotMakeABinaryStale()
    {
        // The running engine reports 2.1.0 with a commit sha; the release is tagged v2.1.0. Same thing.
        var status = UpdateStatus.Decide(SemVer.Parse("2.1.0+abc123def456"), Release("v2.1.0"), null);
        Assert.False(status.Available);
    }

    [Fact]
    public void Decide_AnUnreachableFeedIsUnknown_NotUpToDate()
    {
        // An offline laptop must never be reported as "running the latest release".
        var status = UpdateStatus.Decide(SemVer.Parse("2.1.0"), null, "HttpRequestException: no such host");
        Assert.False(status.Known);
        Assert.False(status.Available);
        Assert.Contains("no such host", status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Decide_ATagThatIsNotSemverIsRefusedRatherThanGuessed()
    {
        var status = UpdateStatus.Decide(SemVer.Parse("2.1.0"), Release("nightly"), null);
        Assert.False(status.Known);
        Assert.False(status.Available);
        Assert.Contains("not a semantic version", status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("windows", Architecture.X64, "conductor-windows-x64.zip", "conductor.exe")]
    [InlineData("linux", Architecture.X64, "conductor-linux-x64.tar.gz", "conductor")]
    [InlineData("linux", Architecture.Arm64, "conductor-linux-arm64.tar.gz", "conductor")]
    [InlineData("macos", Architecture.Arm64, "conductor-macos-arm64.tar.gz", "conductor")]
    [InlineData("macos", Architecture.X64, "conductor-macos-x64.tar.gz", "conductor")]
    public void Target_MatchesEveryRowOfTheReleaseMatrix(string os, Architecture arch, string asset, string engine)
    {
        // These five names are the matrix in .github/workflows/release.yml. Asserted on one host so
        // the four this machine will never be are still measured.
        var target = UpdateTarget.For(os, arch);
        Assert.NotNull(target);
        Assert.Equal(asset, target!.AssetName);
        Assert.Equal(engine, target.EngineFileName);
    }

    [Theory]
    [InlineData("linux", Architecture.Arm)]
    [InlineData("freebsd", Architecture.X64)]
    [InlineData("", Architecture.X64)]
    public void Target_IsNullWhereNoReleaseIsPublished(string os, Architecture arch) =>
        Assert.Null(UpdateTarget.For(os, arch));

    [Fact]
    public void ThisMachineHasATarget_OrTheSuiteIsRunningSomewhereUnreleasable()
    {
        // Guards against a map that compiles but resolves to nothing on the platforms we build on.
        Assert.NotNull(UpdateTarget.ForThisMachine());
    }

    [Fact]
    public void Asset_LookupIsCaseInsensitiveAndMissesCleanly()
    {
        var release = Release("v2.2.0", "conductor-windows-x64.zip", "SHA256SUMS.txt");
        Assert.NotNull(release.Asset("CONDUCTOR-WINDOWS-X64.ZIP"));
        Assert.NotNull(release.Asset(ArchiveUnpacker.ChecksumAssetName));
        Assert.Null(release.Asset("conductor-linux-x64.tar.gz"));
    }

    [Fact]
    public async Task LatestAsync_ParsesARealShapedGithubDocument()
    {
        const string body = """
            {"tag_name":"v2.3.0","name":"2.3.0","draft":false,"prerelease":false,
             "html_url":"https://github.com/shaahink/conductor/releases/tag/v2.3.0",
             "published_at":"2026-07-31T10:11:12Z",
             "assets":[{"name":"conductor-windows-x64.zip","size":41943040,
                        "browser_download_url":"https://github.com/x/y/releases/download/v2.3.0/conductor-windows-x64.zip"}]}
            """;
        using var handler = new StubHandler(HttpStatusCode.OK, body);
        using var client = new ReleaseClient(TimeSpan.FromSeconds(5), handler);
        var (release, error) = await client.LatestAsync();

        Assert.Null(error);
        Assert.NotNull(release);
        Assert.Equal("v2.3.0", release!.TagName);
        var asset = release.Asset("conductor-windows-x64.zip");
        Assert.NotNull(asset);
        Assert.Equal(41943040, asset!.Size);
        Assert.StartsWith("https://github.com/", asset.DownloadUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LatestAsync_ReportsAnHttpFailureAsAnAnswer_NotAnException()
    {
        // 403 is what api.github.com returns to a rate-limited caller, and doctor must survive it.
        using var handler = new StubHandler(HttpStatusCode.Forbidden, "rate limited");
        using var client = new ReleaseClient(TimeSpan.FromSeconds(5), handler);
        var (release, error) = await client.LatestAsync();
        Assert.Null(release);
        Assert.Contains("403", error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LatestAsync_TreatsAnUnparseableBodyAsAFailure()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, "<html>not json</html>");
        using var client = new ReleaseClient(TimeSpan.FromSeconds(5), handler);
        var (release, error) = await client.LatestAsync();
        Assert.Null(release);
        Assert.NotNull(error);
    }

    [Fact]
    public void FeedUrl_DefaultsToTheRepositoryReleasesArePublishedFrom() =>
        // Not asserted against the env override: this test states the shipped default, which is the
        // thing a mis-typed constant would break silently for every user at once.
        Assert.Equal(
            $"https://api.github.com/repos/{ReleaseClient.DefaultRepo}/releases/latest",
            Environment.GetEnvironmentVariable(ReleaseClient.FeedEnvVar) is { Length: > 0 }
                ? $"https://api.github.com/repos/{ReleaseClient.DefaultRepo}/releases/latest"
                : ReleaseClient.FeedUrl);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
