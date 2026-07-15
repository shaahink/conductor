using System.Net;
using System.Text.Json;
using Conductor.Core.Planning;
using Conductor.Models;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Http;

/// <summary>M6.3: plan authoring over the control plane. Reads are served fresh from the plan file on
/// disk so the editor always reflects the current file; writes load fresh, apply, validate, and save
/// back — the live <see cref="PlanConfig"/> instance the run loop holds is never mutated on an HTTP
/// thread (no enumeration races), and the edits take effect on the next run, exactly like
/// <c>conductor plan reload</c>. An invalid edit is rejected whole; the file is only written when valid.</summary>
public sealed partial class ControlPlaneServer
{
    private async Task WritePlanAsync(HttpListenerContext ctx)
    {
        var plan = LoadPlanFresh() ?? _plan;
        await WriteJsonAsync(ctx, PlanDto.FromPlan(plan), ControlPlaneJsonContext.Default.PlanDto).ConfigureAwait(false);
    }

    private async Task HandlePlanEditAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        PlanEditRequestDto? req;
        try { req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.PlanEditRequestDto); }
        catch (JsonException) { await PlanErrorAsync(ctx, "malformed JSON body").ConfigureAwait(false); return; }

        if (req is not { Edits.Count: > 0 }) { await PlanErrorAsync(ctx, "no edits given").ConfigureAwait(false); return; }

        var plan = LoadPlanFresh();
        if (plan is null) { await PlanErrorAsync(ctx, "plan file not available on disk").ConfigureAwait(false); return; }

        foreach (var edit in req.Edits)
        {
            var err = ApplyEdit(plan, edit);
            if (err is not null) { await PlanErrorAsync(ctx, err).ConfigureAwait(false); return; }
        }

        var errors = plan.CollectErrors();
        if (errors.Count > 0)
        {
            await PlanErrorAsync(ctx, "edit would make the plan invalid: " + errors[0]).ConfigureAwait(false);
            return;
        }

        plan.Save();
        await WriteJsonAsync(ctx, new PlanMutationResultDto(true, null, plan.PlanVersion),
            ControlPlaneJsonContext.Default.PlanMutationResultDto, HttpStatusCode.Accepted).ConfigureAwait(false);
    }

    private async Task HandlePlanImportAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
        var body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        PlanImportRequestDto? req;
        try { req = JsonSerializer.Deserialize(body, ControlPlaneJsonContext.Default.PlanImportRequestDto); }
        catch (JsonException) { await PlanImportErrorAsync(ctx, "malformed JSON body").ConfigureAwait(false); return; }

        if (string.IsNullOrWhiteSpace(req?.Source)) { await PlanImportErrorAsync(ctx, "missing 'source'").ConfigureAwait(false); return; }

        var text = await ResolveImportSourceAsync(req.Source, ct).ConfigureAwait(false);
        var incoming = PlanImportService.ParseStructured(text);
        if (incoming is null)
        {
            await PlanImportErrorAsync(ctx, "not a structured plan/tracker — freeform prose needs the CLI advisor path (conductor plan import ... --model)").ConfigureAwait(false);
            return;
        }

        var plan = LoadPlanFresh() ?? _plan;
        var diff = PlanDiff.Compute(plan, incoming);
        var applied = false;
        if (req.Apply && !diff.IsEmpty && LoadPlanFresh() is { } writable)
        {
            diff.Apply(writable);
            plan = writable;
            applied = true;
        }

        await WriteJsonAsync(ctx, new PlanImportResultDto(true, null, PlanDiffDto.From(diff), applied, plan.PlanVersion),
            ControlPlaneJsonContext.Default.PlanImportResultDto,
            applied ? HttpStatusCode.Accepted : HttpStatusCode.OK).ConfigureAwait(false);
    }

    /// <summary>Resolve an import source: an existing file path (absolute, or relative to repo/cwd) is
    /// read; anything else is treated as inline markdown. Keeps the Face's import as flexible as the CLI's.</summary>
    private async Task<string> ResolveImportSourceAsync(string source, CancellationToken ct)
    {
        try
        {
            if (File.Exists(source)) return await File.ReadAllTextAsync(source, ct).ConfigureAwait(false);
            var repoRel = Path.Combine(_plan.Repo, source);
            if (File.Exists(repoRel)) return await File.ReadAllTextAsync(repoRel, ct).ConfigureAwait(false);
        }
        catch (IOException) { /* fall through to inline */ }
        catch (UnauthorizedAccessException) { /* fall through to inline */ }
        return source;
    }

    private PlanConfig? LoadPlanFresh()
    {
        if (string.IsNullOrWhiteSpace(_plan.PlanFilePath) || !File.Exists(_plan.PlanFilePath)) return null;
        try { return PlanConfig.Load(_plan.PlanFilePath); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            _logger.LogWarning(ex, "control plane: could not load the plan file for authoring");
            return null;
        }
    }

    private static string? ApplyEdit(PlanConfig plan, PlanEditDto edit)
    {
        var field = edit.Field.ToLowerInvariant();
        switch (edit.Target.ToLowerInvariant())
        {
            case "stage":
                var stage = plan.Stages.FirstOrDefault(s => string.Equals(s.Id, edit.Id, StringComparison.OrdinalIgnoreCase));
                if (stage is null) return $"unknown stage '{edit.Id}'";
                return ApplyStageEdit(stage, field, edit.Value);
            case "gate":
                var gate = plan.Gates.FirstOrDefault(g => string.Equals(g.Name, edit.Id, StringComparison.OrdinalIgnoreCase));
                if (gate is null) return $"unknown gate '{edit.Id}'";
                return ApplyGateEdit(gate, field, edit.Value);
            case "plan":
                return ApplyPlanEdit(plan, field, edit.Value);
            default:
                return $"unknown edit target '{edit.Target}'";
        }
    }

    private static string? ApplyStageEdit(StageConfig stage, string field, string? value)
    {
        switch (field)
        {
            case "title": stage.Title = value ?? ""; return null;
            case "kind": stage.Kind = string.IsNullOrWhiteSpace(value) ? "deliver" : value; return null;
            case "notes": stage.Notes = string.IsNullOrWhiteSpace(value) ? null : value; return null;
            case "persona": stage.Persona = string.IsNullOrWhiteSpace(value) ? null : value; return null;
            case "workflow": stage.Workflow = string.IsNullOrWhiteSpace(value) ? null : value; return null;
            case "sessions":
                if (!int.TryParse(value, out var n) || n < 1) return "sessions must be a positive integer";
                stage.Sessions = n; return null;
            case "model":
                if (string.IsNullOrWhiteSpace(value)) { if (stage.Agent is not null) stage.Agent.Model = null; return null; }
                stage.Agent ??= new AgentConfig();
                stage.Agent.Model = value; return null;
            case "dependson":
                stage.DependsOn = string.IsNullOrWhiteSpace(value)
                    ? null
                    : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                return null;
            default: return $"stage has no editable field '{field}'";
        }
    }

    private static string? ApplyGateEdit(GateConfig gate, string field, string? value)
    {
        switch (field)
        {
            case "command": gate.Command = value ?? ""; return null;
            case "tier": gate.Tier = string.IsNullOrWhiteSpace(value) ? "full" : value.ToLowerInvariant(); return null;
            case "optional": gate.Optional = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); return null;
            case "timeout":
            case "timeoutminutes":
                if (!int.TryParse(value, out var n) || n < 1) return "timeout must be a positive integer (minutes)";
                gate.TimeoutMinutes = n; return null;
            default: return $"gate has no editable field '{field}'";
        }
    }

    private static string? ApplyPlanEdit(PlanConfig plan, string field, string? value)
    {
        switch (field)
        {
            case "gatepolicy":
                if (!string.Equals(value, "perSession", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(value, "perPhase", StringComparison.OrdinalIgnoreCase))
                    return "gatePolicy must be perSession or perPhase";
                plan.GatePolicy = value!; return null;
            case "defaultworkflow": plan.DefaultWorkflow = string.IsNullOrWhiteSpace(value) ? null : value; return null;
            case "name": if (!string.IsNullOrWhiteSpace(value)) plan.Name = value; return null;
            default: return $"plan has no editable field '{field}'";
        }
    }

    private static Task PlanErrorAsync(HttpListenerContext ctx, string reason) =>
        WriteJsonAsync(ctx, new PlanMutationResultDto(false, reason, 0),
            ControlPlaneJsonContext.Default.PlanMutationResultDto, HttpStatusCode.BadRequest);

    private static Task PlanImportErrorAsync(HttpListenerContext ctx, string reason) =>
        WriteJsonAsync(ctx, new PlanImportResultDto(false, reason,
                new PlanDiffDto([], [], [], []), false, 0),
            ControlPlaneJsonContext.Default.PlanImportResultDto, HttpStatusCode.BadRequest);
}
