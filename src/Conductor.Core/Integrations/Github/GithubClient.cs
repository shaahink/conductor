using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// KS9.1 — the only thing in the GitHub mirror that touches the network. Raw
/// <see cref="HttpClient"/> on the <c>ReleaseClient</c> pattern; deliberately NOT Octokit, which
/// would be a dependency the whole engine carries for one optional, off-by-default integration.
///
/// <para><b>Why the User-Agent is not optional.</b> api.github.com answers a request without one
/// with 403, and 403 reads as "rate limited" or "private repo" to every layer above — the single
/// most likely mis-diagnosis of this integration. It is set once, here.</para>
///
/// <para><b>Nothing throws for a network condition.</b> Every verb returns
/// <c>(value, error)</c>. "GitHub is unreachable" is an ANSWER for a mirror whose whole failure
/// posture is "the run is unharmed and the board converges later" — an exception here would climb
/// into the run loop, which is exactly what KS9.2 forbids.</para>
///
/// <para><b>The base URL override.</b> <c>CONDUCTOR_GITHUB_API</c> replaces api.github.com, on the
/// <c>ReleaseClient.FeedEnvVar</c> precedent: it lets a proof point at a loopback recorder, and it
/// lets the no-token refusal be MEASURED (aim it at a dead port and prove nothing was dialled). The
/// DEFAULT is the real API and an override is announced by every surface that uses it, because a
/// destination that writes issues must never be redirected silently.</para>
/// </summary>
public sealed class GithubClient : IDisposable
{
    /// <summary>Where the mirror writes when nothing overrides it.</summary>
    public const string DefaultApiBase = "https://api.github.com";

    /// <summary>Environment variable that replaces <see cref="DefaultApiBase"/>.</summary>
    public const string ApiBaseEnvVar = "CONDUCTOR_GITHUB_API";

    private readonly HttpClient _http;

