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

        // Runtime safety rail: the plan file edit takes effect next run, but deleting the stage the
        // loop is currently on would leave RunState pointing at a stage that no longer exists. Refuse
        // it here (state, unlike plan validity, isn't something CollectErrors can see).
        var running = _state.CurrentStage;
        if (!string.IsNullOrEmpty(running) &&
            req.Edits.Any(e => IsDelete(e) && string.Equals(e.Target, "stage", StringComparison.OrdinalIgnoreCase)
                               && string.Equals(e.Id, running, StringComparison.OrdinalIgnoreCase)))
        {
            await PlanErrorAsync(ctx, $"cannot delete the running stage '{running}' — pause or goto another stage first").ConfigureAwait(false);
            return;
        }

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
        var interpreter = "structured";
        if (incoming is null)
        {
            // G1.1: freeform prose → the plan's advisor model, same prose path as the CLI's
            // `conductor plan import "<free text>" --model X`, now first-class over the wire.
            var planForAdvisor = LoadPlanFresh() ?? _plan;
            if (planForAdvisor.Advisor is not { Enabled: true } advisor || string.IsNullOrWhiteSpace(advisor.Command))
            {
                await PlanImportErrorAsync(ctx,
                    "not a structured plan/tracker, and no advisor model is configured to interpret prose — set advisor.enabled/command in the plan").ConfigureAwait(false);
                return;
            }
            interpreter = PlanImportService.ResolveInterpreterModel(planForAdvisor, req.Model) ?? advisor.Command;

            // Apply must persist exactly the previewed diff (and not consult — or bill — the model
            // twice), so the preview's parse is cached and reused when the same prompt comes back.
            incoming = TakeCachedImport(text)
                ?? await PlanImportService.ImportAsync(planForAdvisor, text, req.Model,
                    msg => _logger.LogInformation("plan import: {Message}", msg)).ConfigureAwait(false);
            if (incoming is null)
            {
                await PlanImportErrorAsync(ctx, $"the advisor ({interpreter}) could not derive stages or gates from this prompt").ConfigureAwait(false);
                return;
            }
            CacheImport(text, incoming);
        }

        var plan = LoadPlanFresh() ?? _plan;
        var diff = PlanDiff.Compute(plan, incoming);
        var applied = false;
        if (req.Apply && !diff.IsEmpty && LoadPlanFresh() is { } writable)
        {
            // Atomic validate-then-save, the same guarantee /plan/edit gives: a model-shaped (or
            // hand-shaped) import that would break the plan is rejected whole, nothing written.
            diff.ApplyChanges(writable);
            var errors = writable.CollectErrors();
            if (errors.Count > 0)
            {
                await PlanImportErrorAsync(ctx, "import would make the plan invalid: " + errors[0]).ConfigureAwait(false);
                return;
            }
            writable.Save();
            plan = writable;
            applied = true;
        }

        await WriteJsonAsync(ctx, new PlanImportResultDto(true, null, PlanDiffDto.From(diff), applied, plan.PlanVersion, interpreter),
            ControlPlaneJsonContext.Default.PlanImportResultDto,
            applied ? HttpStatusCode.Accepted : HttpStatusCode.OK).ConfigureAwait(false);
    }

    /// <summary>Single-slot preview→apply cache for advisor-interpreted imports (one operator, one
    /// in-flight prompt). Keyed by the exact prose; a stale entry expires after 15 minutes.</summary>
    private sealed record CachedImport(string Key, ImportResult Result, DateTime AtUtc);

    private volatile CachedImport? _importCache;

    private ImportResult? TakeCachedImport(string key)
    {
        var cached = _importCache;
        if (cached is null || !string.Equals(cached.Key, key, StringComparison.Ordinal)) return null;
        if (DateTime.UtcNow - cached.AtUtc > TimeSpan.FromMinutes(15)) return null;
        return cached.Result;
    }

    private void CacheImport(string key, ImportResult result) =>
        _importCache = new CachedImport(key, result, DateTime.UtcNow);

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

    private static bool IsDelete(PlanEditDto e) => string.Equals(e.Op, "delete", StringComparison.OrdinalIgnoreCase);

    private static string? ApplyEdit(PlanConfig plan, PlanEditDto edit)
    {
        var op = (edit.Op ?? "set").ToLowerInvariant();
        if (op is "add" or "delete") return ApplyStructuralEdit(plan, edit.Target.ToLowerInvariant(), op, edit);
        if (op != "set") return $"unknown edit op '{edit.Op}' — use set, add, or delete";

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
            case "telegram":
                return ApplyTelegramEdit(plan, field, edit.Value);
            default:
                return $"unknown edit target '{edit.Target}'";
        }
    }

    /// <summary>add/delete a whole stage or gate. New objects take schema defaults (a stage: 1 session,
    /// deliver kind; a gate: full tier, 20-min timeout) — everything else is editable afterward via a
    /// plain set edit. The caller re-validates the whole plan and only saves if it's still valid, so an
    /// empty gate command or a delete that dangles a dependsOn is rejected there, not here.</summary>
    private static string? ApplyStructuralEdit(PlanConfig plan, string target, string op, PlanEditDto edit)
    {
        var id = edit.Id?.Trim() ?? "";
        switch (target)
        {
            case "stage" when op == "add":
                if (id.Length == 0) return "add stage: an id is required";
                if (plan.Stages.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))) return $"stage '{id}' already exists";
                plan.AddStage(new StageConfig { Id = id, Title = string.IsNullOrWhiteSpace(edit.Value) ? id : edit.Value!.Trim() });
                return null;
            case "stage":
                var stage = plan.Stages.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
                if (stage is null) return $"unknown stage '{id}'";
                plan.Stages.Remove(stage);
                plan.BumpVersion();
                return null;
            case "gate" when op == "add":
                if (id.Length == 0) return "add gate: a name is required";
                if (plan.Gates.Any(g => string.Equals(g.Name, id, StringComparison.OrdinalIgnoreCase))) return $"gate '{id}' already exists";
                plan.Gates.Add(new GateConfig { Name = id, Command = edit.Value?.Trim() ?? "" });
                plan.BumpVersion();
                return null;
            case "gate":
                var gate = plan.Gates.FirstOrDefault(g => string.Equals(g.Name, id, StringComparison.OrdinalIgnoreCase));
                if (gate is null) return $"unknown gate '{id}'";
                plan.Gates.Remove(gate);
                plan.BumpVersion();
                return null;
            default:
                return $"cannot {op} target '{edit.Target}' — only stage or gate can be added or deleted";
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

    /// <summary>M8.2: non-secret Telegram settings only (allowed chat ids, poll interval, two-way
    /// toggle) — these belong in the versioned plan file, same as everything else /plan/edit
    /// touches. The bot token itself never comes through here; see
    /// ControlPlaneServer.Telegram.cs / SecretsStore.</summary>
    private static string? ApplyTelegramEdit(PlanConfig plan, string field, string? value)
    {
        plan.Telegram ??= new TelegramConfig();
        switch (field)
        {
            case "allowedchatids":
                plan.Telegram.AllowedChatIds = string.IsNullOrWhiteSpace(value)
                    ? []
                    : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
                return null;
            case "pollintervalseconds":
                if (!int.TryParse(value, out var n) || n < 1) return "pollIntervalSeconds must be a positive integer";
                plan.Telegram.PollIntervalSeconds = n; return null;
            case "enabletwoway":
                plan.Telegram.EnableTwoWay = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase); return null;
            default: return $"telegram has no editable field '{field}'";
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
