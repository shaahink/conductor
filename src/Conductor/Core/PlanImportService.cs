using System.Text;
using System.Text.Json;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// M6.1 (was F7.1): Converts a mega-plan into a structured task graph (stages, gates, checkpoints).
/// A <b>structured</b> markdown plan/tracker document is parsed <b>deterministically</b> with no model
/// call (<see cref="MarkdownPlanParser"/>) — the zero-spend path; freeform prose falls back to the
/// advisor model. Neither writes the plan file — the caller diffs, previews, or confirms first.
/// </summary>
public static class PlanImportService
{
    /// <summary>The deterministic path: if the text looks like a structured plan/tracker, parse it into
    /// a stage graph with no model call. Returns null when the text isn't structured (caller should then
    /// try <see cref="ImportAsync"/>). This is what makes <c>conductor plan import DESIGN.md</c> free.</summary>
    public static ImportResult? ParseStructured(string markdown)
    {
        if (!MarkdownPlanParser.LooksStructured(markdown)) return null;
        var parsed = MarkdownPlanParser.Parse(markdown);
        return parsed.Stages.Count == 0 ? null : MarkdownPlanParser.ToImportResult(parsed);
    }

    /// <summary>The advisor path for freeform prose: sends the import prompt to the plan's advisor
    /// model and parses the plan JSON straight out of its raw answer. Does NOT write the plan file —
    /// the caller diffs, previews, or interactively confirms. <paramref name="model"/>, when set,
    /// fills a <c>{model}</c> placeholder in the advisor's args (same convention as <c>{prompt}</c>).</summary>
    public static async Task<ImportResult?> ImportAsync(PlanConfig plan, string description, string? model = null, Action<string>? log = null)
    {
        var prompt = BuildImportPrompt(plan, description);
        var restoreArgs = ApplyModelOverride(plan, model);
        string? text;
        try { text = await Advisor.AskTextAsync(plan, prompt, log).ConfigureAwait(false); }
        finally { restoreArgs?.Invoke(); }
        if (text is null) return null;

        // The prompt demands raw JSON, but models pad — take the outermost {...} slice.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            log?.Invoke("plan import: the advisor returned no JSON");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ImportResult>(text[start..(end + 1)], PlanConfig.JsonOpts);
        }
        catch (JsonException)
        {
            log?.Invoke("plan import: the advisor returned unparseable JSON");
            return null;
        }
    }

    /// <summary>The model that interprets a freeform import: an explicit override wins; otherwise the
    /// value following <c>--model</c>/<c>-m</c> in the advisor args (skipping an unfilled
    /// <c>{model}</c> placeholder). Null when the args don't name one — callers fall back to the
    /// advisor command name for display.</summary>
    public static string? ResolveInterpreterModel(PlanConfig plan, string? overrideModel = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideModel)) return overrideModel;
        if (plan.Advisor is not { } a) return null;
        for (var i = 0; i < a.Args.Count - 1; i++)
        {
            if (a.Args[i] is not ("--model" or "-m")) continue;
            var value = a.Args[i + 1];
            return value.Contains("{model}", StringComparison.Ordinal) ? null : value;
        }
        return null;
    }

    /// <summary>Apply the imported stages and gates to the plan, merging with existing entries.</summary>
    public static void ApplyToPlan(PlanConfig plan, ImportResult result)
    {
        // Add or update stages
        foreach (var stage in result.Stages)
        {
            var existing = plan.Stages.FirstOrDefault(s => s.Id == stage.Id);
            if (existing != null)
            {
                existing.Title = stage.Title ?? existing.Title;
                existing.Notes = stage.Notes ?? existing.Notes;
                existing.Sessions = stage.Sessions > 0 ? stage.Sessions : existing.Sessions;
                existing.Kind = stage.Kind ?? existing.Kind;
                if (stage.DependsOn is { Count: > 0 })
                    existing.DependsOn = stage.DependsOn;
            }
            else
            {
                plan.Stages.Add(stage);
            }
        }

        // Add or update gates
        foreach (var gate in result.Gates)
        {
            var existing = plan.Gates.FirstOrDefault(g => g.Name == gate.Name);
            if (existing != null)
            {
                existing.Command = gate.Command ?? existing.Command;
                existing.Tier = gate.Tier ?? existing.Tier;
                existing.TimeoutMinutes = gate.TimeoutMinutes > 0 ? gate.TimeoutMinutes : existing.TimeoutMinutes;
            }
            else
            {
                plan.Gates.Add(gate);
            }
        }

        plan.BumpVersion();
        plan.Save();
    }

    /// <summary>Temporarily substitute a <c>{model}</c> placeholder in the advisor args so the caller can
    /// pick the model for this one import (<c>--model</c>). Returns an action that restores the original
    /// args; no-op when no model was given or the plan has no advisor.</summary>
    private static Action? ApplyModelOverride(PlanConfig plan, string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || plan.Advisor is null) return null;
        var original = plan.Advisor.Args;
        plan.Advisor.Args = [.. original.Select(x => x.Replace("{model}", model, StringComparison.Ordinal))];
        return () => plan.Advisor.Args = original;
    }

    private static string BuildImportPrompt(PlanConfig plan, string description)
    {
        var existingStages = string.Join("\n", plan.Stages.Select(s => $"- {s.Id}: {s.Title}"));
        var existingGates = string.Join("\n", plan.Gates.Select(g => $"- {g.Name}: {g.Command} (tier={g.Tier})"));

        return $$"""
            You are a plan architect for the Conductor orchestrator. Given a natural-language description of
            a multi-session engineering plan, produce a complete structured task graph in JSON.

            DESCRIPTION:
            {{description}}

            EXISTING PLAN CONTEXT:
            Plan name: {{plan.Name}}
            Repo: {{plan.Repo}}
            Gate policy: {{plan.GatePolicy}}

            Existing stages:
            {{existingStages}}

            Existing gates:
            {{existingGates}}

            Produce a JSON object with this EXACT shape (no prose, no markdown fences, raw JSON only):

            {
              "stages": [
                {
                  "id": "STRING — stage identifier (e.g. F0, F1, A1)",
                  "title": "STRING — human-readable title",
                  "sessions": NUMBER — estimated sessions (default 2),
                  "notes": "STRING — any stage-specific notes for the agent",
                  "kind": "STRING — 'deliver' or 'review'",
                  "dependsOn": ["STRING array — stage ids that must complete first"]
                }
              ],
              "gates": [
                {
                  "name": "STRING — gate identifier (e.g. build, test, lint)",
                  "command": "STRING — shell command to run (e.g. 'dotnet build')",
                  "tier": "STRING — 'fast', 'full', or 'truth' (default 'full')",
                  "timeoutMinutes": NUMBER — timeout in minutes (default 20)
                }
              ]
            }

            Rules:
            - Stage ids must be unique and match the pattern F<n> or R<n> or A<n> or similar.
            - Every stage must reference at least one stage id via dependsOn (for ordering) unless it's the first stage.
            - Gates must be actual shell commands that verify correctness (build, test, lint, coverage).
            - Truth-tier gates are product-level assertions that run at stage confirmation only.
            - Fast-tier gates run every session; full-tier gates run at stage confirmation.
            - Keep stage notes concise — one sentence max.
            - Estimate sessions conservatively (2-4 for typical stages).

            Output ONLY the JSON object, no other text.
            """;
    }
}

public sealed class ImportResult
{
    public List<StageConfig> Stages { get; set; } = [];
    public List<GateConfig> Gates { get; set; } = [];
}
