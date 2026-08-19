using System.Globalization;
using System.Text;
using System.Text.Json;

using Conductor.Core.Events;
using Conductor.Core.History;

namespace Conductor.Core.Interop;

/// <summary>
/// KS8.2 — a finished run as an ATIF trajectory (Agent Trajectory Interchange Format, the Harbor /
/// Terminal-Bench interchange spec; RFC 0001 in <c>harbor-framework/harbor</c>).
/// </summary>
/// <remarks>
/// <para><b>What maps to what.</b> ATIF describes one agent's interaction history. Conductor's run is
/// exactly that at a coarser grain: the <i>agent</i> is conductor, each <i>step</i> is one session it
/// dispatched, and the <i>observation</i> is what the engine fed back — the gate battery's verdict,
/// the checkpoints it confirmed, the commits it found. The system steps between them are the run's
/// own framing: the brief, each stage entered, and how it ended.</para>
///
/// <para><b>Two sources, deliberately.</b> The session rows are the spine, because every catalogued
/// run has them — including the v1..v10 databases whose logs predate half the event kinds. The event
/// log then ENRICHES: stage titles, and the per-gate breakdown attributed by walking the fold in
/// order and bucketing each <see cref="GateFinished"/> under the last <see cref="SessionStarted"/>
/// seen. <c>extra.event_log_steps</c> says which of the two a reader is holding, so a thin trajectory
/// off an old database is legible as thin rather than as a run that ran no gates.</para>
///
/// <para><b>Billed dollars only.</b> <c>cost_usd</c> and <c>total_cost_usd</c> are the amounts the
/// provider charged, taken off the cost rows. Conductor has no price table by design, so nothing here
/// is modelled from a token count — which is also why ATIF's own
/// <c>cost = non_cached x rate + cached x rate + completion x rate</c> derivation is NOT applied.
/// <c>prompt_tokens</c> INCLUDES <c>cached_tokens</c>, per that same formula.</para>
///
/// <para>Hand-written with <see cref="Utf8JsonWriter"/> for the reason <c>MoneyJson</c> is: this is a
/// wire format someone else validates, and a source-generated context for it would be a dozen types
/// whose optionality rules are looser than the spec's.</para>
/// </remarks>
public static class AtifExport
{
    /// <summary>The revision this exporter writes. Stamped into every file as <c>schema_version</c>.</summary>
    public const string SchemaVersion = "ATIF-v1.7";

    /// <summary>The agent name every conductor trajectory carries.</summary>
    public const string AgentName = "conductor";

    /// <summary>One run, as one ATIF trajectory document.</summary>
    /// <param name="run">The run row.</param>
    /// <param name="repoLabel">The repo's leaf name — the label every other surface prints.</param>
    /// <param name="reconciledStatus">KS1.3's word, not the stored column. Both are written.</param>
    /// <param name="sessions">Session rows, the step spine.</param>
    /// <param name="costs">Cost rows, per session and category. Billed.</param>
    /// <param name="events">The event log, or empty for a database whose log did not survive.</param>
    /// <param name="exportedUtc">Stamped into <c>extra.exported_utc</c>. Passed in rather than read
    /// off the clock so a golden test can pin it.</param>
    public static string Serialize(
        ArchivedRun run, string repoLabel, string reconciledStatus,
        IReadOnlyList<ArchivedSession> sessions, IReadOnlyList<ArchivedCost> costs,
        IReadOnlyList<ConductorEvent> events, DateTimeOffset exportedUtc)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(events);

