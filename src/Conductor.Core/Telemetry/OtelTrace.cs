using System.Globalization;
using Conductor.Core.Events;

namespace Conductor.Core.Telemetry;

/// <summary>
/// KS7.3 — turns a run's event log into a trace, with the OpenTelemetry <c>gen_ai.*</c> semantic
/// convention names where they apply and conductor's own namespace where they do not.
/// </summary>
/// <remarks>
/// The shape a viewer renders:
/// <code>
/// conductor.run                       one root, the whole run
///   stage KS7                         one per StageEntered
///     chat claude-opus-5              one per session, CLIENT, gen_ai.* usage + per-turn events
///       execute_tool task_update      one per MCP call
///     gate build                      one per GateFinished
/// </code>
/// <para><b>Why gen_ai names on the session and not on the run.</b> The convention describes a call to
/// a model: <c>gen_ai.usage.input_tokens</c> means the tokens THAT call sent. A conductor session is
/// that unit of work — one agent process, one model, one usage total — so it carries the convention
/// attributes and the span name form <c>{operation} {model}</c>. The run and the stage are conductor's
/// own scheduling, and inventing gen_ai attributes for them would tell a backend something untrue.</para>
/// <para><b>Why the cache split matters here.</b> The reason this export exists at all is that ~98% of
/// this project's tokens are cache reads; a trace that reported one <c>input_tokens</c> figure would
/// hide exactly the number the era is trying to move. So the session carries the read half and the
/// write half separately — see <see cref="TokenDelta.CacheWrite"/>, which is a SUBSET of
/// <see cref="TokenDelta.Input"/> and is emitted as such.</para>
/// </remarks>
public static class OtelTrace
{
    /// <summary>Anthropic's own name in the convention's <c>gen_ai.system</c> enumeration.</summary>
    private const string System = "anthropic";

    /// <summary>Build the spans for one run. Events may arrive in any order; they are sorted by
    /// <see cref="ConductorEvent.Seq"/>, which is the log's own ground truth.</summary>
    public static IReadOnlyList<OtelSpan> Build(IEnumerable<ConductorEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var log = events.OrderBy(e => e.Seq).ToList();
        if (log.Count == 0) return [];

        var runId = log.Find(e => !string.IsNullOrEmpty(e.RunId))?.RunId ?? "unknown";
        var ctx = new OtelBuildContext(runId, OtelIds.Trace(runId), log);

        var spans = new List<OtelSpan> { Root(ctx) };
        spans.AddRange(Stages(ctx));
        spans.AddRange(Sessions(ctx));
        spans.AddRange(Gates(ctx));
        spans.AddRange(Tools(ctx));
        return spans;
    }

    private static OtelSpan Root(OtelBuildContext ctx)
    {
        var started = ctx.Log.OfType<RunStarted>().FirstOrDefault();
        var finished = ctx.Log.OfType<RunFinished>().LastOrDefault();
        var totals = LiveMetrics.RunWide(ctx.Log);

        var attrs = new List<KeyValuePair<string, object>>
        {
            new("conductor.run.id", ctx.RunId),
            new("conductor.run.sessions", (long)ctx.Log.OfType<SessionStarted>().Count()),
            new("conductor.run.stages", (long)ctx.Log.OfType<StageEntered>().Count()),
            new("conductor.run.checkpoints_confirmed", (long)ctx.Log.OfType<CheckpointConfirmed>().Count()),
            // The run's own token integral, so a backend can chart spend without folding the children.
            new("gen_ai.usage.input_tokens", totals.Input),
            new("gen_ai.usage.output_tokens", totals.Output),
            new("gen_ai.usage.cache_read_input_tokens", totals.CacheRead),
            new("gen_ai.usage.cache_creation_input_tokens", ctx.Log.OfType<TokenDelta>().Sum(t => t.CacheWrite)),
        };
        if (started is not null)
        {
            attrs.Add(new("conductor.plan", started.Plan));
            attrs.Add(new("conductor.repo", started.Repo));
            if (started.Branch is { Length: > 0 } b) attrs.Add(new("conductor.branch", b));
            if (started.DriverVersion is { Length: > 0 } v) attrs.Add(new("conductor.driver.version", v));
        }
        if (finished is not null) attrs.Add(new("conductor.run.status", finished.Status));

        return new OtelSpan
        {
            TraceId = ctx.Trace,
            SpanId = ctx.RootSpanId,
            Name = "conductor.run",
            Start = ctx.Log[0].Ts,
            End = finished?.Ts ?? ctx.Log[^1].Ts,
            Status = finished is null ? OtelStatus.Unset
                : string.Equals(finished.Status, "completed", StringComparison.OrdinalIgnoreCase) ? OtelStatus.Ok : OtelStatus.Error,
            StatusMessage = finished?.Status,
            Attributes = attrs,
        };
    }

