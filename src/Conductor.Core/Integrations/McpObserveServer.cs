using System.Text.Json;

namespace Conductor.Core.Integrations;

/// <summary>
/// KS8.1 — the read-only MCP surface. A second MCP server, separate from
/// <see cref="McpTaskServer"/> by design: this one exposes the run as RESOURCES an outside client
/// can read (history, status, money) and exposes no tools at all.
/// </summary>
/// <remarks>
/// <para>Two servers rather than one flag on one server, because the difference is the threat model,
/// not a setting. <see cref="McpTaskServer"/> is handed to the agent conductor itself spawned, inside
/// the run it is driving, and it writes: task status, ledger notes, background children, injected
/// instructions. This surface is for anything else that wants to look — an editor, a dashboard, a
/// second model — and the 2026 MCP incident record (OWASP's tool-poisoning entry; the CVSS 9+
/// disclosures against MCP servers that mixed reads with privileged writes) says the way that
/// combination fails is a client being talked into calling the write. There is nothing here to talk it
/// into. See <c>docs/dev/adr/0007-read-only-mcp-surface.md</c>.</para>
/// <para>Read-only is enforced by SQLite, not by discipline: every answer is built from
/// <see cref="History.RunHistory"/> and <see cref="History.ArchiveView"/>, which read through
/// <see cref="History.RunArchive"/>'s <c>Mode=ReadOnly</c> connection. A write added here by mistake
/// would not compile into a working statement — it would be refused by the connection.</para>
/// </remarks>
public sealed partial class McpObserveServer
{
    /// <summary>The state home whose catalogue this server serves.</summary>
    private readonly string _root;

    public McpObserveServer(string root) => _root = root;

    internal const string DefaultProtocolVersion = "2024-11-05";
    internal const string ServerName = "conductor-observe";

    /// <summary>The one sentence every write attempt gets back. Named so the test that proves the
    /// surface has no tools can assert on the refusal rather than on an error code alone.</summary>
    internal const string WriteRefusal =
        "conductor-observe is read-only: it serves resources and has no tools. Control operations "
        + "(run, pause, resume, abort, approve, skip, inject, task writes) are excluded by design — "
        + "see docs/dev/adr/0007-read-only-mcp-surface.md.";

    /// <summary>
    /// The MCP JSON-RPC 2.0 loop over stdio — one message per line in, one response line out.
    /// Identical framing to <see cref="McpTaskServer.RunAsync"/>; only the dispatch differs.
    /// </summary>
    public async Task RunAsync(TextReader stdin, TextWriter stdout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stdin);
        ArgumentNullException.ThrowIfNull(stdout);
        string? line;
        while (!ct.IsCancellationRequested && (line = await stdin.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonRpcResponse? response;
            try
            {
                var req = JsonSerializer.Deserialize(line, McpJsonContext.Default.JsonRpcRequest);
                if (req == null) continue;
                response = HandleRequest(req);
            }
            catch (JsonException)
            {
                response = new JsonRpcResponse { Id = null, Error = new JsonRpcError { Code = -32700, Message = "Parse error" } };
            }

            if (response == null) continue;
            var json = JsonSerializer.Serialize(response, McpJsonContext.Default.JsonRpcResponse);
            await stdout.WriteLineAsync(json).ConfigureAwait(false);
            await stdout.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The protocol revision the client asked for, when it named one.</summary>
    private static string? RequestedProtocolVersion(JsonElement? @params)
    {
        if (@params is not { ValueKind: JsonValueKind.Object } p) return null;
        if (!p.TryGetProperty("protocolVersion", out var v) || v.ValueKind != JsonValueKind.String) return null;
        var version = v.GetString();
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    /// <summary>Dispatch. Internal so tests drive the surface directly instead of through a pipe —
    /// the stdio loop above is framing and is proved once, by the live transcript.</summary>
    internal JsonRpcResponse? HandleRequest(JsonRpcRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        var id = req.Id;
        if (id == null && req.Method != "notifications/initialized")
            return null; // unrecognized notification — ignore

        return req.Method switch
        {
            "initialize" => Ok(id, Handshake(req.Params)),
            "notifications/initialized" => null,
            "ping" => Ok(id, JsonSerializer.SerializeToElement(new { })),
            "resources/list" => Ok(id, ListResources()),
            "resources/templates/list" => Ok(id, ListTemplates()),
            "resources/read" => Read(id, req.Params),
            // Answered, not errored: "how many tools do you have" deserves the true answer, and a
            // client that sees an empty array knows it asked a server that has none — where a
            // method-not-found reads as an older server that might have them behind another name.
            "tools/list" => Ok(id, JsonSerializer.SerializeToElement(new { tools = Array.Empty<object>() })),
            "tools/call" => new JsonRpcResponse { Id = id, Error = new JsonRpcError { Code = -32601, Message = WriteRefusal } },
            _ => new JsonRpcResponse { Id = id, Error = new JsonRpcError { Code = -32601, Message = $"Method not found: {req.Method}" } },
        };
    }

    private static JsonRpcResponse Ok(JsonElement? id, JsonElement result) =>
        new() { Id = id, Result = result };

    /// <summary>The initialize result. <c>capabilities</c> carries <c>resources</c> and NOT
    /// <c>tools</c> — the declaration a conforming client reads before it ever asks.</summary>
    private static JsonElement Handshake(JsonElement? @params) => JsonSerializer.SerializeToElement(new
    {
        protocolVersion = RequestedProtocolVersion(@params) ?? DefaultProtocolVersion,
        capabilities = new { resources = new { subscribe = false, listChanged = false } },
        serverInfo = new { name = ServerName, version = "1.0.0" },
        instructions = WriteRefusal,
    });

    private JsonRpcResponse Read(JsonElement? id, JsonElement? @params)
    {
        var uri = "";
        if (@params is { ValueKind: JsonValueKind.Object } p
            && p.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String)
            uri = u.GetString() ?? "";

        string text;
        try
        {
            text = ReadResource(uri, out var refusal);
            if (refusal is { Length: > 0 })
                return new JsonRpcResponse { Id = id, Error = new JsonRpcError { Code = -32602, Message = refusal } };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new JsonRpcResponse { Id = id, Error = new JsonRpcError { Code = -32603, Message = ex.Message } };
        }

        return Ok(id, JsonSerializer.SerializeToElement(new
        {
            contents = new[] { new { uri, mimeType = "application/json", text } },
        }));
    }
}
