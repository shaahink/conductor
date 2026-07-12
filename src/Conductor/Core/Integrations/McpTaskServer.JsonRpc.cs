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

public sealed class JsonRpcResponse
{
    public string Jsonrpc { get; set; } = "2.0";
    public JsonElement? Id { get; set; }
    public JsonElement? Result { get; set; }
    public JsonRpcError? Error { get; set; }
}

public sealed class JsonRpcError
{
    public int Code { get; set; }
    public string Message { get; set; } = "";
}
