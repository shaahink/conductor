using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// DV6.2 — the GraphQL half of the client, and the only door Projects v2 has.
///
/// <para><b>Why this is not another REST verb.</b> Projects v2 exists ONLY in GitHub's GraphQL API.
/// There is no REST endpoint that moves a board item, so a REST attempt would be a plausible-looking
/// no-op that passes a naive test — which is exactly why KS9.3 refused to write one and left the
/// project half unbuilt rather than half-built.</para>
///
/// <para><b>Hand-written JSON, both ways.</b> A GraphQL envelope is <c>{query, variables}</c> with an
/// open-shaped variables object, and its reply is an arbitrary tree. A source-generated context per
/// query would be a type per query; <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/> are
/// both AOT-safe and reflection-free, which is the property the contexts exist to protect.</para>
///
/// <para><b>A 200 is not a success.</b> GraphQL answers a failed operation with HTTP 200 and an
/// <c>errors</c> array; a caller that checked the status code would read "insufficient scopes" as a
/// board that mirrored fine. Errors are lifted into this client's <c>(value, error)</c> pair with
/// their messages verbatim, because GitHub's own scope error names the scope to grant.</para>
/// </summary>
public sealed partial class GithubClient
{
    /// <summary>
    /// How many GraphQL MUTATIONS this client has issued. The idempotence bar — "a second pass moves
    /// nothing" — is a number about writes, and counting requests would not distinguish a pass that
    /// looked and left from one that wrote everything again.
    /// </summary>
    public int MutationCount { get; private set; }

    /// <summary>
    /// One GraphQL document, with its variables. Returns the <c>data</c> object detached from its
    /// document (so the caller need not manage a <see cref="JsonDocument"/> lifetime), or an error
    /// sentence — never an exception, on the same posture as every REST verb here.
    /// </summary>
    /// <param name="document">The query or mutation text. A document whose first non-space word is
    /// <c>mutation</c> counts toward <see cref="MutationCount"/>; that classification is made HERE,
    /// from the text actually sent, so it cannot drift from what went on the wire.</param>
    /// <param name="variables">Variable values. Strings, integers and nulls only — the whole of what
    /// the Projects v2 documents in this integration take.</param>
    public async Task<(JsonElement? Data, string? Error)> GraphQlAsync(
        string document, IReadOnlyDictionary<string, object?>? variables = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var url = ApiBase + "/graphql";
        var isMutation = document.TrimStart().StartsWith("mutation", StringComparison.Ordinal);
        try
        {
            RequestCount++;
            if (isMutation) MutationCount++;
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(Envelope(document, variables), Encoding.UTF8, "application/json"),
            };
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return (null, await DescribeAsync(resp, url, ct).ConfigureAwait(false));

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(json);
            if (Errors(parsed.RootElement) is { } errors) return (null, errors);
            return parsed.RootElement.TryGetProperty("data", out var data) && data.ValueKind is not JsonValueKind.Null
                ? (data.Clone(), null)
                : (null, $"graphql answered with no data and no errors ({url})");
        }
        catch (Exception ex) when (IsTransport(ex))
        {
            return (null, $"{ex.GetType().Name}: {ex.Message} ({url})");
        }
    }

    /// <summary>Every message in the <c>errors</c> array, joined — or null when there was none.
    /// Verbatim on purpose: GitHub's INSUFFICIENT_SCOPES message names the scope to grant and the
    /// page to grant it on, and a paraphrase would lose exactly the actionable half.</summary>
    private static string? Errors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.ValueKind is not JsonValueKind.Array
            || errors.GetArrayLength() == 0)
            return null;

        var messages = new List<string>();
        foreach (var error in errors.EnumerateArray())
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            var type = error.TryGetProperty("type", out var t) ? t.GetString() : null;
            messages.Add(type is null ? message ?? "(no message)" : $"{type}: {message}");
        }
        // Distinct: one missing scope is reported once per FIELD it blocked, which on a board query
        // is six identical sentences about the same missing scope.
        return string.Join(" | ", messages.Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// The <c>{query, variables}</c> envelope, escaped by <see cref="JsonEncodedText"/> rather than
    /// concatenated raw — a GraphQL document is full of quotes and braces, and hand-quoting one is
    /// how an injection or a 400 gets built. A writer is not used here on purpose: it is an
    /// <c>IAsyncDisposable</c>, and this is a synchronous string builder.
    /// </summary>
    private static string Envelope(string document, IReadOnlyDictionary<string, object?>? variables)
    {
        var sb = new StringBuilder();
        sb.Append("{\"query\":").Append(Quoted(document)).Append(",\"variables\":{");
        var first = true;
        foreach (var (name, value) in variables ?? new Dictionary<string, object?>(StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Quoted(name)).Append(':');
            switch (value)
            {
                case null: sb.Append("null"); break;
                case int number: sb.Append(number.ToString(CultureInfo.InvariantCulture)); break;
                case string text: sb.Append(Quoted(text)); break;
                default:
                    throw new ArgumentException(
                        $"graphql variable '{name}' is a {value.GetType().Name}; only string, int and null are supported",
                        nameof(variables));
            }
        }
        return sb.Append("}}").ToString();
    }

    private static string Quoted(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
}
