using System.Globalization;

using Conductor.Core;
using Conductor.Core.Release;
using Conductor.Core.Store;
using Conductor.Models;

using Spectre.Console;

namespace Conductor.Commands;

/// <summary>
/// CH4.2 — the acts that can be performed, performed; the acts that cannot, named.
///
/// <para>Four mechanical acts, each landing as its own commit so the era-close is reviewable one act
/// at a time: the CHANGELOG rename, the fast-forward merge, the tag, and the doc move with its
/// repoint. Five owner acts, printed on every run whatever the state, each with the command.</para>
///
/// <para><b>The one hard refusal.</b> This verb rewrites the CHANGELOG, moves the plan's own tracker
/// and rewrites the plan file. Doing that to a repository whose run is live would pull the ground out
/// from under the session reading it, so a live engine lock on the plan's own state directory stops
/// the verb before it measures anything. Deliberately the PLAN's lock and not the machine's: a
/// conductor running in another repository is a reason not to swap the binary (an owner act, below),
/// not a reason this repository cannot be merged.</para>
/// </summary>
public sealed partial class ReleaseCommand
{
    private static async Task<int> PerformAsync(Settings settings)
    {
        string planPath;
        PlanConfig plan;
        try
        {
            planPath = settings.ResolvePlanPath();
            plan = PlanConfig.Load(planPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException)
        {
            AnsiConsole.MarkupLine($"[red]the plan does not load:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var repo = Directory.Exists(plan.Repo) ? plan.Repo : Directory.GetCurrentDirectory();
        var dryRun = !settings.Yes;
        var tag = settings.Tag?.Trim();

        if (LiveHolder(repo) is { } holder)
        {
            AnsiConsole.MarkupLine(
                $"[red]refusing:[/] a conductor run is live in {Markup.Escape(Path.Combine(repo, StateHome.ScratchDirName))} (engine pid {holder.Pid}).");
            AnsiConsole.MarkupLine("[grey]this verb rewrites the CHANGELOG, moves the plan's tracker and repoints the plan itself —[/]");
            AnsiConsole.MarkupLine("[grey]doing that under a live session pulls the ground out from under it. Let the run end first.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[bold aqua]conductor release perform[/] — {Markup.Escape(plan.Name)}");
        AnsiConsole.MarkupLine(
            $"repo: {Markup.Escape(repo)}  ·  release: {(tag is { Length: > 0 } ? Markup.Escape(tag) : "[yellow]unnamed[/]")}" +
            (dryRun ? "  ·  [yellow]DRY RUN[/] (nothing is written; add --yes to perform)" : "  ·  [red]PERFORMING[/]"));
        AnsiConsole.WriteLine();

        var acts = await RunActsAsync(plan, planPath, repo, settings, dryRun).ConfigureAwait(false);

        foreach (var act in acts)
        {
            AnsiConsole.MarkupLine(RenderAct(act));
            foreach (var line in act.Detail)
                AnsiConsole.MarkupLine($"             [grey]{Markup.Escape(line)}[/]");
        }

        var exit = ReleasePerform.ExitCode(acts);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[{(exit == 1 ? "red" : "yellow")}]{Markup.Escape(ReleasePerform.Verdict(acts, dryRun))}[/]");
        if (!dryRun)
            AnsiConsole.MarkupLine("[grey]nothing left this machine: every act above is local, and pushing is stopped at.[/]");
        return exit;
    }

    /// <summary>Plan every mechanical act, then perform the ready ones IN ORDER, stopping at the
    /// first that fails. Re-planning between acts is the point rather than an inefficiency: the merge
    /// act's facts change the moment the CHANGELOG commit lands, and an act decided from a snapshot
    /// taken before the previous act ran is an act decided from a document.</summary>
    private static async Task<IReadOnlyList<ReleaseAct>> RunActsAsync(
        PlanConfig plan, string planPath, string repo, Settings settings, bool dryRun)
    {
        var performed = new List<ReleaseAct>();
        var halted = false;

        // MEASURED on the CH4.2 rig, and it made the dry run lie. A real run re-plans between acts, so
        // by the time `tag` is decided the CHANGELOG section the `changelog` act just wrote is on
        // disk. A dry run performs nothing, so every act was being decided against the state BEFORE
        // the sequence — and `tag` refused "no CHANGELOG section yet" for a sequence that would in
        // fact have succeeded. A rehearsal that reports STOPPED for a run that would work is worse
        // than no rehearsal: it teaches the owner to ignore it. So the dry run PROJECTS the one
        // ordering dependency the sequence actually has.
        var changelogWillLand = false;

        foreach (var name in ReleasePerform.MechanicalOrder)
        {
            var act = halted
                ? new ReleaseAct(name, ReleaseAct.Mechanical, ReleaseAct.Refused,
                    "not attempted - an earlier act refused or failed",
                    ["the era-close is ordered; performing this one on a half-done sequence is how a release goes wrong quietly"])
                : Plan(name, plan, planPath, repo, settings, dryRun && changelogWillLand);

            if (!halted && act.State == ReleaseAct.Ready && !dryRun)
                act = await PerformActAsync(name, act, plan, planPath, repo, settings).ConfigureAwait(false);

            if (name == ReleasePerform.ChangelogAct && act.State is ReleaseAct.Ready or ReleaseAct.Done or ReleaseAct.Nothing)
                changelogWillLand = true;

            if (act.State is ReleaseAct.Refused or ReleaseAct.Failed) halted = true;
            performed.Add(act);
        }

        var installed = InstalledStamp(repo);
        var store = PeekStore(plan, planPath);
        var backfill = ProbeBackfill(plan, settings.Repo, store);
        var live = ProbeProcesses(plan, planPath, installed);

        performed.AddRange(ReleasePerform.OwnerActs(new OwnerFacts(
            settings.Tag?.Trim(),
            string.IsNullOrWhiteSpace(settings.Base) ? "master" : settings.Base.Trim(),
            backfill.Repo,
            [.. backfill.Runs.Where(r => !r.InFlight && r.MirroredIssues == 0).Select(r => r.RunId)],
            live.Live.Count > 0)));

        return performed;
    }

    /// <param name="changelogSectionProjected">Dry run only: treat the CHANGELOG section as present
    /// because the act above it would write one. Never set on a real run, where the section is
    /// simply on disk by the time this is asked.</param>
    private static ReleaseAct Plan(string name, PlanConfig plan, string planPath, string repo,
        Settings settings, bool changelogSectionProjected)
        => name switch
        {
            ReleasePerform.ChangelogAct => ReleasePerform.Changelog(settings.Tag?.Trim(), ProbeChangelogRename(repo, settings.Tag)),
            ReleasePerform.MergeAct => MergeAct(repo, settings),
            ReleasePerform.TagAct => TagAct(repo, settings, changelogSectionProjected),
            _ => ReleasePerform.DocMove(ProbeDocMove(plan, planPath, repo, settings)),
        };

    private static ReleaseAct TagAct(string repo, Settings settings, bool changelogSectionProjected)
    {
        var facts = ProbeTag(repo, settings);
        if (!facts.ChangelogSectionOk && changelogSectionProjected)
        {
            var act = ReleasePerform.Tag(settings.Tag?.Trim(), facts with { ChangelogSectionOk = true });
            return act with
            {
                Detail = [.. act.Detail, "the CHANGELOG section this depends on does not exist yet - the changelog act above writes it first"],
            };
        }
        return ReleasePerform.Tag(settings.Tag?.Trim(), facts);
    }

    private static ReleaseAct MergeAct(string repo, Settings settings)
    {
        var facts = ProbeMerge(repo, settings.Base, settings.Branch);
        return ReleasePerform.Merge(ReleasePreflight.Merge(facts), facts);
    }

    // ---- performing ------------------------------------------------------------------------

    private static async Task<ReleaseAct> PerformActAsync(string name, ReleaseAct act, PlanConfig plan, string planPath,
        string repo, Settings settings)
    {
        try
        {
            return name switch
            {
                ReleasePerform.ChangelogAct => await DoChangelogAsync(act, repo, settings).ConfigureAwait(false),
                ReleasePerform.MergeAct => DoMerge(act, repo, settings),
                ReleasePerform.TagAct => DoTag(act, repo, settings),
                _ => await DoDocMoveAsync(act, plan, planPath, repo, settings).ConfigureAwait(false),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return act with { State = ReleaseAct.Failed, Headline = $"{act.Headline} - FAILED: {ex.Message}" };
        }
    }

    private static async Task<ReleaseAct> DoChangelogAsync(ReleaseAct act, string repo, Settings settings)
    {
        var version = settings.Tag!.Trim().TrimStart('v', 'V');
        var path = Path.Combine(repo, "CHANGELOG.md");
        var text = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        var heading = $"## [{version}] - {Today()}";
        var replaced = ReplaceFirst(text, "## [Unreleased]", heading);
        if (replaced is null)
            return act with { State = ReleaseAct.Failed, Headline = "the '## [Unreleased]' heading vanished between planning and writing" };

        await File.WriteAllTextAsync(path, replaced).ConfigureAwait(false);
        var commit = Commit(repo, ["CHANGELOG.md"], $"chore(release): CHANGELOG section for {version}");
        return commit ?? (act with
        {
            State = ReleaseAct.Done,
            Headline = $"renamed '## [Unreleased]' to '{heading}' and committed it",
        });
    }

    private static ReleaseAct DoMerge(ReleaseAct act, string repo, Settings settings)
    {
        var baseRef = string.IsNullOrWhiteSpace(settings.Base) ? "master" : settings.Base.Trim();
        var branch = string.IsNullOrWhiteSpace(settings.Branch) ? Git.Branch(repo) : settings.Branch.Trim();

        var checkout = Git.Exec(repo, "checkout", baseRef);
        if (checkout.ExitCode != 0)
            return act with { State = ReleaseAct.Failed, Headline = $"git checkout {baseRef} failed: {checkout.FailureReason()}" };

        var merge = Git.Exec(repo, "merge", "--ff-only", branch);
        if (merge.ExitCode != 0)
            return act with { State = ReleaseAct.Failed, Headline = $"git merge --ff-only {branch} failed: {merge.FailureReason()}" };

        return act with
        {
            State = ReleaseAct.Done,
            Headline = $"fast-forwarded {baseRef} to {branch} ({Git.Head(repo)[..12]})",
            Detail = [.. act.Detail, $"the working tree is now on {baseRef} - this verb does not put it back"],
        };
    }

    private static ReleaseAct DoTag(ReleaseAct act, string repo, Settings settings)
    {
        var version = settings.Tag!.Trim().TrimStart('v', 'V');
        var name = "v" + version;
        var r = Git.Exec(repo, "tag", "-a", name, "-m", $"{name} - see the CHANGELOG section of the same name");
        return r.ExitCode != 0
            ? act with { State = ReleaseAct.Failed, Headline = $"git tag -a {name} failed: {r.FailureReason()}" }
            : act with { State = ReleaseAct.Done, Headline = $"created annotated tag {name} at {Git.Head(repo)[..12]} (local)" };
    }

    /// <summary>The move and the repoint, as ONE commit. The plan is edited by targeted string
    /// replacement rather than through <c>PlanDocumentEditor</c>: that editor rewrites the whole
    /// document from the model, which drops the plan's comment header and normalises fields nobody
    /// asked it to touch. An era-close commit that silently rewrote the plan would be indefensible in
    /// review, so only the paths that moved are changed.</summary>
    private static async Task<ReleaseAct> DoDocMoveAsync(ReleaseAct act, PlanConfig plan, string planPath, string repo, Settings settings)
    {
        var facts = ProbeDocMove(plan, planPath, repo, settings);
        var staged = new List<string>();

        foreach (var move in facts.Moves.Where(m => m.SourceExists && !m.AlreadyInPlace))
        {
            var destination = Path.Combine(repo, move.To);
            var dir = Path.GetDirectoryName(destination);
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);

            var r = Git.Exec(repo, "mv", move.From, move.To);
            if (r.ExitCode != 0)
                return act with { State = ReleaseAct.Failed, Headline = $"git mv {move.From} failed: {r.FailureReason()}" };
            staged.Add(move.To);
        }

        var planText = await File.ReadAllTextAsync(planPath).ConfigureAwait(false);
        var rewritten = planText;
        foreach (var move in facts.Moves)
            rewritten = rewritten.Replace(JsonPath(move.From), JsonPath(move.To), StringComparison.Ordinal);

        if (!string.Equals(rewritten, planText, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(planPath, rewritten).ConfigureAwait(false);
            staged.Add(Relative(repo, planPath));
        }

        var moved = facts.Moves.Count(m => m.SourceExists && !m.AlreadyInPlace);
        var commit = Commit(repo, staged, $"docs(release): the era's plan and tracker join the record");
        return commit ?? (act with
        {
            State = ReleaseAct.Done,
            Headline = $"moved {moved} file(s) into the record and repointed the plan, in one commit",
            Detail = [.. act.Detail, "the plan was edited by targeted replacement - its comment header and every untouched field are intact"],
        });
    }

    // ---- probes for the acts ---------------------------------------------------------------

    internal static ChangelogRenameFacts ProbeChangelogRename(string repo, string? tag)
    {
        var path = Path.Combine(repo, "CHANGELOG.md");
        if (!File.Exists(path))
            return new ChangelogRenameFacts(false, false, false, 0, false, Today());

        var lines = File.ReadLines(path).ToArray();
        var version = tag?.Trim().TrimStart('v', 'V') ?? "";
        var already = version.Length > 0
            && lines.Any(l => l.StartsWith($"## [{version}]", StringComparison.Ordinal));

        var at = Array.FindIndex(lines, l => l.StartsWith("## [Unreleased]", StringComparison.Ordinal));
        if (at < 0) return new ChangelogRenameFacts(true, false, false, 0, already, Today());

        var body = new List<string>();
        for (var i = at + 1; i < lines.Length && !lines[i].StartsWith("## [", StringComparison.Ordinal); i++)
            if (!string.IsNullOrWhiteSpace(lines[i])) body.Add(lines[i]);

        var placeholder = body.Count == 0
            || (body.Count <= 4 && body.Any(l => l.Contains("Nothing yet", StringComparison.OrdinalIgnoreCase)
                                              || l.Contains("entries for the next era", StringComparison.OrdinalIgnoreCase)));

        return new ChangelogRenameFacts(true, true, placeholder, body.Count, already, Today());
    }

    internal static TagFacts ProbeTag(string repo, Settings settings)
    {
        var version = settings.Tag?.Trim().TrimStart('v', 'V') ?? "";
        var name = version.Length > 0 ? "v" + version : "";
        var exists = name.Length > 0
            && Git.Exec(repo, "rev-parse", "--verify", "--quiet", $"refs/tags/{name}").ExitCode == 0;

        var sectionOk = version.Length > 0
            && ProbeChangelog(repo, version) is { ScriptRan: true, ScriptExit: 0 } c
            && c.SectionLines.Any(l => !string.IsNullOrWhiteSpace(l));

        var baseRef = string.IsNullOrWhiteSpace(settings.Base) ? "master" : settings.Base.Trim();
        var sha = Git.Exec(repo, "rev-parse", "--verify", "--quiet", baseRef).Output.Trim();
        return new TagFacts(exists, sectionOk, baseRef, sha.Length > 0 ? sha : null);
    }

    /// <summary>What moves and where. Derived from the plan rather than from a hand-typed table: the
    /// two documents an era leaves behind are exactly the ones the plan names as <c>planDoc</c> and
    /// <c>tracker</c>, which is also why they are the two the repoint has to cover.</summary>
    internal static DocMoveFacts ProbeDocMove(PlanConfig plan, string planPath, string repo, Settings settings)
    {
        var history = string.IsNullOrWhiteSpace(settings.History) ? "docs/history" : settings.History.Trim().Replace('\\', '/');
        var moves = new List<DocMove>();

        Add(plan.PlanDoc, history);
        Add(plan.Tracker, $"{history}/archive/trackers");

        var writable = planPath.Length > 0 && File.Exists(planPath) && !new FileInfo(planPath).IsReadOnly;
        var dirty = Git.Exec(repo, "status", "--porcelain").Output.Trim().Length > 0;
        return new DocMoveFacts(moves, planPath, writable, dirty);

        void Add(string? source, string destinationDir)
        {
            if (string.IsNullOrWhiteSpace(source)) return;
            var from = source.Replace('\\', '/').TrimStart('.', '/');
            var name = Path.GetFileName(from);

            // A tracker is called TRACKER.md in every era, so the record would collide with itself
            // after two of them. The plan's own directory is what tells them apart.
            if (name.Equals("TRACKER.md", StringComparison.OrdinalIgnoreCase))
            {
                var era = Path.GetFileName(Path.GetDirectoryName(from) ?? "");
                if (era.Length > 0) name = $"{era.ToUpperInvariant()}-TRACKER.md";
            }

            var to = $"{destinationDir}/{name}";
            var sourceExists = File.Exists(Path.Combine(repo, from));
            var destination = Path.Combine(repo, to);
            moves.Add(new DocMove(from, to, sourceExists,
                DestinationOccupied: File.Exists(destination) && sourceExists,
                ReferencedByPlan: true));
        }
    }

    // ---- small shared helpers ---------------------------------------------------------------

    private static EngineLock.Holder? LiveHolder(string repo)
    {
        var holder = EngineLock.Read(Path.Combine(repo, StateHome.ScratchDirName));
        return holder is not null && EngineLock.IsLive(holder) ? holder : null;
    }

    private static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A path as it appears inside the plan's JSON, so a replacement cannot catch a
    /// substring of some longer path that merely starts the same way.</summary>
    private static string JsonPath(string path) => "\"" + path + "\"";

    private static string Relative(string repo, string path)
    {
        try { return Path.GetRelativePath(repo, path).Replace('\\', '/'); }
        catch (ArgumentException) { return path; }
    }

    private static string? ReplaceFirst(string text, string find, string replace)
    {
        var at = text.IndexOf(find, StringComparison.Ordinal);
        return at < 0 ? null : text[..at] + replace + text[(at + find.Length)..];
    }

    /// <summary>Stage exactly the named paths and commit them. Returns a FAILED act on trouble and
    /// null on success, so a caller reads it as "did this go wrong".</summary>
    private static ReleaseAct? Commit(string repo, IReadOnlyList<string> paths, string message)
    {
        if (paths.Count == 0) return null;
        var add = Git.Exec(repo, ["add", "--", .. paths]);
        if (add.ExitCode != 0)
            return new ReleaseAct("", ReleaseAct.Mechanical, ReleaseAct.Failed, $"git add failed: {add.FailureReason()}", []);

        var commit = Git.Exec(repo, "commit", "-m", message);
        return commit.ExitCode == 0
            ? null
            : new ReleaseAct("", ReleaseAct.Mechanical, ReleaseAct.Failed, $"git commit failed: {commit.FailureReason()}", []);
    }

    private static string RenderAct(ReleaseAct act)
    {
        var (glyph, colour) = act.State switch
        {
            ReleaseAct.Done => ("✓", "green"),
            ReleaseAct.Nothing => ("=", "grey"),
            ReleaseAct.Ready => ("→", "aqua"),
            ReleaseAct.Stopped => ("?", "yellow"),
            _ => ("✗", "red"),
        };
        return $"[{colour}]{glyph}[/] [bold]{Markup.Escape(act.Name),-10}[/] " +
               $"[grey]{act.Kind,-10}[/] {Markup.Escape(act.Headline)}";
    }
}
