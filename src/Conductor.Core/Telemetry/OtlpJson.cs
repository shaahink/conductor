using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Conductor.Core.Telemetry;

/// <summary>
/// KS7.3 — spans to an OTLP/HTTP JSON request body.
/// </summary>
/// <remarks>
/// OTLP's JSON encoding is protobuf's canonical JSON mapping, and two of its rules are easy to get
/// wrong and produce a body a collector accepts while dropping everything in it:
/// <list type="bullet">
/// <item>64-bit integers are STRINGS. <c>startTimeUnixNano</c>, <c>endTimeUnixNano</c> and every
/// <c>intValue</c> must be quoted — a JSON number loses precision past 2^53 and nanosecond timestamps
/// are past it by a factor of a million.</item>
/// <item>Trace and span ids are lowercase base16 in JSON (they are bytes in protobuf), NOT the
/// hyphenated form a UUID prints as.</item>
/// </list>
/// <para>Written by hand with <see cref="Utf8JsonWriter"/> rather than through the OpenTelemetry SDK:
/// see <see cref="OtelSpan"/> for why the SDK is the wrong tool for exporting a finished log, and this
/// is the whole of what it would have been used for.</para>
/// </remarks>
public static class OtlpJson
{
    /// <summary>One OTLP <c>ExportTraceServiceRequest</c> carrying <paramref name="spans"/>.</summary>
    public static string Request(IReadOnlyList<OtelSpan> spans, string serviceName, string serviceVersion)
    {
        ArgumentNullException.ThrowIfNull(spans);
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteStartArray("resourceSpans");
            w.WriteStartObject();

            w.WriteStartObject("resource");
            w.WriteStartArray("attributes");
            WriteAttribute(w, "service.name", serviceName);
            WriteAttribute(w, "service.version", serviceVersion);
            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartArray("scopeSpans");
            w.WriteStartObject();
            w.WriteStartObject("scope");
            w.WriteString("name", "conductor.otel");
            w.WriteString("version", serviceVersion);
            w.WriteEndObject();

            w.WriteStartArray("spans");
            foreach (var s in spans) WriteSpan(w, s);
            w.WriteEndArray();

            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteSpan(Utf8JsonWriter w, OtelSpan s)
    {
        w.WriteStartObject();
        w.WriteString("traceId", s.TraceId);
        w.WriteString("spanId", s.SpanId);
        if (s.ParentSpanId is { Length: > 0 } parent) w.WriteString("parentSpanId", parent);
        w.WriteString("name", s.Name);
        w.WriteNumber("kind", s.Kind);
        w.WriteString("startTimeUnixNano", Nanos(s.Start));
        // A span whose end precedes its start is rejected by some backends and rendered as a negative
        // bar by others. Durations here are reconstructed (a gate anchors backwards from its finish), so
        // clamp rather than emit an impossibility.
        w.WriteString("endTimeUnixNano", Nanos(s.End < s.Start ? s.Start : s.End));

        w.WriteStartArray("attributes");
        foreach (var a in s.Attributes) WriteAttribute(w, a.Key, a.Value);
        w.WriteEndArray();

        if (s.Events.Count > 0)
        {
            w.WriteStartArray("events");
            foreach (var e in s.Events)
            {
                w.WriteStartObject();
                w.WriteString("timeUnixNano", Nanos(e.Ts));
                w.WriteString("name", e.Name);
                w.WriteStartArray("attributes");
                foreach (var a in e.Attributes) WriteAttribute(w, a.Key, a.Value);
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (s.Status != OtelStatus.Unset)
        {
            w.WriteStartObject("status");
            w.WriteNumber("code", (int)s.Status);
            if (s.StatusMessage is { Length: > 0 } msg) w.WriteString("message", msg);
            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    private static void WriteAttribute(Utf8JsonWriter w, string key, object value)
    {
        w.WriteStartObject();
        w.WriteString("key", key);
        w.WriteStartObject("value");
        switch (value)
        {
            case bool b: w.WriteBoolean("boolValue", b); break;
            case long l: w.WriteString("intValue", l.ToString(CultureInfo.InvariantCulture)); break;
            case int i: w.WriteString("intValue", i.ToString(CultureInfo.InvariantCulture)); break;
            case double d: w.WriteNumber("doubleValue", d); break;
            default: w.WriteString("stringValue", value?.ToString() ?? ""); break;
        }

        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static string Nanos(DateTimeOffset ts) =>
        (ts.ToUnixTimeMilliseconds() * 1_000_000L).ToString(CultureInfo.InvariantCulture);
}
