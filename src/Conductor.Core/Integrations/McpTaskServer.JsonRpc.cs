using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations;

public sealed class JsonRpcRequest
{
    public string Jsonrpc { get; set; } = "2.0";
    public string Method { get; set; } = "";
    public JsonElement? Id { get; set; }
    public JsonElement? Params { get; set; }
}

/// <summary>
/// A JSON-RPC 2.0 response. <c>result</c> and <c>error</c> are mutually exclusive by spec — "either
/// the result member or the error member MUST be included, but both members MUST NOT be included" —
/// so the unused one is omitted rather than written as null.
/// </summary>
/// <remarks>
/// W2.1: emitting <c>"error":null</c> next to a result is what a strict MCP client sees as a malformed
/// envelope. Our own test client parsed it happily, so every test passed while the real claude CLI
/// left the server stuck at <c>status:"pending"</c> and the agent could not reach a single conductor
/// tool. <c>id</c> is deliberately NOT omitted when null: the spec requires it to be present-and-null
/// on a response to a request whose id could not be determined (a parse error).
/// </remarks>
public sealed class JsonRpcResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public JsonElement? Id { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Result { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonRpcError? Error { get; set; }
}

public sealed class JsonRpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = "";
}
