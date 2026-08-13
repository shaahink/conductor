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

        // SF6.3: one resolution path. This used to hand-roll the prompt here — which meant the
        // chat.md template (built-in AND any templates/chat.md an operator wrote) was never read, and
        // the throwaway Deliver() call above it rendered a full session prompt, persona and packs
        // included, purely to be discarded.
        var prompt = new PromptBuilder(plan).Chat(query);

        // SC3.4: one advisor spawn path. This used to re-implement the spawn and the envelope unwrap
        // inline, so `chat` and a verdict consult could disagree about what the advisor does — and
        // chat missed every guard the shared path grew (an argless invocation, chief among them).
        var advisorCfg = plan.Advisor!;
        AnsiConsole.MarkupLine($"[grey]Running: {Markup.Escape(advisorCfg.Command)} " +
                               $"{Markup.Escape(string.Join(" ", advisorCfg.Args))} ({advisorCfg.TimeoutMinutes}m timeout)[/]");
        AnsiConsole.WriteLine();

        var reply = await Advisor.AskAsync(plan, prompt,
            m => AnsiConsole.MarkupLine($"[grey]{Markup.Escape(m)}[/]")).ConfigureAwait(false);
        // KS5.2: `chat` is an operator asking a question of the plan's advisor — no run, no session,
        // nothing to key a costs row to. It states its bill instead of writing one.
        AnsiConsole.MarkupLine(reply.Spend is null
            ? "[grey]advisor: the provider reported no billed figure (unknown, not zero)[/]"
            : $"[grey]advisor: ${reply.Spend.CostUsd:0.0000} billed, {reply.Spend.Tokens} tokens — not recorded against any run[/]");
        var answer = reply.Text;

        if (string.IsNullOrWhiteSpace(answer))
        {
            AnsiConsole.MarkupLine("[red]The advisor answered nothing — it failed to spawn, timed out, or printed no output.[/]");
            return 1;
        }

        AnsiConsole.WriteLine(answer.Trim());
        return 0;
    }
}
