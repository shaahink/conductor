using System.ComponentModel;
using Conductor.Core.History;
using Conductor.Core.Integrations.Github;
using Conductor.Core.Store;
using Conductor.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Conductor.Commands;

/// <summary>
/// KS9.1 — <c>conductor github sync --backfill &lt;run&gt;</c>: push a finished run's board and diary
/// to GitHub issues.
///
/// <para><b>Push only, and off by default.</b> L6.3 / D-7 / ADR 0005. There is no inbound path here
/// and none anywhere else: the tracker stays the verified contract, and nothing this verb reads from
/// GitHub reaches run state. A plan without a <c>github</c> block behaves exactly as it did before
/// this verb existed, because this verb is the only thing that runs it.</para>
///
/// <para><b>The destination is always explicit.</b> A backfill will not derive its target from the
/// working repo's <c>origin</c> unless the plan has opted in with <c>github.enabled</c>: the whole
/// failure mode worth designing against is a proof that meant to write to a scratch repository and
/// wrote to the real one. <c>--repo owner/name</c> is the explicit form.</para>
///
/// <para><b>The run is opened READ-ONLY.</b> <see cref="ArchiveView"/> over <c>Mode=ReadOnly</c> — a
/// mirror must not be able to touch the thing it mirrors, and pointing this at a live run's database
/// must be safe.</para>
/// </summary>
public sealed class GithubCommand : AsyncCommand<GithubCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("Sub-command: sync. Omit to show help.")]
        public string Verb { get; init; } = "";

        [CommandOption("--backfill <RUN>")]
        [Description("Push this run's whole board and diary. Run id, prefix, slug, repo name, or a path to a run.db.")]
        public string? Backfill { get; init; }

        // The value name is a bare word on purpose. Spectre parses the template at model-build time
        // and rejects '/' in a value name — and it builds the model for EVERY command at startup, so
        // `--repo <OWNER/NAME>` did not break this verb, it took the whole CLI down with a
        // CommandTemplateException before any verb ran. Found by running it, not by reading it.
        [CommandOption("--repo <REPO>")]
        [Description("Mirror INTO this repository, overriding the plan's github.repo. Use a scratch repo to try it.")]
        public string? Repo { get; init; }

        [CommandOption("--no-diary")]
        [Description("Board only: skip the run issue and its per-session comments.")]
        public bool NoDiary { get; init; }

        [CommandOption("--dry-run")]
        [Description("Reconcile and report what would change, writing nothing.")]
        public bool DryRun { get; init; }

        // KS9.3. Asking for the project board from the command line instead of by editing a plan is
        // what makes the gate REACHABLE — and today the gate is the whole of the project half, so
        // this option's only observable behaviour is a precise refusal.
        [CommandOption("--project <NUMBER>")]
        [Description("Also mirror a Projects v2 board (needs a token with the 'project' scope). Refuses without it.")]
        public int? Project { get; init; }

        [CommandOption("--home <PATH>")]
        [Description("Read a state home other than this machine's.")]
        public string? Home { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var verb = settings.Verb.ToLowerInvariant();
        if (verb is not ("sync" or "backfill")) return Help();

        var plan = PlanConfig.Load(settings.ResolvePlanPath());

        // KS9.3 — the board's own coherence is decided first, because it costs nothing and because a
        // misspelt board or a missing project number is a fact about the plan, not a discovery made
        // against the network. It fires whether the project board was asked for in the plan or with
        // --project, and it fires before a destination is even resolved.
        var board = BoardView(plan, settings.Project);
        if (board.BoardRefusal() is { } configRefusal)
            return Refuse([configRefusal, "nothing was contacted and nothing was written."]);

        var repo = Destination(plan, settings.Repo);
        if (repo is null) return RefuseNoDestination(plan);
        if (!repo.Contains('/', StringComparison.Ordinal))
        {
            AnsiConsole.MarkupLine($"[red]'{Markup.Escape(repo)}' is not a repository.[/] it must be [grey]owner/name[/].");
            return 2;
        }

        // The token is resolved BEFORE anything is dialled, so "no token" costs zero requests — the
        // refusal is a fact about configuration, not a discovery made against the network.
        var (token, source) = GithubIdentity.ResolveToken(plan);
        if (token is null) return RefuseNoToken(plan);

        // KS9.3 — the scope check is a GET, and it happens before the backfill's first write rather
        // than being discovered by one that failed. A caller who asked for a project board and cannot
        // have one is refused WHOLE: pushing the issue half while quietly dropping the half that was
        // asked for is precisely the silent no-op this gate exists to prevent. Set github.board back
        // to 'issues' — which the refusal says — and the issue mirror runs untouched.
        if (board.WantsProjectBoard)
        {
            using var probe = new GithubClient(token, TimeSpan.FromSeconds(30));
            var stop = await GithubProjects.PreflightAsync(probe, board, source).ConfigureAwait(false);
            if (stop.Count > 0) return Refuse(stop);
        }

        if (string.IsNullOrWhiteSpace(settings.Backfill))
        {
            AnsiConsole.MarkupLine("[red]nothing to sync.[/] pass [grey]--backfill <run>[/] — " +
                "a run id, a prefix, a catalogue slug, a repo name, or a path to a run.db.");
            return 2;
        }

        var view = OpenRun(settings, out var refusal);
        if (view is null)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(refusal)}[/]");
            return 1;
        }

        return await PushAsync(view, plan, repo, source, settings).ConfigureAwait(false);
    }

    /// <summary>Where this push goes, or null when nobody said. An explicit <c>--repo</c> always
    /// wins; otherwise the plan must have opted in, and only then is deriving the destination from
    /// the working repo's origin the operator's own instruction rather than a guess.</summary>
    private static string? Destination(PlanConfig plan, string? overrideRepo)
    {
        if (!string.IsNullOrWhiteSpace(overrideRepo)) return overrideRepo.Trim();
        return plan.Github is { Enabled: true } ? GithubIdentity.Resolve(plan) : null;
    }

    /// <summary>KS9.3 — the github block as the BOARD gate sees it. <c>--project N</c> asks for the
    /// project board without editing a plan, so it synthesises the block the gate would have read;
    /// only <c>board</c> and <c>projectNumber</c> are consulted by that gate, and the destination and
    /// token are resolved from the plan and <c>--repo</c> exactly as before.</summary>
    private static GithubConfig BoardView(PlanConfig plan, int? projectOverride) =>
        projectOverride is null
            ? plan.Github ?? new GithubConfig()
            : new GithubConfig
            {
                Board = GithubConfig.BoardIssuesAndProject,
                ProjectNumber = projectOverride.Value,
            };

    /// <summary>A refusal built in Core, printed here: first line loud, the rest indented. Same shape
    /// as <see cref="RefuseNoToken"/>, and 2 for the same reason — the caller asked for something the
    /// configuration or the credential cannot give, which is not a run failure.</summary>
    private static int Refuse(IReadOnlyList<string> lines)
    {
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(lines[0])}[/]");
        foreach (var line in lines.Skip(1))
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(line)}[/]");
        return 2;
    }

    private ArchiveView? OpenRun(Settings settings, out string refusal)
    {
        var root = string.IsNullOrWhiteSpace(settings.Home) ? StateHome.Root : Path.GetFullPath(settings.Home);
        var direct = RunSources.AsDatabasePath(settings.Backfill);
        return direct is not null
            ? ArchiveView.OpenDb(direct, null, out refusal)
            : ArchiveView.Open(root, settings.Backfill!, out refusal);
    }

    private static async Task<int> PushAsync(
        ArchiveView view, PlanConfig plan, string repo, string tokenSource, Settings settings)
    {
        var (token, _) = GithubIdentity.ResolveToken(plan);
        var prefix = plan.Github?.LabelPrefix ?? "conductor";
        var diary = !settings.NoDiary && (plan.Github?.RunHistoryIssue ?? true);

        AnsiConsole.MarkupLine(
            $"[grey]run[/] {Markup.Escape(view.Run.ShortRunId)}  [grey]plan[/] {Markup.Escape(view.Run.PlanName)}  " +
            $"[grey]→[/] [aqua]{Markup.Escape(repo)}[/]  [grey]token from[/] {Markup.Escape(tokenSource)}");
        // A destination that is not api.github.com is announced, every time. A write target must
        // never be redirected silently.
        if (GithubClient.ApiBaseIsOverridden)
            AnsiConsole.MarkupLine($"[yellow]api base overridden[/] → {Markup.Escape(GithubClient.ApiBase)} " +
                $"[grey]({GithubClient.ApiBaseEnvVar})[/]");
        if (settings.DryRun) AnsiConsole.MarkupLine("[yellow]dry run[/] — nothing will be written.");

        using var client = new GithubClient(token!, TimeSpan.FromSeconds(30));
        var sync = new GithubBoardSync(client, repo, prefix);
        var result = await sync.BackfillAsync(
            view.Log(), view.Run, view.Run.EngineStampText ?? Core.BuildInfo.Current.Full,
            diary, settings.DryRun).ConfigureAwait(false);

        AnsiConsole.MarkupLine(Markup.Escape(result.Summary()));
        foreach (var (key, url) in result.Urls.OrderBy(u => u.Key, StringComparer.Ordinal).Take(5))
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(key)}[/] {Markup.Escape(url)}");
        if (result.Urls.Count > 5)
            AnsiConsole.MarkupLine($"  [grey]… {result.Urls.Count - 5} more[/]");
        foreach (var error in result.Errors.Take(10))
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
        AnsiConsole.MarkupLine($"[grey]{client.RequestCount} requests[/]");
        return result.Ok ? 0 : 1;
    }

    private static int RefuseNoDestination(PlanConfig plan)
    {
        var key = plan.Github is null ? "there is no github block in the plan"
            : plan.Github.Enabled ? "github.repo is empty and origin names no owner/name"
            : "github.enabled is false";
        AnsiConsole.MarkupLine($"[red]no destination.[/] {Markup.Escape(key)}.");
        AnsiConsole.MarkupLine("  pass [grey]--repo owner/name[/] (use a scratch repository to try it), " +
            "or set [grey]github.enabled[/] and [grey]github.repo[/] in the plan.");
        return 2;
    }

    /// <summary>The refusal text is built in Core (<c>GithubIdentity.MissingTokenRefusal</c>) so the
    /// bar "names both sources" is asserted against the sentence itself, not against a reading of
    /// this file. Printed here, decided there.</summary>
    private static int RefuseNoToken(PlanConfig plan) => Refuse(GithubIdentity.MissingTokenRefusal(plan));

    private static int Help()
    {
        AnsiConsole.MarkupLine("[bold]conductor github[/] — push a run's board to GitHub issues. One way, off by default.");
        AnsiConsole.MarkupLine("  [aqua]github sync --backfill <run> [[--repo owner/name]] [[--dry-run]] [[--no-diary]][/]");
        AnsiConsole.MarkupLine("[grey]  one issue per checkpoint, one run issue with a comment per session.[/]");
        AnsiConsole.MarkupLine("[grey]  re-running mints nothing: identity is a marker in the issue body.[/]");
        AnsiConsole.MarkupLine("[grey]  nothing is ever read back from GitHub into the run.[/]");
        AnsiConsole.MarkupLine($"[grey]  --project <n> asks for a Projects v2 board: needs the " +
            $"'{GithubProjects.RequiredScope}' scope, and refuses by name without it.[/]");
        return 1;
    }
}
