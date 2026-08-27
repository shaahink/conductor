namespace Conductor.Core.Release;

/// <summary>
/// CH4.2 — what the era-close can perform, and what it must only name.
///
/// <para><b>The failure being fixed.</b> <c>ks12-3-owner-runbook.md</c> listed seven acts. Six were
/// never carried out, and the reason is not carelessness: in a prose checklist, "this one is yours,
/// go and do it" and "nobody did this one" are the same sentence. The next era's runbook opened by
/// discovering that. So the split is made structurally here — every act is <b>either</b> derivable
/// from measured facts, in which case the engine performs it, <b>or</b> a judgement, in which case
/// it is <see cref="ReleaseAct.Stopped"/> with the exact command, always, on every run.</para>
///
/// <para><b>Nothing here performs anything.</b> These are pure decisions over facts the verb
/// measured; the verb executes what comes back <see cref="ReleaseAct.Ready"/>. That is what lets a
/// test seed one unmet precondition and prove the act refuses BY NAME rather than quietly doing
/// nothing — the property the runbook never had.</para>
///
/// <para><b>An act may never run on an unmeasured precondition.</b> Every mechanical act below
/// either has its own facts record or is gated on a CH4.1 preflight line, and when the precondition
/// is absent the answer is <see cref="ReleaseAct.Refused"/> — never "assume it is fine".</para>
/// </summary>
public static class ReleasePerform
{
    public const string ChangelogAct = "changelog";
    public const string MergeAct = "merge";
    public const string TagAct = "tag";
    public const string DocMoveAct = "docmove";
    public const string VersionAct = "version";
    public const string SplitAct = "split";
    public const string CorpusAct = "corpus";
    public const string ReinstallAct = "reinstall";
    public const string PublishAct = "publish";

    /// <summary>The mechanical acts, in the order they must happen. The CHANGELOG is renamed before
    /// the tag because the tag build reads the section; the merge is before the tag because the tag
    /// names the merged tip; the doc move is last because it rewrites the plan the run is reading.</summary>
    public static IReadOnlyList<string> MechanicalOrder => [ChangelogAct, MergeAct, TagAct, DocMoveAct];

    /// <summary>The acts that are the owner's whatever the facts say.</summary>
    public static IReadOnlyList<string> OwnerOrder => [VersionAct, SplitAct, CorpusAct, ReinstallAct, PublishAct];

    /// <summary>1 when an act failed or was refused, 2 when the only thing left is the owner's, 0
    /// when every mechanical act is done or was already done. Note that a complete run is normally
    /// <b>2</b>, not 0: the era-close is not finished until a person does their part, and a script
    /// that read 0 here would be reading "finished" off a document again.</summary>
    public static int ExitCode(IReadOnlyList<ReleaseAct> acts)
    {
        ArgumentNullException.ThrowIfNull(acts);
        if (acts.Any(a => a.State is ReleaseAct.Failed or ReleaseAct.Refused)) return 1;
        return acts.Any(a => a.State == ReleaseAct.Stopped) ? 2 : 0;
    }

    /// <summary>One sentence naming what happened, never counting it.</summary>
    public static string Verdict(IReadOnlyList<ReleaseAct> acts, bool dryRun)
    {
        ArgumentNullException.ThrowIfNull(acts);
        var bad = acts.Where(a => a.State is ReleaseAct.Failed or ReleaseAct.Refused).Select(a => a.Name).ToList();
        var stopped = acts.Where(a => a.State == ReleaseAct.Stopped).Select(a => a.Name).ToList();
        var moved = acts.Where(a => a.State is ReleaseAct.Done or ReleaseAct.Ready).Select(a => a.Name).ToList();
        var did = dryRun ? "would perform" : "performed";

        if (bad.Count > 0)
            return $"STOPPED - {bad.Count} act(s) refused or failed: {string.Join(", ", bad)}; " +
                   $"{did} {(moved.Count == 0 ? "nothing" : string.Join(", ", moved))}. " +
                   "Nothing after the first refusal was attempted.";

        var head = moved.Count == 0
            ? "nothing mechanical was left to do"
            : $"{did}: {string.Join(", ", moved)}";
        return $"OWNER - {head}. {stopped.Count} act(s) are yours and were stopped at: {string.Join(", ", stopped)}";
    }

    /// <summary>The CHANGELOG rename. Mechanical because the heading and the date are derivable —
    /// and refused when the body is a placeholder, because the version number is the owner's input
    /// but the CONTENT under it is the release notes, and shipping "Nothing yet" to the world is a
    /// mistake nobody can take back (bug #88).</summary>
    public static ReleaseAct Changelog(string? tag, ChangelogRenameFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);

        if (string.IsNullOrWhiteSpace(tag))
            return Refuse(ChangelogAct, "no version named, so there is nothing to rename the heading to",
                ["the version number is the owner's - pass --tag <x.y.z>"]);

        var version = tag.TrimStart('v', 'V');