        var stageTitles = StageTitles(events);
        var gatesBySession = GatesBySession(events);
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema_version", SchemaVersion);
            w.WriteString("session_id", run.RunId);
            w.WriteString("trajectory_id", run.RunId);
            WriteAgent(w, run, repoLabel);
            var steps = WriteSteps(w, run, repoLabel, reconciledStatus, sessions, costs, stageTitles, gatesBySession);
            WriteFinalMetrics(w, costs, steps);
            WriteRootExtra(w, run, repoLabel, reconciledStatus, sessions, events, exportedUtc);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteAgent(Utf8JsonWriter w, ArchivedRun run, string repoLabel)
    {
        w.WriteStartObject("agent");
        w.WriteString("name", AgentName);
        // ATIF requires a version string. A v1..v10 row records the assembly version (2.0.0.0) and
        // nothing more — true of every build ever made — so it is written as-is rather than dressed
        // up as provenance it never had, exactly as `EngineStampText` does everywhere else.
        w.WriteString("version", run.EngineStampText ?? "unknown");
        w.WriteStartObject("extra");
        w.WriteString("plan", run.PlanName);
        w.WriteString("repo", repoLabel);
        if (run.Branch is { Length: > 0 } branch) w.WriteString("branch", branch);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    /// <summary>The steps, in run order. Returns how many were written — <c>total_steps</c>.</summary>
    private static int WriteSteps(
        Utf8JsonWriter w, ArchivedRun run, string repoLabel, string reconciledStatus,
        IReadOnlyList<ArchivedSession> sessions, IReadOnlyList<ArchivedCost> costs,
        IReadOnlyDictionary<string, string> stageTitles,
        IReadOnlyDictionary<int, List<GateFinished>> gatesBySession)
    {
        w.WriteStartArray("steps");
        var id = 0;

        WriteSystemStep(w, ++id, run.StartedUtc, Brief(run, repoLabel));

        var stage = "";
        foreach (var s in sessions.OrderBy(s => s.Number))
        {
            if (!string.Equals(s.StageId, stage, StringComparison.Ordinal))
            {
                stage = s.StageId;
                var title = stageTitles.TryGetValue(stage, out var t) && t.Length > 0 ? $" — {t}" : "";
                WriteSystemStep(w, ++id, s.StartedUtc, $"Stage {stage} entered{title}.");
            }
            WriteAgentStep(w, ++id, s, costs, gatesBySession);
        }

        WriteSystemStep(w, ++id, run.EndedUtc ?? run.LastActivityUtc,
            $"Run {reconciledStatus} after {sessions.Count} sessions, "
            + $"${run.CostUsd.ToString("0.00", CultureInfo.InvariantCulture)} billed.");

        w.WriteEndArray();
        return id;
    }

    private static string Brief(ArchivedRun run, string repoLabel) =>
        $"Run of plan \"{run.PlanName}\" in {repoLabel}"
        + (run.Branch is { Length: > 0 } b ? $" on {b}" : "")
        + $", driven by conductor {run.EngineStampText ?? "(unstamped)"}. "
        + "Each agent step below is one autonomous session conductor dispatched; each observation is "
        + "what the engine fed back to it — the gate battery, the checkpoints it confirmed, the "
        + "commits it landed.";

    private static void WriteSystemStep(Utf8JsonWriter w, int id, string? stamp, string message)
    {
        w.WriteStartObject();
        w.WriteNumber("step_id", id);
        WriteStamp(w, stamp);
        w.WriteString("source", "system");
        w.WriteString("message", message);
        w.WriteEndObject();
    }

    private static void WriteAgentStep(
        Utf8JsonWriter w, int id, ArchivedSession s, IReadOnlyList<ArchivedCost> costs,
        IReadOnlyDictionary<int, List<GateFinished>> gatesBySession)
    {
        w.WriteStartObject();
        w.WriteNumber("step_id", id);
        WriteStamp(w, s.StartedUtc);
        w.WriteString("source", "agent");
        w.WriteString("message", string.IsNullOrWhiteSpace(s.ResultSummary)
            ? $"(session {s.Number} recorded no result summary)"
            : s.ResultSummary);
        // 0 would claim a deterministic dispatch, which a session never is. Written only when the
        // run measured its turns; absent means unmeasured, not zero.
        if (s.ContextTurns is > 0 and { } turns) w.WriteNumber("llm_call_count", turns);
        WriteObservation(w, s, gatesBySession);
        WriteMetrics(w, costs.Where(c => c.SessionNumber == s.Number).ToList());
        WriteStepExtra(w, s);
        w.WriteEndObject();
    }

    private static void WriteObservation(
        Utf8JsonWriter w, ArchivedSession s,
        IReadOnlyDictionary<int, List<GateFinished>> gatesBySession)
    {
        w.WriteStartObject("observation");
        w.WriteStartArray("results");

        w.WriteStartObject();
        var closed = s.ClosedCheckpoints;
        var content = new StringBuilder()
            .Append("outcome: ").Append(s.Outcome ?? "unrecorded")
            .Append("; gates: ").Append(string.IsNullOrWhiteSpace(s.GateSummary) ? "none recorded" : s.GateSummary)
            .Append("; checkpoints closed: ").Append(closed.Count == 0 ? "none" : string.Join(", ", closed))
            .Append("; commits: ").Append(s.Commits.ToString(CultureInfo.InvariantCulture));
        w.WriteString("content", content.ToString());
        w.WriteEndObject();

        if (gatesBySession.TryGetValue(s.Number, out var gates))
        {
            foreach (var g in gates)
            {
                w.WriteStartObject();
                w.WriteString("content",
                    $"gate {g.Name}: {(g.Skipped ? "skipped" : g.Passed ? "passed" : "FAILED")} "
                    + $"(exit {g.ExitCode.ToString(CultureInfo.InvariantCulture)}, "
                    + $"{g.DurationMs.ToString(CultureInfo.InvariantCulture)}ms)");
                w.WriteStartObject("extra");
                w.WriteString("gate", g.Name);
                w.WriteBoolean("passed", g.Passed);
                w.WriteBoolean("skipped", g.Skipped);
                w.WriteBoolean("optional", g.Optional);
                w.WriteNumber("exit_code", g.ExitCode);
                w.WriteNumber("duration_ms", g.DurationMs);
                w.WriteEndObject();
                w.WriteEndObject();
            }
        }

        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteMetrics(Utf8JsonWriter w, IReadOnlyList<ArchivedCost> rows)
    {
        if (rows.Count == 0) return;
        w.WriteStartObject("metrics");
        var cached = rows.Sum(c => c.TokensCacheRead);
        // ATIF's own formula is `non_cached_prompt_tokens = prompt_tokens - cached_tokens`, so the
        // cache-read tokens belong INSIDE prompt_tokens. Conductor stores them beside the input
        // tokens, which is why this addition exists and why it is not double counting.
        w.WriteNumber("prompt_tokens", rows.Sum(c => c.TokensIn) + cached);
        w.WriteNumber("completion_tokens", rows.Sum(c => c.TokensOut) + rows.Sum(c => c.TokensThink));
        w.WriteNumber("cached_tokens", cached);
        w.WriteNumber("cost_usd", decimal.ToDouble(rows.Sum(c => c.CostUsd)));
        w.WriteStartObject("extra");
        w.WriteNumber("reasoning_tokens", rows.Sum(c => c.TokensThink));
        w.WriteNumber("wall_ms", rows.Sum(c => c.WallMs));
        w.WriteStartArray("categories");
        foreach (var c in rows.OrderBy(c => c.Category, StringComparer.Ordinal))
        {
            w.WriteStartObject();
            w.WriteString("label", c.Category);
            w.WriteNumber("tokens", c.Tokens);
            w.WriteNumber("cost_usd", decimal.ToDouble(c.CostUsd));
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteStepExtra(Utf8JsonWriter w, ArchivedSession s)
    {
        w.WriteStartObject("extra");
        w.WriteNumber("session_number", s.Number);
        w.WriteString("stage_id", s.StageId);
        w.WriteString("session_kind", s.Kind);
        w.WriteNumber("attempt", s.Attempt);
        w.WriteNumber("resume_count", s.ResumeCount);
        w.WriteNumber("commits", s.Commits);
        if (s.EndedUtc is { Length: > 0 } ended) w.WriteString("ended_utc", ended);
        if (s.ContextHighWater is { } high) w.WriteNumber("context_high_water_tokens", high);
        if (s.ContextMeanTurn is { } mean) w.WriteNumber("context_mean_turn_tokens", mean);
        w.WriteStartArray("closed_checkpoints");
        foreach (var c in s.ClosedCheckpoints) w.WriteStringValue(c);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteFinalMetrics(Utf8JsonWriter w, IReadOnlyList<ArchivedCost> costs, int steps)
    {
        var cached = costs.Sum(c => c.TokensCacheRead);
        w.WriteStartObject("final_metrics");
        w.WriteNumber("total_prompt_tokens", costs.Sum(c => c.TokensIn) + cached);
        w.WriteNumber("total_completion_tokens", costs.Sum(c => c.TokensOut) + costs.Sum(c => c.TokensThink));
        w.WriteNumber("total_cached_tokens", cached);
        w.WriteNumber("total_cost_usd", decimal.ToDouble(costs.Sum(c => c.CostUsd)));
        w.WriteNumber("total_steps", steps);
        w.WriteEndObject();
    }

    private static void WriteRootExtra(
        Utf8JsonWriter w, ArchivedRun run, string repoLabel, string reconciledStatus,
        IReadOnlyList<ArchivedSession> sessions, IReadOnlyList<ConductorEvent> events,
        DateTimeOffset exportedUtc)
    {
        w.WriteStartObject("extra");
        w.WriteString("generator", "conductor history export --atif");
        w.WriteString("exported_utc", exportedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        w.WriteString("run_id", run.RunId);
        w.WriteString("plan", run.PlanName);
        w.WriteString("repo", repoLabel);
        // Both words, for the reason every conductor surface writes both: the column said `running`
        // for ever on runs nobody closed, and a shareable artifact that repeated it would carry the
        // lie further than the listing ever did.
        w.WriteString("status", reconciledStatus);
        w.WriteString("stored_status", run.Status);
        if (run.StartedUtc is { Length: > 0 } started) w.WriteString("started_utc", started);
        if (run.EndedUtc is { Length: > 0 } ended) w.WriteString("ended_utc", ended);
        w.WriteNumber("sessions", sessions.Count);
        w.WriteNumber("events", events.Count);
        w.WriteBoolean("event_log_steps", events.Count > 0);
        w.WriteEndObject();
    }

    private static void WriteStamp(Utf8JsonWriter w, string? stamp)
    {
        if (RunHistory.ParseUtc(stamp) is { } ts)
            w.WriteString("timestamp", ts.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary>Stage id → title, off the log. Empty for a database whose log did not survive; the
    /// step then names the stage without a title rather than inventing one.</summary>
    private static Dictionary<string, string> StageTitles(IReadOnlyList<ConductorEvent> events)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in events.OfType<StageEntered>())
            if (e.Title is { Length: > 0 } t) titles[e.StageId] = t;
        return titles;
    }

    /// <summary>Gates bucketed under the session that ran them, by walking the fold in order. The
    /// gate event carries no session number, and the base <c>SessionId</c> is not a number — but the
    /// log's ORDER is the ground truth the whole store is built on, so the last
    /// <see cref="SessionStarted"/> seen is the session a gate belongs to.</summary>
    private static Dictionary<int, List<GateFinished>> GatesBySession(IReadOnlyList<ConductorEvent> events)
    {
        var bucketed = new Dictionary<int, List<GateFinished>>();
        var current = -1;
        foreach (var e in events)
        {
            switch (e)
            {
                case SessionStarted started:
                    current = started.Number;
                    break;
                case GateFinished gate when current > 0:
                    if (!bucketed.TryGetValue(current, out var list))
                        bucketed[current] = list = [];
                    list.Add(gate);
                    break;
                default:
                    break;
            }
        }
        return bucketed;
    }
}