    private static List<OtelSpan> Stages(OtelBuildContext ctx)
    {
        var entries = ctx.Log.OfType<StageEntered>().ToList();
        var spans = new List<OtelSpan>(entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            // A stage ends where the next one begins. Re-entry after a resume is a SECOND span, keyed by
            // seq — collapsing them would draw a stage as one long bar across a gap it was not running in.
            var end = i + 1 < entries.Count ? entries[i + 1].Ts : ctx.Log[^1].Ts;
            var confirmed = ctx.Log.OfType<StageConfirmed>().Any(c =>
                string.Equals(c.StageId, e.StageId, StringComparison.Ordinal) && c.Seq > e.Seq && c.Ts <= end);

            var attrs = new List<KeyValuePair<string, object>>
            {
                new("conductor.stage.id", e.StageId),
                new("conductor.stage.confirmed", confirmed),
            };
            if (e.Title is { Length: > 0 } t) attrs.Add(new("conductor.stage.title", t));

            spans.Add(new OtelSpan
            {
                TraceId = ctx.Trace,
                SpanId = ctx.StageSpanId(e.Seq),
                ParentSpanId = ctx.RootSpanId,
                Name = "stage " + e.StageId,
                Start = e.Ts,
                End = end,
                Attributes = attrs,
            });
        }

        return spans;
    }

    private static List<OtelSpan> Sessions(OtelBuildContext ctx)
    {
        var spans = new List<OtelSpan>();
        foreach (var s in ctx.Log.OfType<SessionStarted>())
        {
            var sid = s.Number.ToString(CultureInfo.InvariantCulture);
            var fin = ctx.Log.OfType<SessionFinished>().FirstOrDefault(f => f.Number == s.Number && f.Seq > s.Seq);
            var model = s.Model is { Length: > 0 } m ? m : "unknown";

            var totals = LiveMetrics.ForSession(ctx.Log, s.Number);
            var context = LiveMetrics.ContextForSession(ctx.Log, s.Number);
            var cacheWrite = ctx.Log.OfType<TokenDelta>()
                .Where(t => string.Equals(t.SessionId, sid, StringComparison.Ordinal)).Sum(t => t.CacheWrite);

            var attrs = new List<KeyValuePair<string, object>>
            {
                new("gen_ai.system", System),
                new("gen_ai.provider.name", System),
                new("gen_ai.operation.name", "chat"),
                new("gen_ai.request.model", model),
                new("gen_ai.usage.input_tokens", totals.Input),
                new("gen_ai.usage.output_tokens", totals.Output),
                new("gen_ai.usage.cache_read_input_tokens", totals.CacheRead),
                new("gen_ai.usage.cache_creation_input_tokens", cacheWrite),
                new("gen_ai.conversation.id", s.AgentSessionId ?? sid),
                new("conductor.session.number", (long)s.Number),
                new("conductor.session.kind", s.Kind),
                new("conductor.session.attempt", (long)s.Attempt),
                new("conductor.stage.id", s.StageId),
                // K4.1's per-turn context profile, computed by K4.1's own fold — see the reconciliation
                // test. This is the curve the checkpoint asks a collector to render.
                new("conductor.context.high_water_tokens", context.HighWaterTokens),
                new("conductor.context.mean_turn_tokens", context.MeanTurnTokens),
                new("conductor.context.turns", (long)context.Turns),
            };
            if (s.Persona is { Length: > 0 } p) attrs.Add(new("conductor.session.persona", p));
            if (fin is not null)
            {
                attrs.Add(new("conductor.session.outcome", fin.Outcome));
                if (fin.CostUsd is { } cost) attrs.Add(new("conductor.session.cost_usd", (double)cost));
            }

            spans.Add(new OtelSpan
            {
                TraceId = ctx.Trace,
                SpanId = ctx.SessionSpanId(s.Seq),
                ParentSpanId = ctx.StageSpanIdAt(s.Seq),
                Name = "chat " + model,
                Kind = 3,
                Start = s.Ts,
                End = fin?.Ts ?? ctx.Log[^1].Ts,
                Status = fin is null ? OtelStatus.Unset
                    : string.Equals(fin.Outcome, "success", StringComparison.OrdinalIgnoreCase) ? OtelStatus.Ok : OtelStatus.Error,
                StatusMessage = fin?.Outcome,
                Attributes = attrs,
                Events = Turns(ctx, sid, s.Seq, fin?.Seq ?? long.MaxValue),
            });
        }

        return spans;
    }

