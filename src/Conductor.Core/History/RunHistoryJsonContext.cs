using System.Text.Json.Serialization;

namespace Conductor.Core.History;

/// <summary>Source-generated serialisation for the two <c>conductor history --json</c> shapes.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RunHistoryListJson))]
[JsonSerializable(typeof(RunHistoryDetailJson))]
public sealed partial class RunHistoryJsonContext : JsonSerializerContext;
