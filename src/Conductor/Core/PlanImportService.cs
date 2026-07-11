using System.Text;
using System.Text.Json;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// F7.1: Converts a natural-language mega-plan description into a structured task graph
/// (stages, gates, checkpoints) by consulting the advisor model, then adds the result to
/// the plan file via PlanConfig's existing save/add-stage machinery.
/// </summary>
public static class PlanImportService
{
    /// <summary>Parse token from opencode (deepseek) or claude agent output. Returns stages, gates, and
    /// checkpoints parsed from the LLM's response. Does NOT write the plan file — the caller decides
    /// whether to apply, preview, or interactively confirm.</summary>
    public static async Task<ImportResult?> ImportAsync(PlanConfig plan, string description, Action<string>? log = null)
    {
        var prompt = BuildImportPrompt(plan, description);
        var verdict = await Advisor.ConsultAsync(plan, prompt, log).ConfigureAwait(false);
        if (verdict is null) return null;

        var json = verdict.Reason;
        // Try to extract a JSON object from the reason text
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start >= 0 && end > start)
            json = json[start..(end + 1)];

        ImportResult? result;
        try
        {
            result = JsonSerializer.Deserialize<ImportResult>(json, PlanConfig.JsonOpts);
        }
        catch (JsonException)
        {
            log?.Invoke("plan import: LLM returned unparseable JSON, falling back to raw text");
            result = null;
        }

        return result;
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