    /// <summary>The per-turn curve: one span event per deduplicated API call, carrying the four-way split
    /// and the prompt size that call re-sent (<c>Input + CacheRead</c> — K4.1's definition, unchanged).</summary>
    private static List<OtelSpanEvent> Turns(OtelBuildContext ctx, string sessionId, long fromSeq, long toSeq)
    {
        var turns = new List<OtelSpanEvent>();
        foreach (var td in ctx.Log.OfType<TokenDelta>())
        {
            if (!string.Equals(td.SessionId, sessionId, StringComparison.Ordinal)) continue;
            if (td.Seq < fromSeq || td.Seq > toSeq) continue;
            turns.Add(new OtelSpanEvent("gen_ai.turn", td.Ts,
            [
                new("gen_ai.usage.input_tokens", td.Input),
                new("gen_ai.usage.output_tokens", td.Output),
                new("gen_ai.usage.cache_read_input_tokens", td.CacheRead),
                new("gen_ai.usage.cache_creation_input_tokens", td.CacheWrite),
                new("conductor.context.prompt_tokens", td.Input + td.CacheRead),
            ]));
        }

        return turns;
    }

    private static List<OtelSpan> Gates(OtelBuildContext ctx)
    {
        var spans = new List<OtelSpan>();
        foreach (var g in ctx.Log.OfType<GateFinished>())
        {
            var attrs = new List<KeyValuePair<string, object>>
            {
                new("conductor.gate.name", g.Name),
                new("conductor.gate.passed", g.Passed),
                new("conductor.gate.skipped", g.Skipped),
                new("conductor.gate.optional", g.Optional),
                new("conductor.gate.exit_code", (long)g.ExitCode),
            };
            if (g.Scope is { Length: > 0 } sc) attrs.Add(new("conductor.gate.scope", sc));

            spans.Add(new OtelSpan
            {
                TraceId = ctx.Trace,
                SpanId = OtelIds.Span(ctx.RunId, "gate/" + g.Seq.ToString(CultureInfo.InvariantCulture)),
                ParentSpanId = ctx.SessionSpanIdAt(g) ?? ctx.StageSpanIdAt(g.Seq),
                Name = "gate " + g.Name,
                // A gate reports duration, not a start: the span is anchored backwards from the finish.
                Start = g.Ts.AddMilliseconds(-g.DurationMs),
                End = g.Ts,
                Status = g.Skipped ? OtelStatus.Unset : g.Passed ? OtelStatus.Ok : OtelStatus.Error,
                Attributes = attrs,
            });
        }

        return spans;
    }

    private static List<OtelSpan> Tools(OtelBuildContext ctx)
    {
        var spans = new List<OtelSpan>();
        foreach (var c in ctx.Log.OfType<McpCallFinished>())
        {
            spans.Add(new OtelSpan
            {
                TraceId = ctx.Trace,
                SpanId = OtelIds.Span(ctx.RunId, "tool/" + c.Seq.ToString(CultureInfo.InvariantCulture)),
                ParentSpanId = ctx.SessionSpanIdAt(c) ?? ctx.StageSpanIdAt(c.Seq),
                // The convention's span name for a tool invocation is "execute_tool {tool name}".
                Name = "execute_tool " + c.ToolName,
                Kind = 3,
                Start = c.Ts.AddMilliseconds(-c.DurationMs),
                End = c.Ts,
                Status = c.Success ? OtelStatus.Ok : OtelStatus.Error,
                Attributes =
                [
                    new("gen_ai.operation.name", "execute_tool"),
                    new("gen_ai.tool.name", c.ToolName),
                    new("gen_ai.tool.type", "extension"),
                    new("conductor.tool.success", c.Success),
                ],
            });
        }

        return spans;
    }
}