    /// <param name="disposeHandler">Whether disposing this client disposes the handler it was given.
    /// False when the handler OUTLIVES the client — a recording fake driven through two consecutive
    /// passes, and (KS9.2) a long-lived pooled handler shared by every reconcile of a run.</param>
    public GithubClient(string token, TimeSpan timeout, HttpMessageHandler? handler = null, bool disposeHandler = true)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler);
        _http.Timeout = timeout;
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("conductor", BuildInfo.Current.Version));
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
    }

    /// <summary>The API root in force, override included. Public because every surface that writes
    /// to GitHub prints it when it is not the default.</summary>
    public static string ApiBase
    {
        get
        {
            var over = Environment.GetEnvironmentVariable(ApiBaseEnvVar);
            return string.IsNullOrWhiteSpace(over) ? DefaultApiBase : over.Trim().TrimEnd('/');
        }
    }

    /// <summary>True when the mirror has been pointed somewhere other than the real API.</summary>
    public static bool ApiBaseIsOverridden =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ApiBaseEnvVar));

    /// <summary>How many HTTP requests this client has issued. The batching bar ("one session
    /// boundary is ONE reconcile pass with a bounded request count") is a number, so the number is
    /// countable without a recording handler in the way.</summary>
    public int RequestCount { get; private set; }

    // ── issues ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every issue in the repository, open and closed, pull requests filtered out. The
    /// mirror reconciles against this rather than against GitHub's SEARCH api on purpose: search is
    /// eventually consistent, so a backfill run twice in quick succession would mint duplicates for
    /// whatever the index had not caught up with — idempotence that depends on timing is not
    /// idempotence.</summary>
    public Task<(List<GithubIssue>? Value, string? Error)> ListIssuesAsync(
        string repo, CancellationToken ct = default) =>
        PagedAsync($"/repos/{repo}/issues?state=all&per_page=100", GithubJsonContext.Default.ListGithubIssue,
            page => page.FindAll(i => i.PullRequest is null), ct);

    public Task<(GithubIssue? Value, string? Error)> CreateIssueAsync(
        string repo, GithubIssueRequest request, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Post, $"/repos/{repo}/issues",
            JsonSerializer.Serialize(request, GithubJsonContext.Default.GithubIssueRequest),
            GithubJsonContext.Default.GithubIssue, ct);

    /// <summary>KS9.2 — ONE issue, by number. The list endpoint is a read replica and does not show a
    /// just-created issue for seconds (measured live: four issues created, invisible to a list two
    /// seconds later). When the local map says we made an issue the listing does not show, this is
    /// what lets the pass diff against the real document instead of blind-writing over it.</summary>
    public Task<(GithubIssue? Value, string? Error)> GetIssueAsync(
        string repo, int number, CancellationToken ct = default) =>
        GetAsync($"/repos/{repo}/issues/{Num(number)}", GithubJsonContext.Default.GithubIssue, ct);

    public Task<(GithubIssue? Value, string? Error)> UpdateIssueAsync(
        string repo, int number, GithubIssueRequest request, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Patch, $"/repos/{repo}/issues/{Num(number)}",
            JsonSerializer.Serialize(request, GithubJsonContext.Default.GithubIssueRequest),
            GithubJsonContext.Default.GithubIssue, ct);

    // ── comments ─────────────────────────────────────────────────────────────────────────────────

    public Task<(List<GithubComment>? Value, string? Error)> ListCommentsAsync(
        string repo, int number, CancellationToken ct = default) =>
        PagedAsync($"/repos/{repo}/issues/{Num(number)}/comments?per_page=100",
            GithubJsonContext.Default.ListGithubComment, page => page, ct);

    public Task<(GithubComment? Value, string? Error)> CreateCommentAsync(
        string repo, int number, string body, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Post, $"/repos/{repo}/issues/{Num(number)}/comments",
            JsonSerializer.Serialize(new GithubCommentRequest { Body = body },
                GithubJsonContext.Default.GithubCommentRequest),
            GithubJsonContext.Default.GithubComment, ct);

    // ── milestones (stages) ──────────────────────────────────────────────────────────────────────

    public Task<(List<GithubMilestoneRef>? Value, string? Error)> ListMilestonesAsync(
        string repo, CancellationToken ct = default) =>
        PagedAsync($"/repos/{repo}/milestones?state=all&per_page=100",
            GithubJsonContext.Default.ListGithubMilestoneRef, page => page, ct);

    public Task<(GithubMilestoneRef? Value, string? Error)> CreateMilestoneAsync(
        string repo, string title, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Post, $"/repos/{repo}/milestones",
            JsonSerializer.Serialize(new GithubMilestoneRequest { Title = title },
                GithubJsonContext.Default.GithubMilestoneRequest),
            GithubJsonContext.Default.GithubMilestoneRef, ct);

    // ── transport ────────────────────────────────────────────────────────────────────────────────

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Walks <c>page=1..n</c> until a short page comes back. Bounded at 20 pages (2,000
    /// items): a mirror that walked forever because the server ignored <c>page</c> would hammer the
    /// API, and no conductor board is that large.</summary>
    private async Task<(List<T>? Value, string? Error)> PagedAsync<T>(
        string pathWithQuery, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> info,
        Func<List<T>, List<T>> keep, CancellationToken ct)
    {
        var all = new List<T>();
        for (var page = 1; page <= 20; page++)
        {
            var (batch, error) = await GetAsync($"{pathWithQuery}&page={Num(page)}", info, ct).ConfigureAwait(false);
            if (error is not null) return (null, error);
            if (batch is null || batch.Count == 0) break;
            all.AddRange(keep(batch));
            if (batch.Count < 100) break;
        }
        return (all, null);
    }

    private async Task<(T? Value, string? Error)> GetAsync<T>(
        string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct)
        where T : class
    {
        var url = ApiBase + path;
        try
        {
            RequestCount++;
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            // A repository with no milestones/comments answers 404 for some of these paths on old
            // enterprise builds; an empty result is the honest reading, not a failure.
            if (resp.StatusCode == HttpStatusCode.NotFound) return (null, $"404 not found: {url}");
            if (!resp.IsSuccessStatusCode) return (null, await DescribeAsync(resp, url, ct).ConfigureAwait(false));
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (JsonSerializer.Deserialize(json, info), null);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return (null, $"{ex.GetType().Name}: {ex.Message} ({url})");
        }
    }

    private async Task<(T? Value, string? Error)> SendJsonAsync<T>(
        HttpMethod method, string path, string body,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct)
        where T : class
    {
        var url = ApiBase + path;
        try
        {
            RequestCount++;
            using var req = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, await DescribeAsync(resp, url, ct).ConfigureAwait(false));
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return (JsonSerializer.Deserialize(json, info), null);
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return (null, $"{ex.GetType().Name}: {ex.Message} ({url})");
        }
    }

    /// <summary>The failure sentence. It carries the status, the URL, and — for the two conditions
    /// this integration actually hits — the header GitHub answers with, because "403" alone sends
    /// every reader to the wrong place.</summary>
    private static async Task<string> DescribeAsync(HttpResponseMessage resp, string url, CancellationToken ct)
    {
        var detail = "";
        try
        {
            var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (raw.Length > 0) detail = " — " + raw[..Math.Min(raw.Length, 200)].ReplaceLineEndings(" ");
        }
        catch (Exception ex) when (IsTransport(ex)) { /* the status is the answer; a body is a bonus */ }

        var scopes = resp.Headers.TryGetValues("X-OAuth-Scopes", out var s) ? string.Join(",", s) : null;
        var scopeNote = resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound && scopes is not null
            ? $" [token scopes: {scopes}]"
            : "";
        return $"{(int)resp.StatusCode} {resp.ReasonPhrase} from {url}{scopeNote}{detail}";
    }

    private static bool IsTransport(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or JsonException
            or UriFormatException or InvalidOperationException;

    public void Dispose() => _http.Dispose();
}
