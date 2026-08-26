using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Github;
using Conductor.Models;

using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// CH1.3 — <c>conductor github ci</c>: what does CI say about the commit this run is on?
///
/// <para><b>Why this verb exists.</b> For the whole Divan era the local gate battery was green for
/// 23 checkpoints while CI's windows leg was red on every commit, and nothing compared them. The
/// engine's phase gate is what this project trusts a checkpoint against; a verdict that can be green
/// beside a red CI is not worth what the run believes it is.</para>
///
/// <para><b>It asks the question the way that cannot be silent.</b> Not the commit's check-runs — a
/// commit only shows the workflows IT TRIGGERED, so a schedule-only or dispatch-only workflow is
/// invisible there and a branch reads CLEAN while a broken workflow sits red (measured on this repo,
/// 2026-08-26). Instead: every ACTIVE workflow the repository has, then its latest run on this
/// branch, then compared to HEAD. A workflow that has never run on the branch is a row saying so.</para>
///
/// <para>What it writes is an OBSERVATION, dated and carrying the sha it was about
/// (<see cref="CiStatus"/>); the health the report and the owner queue show is derived from it every
/// time they render, so a green cannot outlive the commit it was for.</para>
/// </summary>
public sealed partial class GithubCommand
{
    private static async Task<int> CiAsync(PlanConfig plan, string repo, string tokenSource, string? branchOverride)
    {
        var (token, _) = GithubIdentity.ResolveToken(plan);
        var branch = string.IsNullOrWhiteSpace(branchOverride) ? Git.Branch(plan.Repo) : branchOverride.Trim();
        var head = Git.Head(plan.Repo);

        AnsiConsole.MarkupLine(
            $"[grey]asking[/] [aqua]{Markup.Escape(repo)}[/] [grey]about[/] {Markup.Escape(branch)} "
          + $"[grey]at[/] {Markup.Escape(Short(head))}  [grey]token from[/] {Markup.Escape(tokenSource)}");
        if (!string.Equals(GithubClient.ApiBase, GithubClient.DefaultApiBase, StringComparison.Ordinal))
            AnsiConsole.MarkupLine($"[yellow]api base overridden:[/] {Markup.Escape(GithubClient.ApiBase)}");

        using var client = new GithubClient(token!, TimeSpan.FromSeconds(30));
        var (list, listError) = await client.ListWorkflowsAsync(repo).ConfigureAwait(false);
        if (list is null)
        {
            AnsiConsole.MarkupLine($"[red]could not list workflows:[/] {Markup.Escape(listError ?? "unknown error")}");
            return 1;
        }

        var verdicts = new List<CiWorkflowVerdict>();
        foreach (var wf in list.Workflows.OrderBy(w => w.Path, StringComparer.Ordinal))
        {
            // A DISABLED workflow is recorded with no run rather than skipped: "there are five
            // workflows and two of them are switched off" is part of the answer to "is CI green",
            // and dropping the row here is how that fact would stop being visible.
            if (!string.Equals(wf.State, "active", StringComparison.OrdinalIgnoreCase))
            {
                verdicts.Add(new CiWorkflowVerdict(wf.Name, wf.Path, wf.State, "", "", "", ""));
                continue;
            }

            var (runs, runError) = await client.WorkflowRunsAsync(repo, wf.Id, branch).ConfigureAwait(false);
            if (runs is null)
            {
                AnsiConsole.MarkupLine($"[red]could not read runs of {Markup.Escape(wf.Name)}:[/] {Markup.Escape(runError ?? "unknown")}");
                return 1;
            }

            var run = runs.Runs.FirstOrDefault();
            verdicts.Add(new CiWorkflowVerdict(
                wf.Name, wf.Path, wf.State,
                run?.HeadSha ?? "", run?.Status ?? "", run?.Conclusion ?? "", run?.HtmlUrl ?? ""));
        }

        var status = new CiStatus(
            DateTime.UtcNow.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
            repo, branch, head, verdicts);
        CiStatus.Write(plan, status);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("workflow"); table.AddColumn("state"); table.AddColumn("latest run on branch"); table.AddColumn("commit");
        foreach (var v in verdicts)
            table.AddRow(
                Markup.Escape(v.Workflow),
                Markup.Escape(v.State),
                Markup.Escape(v.Conclusion.Length > 0 ? v.Conclusion : v.Status.Length > 0 ? v.Status : "never run on this branch"),
                Markup.Escape(v.RunSha.Length == 0 ? "-"
                    : Short(v.RunSha) + (string.Equals(v.RunSha, head, StringComparison.OrdinalIgnoreCase) ? " (HEAD)" : " (not HEAD)")));
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]recorded at[/] {Markup.Escape(Path.Combine(plan.StateDir, CiStatus.FileName))}");

        // The verdict IS the exit code. A verb whose whole purpose is "surface the divergence" and
        // which exits 0 on a red CI would be the Divan era in miniature.
        var rows = CiAgreementProbe.Collect(plan, headSha: head);
        foreach (var row in rows)
            AnsiConsole.MarkupLine(row.State switch
            {
                ChannelState.Dead => $"[red]x {Markup.Escape(row.Line)}[/]",
                ChannelState.Degraded => $"[yellow]! {Markup.Escape(row.Line)}[/]",
                ChannelState.Ready => $"[green]ok {Markup.Escape(row.Line)}[/]",
                _ => $"[grey]- {Markup.Escape(row.Line)}[/]",
            });
        foreach (var row in rows.Where(r => r.IsLoud && r.Fix.Length > 0))
            AnsiConsole.MarkupLine($"  [grey]fix:[/] {Markup.Escape(row.Fix)}");

        return rows.Any(r => r.State == ChannelState.Dead) ? 1 : 0;
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}
