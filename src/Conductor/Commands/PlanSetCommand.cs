using System.Text.Json;
using System.Text.Json.Nodes;

using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Models;
using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// P1 — `conductor plan set &lt;key&gt; &lt;value&gt;`: hot-update a single plan field via dot-notation path.
///
/// <para>SC3.2: three silent failures used to stack on this one two-word command. A key the plan
/// schema does not declare was CREATED (`limits.maxRunCostUsdd` → a cost cap nothing reads, run
/// uncapped, console says it landed); the rewrite dropped every `//` comment in the file with no
/// warning; and the edit reached no running engine at all, despite this doc comment once claiming
/// "the orchestrator picks it up at next session boundary" — only `plan reload` ever queued the
/// verb that makes that true.</para>
///
/// <para>KS3.2: the comment half is solved rather than apologised for. The edit is spliced into the
/// raw file by <see cref="PlanDocumentEditor"/> — comments, key order and formatting survive, no
/// default the file never carried is materialised, and the `.bak`-plus-warning path is gone.</para>
/// </summary>
public static class PlanSetCommand
{
    public static int ExecuteSet(string planPath, string? key, string? value, bool create = false)
    {
        if (string.IsNullOrWhiteSpace(key) || value == null)
        {
            AnsiConsole.MarkupLine("[red]plan set requires <key> <value>. Examples:[/]");
            AnsiConsole.MarkupLine("  conductor plan set limits.maxRunCostUsd 0.50");
            AnsiConsole.MarkupLine("  conductor plan set limits.stallMinutes 15");
            AnsiConsole.MarkupLine("  conductor plan set gates.0.timeoutMinutes 30");
            AnsiConsole.MarkupLine("  conductor plan set report.heartbeatMinutes 5");
            AnsiConsole.MarkupLine("[grey]--create writes a key the plan schema does not declare (nothing reads it).[/]");
            return 1;
        }

        try
        {
            if (!File.Exists(planPath))
            {
                AnsiConsole.MarkupLine($"[red]Plan file not found: {Markup.Escape(planPath)}[/]");
                return 1;
            }

            var originalRaw = File.ReadAllBytes(planPath);

            // Load+serialise roundtrip to get clean JSON (strips comments). `doc` takes the edit;
            // `before` stays as loaded — the diff between them is exactly what reaches the file.
            var plan = PlanConfig.Load(planPath);
            var cleanJson = JsonSerializer.Serialize(plan, PlanConfig.JsonOpts);
            var before = JsonNode.Parse(cleanJson)
                ?? throw new InvalidOperationException("Plan file produced empty JSON on serialisation.");
            var doc = JsonNode.Parse(cleanJson, new JsonNodeOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidOperationException("Plan file produced empty JSON on serialisation.");

            // SC3.2: judged against the DECLARED shape, not against what is present in the document —
            // JsonOpts omits nulls, so an unset `limits.maxRunCostUsd` is absent from the JSON and is
            // still the most common edit there is.
            var lookup = PlanKeySchema.Resolve(key);
            if (!lookup.Known)
            {
                foreach (var line in RefusalLines(key, lookup, doc, create)) AnsiConsole.MarkupLine(line);
                if (!create) return 1;
            }

            // Canonical casing when the schema knows the path: `Limits.MaxRunCostUsd` then lands ON the
            // existing key instead of beside it.
            string[] parts = lookup.Known ? [.. lookup.Canonical] : key.Split('.');
            var node = doc.Root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (int.TryParse(part, out var idx) && node is JsonArray arr)
                {
                    if (idx < 0 || idx >= arr.Count)
                    {
                        AnsiConsole.MarkupLine($"[red]Array index {idx} out of range for '{Markup.Escape(key)}' (array has {arr.Count} items).[/]");
                        return 1;
                    }
                    node = arr[idx];
                    continue;
                }

                var child = node?[part];
                if (child == null)
                {
                    // An absent object the schema DOES declare is a null the serialiser omitted, not a
                    // typo — vivify it, or `telegram.allowedChatIds` on a plan with no telegram block
                    // stays impossible from the CLI that documents it.
                    var pathSoFar = string.Join('.', parts.Take(i + 1));
                    if (node is JsonObject obj && (create || PlanKeySchema.IsObjectAt(pathSoFar)))
                    {
                        child = new JsonObject();
                        obj[part] = child;
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]Key segment '{Markup.Escape(part)}' not found in path '{Markup.Escape(key)}' and cannot be created here.[/]");
                        return 1;
                    }
                }
                node = child;
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

            var newJson = doc.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

            // Validate the result by deserialising it
            try
            {
                var test = JsonSerializer.Deserialize<PlanConfig>(newJson, PlanConfig.JsonOpts);
                if (test != null)
                {
                    test.PlanFilePath = planPath;
                    test.ResolveRepoAgainstPlanFile(); // CH1.2: same resolution Load does
                    test.Validate();
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Validation failed after set: {Markup.Escape(ex.Message)}[/]");
                AnsiConsole.MarkupLine("[yellow]Plan file was NOT modified. Fix the value and try again.[/]");
                return 1;
            }

            // KS3.2: splice only the edited leaf (and the planVersion bump) into the raw file —
            // comments, key order and formatting survive; nothing the file never carried appears.
            // WriteEdited self-checks the spliced bytes against the intent and falls back to the
            // whole-document rewrite when the splice would lie (a duplicated key, a span it cannot
            // place) — the safety valve, not the path, and it says what that cost.
            if (!PlanDocumentEditor.WriteEdited(planPath, originalRaw, before, doc))
            {
                var dropped = PlanDocumentEditor.CountCommentLines(System.Text.Encoding.UTF8.GetString(originalRaw));
                if (dropped > 0)
                    AnsiConsole.MarkupLine($"[yellow]The edit could not be spliced in place — the file was rewritten whole and {dropped} comment line(s) did not survive.[/]");
            }
            AnsiConsole.MarkupLine($"[green]plan set[/] {Markup.Escape(string.Join('.', parts))} = [bold]{Markup.Escape(value)}[/] (was {Markup.Escape(oldValue)})");

            // Reach: the run loop swaps a plan only on the `reload-plan` control verb, which this verb
            // never queued — so a live run kept running the plan it started with.
            var stateDir = plan.StateDir;
            var reach = DecideReach(stateDir);
            if (reach == Reach.Queued)
            {
                try
                {
                    File.WriteAllText(Path.Combine(stateDir, "control.json"),
                        JsonSerializer.Serialize(new { command = "reload-plan", issuedUtc = DateTime.UtcNow }));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AnsiConsole.MarkupLine($"[yellow]An engine is running this plan but the reload could not be queued: {Markup.Escape(ex.Message)}[/]");
                    reach = Reach.ControlBusy;
                }
            }
            AnsiConsole.MarkupLine(ReachLine(reach, EngineLock.Read(stateDir)?.Pid, planPath));
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or JsonException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    /// <summary>What to print for a key the plan does not declare. A refusal that does not say where the
    /// key really lives just moves the guessing — so a bare name that matches exactly one nested leaf
    /// comes back as the dotted path, and a near-miss inside a real block names its neighbour.</summary>
    internal static IReadOnlyList<string> RefusalLines(string key, PlanKeySchema.KeyLookup lookup, JsonNode? doc, bool create)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var lines = new List<string>();
        var where = lookup.ParentPath.Length == 0 ? "the plan" : $"'{Markup.Escape(lookup.ParentPath)}'";
        var colour = create ? "yellow" : "red";
        lines.Add($"[{colour}]{where} has no key '{Markup.Escape(lookup.UnknownSegment)}'.[/]");

        // A single segment that is really a nested leaf: the field-log case, `plan set maxRunCostUsd 100`
        // creating a root-level key nothing reads.
        var nested = key.Contains('.', StringComparison.Ordinal)
            ? []
            : PlanKeySchema.FindPaths(lookup.UnknownSegment, doc);
        if (nested.Count == 1)
            lines.Add($"[yellow]Did you mean [bold]{Markup.Escape(nested[0])}[/]?  conductor plan set {Markup.Escape(nested[0])} <value>[/]");
        else if (nested.Count > 1)
            lines.Add($"[yellow]It appears nested at: {Markup.Escape(string.Join(", ", nested.Take(6)))} — set one of those.[/]");
        else if (PlanKeySchema.NearMisses(lookup.UnknownSegment, lookup.ParentKeys) is { Count: > 0 } near)
            lines.Add($"[yellow]Did you mean [bold]{Markup.Escape(near[0])}[/]?[/]");
        else if (lookup.ParentKeys.Count > 0)
            lines.Add($"[grey]Keys here: {Markup.Escape(string.Join(", ", lookup.ParentKeys.Take(12)))}{(lookup.ParentKeys.Count > 12 ? ", ..." : "")}[/]");

        lines.Add(create
            ? "[yellow]--create given: writing it anyway. Nothing in the engine reads it.[/]"
            : "[grey]Nothing reads a key the plan does not declare, so this edit would look like it landed and change nothing. Use --create to write it regardless.[/]");
        return lines;
    }

    /// <summary>Whether a written edit reaches a running engine — the third silent failure. The run
    /// loop swaps a plan only on the `reload-plan` control verb, which `plan set` never queued.</summary>
    internal enum Reach
    {
        /// <summary>No engine holds this plan: the file is the whole story until the next `conductor run`.</summary>
        NoEngine,
        /// <summary>A live engine, and nothing else queued — the reload was dropped for it to pick up.</summary>
        Queued,
        /// <summary>A live engine with an unconsumed control command: queuing would eat it.</summary>
        ControlBusy,
    }

    internal static Reach DecideReach(string stateDir)
    {
        if (EngineLock.Read(stateDir) is not { } holder || !EngineLock.IsLive(holder)) return Reach.NoEngine;
        return File.Exists(Path.Combine(stateDir, "control.json")) ? Reach.ControlBusy : Reach.Queued;
    }

    /// <summary>What the operator is told about reach. Every branch ends in something they can act on:
    /// either the reload is already on its way to a named pid, or the exact command that sends it.</summary>
    internal static string ReachLine(Reach reach, int? pid, string planPath)
    {
        var reload = Markup.Escape($"conductor plan reload --plan {planPath}");
        return reach switch
        {
            Reach.Queued =>
                $"[grey]reload-plan queued — the engine running this plan (pid {pid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}) swaps it in at its next session boundary.[/]",
            Reach.ControlBusy =>
                $"[yellow]An engine is running this plan and a control command is already queued — not overwriting it. Once it is consumed, run: {reload}[/]",
            _ =>
                $"[grey]No engine is running this plan — the next `conductor run` reads the file. To push this into a live run: {reload}[/]",
        };
    }
}
