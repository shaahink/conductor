using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// DV6.4 — the code-scanning half of the client: one POST that hands GitHub a SARIF run, and the
/// GET that says what GitHub made of it.
///
/// <para>The upload is asynchronous by design. A 202 means "accepted", NOT "ingested": GitHub
/// validates and processes the document afterwards, and a document it rejects fails on the STATUS
/// call with the reason. A proof that stops at the 202 has proved nothing about the SARIF, so the
/// status call is part of the path and not a debugging aid.</para>
/// </summary>
public sealed partial class GithubClient
{
    /// <summary>Uploads one SARIF run. <c>validate=true</c> asks GitHub to check the document
    /// against the schema rather than accept it and drop it silently — the whole reason this returns
    /// a usable failure instead of a quiet nothing.</summary>
    public async Task<(GithubSarifUpload? Value, string? Error)> UploadSarifAsync(
        string repo, string sarifJson, string commitSha, string gitRef, CancellationToken ct = default)
    {
        var request = new GithubSarifRequest
        {
            CommitSha = commitSha,
            Ref = gitRef,
            Sarif = await GzipBase64Async(sarifJson, ct).ConfigureAwait(false),
            Validate = true,
        };
        var body = JsonSerializer.Serialize(request, GithubJsonContext.Default.GithubSarifRequest);
        return await SendJsonAsync(
            HttpMethod.Post, $"/repos/{repo}/code-scanning/sarifs", body,
            GithubJsonContext.Default.GithubSarifUpload, ct).ConfigureAwait(false);
    }

    /// <summary>What became of an upload: <c>pending</c>, <c>complete</c>, or <c>failed</c> with the
    /// errors GitHub found in the document.</summary>
    public Task<(GithubSarifStatus? Value, string? Error)> SarifStatusAsync(
        string repo, string sarifId, CancellationToken ct = default) =>
        GetAsync($"/repos/{repo}/code-scanning/sarifs/{sarifId}", GithubJsonContext.Default.GithubSarifStatus, ct);

    /// <summary>The repository as GitHub sees it — needed for one fact only: whether it is private,
    /// which is what decides between "this is free" and "this needs Advanced Security".</summary>
    public Task<(GithubRepoInfo? Value, string? Error)> GetRepoAsync(
        string repo, CancellationToken ct = default) =>
        GetAsync($"/repos/{repo}", GithubJsonContext.Default.GithubRepoInfo, ct);

    /// <summary>GitHub takes the SARIF as gzip-then-base64, not as JSON. Getting this wrong is a
    /// 400 with a body that says nothing useful, so it lives in one place.</summary>
    internal static async Task<string> GzipBase64Async(string json, CancellationToken ct = default)
    {
        var buffer = new MemoryStream();
        var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true);
        await using (gzip.ConfigureAwait(false))
        {
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
        }
        return Convert.ToBase64String(buffer.ToArray());
    }

    /// <summary>The inverse, for tests and for anyone reading a captured request body: what the
    /// server actually received.</summary>
    internal static async Task<string> UngzipBase64Async(string encoded, CancellationToken ct = default)
    {
        using var source = new MemoryStream(Convert.FromBase64String(encoded));
        var gzip = new GZipStream(source, CompressionMode.Decompress);
        await using var closing = gzip.ConfigureAwait(false);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }
}
