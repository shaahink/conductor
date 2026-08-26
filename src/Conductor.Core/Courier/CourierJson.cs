using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Core.Courier;

/// <summary>The shared JSON shape. One options object for both ends: camelCase, case-insensitive on
/// read, nulls omitted — the same settings every other courier file uses, so a person reading
/// <c>courier.run.json</c> and a person reading a captured request see the same names.</summary>
public static class CourierJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
