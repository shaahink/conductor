namespace Conductor.Core.Telemetry;

/// <summary>
/// KS7.3 — one span, in the shape OTLP wants and nothing more.
/// </summary>
/// <remarks>
/// Deliberately NOT the OpenTelemetry SDK's <c>Activity</c>. The SDK is built to instrument a process
/// as it runs; conductor's spans are reconstructed AFTER the fact from an event log that may be days
/// old and may belong to another machine's run, so the one thing the SDK gives — ambient context
/// propagation from the current call stack — is the one thing that would be wrong here. What is left
/// is a data shape and an HTTP POST, which is a dependency this repo does not need to take on.
/// <para>Ids are hex strings (OTLP JSON's base16 encoding), derived deterministically from the run id
/// so that exporting the same run twice yields the SAME trace rather than a duplicate — see
/// <c>OtelIds</c>.</para>
/// </remarks>
public sealed record OtelSpan
{
    public required string TraceId { get; init; }
    public required string SpanId { get; init; }
    public string? ParentSpanId { get; init; }
    public required string Name { get; init; }

    /// <summary>OTLP span kind: 1 INTERNAL, 3 CLIENT. A session is CLIENT (it calls a model provider);
    /// everything conductor does itself is INTERNAL.</summary>
    public int Kind { get; init; } = 1;

    public required DateTimeOffset Start { get; init; }
    public required DateTimeOffset End { get; init; }
    public OtelStatus Status { get; init; } = OtelStatus.Unset;
    public string? StatusMessage { get; init; }
    public IReadOnlyList<KeyValuePair<string, object>> Attributes { get; init; } = [];

    /// <summary>Per-turn usage lands here rather than as child spans: 6,899 token deltas in this repo's
    /// own log would be 6,899 spans, which buries the structure a trace view exists to show. As span
    /// events they stay on the session's timeline where the curve is legible.</summary>
    public IReadOnlyList<OtelSpanEvent> Events { get; init; } = [];
}

/// <summary>KS7.3 — a timestamped point on a span. One API turn's usage, in practice.</summary>
public sealed record OtelSpanEvent(
    string Name,
    DateTimeOffset Ts,
    IReadOnlyList<KeyValuePair<string, object>> Attributes);

/// <summary>OTLP status codes, by their wire numbers.</summary>
public enum OtelStatus
{
    Unset = 0,
    Ok = 1,
    Error = 2,
}
