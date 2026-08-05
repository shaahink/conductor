using Conductor.Core;
using Conductor.Core.Http;
using System.Net;
using System.Text.Json;

namespace Conductor.Http;

/// <summary>M7: knowledge that compounds — serves the run's knowledge ledger and tracked bugs to the
/// Face (GET /ledger, GET /bugs), the same run.db rows the prompt batteries and the audit phase
/// consume, and (write side) lets the Face file a note/bug and resolve a bug (POST /note, /bug,
/// /bug/resolve) so the Knowledge tab isn't read-only.</summary>
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
        var bugs = _store.QueryBugs(_state.RunId, filter).Select(b => ToDto(b, null))
            // SF0.4: plus the open bugs earlier runs in this repo left behind — the ledger is stored
            // per run, so the Face went blank on every open bug the moment a new run started.
            .Concat(_store.QueryCarriedBugs(_state.RunId).Select(c => ToDto(c.Bug, c.PlanName)))
            .ToList();
        await WriteJsonAsync(ctx, new BugsDto(bugs), ControlPlaneJsonContext.Default.BugsDto).ConfigureAwait(false);

        static BugDto ToDto(Conductor.Core.Store.BugRow b, string? carriedFromPlan) => new(
            b.Id, b.Title, b.Detail, b.Severity, b.Status,
            b.StageId, b.FoundSession, b.FixedSession, b.CreatedAt, b.UpdatedAt, carriedFromPlan);
    }

    private async Task HandleNotePostAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var body = await ReadBodyAsync(ctx, ct).ConfigureAwait(false);
        NoteRequestDto? req;
        try { req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.NoteRequestDto); }
        catch (JsonException) { await KnowledgeErrorAsync(ctx, "malformed JSON body").ConfigureAwait(false); return; }

        if (string.IsNullOrWhiteSpace(req?.Content)) { await KnowledgeErrorAsync(ctx, "note content is empty").ConfigureAwait(false); return; }

        var kind = string.IsNullOrWhiteSpace(req.Kind) ? "note" : req.Kind!.Trim();
        var stageId = string.IsNullOrWhiteSpace(req.StageId) ? null : req.StageId!.Trim();
        _store.WriteLedger(_state.RunId, null, stageId, kind, req.Content.Trim());
        await WriteJsonAsync(ctx, new KnowledgeWriteResultDto(true, null, null),
            ControlPlaneJsonContext.Default.KnowledgeWriteResultDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    private async Task HandleBugPostAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var body = await ReadBodyAsync(ctx, ct).ConfigureAwait(false);
        BugNewRequestDto? req;
        try { req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.BugNewRequestDto); }
        catch (JsonException) { await KnowledgeErrorAsync(ctx, "malformed JSON body").ConfigureAwait(false); return; }

        if (string.IsNullOrWhiteSpace(req?.Title)) { await KnowledgeErrorAsync(ctx, "bug title is empty").ConfigureAwait(false); return; }

        var severity = (req.Severity ?? "").Trim().ToLowerInvariant();
        if (severity is not ("high" or "medium" or "low")) severity = "medium";
        var detail = string.IsNullOrWhiteSpace(req.Detail) ? null : req.Detail!.Trim();
        var stageId = string.IsNullOrWhiteSpace(req.StageId) ? null : req.StageId!.Trim();
        var id = _store.WriteBug(_state.RunId, req.Title.Trim(), detail, severity, stageId, null);
        await WriteJsonAsync(ctx, new KnowledgeWriteResultDto(true, id, null),
            ControlPlaneJsonContext.Default.KnowledgeWriteResultDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    private async Task HandleBugResolveAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var body = await ReadBodyAsync(ctx, ct).ConfigureAwait(false);
        BugResolveRequestDto? req;
        try { req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.BugResolveRequestDto); }
        catch (JsonException) { await KnowledgeErrorAsync(ctx, "malformed JSON body").ConfigureAwait(false); return; }

        if (req is null || req.Id <= 0) { await KnowledgeErrorAsync(ctx, "a positive bug id is required").ConfigureAwait(false); return; }

        var status = string.IsNullOrWhiteSpace(req.Status) ? "fixed" : req.Status!.Trim().ToLowerInvariant();
        var ok = _store.UpdateBugStatus(_state.RunId, req.Id, status, null);
        if (!ok) { await KnowledgeErrorAsync(ctx, $"no open bug #{req.Id} to resolve").ConfigureAwait(false); return; }

        await WriteJsonAsync(ctx, new KnowledgeWriteResultDto(true, req.Id, null),
            ControlPlaneJsonContext.Default.KnowledgeWriteResultDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static Task KnowledgeErrorAsync(HttpListenerContext ctx, string reason) =>
        WriteJsonAsync(ctx, new KnowledgeWriteResultDto(false, null, reason),
            ControlPlaneJsonContext.Default.KnowledgeWriteResultDto, HttpStatusCode.BadRequest);
}
