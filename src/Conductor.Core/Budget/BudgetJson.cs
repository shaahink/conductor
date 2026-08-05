using System.Text;
using System.Text.Json;

namespace Conductor.Core.Budget;

/// <summary>
/// K4.2 — <c>conductor budget --json</c>. Written with <see cref="Utf8JsonWriter"/> rather than a
/// serializer context because the shape is nested and small: a source-generated context for it would
/// be three more types in a tree whose architecture baseline caps a file at three, and reflection
/// serialization is off in the published build.
/// </summary>
public static class BudgetJson
{
    /// <summary>One object per run, in the order given.</summary>
    public static string Serialize(IReadOnlyList<BudgetProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteStartArray("runs");
            foreach (var p in profiles) WriteProfile(w, p);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteProfile(Utf8JsonWriter w, BudgetProfile p)
    {
        w.WriteStartObject();
        w.WriteString("runId", p.RunId);
        w.WriteString("plan", p.PlanName);
        if (p.CapPayoff is { } payoff) w.WriteNumber("capPayoff", Math.Round(payoff, 3));
        w.WriteStartArray("windows");
        foreach (var win in p.Windows) WriteWindow(w, win);
        w.WriteEndArray();
        WritePrescription(w, p.Prescription);
        w.WriteEndObject();
    }

    private static void WriteWindow(Utf8JsonWriter w, BudgetWindow win)
    {
        w.WriteStartObject();
        w.WriteString("label", win.Label);
        w.WriteNumber("firstSession", win.FirstSession);
        w.WriteNumber("lastSession", win.LastSession);
        w.WriteNumber("sessions", win.Sessions);
        w.WriteNumber("costedSessions", win.Costed);
        WriteNullable(w, "capTokens", win.CapTokens);
        w.WriteBoolean("capMeasured", win.CapMeasured);
        WriteNullable(w, "nudgeTokens", win.NudgeTokens);
        if (win.NudgeRatio is { } r) w.WriteNumber("nudgeRatio", Math.Round(r, 4));
        WriteNullable(w, "headroomTokens", win.Headroom);
        w.WriteNumber("tokens", win.Tokens);
        w.WriteNumber("checkpoints", win.Checkpoints);
        if (win.TokensPerCheckpoint is { } t) w.WriteNumber("tokensPerCheckpoint", Math.Round(t));
        w.WriteNumber("rollovers", win.Rollovers);
        w.WriteNumber("rolloverRate", Math.Round(win.RolloverRate, 4));
        w.WriteNumber("nudged", win.Nudged);
        w.WriteNumber("nudgedAndEndedClean", win.NudgedAndClean);
        w.WriteNumber("closers", win.Closers);
        w.WriteNumber("floorTokens", win.Floor);
        w.WriteNumber("medianCloserTokens", win.ClosingMedian);
        w.WriteNumber("maxCloserTokens", win.ClosingMax);
        if (win.WrapUp is { } u)
        {
            w.WriteStartObject("wrapUp");
            w.WriteNumber("min", u.Min);
            w.WriteNumber("median", u.Median);
            w.WriteNumber("max", u.Max);
            w.WriteNumber("samples", u.Samples);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static void WritePrescription(Utf8JsonWriter w, BudgetPrescription p)
    {
        w.WriteStartObject("prescription");
        w.WriteNumber("maxSessionTokens", p.MaxSessionTokens);
        w.WriteNumber("softBreakRatio", Math.Round(p.SoftBreakRatio, 2));
        w.WriteNumber("nudgeTokens", p.NudgeTokens);
        w.WriteNumber("headroomTokens", p.Headroom);
        w.WriteNumber("wrapUpBasis", p.WrapUpBasis);
        w.WriteBoolean("wrapUpMeasured", p.WrapUpMeasured);
        w.WriteString("verdict", p.Verdict);
        w.WriteStartArray("findings");
        foreach (var f in p.Findings) w.WriteStringValue(f);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } v) w.WriteNumber(name, v);
        else w.WriteNull(name);
    }
}
