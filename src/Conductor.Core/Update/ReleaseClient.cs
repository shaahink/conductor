using System.Net.Http.Headers;
using System.Text.Json;

namespace Conductor.Core.Update;

/// <summary>
/// SC8.3 — the only thing here that touches the network. Asks GitHub what the latest release is and
/// pulls an asset off it.
///
/// <para><b>Why a User-Agent is not optional:</b> api.github.com answers a request without one with
/// 403, which reads as "rate limited" or "private repo" to every layer above. It is set once, here.</para>
///
/// <para><b>The feed override.</b> <c>CONDUCTOR_UPDATE_FEED</c> replaces the release document's URL.
/// It exists so the swap can be exercised end to end — against a local HTTP server serving a
/// GitHub-shaped document — without publishing a real release to test with, and it doubles as the
/// escape hatch for an internal mirror. The DEFAULT is the real repository; an override is announced
/// by every surface that uses it, because an update source is exactly the kind of thing that must
/// never be redirected silently.</para>
/// </summary>
public sealed class ReleaseClient : IDisposable
{
    /// <summary>The repository releases are published from — the remote
    /// <c>.github/workflows/release.yml</c> attaches archives to.</summary>
    public const string DefaultRepo = "shaahink/conductor";

    /// <summary>Environment variable that replaces the latest-release document URL.</summary>
    public const string FeedEnvVar = "CONDUCTOR_UPDATE_FEED";

    private readonly HttpClient _http;

    public ReleaseClient(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.Timeout = timeout;
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("conductor", BuildInfo.Current.Version));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>The URL the latest-release document is read from, override included. Public because
    /// every surface that reports an update prints it when it is not the default.</summary>
    public static string FeedUrl
    {
        get
        {
            var over = Environment.GetEnvironmentVariable(FeedEnvVar);
            return string.IsNullOrWhiteSpace(over)
                ? $"https://api.github.com/repos/{DefaultRepo}/releases/latest"
                : over.Trim();
        }
    }

    /// <summary>True when the update source has been pointed somewhere other than the real repo.</summary>
    public static bool FeedIsOverridden =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FeedEnvVar));

    /// <summary>The latest published release, or a failure reason. Never throws for a network
    /// condition: "GitHub is unreachable" is an ANSWER for doctor and for <c>update --check</c>, not
    /// an error that should take a command down.</summary>
    public async Task<(GithubRelease? Release, string? Error)> LatestAsync(CancellationToken ct = default)
    {
        var url = FeedUrl;
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return (null, $"{(int)resp.StatusCode} {resp.ReasonPhrase} from {url}");

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(json, ReleaseJsonContext.Default.GithubRelease);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return (null, $"no tag_name in the release document from {url}");
            return (release, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message} ({url})");
        }
    }

    /// <summary>Streams an asset to <paramref name="destination"/>. Streamed rather than buffered
    /// because a self-contained single-file engine is tens of megabytes and this runs on laptops.</summary>
    public async Task DownloadAsync(GithubAsset asset, string destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        using var resp = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (src.ConfigureAwait(false))
        {
            var dst = File.Create(destination);
            await using (dst.ConfigureAwait(false))
            {
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads a small text asset (the checksum manifest) into memory, or null when it is not
    /// on the release — releases published before SC8.3 have no manifest and must still be installable.</summary>
    public async Task<string?> TryReadTextAsync(GithubAsset? asset, CancellationToken ct = default)
    {
        if (asset is null) return null;
        try
        {
            using var resp = await _http.GetAsync(asset.DownloadUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
