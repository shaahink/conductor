using System.Globalization;
using System.Net.Http;
using System.Text;

namespace Conductor.Core.Telemetry;

/// <summary>
/// KS7.3 — posts spans to an OTLP/HTTP endpoint, in batches.
/// </summary>
/// <remarks>
/// Batching is not an optimisation. A collector's default request ceiling is a few megabytes and this
/// repo's own run carries ~7,000 per-turn events on ~100 session spans; one request would be refused
/// with a 413 and the operator would be told "export failed" about a run that is perfectly exportable.
/// <para>The endpoint is the base URL, <c>http://host:4318</c>, and <c>/v1/traces</c> is appended — the
/// same convention <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> uses, so an operator can paste what their
/// collector docs gave them.</para>
/// </remarks>
public sealed class OtlpHttpExporter(HttpClient http, string endpoint, string serviceName, string serviceVersion)
{
    /// <summary>Spans per request. Chosen against the collector's 4 MiB default with per-turn events
    /// attached; a session span with 200 turns is ~40 KB.</summary>
    private const int BatchSize = 50;

    public async Task<OtlpExportResult> ExportAsync(IReadOnlyList<OtelSpan> spans, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var url = endpoint.TrimEnd('/') + "/v1/traces";
        var sent = 0;
        var batches = 0;

        for (var i = 0; i < spans.Count; i += BatchSize)
        {
            var batch = spans.Skip(i).Take(BatchSize).ToList();
            var body = OtlpJson.Request(batch, serviceName, serviceVersion);
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(new Uri(url), content, ct).ConfigureAwait(false);
            batches++;

            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new OtlpExportResult(false, sent, batches,
                    $"{url} answered {(int)response.StatusCode} {response.ReasonPhrase}: {Trim(detail)}");
            }

            sent += batch.Count;
        }

        return new OtlpExportResult(true, sent, batches, null);
    }

    private static string Trim(string s) =>
        s.Length <= 300 ? s : string.Concat(s.AsSpan(0, 300), "...");
}

/// <summary>What the export did, in the operator's terms. <paramref name="Error"/> is the collector's
/// own words when it refused — never a paraphrase, because "export failed" has cost debugging time
/// before.</summary>
public sealed record OtlpExportResult(bool Ok, int SpansSent, int Batches, string? Error)
{
    public string Describe() => Ok
        ? $"{SpansSent.ToString(CultureInfo.InvariantCulture)} spans in {Batches.ToString(CultureInfo.InvariantCulture)} batches"
        : Error ?? "refused";
}
