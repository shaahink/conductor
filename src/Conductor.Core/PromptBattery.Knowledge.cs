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
        IReadOnlyList<CarriedBugRow> carried;
        try
        {
            rows = store.QueryBugs(runId, status: "open");
            // SF0.4: the ledger is stored per run, so without this an open bug reached prompts only
            // until the next `conductor run` started a new run — and then vanished from every session
            // that could have fixed it. This is the line that makes a bug outlive its run.
            carried = store.QueryCarriedBugs(runId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { _section = null; return; }

        if (rows.Count == 0 && carried.Count == 0) { _section = null; return; }

        var sb = new StringBuilder();
        sb.AppendLine("Open bugs filed by earlier sessions (via `conductor bug new`). Fix in scope, or avoid re-finding — do NOT re-file:");
        foreach (var b in rows.Take(maxEntries))
            Append(sb, b, "");
        // Carried rows fill what is left of the same cap, so this cannot grow the prompt past what one
        // run's ledger already could — this run's own bugs keep priority for the slots.
        foreach (var c in carried.Take(Math.Max(0, maxEntries - rows.Count)))
            Append(sb, c.Bug, $" [carried from an earlier run: {Compact(c.PlanName)}]");
        sb.Append("Mark one fixed with `conductor bug fix <id>` once you have genuinely resolved it.");
        _section = sb.ToString().TrimEnd();
    }

    private static void Append(StringBuilder sb, BugRow b, string suffix)
    {
        var where = b.StageId is { Length: > 0 } st ? $" [{st}]" : "";
        sb.AppendLine($"- #{b.Id} ({b.Severity}){where} {Compact(b.Title)}{suffix}");
        if (string.IsNullOrWhiteSpace(b.Detail)) return;
        var d = Compact(b.Detail!);
        sb.AppendLine($"    {(d.Length > 200 ? d[..200] + "…" : d)}");
    }

    private static string Compact(string s) =>
        s.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();

    public string Name => "open bugs";
    public string Section => _section ?? "";
    public bool IsEmpty => string.IsNullOrEmpty(_section);
}
