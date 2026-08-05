using System.Text;
using System.Text.Json;

namespace Conductor.Core.Money;

/// <summary>
/// K4.3 — <c>conductor money --json</c>. Hand-written with <see cref="Utf8JsonWriter"/> for the same
/// reason <c>BudgetJson</c> is: reflection serialization is off in the published build, and a
/// source-generated context for this shape would be five more types.
/// </summary>
public static class MoneyJson
{
    /// <summary>The whole report, runs nested inside their scope.</summary>
    public static string Serialize(MoneyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("scope", report.Scope);
            WriteLine(w, "total", report.Total);
            WriteLines(w, "months", report.Months);
            WriteLines(w, "categories", report.Categories);
            w.WriteStartArray("runs");
            foreach (var r in report.Runs)
            {
                w.WriteStartObject();
                w.WriteString("runId", r.RunId);
                w.WriteString("plan", r.PlanName);
                w.WriteString("repo", r.RepoLabel);
                if (r.StartedUtc is { } started) w.WriteString("startedUtc", started);
                if (r.LastActivityUtc is { } last) w.WriteString("lastActivityUtc", last);
                if (r.CapTokenPayoff is { } tokenPayoff) w.WriteNumber("capTokenPayoff", Math.Round(tokenPayoff, 3));
                if (r.CapCostPayoff is { } costPayoff) w.WriteNumber("capCostPayoff", Math.Round(costPayoff, 3));
                WriteLine(w, "total", r.Total);
                WriteLines(w, "windows", r.Windows);
                WriteLines(w, "stages", r.Stages);
                WriteLines(w, "months", r.Months);
                WriteLines(w, "categories", r.Categories);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteLines(Utf8JsonWriter w, string name, IReadOnlyList<MoneyLine> lines)
    {
        w.WriteStartArray(name);
        foreach (var l in lines) WriteLine(w, null, l);
        w.WriteEndArray();
    }

    private static void WriteLine(Utf8JsonWriter w, string? name, MoneyLine l)
    {
        if (name is null) w.WriteStartObject(); else w.WriteStartObject(name);
        w.WriteString("label", l.Label);
        w.WriteNumber("sessions", l.Sessions);
        w.WriteNumber("tokens", l.Tokens);
        w.WriteNumber("cacheReadTokens", l.CacheReadTokens);
        w.WriteNumber("cacheReadShare", Math.Round(l.CacheReadShare, 5));
        w.WriteNumber("inputTokens", l.InputTokens);
        w.WriteNumber("outputTokens", l.OutputTokens);
        w.WriteNumber("costUsd", Math.Round(l.Cost, 4));
        w.WriteNumber("checkpoints", l.Checkpoints);
        if (l.TokensPerCheckpoint is { } tpc) w.WriteNumber("tokensPerCheckpoint", Math.Round(tpc, 0));
        if (l.CostPerCheckpoint is { } cpc) w.WriteNumber("costPerCheckpoint", Math.Round(cpc, 4));
        if (l.CostPerMillionTokens is { } rate) w.WriteNumber("costPerMillionTokens", Math.Round(rate, 4));
        w.WriteEndObject();
    }
}
