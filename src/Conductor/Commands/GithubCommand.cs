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
public sealed partial class GithubCommand : AsyncCommand<GithubCommand.Settings>
{
    public sealed class Settings : PlanSettings
    {
        [CommandArgument(0, "[VERB]")]
        [Description("Sub-command: sync, sarif, ci. Omit to show help.")]
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

        // CH1.3 - `ci` asks about a BRANCH, and the branch it should ask about is this checkout is
        // the default. The override is what lets a proof ask about a branch this working tree is
        // not on.
        [CommandOption("--branch <BRANCH>")]
        [Description("ci: ask about this branch instead of the one this checkout is on.")]
        public string? Branch { get; init; }

        [CommandOption("--home <PATH>")]
        [Description("Read a state home other than this machine's.")]
        public string? Home { get; init; }

        // DV6.4 — the three options the sarif verb adds. --sha and --gitref are not conveniences:
        // code scanning anchors an alert to a commit that must EXIST in the destination repository,
        // and a scratch repo has never seen this working tree's HEAD.
        [CommandOption("--out <PATH>")]
        [Description("sarif: also write the rendered document here. Written before anything is sent.")]
        public string? Out { get; init; }

        [CommandOption("--sha <SHA>")]
        [Description("sarif: the commit the alerts anchor to. Must exist in the destination. Default: this repo's HEAD.")]
        public string? Sha { get; init; }

        [CommandOption("--gitref <REF>")]
        [Description("sarif: the ref the alerts belong to, e.g. refs/heads/main. Default: this repo's branch.")]
        public string? GitRef { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var verb = settings.Verb.ToLowerInvariant();
        if (verb is not ("sync" or "backfill" or "sarif" or "ci")) return Help();

        // DV6.4 — the sarif verb writes no issues and no columns, so the project-board gates below
        // are not its business: refusing a SARIF upload because a plan also asked for a Projects v2
        // board would deny a feature over an unrelated missing scope.
        var sarif = verb is "sarif";
        // CH1.3 - `ci` writes nothing to the mirror and needs no run: it READS the destination
        // repository and asks what CI said. The board gates below are about writing issues and
        // columns, so refusing this verb over a project-board misconfiguration would deny a
        // read because a write is not set up.
        var ci = verb is "ci";
        var plan = PlanConfig.Load(settings.ResolvePlanPath());

        // KS9.3 — the board's own coherence is decided first, because it costs nothing and because a
        // misspelt board or a missing project number is a fact about the plan, not a discovery made
        // against the network. It fires whether the project board was asked for in the plan or with
        // --project, and it fires before a destination is even resolved.
        var board = BoardView(plan, settings.Project);
        if (!sarif && !ci && board.BoardRefusal() is { } configRefusal)
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
        if (!sarif && !ci && board.WantsProjectBoard)
        {
            using var probe = new GithubClient(token, TimeSpan.FromSeconds(30));
            var stop = await GithubProjects.PreflightAsync(probe, board, source).ConfigureAwait(false);
            if (stop.Count > 0) return Refuse(stop);
        }

        if (ci) return await CiAsync(plan, repo, source, settings.Branch).ConfigureAwait(false);

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

        return sarif
            ? await SarifAsync(view, plan, repo, source, settings).ConfigureAwait(false)
            : await PushAsync(view, plan, repo, source, settings).ConfigureAwait(false);
    }

    /// <summary>DV6.4 — the bug ledger as code-scanning alerts. Reads the same archive the issue
    /// mirror reads, resolves every citation against the tracked files of the repository the run
    /// worked in, and hands GitHub one SARIF run in its own analysis category.</summary>
    private static async Task<int> SarifAsync(
        ArchiveView view, PlanConfig plan, string repo, string tokenSource, Settings settings)
    {
        var (token, _) = GithubIdentity.ResolveToken(plan);
        var payload = SarifDocument.Payload(
            view.Bugs(), SarifBugLocations.Resolver(TrackedFiles(view.Repo)),
            view.Run.EngineStampText ?? Core.BuildInfo.Current.Full);

        // Written FIRST, always. A document that failed to upload is still the thing a reader needs
        // to see, and an evidence artifact that only exists on success proves the wrong half.
        if (!string.IsNullOrWhiteSpace(settings.Out))
        {
            var outPath = Path.GetFullPath(settings.Out);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            await File.WriteAllTextAsync(outPath, payload.Json).ConfigureAwait(false);
            AnsiConsole.MarkupLine($"[grey]wrote[/] {Markup.Escape(outPath)}");
        }

        var sha = string.IsNullOrWhiteSpace(settings.Sha) ? Core.Git.Head(view.Repo) : settings.Sha.Trim();
        var gitRef = string.IsNullOrWhiteSpace(settings.GitRef)
            ? "refs/heads/" + Core.Git.Branch(view.Repo)
            : settings.GitRef.Trim();

        AnsiConsole.MarkupLine(
            $"[grey]run[/] {Markup.Escape(view.Run.ShortRunId)}  [grey]→[/] [aqua]{Markup.Escape(repo)}[/]  " +
            $"[grey]category[/] {Markup.Escape(SarifDocument.Category)}  [grey]token from[/] {Markup.Escape(tokenSource)}");
        AnsiConsole.MarkupLine($"[grey]commit[/] {Markup.Escape(sha)}  [grey]ref[/] {Markup.Escape(gitRef)}");
        if (GithubClient.ApiBaseIsOverridden)
            AnsiConsole.MarkupLine($"[yellow]api base overridden[/] → {Markup.Escape(GithubClient.ApiBase)} " +
                $"[grey]({GithubClient.ApiBaseEnvVar})[/]");
        if (settings.DryRun) AnsiConsole.MarkupLine("[yellow]dry run[/] — nothing will be sent.");

        using var client = new GithubClient(token!, TimeSpan.FromSeconds(30));
        var pass = await new GithubSarifSync(client, repo)
            .PushAsync(payload, sha, gitRef, tokenSource, settings.DryRun).ConfigureAwait(false);

        AnsiConsole.MarkupLine(Markup.Escape(pass.Summary()));
        foreach (var finding in payload.Findings.Take(10))
            AnsiConsole.MarkupLine($"  [grey]bug #{finding.Bug.Id}[/] {Markup.Escape(finding.Locations[0].Cite())}");
        if (payload.Findings.Count > 10)
            AnsiConsole.MarkupLine($"  [grey]… {payload.Findings.Count - 10} more[/]");
        foreach (var note in pass.Notes) AnsiConsole.MarkupLine($"[yellow]note[/] [grey]{Markup.Escape(note)}[/]");
        if (pass.StatusUrl is { } statusUrl) AnsiConsole.MarkupLine($"  [grey]status[/] {Markup.Escape(statusUrl)}");
        foreach (var error in pass.Errors) AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
        AnsiConsole.MarkupLine($"[grey]{client.RequestCount} requests[/]");
        return pass.Ok ? 0 : 1;
    }

