using System.Net;

namespace Conductor.Core.Http;

/// <summary>M7: knowledge that compounds — serves the run's knowledge ledger and tracked bugs to the
/// Face (GET /ledger, GET /bugs), the same run.db rows the prompt batteries and the audit phase consume.</summary>
public sealed partial class ControlPlaneServer
{
    private async Task WriteLedgerAsync(HttpListenerContext ctx)
    {
        var rows = _store.QueryLedger(_state.RunId);
        var entries = rows
            .Where(r => !string.Equals(r.Kind, "hand-edit", StringComparison.Ordinal))
            .Select(r => new LedgerEntryDto(r.Id, r.SessionNumber, r.StageId, r.Kind, r.Content, r.CreatedAt))
            .ToList();
        await WriteJsonAsync(ctx, new LedgerDto(entries), ControlPlaneJsonContext.Default.LedgerDto).ConfigureAwait(false);
    }

    private async Task WriteBugsAsync(HttpListenerContext ctx)
    {
        // No ?status → open only (the common "what's outstanding" view); ?status=all → every bug;
        // ?status=<x> → that status.
        var status = ctx.Request.QueryString["status"];
        var filter = string.IsNullOrWhiteSpace(status) ? "open"
            : status.Equals("all", StringComparison.OrdinalIgnoreCase) ? null
            : status;
        var rows = _store.QueryBugs(_state.RunId, filter);
        var bugs = rows.Select(b => new BugDto(
            b.Id, b.Title, b.Detail, b.Severity, b.Status,
            b.StageId, b.FoundSession, b.FixedSession, b.CreatedAt, b.UpdatedAt)).ToList();
        await WriteJsonAsync(ctx, new BugsDto(bugs), ControlPlaneJsonContext.Default.BugsDto).ConfigureAwait(false);
    }
}
