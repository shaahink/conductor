using System.Text.Json.Serialization;

namespace Conductor.Core.Integrations;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
[JsonSerializable(typeof(JsonRpcRequest))]
[JsonSerializable(typeof(JsonRpcResponse))]
[JsonSerializable(typeof(JsonRpcError))]
internal sealed partial class McpJsonContext : JsonSerializerContext;
