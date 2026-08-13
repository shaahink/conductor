using System.Text.Json.Serialization;

namespace Conductor.Commands;

/// <summary>The <c>--json</c> shape of a record change.</summary>
public sealed record RunRecordJson(bool Ok, string Message);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RunRecordJson))]
internal sealed partial class RunRecordJsonContext : JsonSerializerContext;
