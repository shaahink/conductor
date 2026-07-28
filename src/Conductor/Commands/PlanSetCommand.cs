using System.Text.Json;

using Conductor.Models;
using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// P1 — `conductor plan set <key> <value>`: hot-update a single plan field via dot-notation path.
/// Loads the plan JSON, navigates to the key, writes the value, re-serialises, and validates.
/// Applied immediately to the plan file on disk; the orchestrator picks it up at next session boundary.
/// </summary>
public static class PlanSetCommand
{
    public static int ExecuteSet(string planPath, string? key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key) || value == null)
        {
            AnsiConsole.MarkupLine("[red]plan set requires <key> <value>. Examples:[/]");
            AnsiConsole.MarkupLine("  conductor plan set limits.maxRunCostUsd 0.50");
            AnsiConsole.MarkupLine("  conductor plan set limits.stallMinutes 15");
            AnsiConsole.MarkupLine("  conductor plan set gates.0.timeoutMinutes 30");
            AnsiConsole.MarkupLine("  conductor plan set report.heartbeatMinutes 5");
            return 1;
        }

        try
        {
            if (!File.Exists(planPath))
            {
                AnsiConsole.MarkupLine($"[red]Plan file not found: {Markup.Escape(planPath)}[/]");
                return 1;
            }

            // Load+serialise roundtrip to get clean JSON (strips comments)
            var plan = PlanConfig.Load(planPath);
            var cleanJson = System.Text.Json.JsonSerializer.Serialize(plan, PlanConfig.JsonOpts);
            var doc = System.Text.Json.Nodes.JsonNode.Parse(cleanJson, new System.Text.Json.Nodes.JsonNodeOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Plan file produced empty JSON on serialisation.");

            // Navigate to the parent and set the leaf value
            var parts = key.Split('.');
            var node = doc.Root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (int.TryParse(part, out var idx) && node is System.Text.Json.Nodes.JsonArray arr)
                {
                    if (idx < 0 || idx >= arr.Count)
                    {
                        AnsiConsole.MarkupLine($"[red]Array index {idx} out of range for '{key}' (array has {arr.Count} items).[/]");
                        return 1;
                    }
                    node = arr[idx];
                }
                else
                {
                    var child = node![part];
                    if (child == null)
                    {
                        AnsiConsole.MarkupLine($"[red]Key segment '{part}' not found in path '{key}'. Check the key name (case-insensitive).[/]");
                        return 1;
                    }
                    node = child;
                }
            }

            var leafKey = parts[^1];
            var oldValue = node?[leafKey]?.ToString() ?? "(null)";

            // Parse the value: try numbers, booleans, then string
            if (decimal.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var decimalVal))
            {
                if (value.Contains('.', StringComparison.Ordinal))
                    node![leafKey] = decimalVal;
                else
                    node![leafKey] = (int)decimalVal;
            }
            else if (bool.TryParse(value, out var boolVal))
            {
                node![leafKey] = boolVal;
            }
            else
            {
                node![leafKey] = value;
            }

            // Bump planVersion
            var pv = doc.Root["planVersion"];
            if (pv != null)
                doc.Root["planVersion"] = pv.GetValue<int>() + 1;
            else
                doc.Root["planVersion"] = 2;

            var newJson = doc.ToJsonString(new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });

            // Validate the result by deserialising it
            try
            {
                var test = System.Text.Json.JsonSerializer.Deserialize<PlanConfig>(newJson, PlanConfig.JsonOpts);
                if (test != null)
                {
                    test.PlanFilePath = planPath;
                    test.Validate();
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Validation failed after set: {Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine("[yellow]Plan file was NOT modified. Fix the value and try again.[/]");
                return 1;
            }

            File.WriteAllText(planPath, newJson, System.Text.Encoding.UTF8);
            AnsiConsole.MarkupLine($"[green]plan set[/] {Markup.Escape(key)} = [bold]{Markup.Escape(value)}[/] (was {Markup.Escape(oldValue)})");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }
}