        if (!f.FileExists)
            return Refuse(ChangelogAct, "CHANGELOG.md does not exist", []);

        if (f.AlreadyHasVersionSection)
            return new ReleaseAct(ChangelogAct, ReleaseAct.Mechanical, ReleaseAct.Nothing,
                $"CHANGELOG already has a [{version}] section", ["nothing to rename - this act is idempotent"]);

        if (!f.HasUnreleased)
            return Refuse(ChangelogAct, "CHANGELOG has no '## [Unreleased]' heading to rename",
                [$"add one, or write the '## [{version}] - {f.Date}' section by hand"]);

        if (f.BodyIsPlaceholder)
            return Refuse(ChangelogAct,
                $"the [Unreleased] section is a placeholder ({f.BodyLines} non-blank line(s))",
                ["that body becomes the release notes the world reads, verbatim, via changelog-section.sh",
                 "writing what an era shipped is not derivable from anything this engine measures - it is yours"]);

        return new ReleaseAct(ChangelogAct, ReleaseAct.Mechanical, ReleaseAct.Ready,
            $"rename '## [Unreleased]' to '## [{version}] - {f.Date}' over {f.BodyLines} lines",
            [$"after this, `sh tools/changelog-section.sh {version}` exits 0 and prints that body"]);
    }

    /// <summary>The fast-forward merge. Gated on CH4.1's own merge verdict rather than on a second
    /// opinion: if the preflight line is not green, this act does not run, and it says which line
    /// stopped it. Two verbs disagreeing about whether a merge is a fast-forward is precisely the
    /// bug this stage exists to make impossible.</summary>
    public static ReleaseAct Merge(ReleaseCheck preflightMerge, MergeFacts f)
    {
        ArgumentNullException.ThrowIfNull(preflightMerge);
        ArgumentNullException.ThrowIfNull(f);

        // MEASURED on the CH4.2 rig: asked twice, the second run refused the merge because `master`
        // was one ahead of the branch — the doc-move commit this same verb had just landed on it. But
        // the branch carries nothing the base lacks, so `git merge --ff-only` is the no-op git itself
        // calls "Already up to date". "Already done" and "refused" are the two answers KS12.3 could
        // not tell apart, so this question is asked BEFORE the preflight gate: a verdict about
        // whether a merge can happen is irrelevant to a merge that has.
        if (f.BaseExists && f.BranchExists && f.Ahead == 0)
            return new ReleaseAct(MergeAct, ReleaseAct.Mechanical, ReleaseAct.Nothing,
                $"{f.BaseBranch} already contains every commit of {f.Branch}",
                ["nothing to fast-forward - this act is idempotent, and it stays so after the acts below move the base on"]);

        if (preflightMerge.State != ReleaseCheck.Ok)
            return Refuse(MergeAct, $"the preflight's merge line is not green: {preflightMerge.Headline}",
                [.. preflightMerge.Detail, "`conductor release preflight` is the measurement; this act only acts on it"]);

        return new ReleaseAct(MergeAct, ReleaseAct.Mechanical, ReleaseAct.Ready,
            $"git checkout {f.BaseBranch} && git merge --ff-only {f.Branch} ({f.Ahead} commit(s))",
            ["local only - pushing is an outward-facing act and is stopped at below"]);
    }

    /// <summary>The tag. Derivable from the version, and refused until the section it will publish
    /// exists: a tag pushed without one stops the release build before five platforms compile, which
    /// is what KS12.3 measured and DV7.3 measured again a whole era later.</summary>
    public static ReleaseAct Tag(string? tag, TagFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);

        if (string.IsNullOrWhiteSpace(tag))
            return Refuse(TagAct, "no version named, so there is no tag to derive",
                ["the version number is the owner's - pass --tag <x.y.z>"]);

        var name = "v" + tag.TrimStart('v', 'V');

        if (f.Exists)
            return new ReleaseAct(TagAct, ReleaseAct.Mechanical, ReleaseAct.Nothing,
                $"{name} already exists", ["nothing to tag - this act is idempotent, and it will not move an existing tag"]);

        if (!f.ChangelogSectionOk)
            return Refuse(TagAct, $"no CHANGELOG section for {tag.TrimStart('v', 'V')} yet, so {name} would be refused by the tag build",
                ["release.yml runs changelog-section.sh as its first job and uses the output as the release body",
                 "the changelog act above writes that section - let it run first"]);

        var target = f.TargetSha is { Length: > 0 } sha ? $" at {sha[..Math.Min(12, sha.Length)]}" : "";
        return new ReleaseAct(TagAct, ReleaseAct.Mechanical, ReleaseAct.Ready,
            $"git tag -a {name} on {f.TargetRef ?? "the merged tip"}{target}",
            ["local only - pushing the tag is what starts the release build, and that is stopped at below"]);
    }

    /// <summary>The doc move, which is a move AND a repoint or it is nothing. Trap 13: the plan's
    /// <c>tracker</c>, <c>planDoc</c> and <c>readOrder</c> are read at the start of every session, so
    /// a <c>git mv</c> that lands without them means the next session opens an empty file and says
    /// so to nobody.</summary>
    public static ReleaseAct DocMove(DocMoveFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);

        var pending = f.Moves.Where(m => m.SourceExists && !m.AlreadyInPlace).ToList();
        if (pending.Count == 0)
            return new ReleaseAct(DocMoveAct, ReleaseAct.Mechanical, ReleaseAct.Nothing,
                "nothing left to move - the plan already points into the record",
                [.. f.Moves.Select(m => m.AlreadyInPlace ? $"{m.To}  (already in the record)" : $"{m.From} -> {m.To}")]);

        if (f.PlanPath is null or { Length: 0 } || !f.PlanFileWritable)
            return Refuse(DocMoveAct, "the plan file cannot be rewritten, so the repoint half cannot happen",
                ["a move without the repoint leaves the next session reading nothing - this act is both or neither"]);

        var occupied = pending.Where(m => m.DestinationOccupied).ToList();
        if (occupied.Count > 0)
            return Refuse(DocMoveAct, $"{occupied.Count} destination(s) already hold a different file",
                [.. occupied.Select(m => $"{m.To} exists and {m.From} is not it")]);

        if (f.WorkingTreeDirty)
            return Refuse(DocMoveAct, "the working tree is dirty, and this act rewrites the plan the run is reading",
                ["commit or stash first, so the move and the repoint land as one reviewable change"]);

        var detail = pending.Select(m => $"{m.From} -> {m.To}" + (m.ReferencedByPlan ? "  (plan repointed)" : "")).ToList();
        detail.Add($"the plan at {f.PlanPath} is rewritten in the SAME act - tracker, planDoc and readOrder");
        return new ReleaseAct(DocMoveAct, ReleaseAct.Mechanical, ReleaseAct.Ready,
            $"git mv {pending.Count} file(s) into the record, and repoint the plan at them", detail);
    }

    /// <summary>The five acts this engine will not perform, named on every run whatever the state.
    /// Each carries what the owner types. None of them is ever <see cref="ReleaseAct.Nothing"/>:
    /// the whole point is that an owner-only act cannot be mistaken for one that has been handled.</summary>
    public static IReadOnlyList<ReleaseAct> OwnerActs(OwnerFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var version = f.Tag?.TrimStart('v', 'V');
        var tagName = version is { Length: > 0 } ? "v" + version : "v<x.y.z>";
        var baseBranch = f.BaseBranch is { Length: > 0 } b ? b : "master";

        return
        [
            new ReleaseAct(VersionAct, ReleaseAct.Owner, ReleaseAct.Stopped,
                version is { Length: > 0 } ? $"the release is named {version} because you said so" : "the release has no name yet",
                ["MinVer derives a build id, not a release name; nothing in this repo can decide which number an era is",
                 "everything mechanical above takes it as input - `--tag <x.y.z>`"]),

            new ReleaseAct(SplitAct, ReleaseAct.Owner, ReleaseAct.Stopped,
                "one release or two is a call about what the world reads, not about the tree",
                ["a single section covering both eras is one rename; splitting means tagging an intermediate commit",
                 "and cutting one section into two by hand. History usually makes one branch much cheaper - it does not make it right"]),

            new ReleaseAct(CorpusAct, ReleaseAct.Owner, ReleaseAct.Stopped,
                f.RunsOwedARecord.Count == 0
                    ? "no run is owed a GitHub record"
                    : $"{f.RunsOwedARecord.Count} run(s) have no GitHub record",
                [.. f.RunsOwedARecord.Select(r => $"conductor github sync --backfill {r} --dry-run   # then once, for real"),
                 "whether a run joins the published corpus is a decision about what the world sees, so this engine will not take it",
                 "run each backfill ONCE - a second pass inside GitHub's replica lag mints the board again (bug #79)"]),

            new ReleaseAct(ReinstallAct, ReleaseAct.Owner, ReleaseAct.Stopped,
                f.AnyConductorLive
                    ? "the reinstall cannot happen yet - a conductor is live on this machine"
                    : "the reinstall is yours: it overwrites the binary every run on this machine executes",
                ["tools/install.ps1, then `conductor version` to confirm it matches the tag",
                 "re-check the process table at the moment you type it: another repository's run may have started since"]),

            new ReleaseAct(PublishAct, ReleaseAct.Owner, ReleaseAct.Stopped,
                "pushing is what makes any of this public",
                [$"git push origin {baseBranch}",
                 $"git push origin {tagName}   # this is what starts the release build across five platforms",
                 "the merge and the tag above are LOCAL; nothing this engine did has left the machine"]),
        ];
    }

    private static ReleaseAct Refuse(string name, string headline, IReadOnlyList<string> detail)
        => new(name, ReleaseAct.Mechanical, ReleaseAct.Refused, headline, detail);
}
