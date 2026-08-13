using System.Text.Json.Serialization;

namespace Conductor.Core.History;

/// <summary>Source-generated serialisation for the two <c>conductor history --json</c> shapes.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(RunHistoryListJson))]
[JsonSerializable(typeof(RunHistoryDetailJson))]
// KS1.3: registered explicitly rather than left to reachability, because the day a caller serialises
// the array on its own is the day a trimmed build discovers there is no metadata for it.
[JsonSerializable(typeof(UnreadableEntryJson))]
[JsonSerializable(typeof(IReadOnlyList<UnreadableEntryJson>))]
public sealed partial class RunHistoryJsonContext : JsonSerializerContext;