    /// <summary>The tracked files of the repository the run worked in — the authority a bare
    /// <c>Foo.cs:12</c> is resolved against. Git's list, not a directory walk: build output and
    /// ignored scratch must never become the target of an alert.</summary>
    private static IReadOnlyList<string> TrackedFiles(string repo)
    {
        var result = Core.Git.Exec(repo, "ls-files");
        return result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

        // DV6.2 — the columns. Only when the board was asked for, and only AFTER the scope gate above
        // returned empty: a project sync built here is one that GitHub has already said this token
        // may write.
        var board = BoardView(plan, settings.Project);
        var project = board.WantsProjectBoard
            ? new GithubProjectSync(client, repo.Split('/', 2)[0], board.ProjectNumber)
            : null;
        if (project is not null)
            AnsiConsole.MarkupLine($"[grey]project board[/] #{board.ProjectNumber} " +
                $"[grey]under[/] {Markup.Escape(repo.Split('/', 2)[0])}");

        var sync = new GithubBoardSync(client, repo, prefix, map: null, project);
        var result = await sync.BackfillAsync(
            view.Log(), view.Run, view.Run.EngineStampText ?? Core.BuildInfo.Current.Full,
            diary, settings.DryRun, Ledger(view, prefix)).ConfigureAwait(false);

        AnsiConsole.MarkupLine(Markup.Escape(result.Summary()));
        // CH4.3 - the sweep says what it refused to close, every id, never truncated. A backfill
        // into a repository that already carries another era's board is the ordinary case here,
        // and the whole failure this replaces was a sweep that closed 23 of them without a word.
        if (result.RetireRefused.Count > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]retire refused[/] [grey]{result.RetireRefused.Count} task-marked " +
                "issue(s) are out of this plan but not attributable to this run - left untouched[/]");
            foreach (var refused in result.RetireRefused)
                AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(refused)}[/]");
        }
        foreach (var note in result.Project?.Notes ?? [])
            AnsiConsole.MarkupLine($"[yellow]column[/] [grey]{Markup.Escape(note)}[/]");
        if (result.Project?.ProjectUrl is { } boardUrl)
            AnsiConsole.MarkupLine($"  [grey]board[/] {Markup.Escape(boardUrl)}");
        foreach (var error in result.Project?.Errors.Take(5) ?? [])
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
        foreach (var (key, url) in result.Urls.OrderBy(u => u.Key, StringComparer.Ordinal).Take(5))
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(key)}[/] {Markup.Escape(url)}");
        if (result.Urls.Count > 5)
            AnsiConsole.MarkupLine($"  [grey]… {result.Urls.Count - 5} more[/]");
        foreach (var error in result.Errors.Take(10))
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(error)}[/]");
        AnsiConsole.MarkupLine($"[grey]{client.RequestCount} requests[/]");
        return result.Ok ? 0 : 1;
    }

    /// <summary>DV6.1 — the bug and followup half of a backfill. Read from the ARCHIVE (read-only,
    /// never migrated) and from the followups file of the repo that run worked in, because that is
    /// where the rows the ledger is talking about actually live.</summary>
    private static IReadOnlyList<GithubLedgerCard> Ledger(ArchiveView view, string prefix)
    {
        var followupsPath = Path.Combine(view.Repo, StateHome.ScratchDirName, "followups.md");
        var followups = File.Exists(followupsPath) ? Core.FollowupParser.Read(followupsPath) : [];
        return GithubLedgerPlan.Cards(view.Bugs(), followups, prefix);
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
        AnsiConsole.MarkupLine($"[grey]  --project <n> mirrors the COLUMNS to a Projects v2 board: needs the " +
            $"'{GithubProjects.RequiredScope}' scope, and refuses by name without it " +
            $"({GithubProjects.GrantCommand}).[/]");
        AnsiConsole.MarkupLine("  [aqua]github sarif --backfill <run> [[--repo owner/name]] [[--out file.sarif]] " +
            "[[--sha SHA]] [[--gitref REF]] [[--dry-run]][/]");
        AnsiConsole.MarkupLine("[grey]  every OPEN bug that names a file and a line becomes a code-scanning alert.[/]");
        AnsiConsole.MarkupLine("[grey]  free on a PUBLIC repository; a PRIVATE one needs GitHub Advanced Security.[/]");
        AnsiConsole.MarkupLine($"[grey]  a private repo also needs the '{GithubSarifSync.PrivateScope}' scope " +
            $"({GithubSarifSync.GrantCommand}).[/]");
        return 1;
    }
}
