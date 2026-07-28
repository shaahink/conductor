using Conductor.Core.Store;
using System.Text;

namespace Conductor.Core;

/// <summary>M7.1: injects the most recent knowledge-ledger entries (findings, traps, decisions) from
/// prior sessions into the next prompt, so knowledge from session 9 is in session 10's prompt. Reads
/// straight from run.db — the same rows <c>ledger_list</c> serves — so what an agent noted with
/// <c>conductor note</c> compounds instead of dying with the session that learned it.</summary>
public sealed class LedgerBattery : IPromptBattery
{
    private readonly string? _section;

    public LedgerBattery(IRunStore store, string runId, int maxEntries = 8, int maxContentLen = 240)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<LedgerRow> rows;
        try { rows = store.QueryLedger(runId); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { _section = null; return; }

        // hand-edit entries are engine bookkeeping (M4.1), not agent knowledge — don't echo them back.
        var kept = rows.Where(r => !string.Equals(r.Kind, "hand-edit", StringComparison.Ordinal))
                       .Take(maxEntries).ToList();
        if (kept.Count == 0) { _section = null; return; }

        var sb = new StringBuilder();
        sb.AppendLine("What earlier sessions recorded (via `conductor note`). Do not re-derive or re-discover these:");
        foreach (var r in kept)
        {
            var content = r.Content.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
            if (content.Length > maxContentLen) content = content[..maxContentLen] + "…";
            var where = r.SessionNumber is { } sn ? $" (s{sn}{(r.StageId is { Length: > 0 } st ? $"/{st}" : "")})"
                : r.StageId is { Length: > 0 } st2 ? $" ({st2})" : "";
            sb.AppendLine($"- [{r.Kind}] {content}{where}");
        }
        _section = sb.ToString().TrimEnd();
    }

    public string Name => "knowledge ledger";
    public string Section => _section ?? "";
    public bool IsEmpty => string.IsNullOrEmpty(_section);
}

/// <summary>M7.2: injects the run's OPEN tracked bugs into the next prompt so an agent stops re-finding
/// a bug a prior session already filed, and a fix session knows what is outstanding. Reads run.db's
/// <c>bugs</c> table — the same rows <c>conductor bug list</c> and the audit phase see.</summary>
public sealed class BugsBattery : IPromptBattery
{
    private readonly string? _section;

    public BugsBattery(IRunStore store, string runId, int maxEntries = 12)
    {
        ArgumentNullException.ThrowIfNull(store);
        IReadOnlyList<BugRow> rows;
        try { rows = store.QueryBugs(runId, status: "open"); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { _section = null; return; }

        if (rows.Count == 0) { _section = null; return; }

        var sb = new StringBuilder();
        sb.AppendLine("Open bugs filed by earlier sessions (via `conductor bug new`). Fix in scope, or avoid re-finding — do NOT re-file:");
        foreach (var b in rows.Take(maxEntries))
        {
            var where = b.StageId is { Length: > 0 } st ? $" [{st}]" : "";
            var line = $"- #{b.Id} ({b.Severity}){where} {b.Title.Replace("\n", " ", StringComparison.Ordinal).Trim()}";
            sb.AppendLine(line);
            if (!string.IsNullOrWhiteSpace(b.Detail))
            {
                var d = b.Detail!.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
                if (d.Length > 200) d = d[..200] + "…";
                sb.AppendLine($"    {d}");
            }
        }
        sb.Append("Mark one fixed with `conductor bug fix <id>` once you have genuinely resolved it.");
        _section = sb.ToString().TrimEnd();
    }

    public string Name => "open bugs";
    public string Section => _section ?? "";
    public bool IsEmpty => string.IsNullOrEmpty(_section);
}
