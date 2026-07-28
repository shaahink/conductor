using System.ComponentModel;
using System.Diagnostics;

using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// F8.1: Spawns a conductor chat agent — an LLM wired (MCP) to run.db, the ledger, logs, and
/// control verbs. The agent answers ad-hoc questions about the run and can perform actions
/// (inject instructions, add notes, update tasks). Usage: conductor chat "how did s9 die?"
/// or conductor chat "inject X into retry for F6"
/// </summary>
public sealed class ChatCommand : Command<ChatCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[QUERY]")]
        [Description("Your question about the run. Leave blank for interactive mode.")]
        public string? Query { get; init; }
    }

    public override int Execute(CommandContext context, Settings settings)
    {
        try
        {
            var plan = PlanConfig.Load(settings.ResolvePlanPath());
            if (plan.Advisor is not { Enabled: true } || string.IsNullOrWhiteSpace(plan.Advisor.Command))
            {
                AnsiConsole.MarkupLine("[red]Advisor model is not configured. Set advisor.enabled, advisor.command, and advisor.args in your plan.[/]");
                return 1;
            }

            var query = settings.Query?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(query))
            {
                AnsiConsole.MarkupLine("[bold aqua]conductor chat[/] — ask questions about your run");
                AnsiConsole.MarkupLine("[grey]Type your question and press Enter. Leave blank to exit.[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.Write("> ");
                query = Console.ReadLine()?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(query))
                {
                    AnsiConsole.MarkupLine("[grey]No question — exiting.[/]");
                    return 0;
                }
            }

            return ExecuteChatAsync(plan, query).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static async Task<int> ExecuteChatAsync(PlanConfig plan, string query)
    {
        AnsiConsole.MarkupLine("[grey]Consulting advisor model...[/]");
        AnsiConsole.WriteLine();

        // Build a chat prompt with the user's query
        var promptBuilder = new PromptBuilder(plan);
        var stage = new StageConfig { Id = "chat", Title = "Conductor Chat", Kind = "deliver", Sessions = 1 };
        promptBuilder.Deliver(stage, 0, 1, 1); // warm the builder — we need the BuiltIn access pattern

        // Construct the prompt manually with the chat template
        var readOrder = plan.ReadOrder is { Count: > 0 }
            ? string.Join("\n", plan.ReadOrder.Select((d, i) => $"{i + 1}. {d}"))
            : "(no read order configured)";

        var prompt = $"""
            System: You are a helpful engineering analyst that answers questions about the "{plan.Name}" conductor run. You have access to MCP tools (run.db SQL querying, session detail lookup, ledger entries, task management, bg process control). Be concise and data-driven.

            Context:
            - Repo: {plan.Repo}
            - Tracker file: {plan.Tracker}
            - The run.db is at: .conductor/run.db relative to the repo root
            - Use the `run_query` MCP tool to execute SQL queries against run.db
            - Use `session_detail` to look up specific session info
            - Use `ledger_list` to see recent findings
            - Use `inject_instruction` to write an instruction if asked

            USER QUERY: {query}
            """;

        // Get the advisor config
        var advisorCfg = plan.Advisor!;
        var args = advisorCfg.Args.Select(a => a.Replace("{prompt}", prompt)).ToList();

        AnsiConsole.MarkupLine($"[grey]Running: {Markup.Escape(advisorCfg.Command)} {Markup.Escape(string.Join(" ", args))}[/]");
        AnsiConsole.WriteLine();

        var r = await ProcessRunner.RunAsync(advisorCfg.Command, args, plan.Repo,
            TimeSpan.FromMinutes(advisorCfg.TimeoutMinutes)).ConfigureAwait(false);

        if (r.TimedOut)
        {
            AnsiConsole.MarkupLine("[red]Chat agent timed out.[/]");
            return 1;
        }

        // Output the agent's raw response
        var output = r.Output.Trim();
        if (advisorCfg.Output.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(output);
                if (doc.RootElement.TryGetProperty("result", out var res) && res.ValueKind == System.Text.Json.JsonValueKind.String)
                    output = res.GetString() ?? output;
            }
            catch (System.Text.Json.JsonException) { /* print raw */ }
        }

        if (output.Length > 0)
            AnsiConsole.WriteLine(output);
        else
            AnsiConsole.MarkupLine("[grey](agent produced no output)[/]");

        if (!string.IsNullOrWhiteSpace(r.StdErr))
            AnsiConsole.MarkupLine($"[grey](stderr: {r.StdErr.Trim()})[/]");

        return r.ExitCode == 0 ? 0 : 1;
    }
}
