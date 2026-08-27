namespace Conductor.Core.Release;

/// <summary>
/// CH4.1 — every precondition <c>.conductor/evidence/DV7/dv7-3-owner-runbook.md</c> measured by hand,
/// as something the engine measures.
///
/// <para><b>Why this is a type and not a command.</b> The runbook was written twice, in the same
/// shape, and the second one's first finding was that the first had not been carried out. A verdict
/// that lives inside a Spectre command can only be checked by running the release; these are pure
/// functions over measured facts, so a test can seed a red one and prove the exit code moves — which
/// is the one property a hand-written checklist never had.</para>
///
/// <para><b>The split is deliberate.</b> Measuring is impure — git, schtasks, sqlite, the process
/// table — and lives in the verb. Deciding is pure and lives here. Nothing in this file reads a
/// document and agrees with it.</para>
/// </summary>
public static class ReleasePreflight
{
    public const string MergeCheck = "merge";
    public const string ChangelogCheck = "changelog";
    public const string ProcessesCheck = "processes";
    public const string MigrationCheck = "migration";
    public const string CourierCheck = "courier";
    public const string BackfillCheck = "backfill";

    /// <summary>The names, in the order they run and print.</summary>
    public static IReadOnlyList<string> CheckNames =>
        [MergeCheck, ChangelogCheck, ProcessesCheck, MigrationCheck, CourierCheck, BackfillCheck];

    /// <summary>1 when anything is red, 2 when nothing is red but something is the owner's, 0 only
    /// when every line is green. Non-zero on red is the checkpoint's own bar.</summary>
    public static int ExitCode(IReadOnlyList<ReleaseCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Any(c => c.State == ReleaseCheck.Fail)) return 1;
        return checks.Any(c => c.State == ReleaseCheck.Owner) ? 2 : 0;
    }

    /// <summary>One sentence for the bottom of the page, naming the lines rather than counting them:
    /// "2 red" is unactionable, "red: changelog, courier" is a place to start.</summary>
    public static string Verdict(IReadOnlyList<ReleaseCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var red = checks.Where(c => c.State == ReleaseCheck.Fail).Select(c => c.Name).ToList();
        var owner = checks.Where(c => c.State == ReleaseCheck.Owner).Select(c => c.Name).ToList();
        if (red.Count > 0)
        {
            var tail = owner.Count > 0 ? $"; {owner.Count} waiting on the owner: {string.Join(", ", owner)}" : "";
            return $"NOT READY - {red.Count} of {checks.Count} red: {string.Join(", ", red)}{tail}";
        }
        if (owner.Count > 0)
            return $"OWNER - nothing is red, {owner.Count} of {checks.Count} need a decision only you make: " +
                   string.Join(", ", owner);
        return $"READY - all {checks.Count} preconditions measured green; nothing was written and nothing was tagged";
    }

    /// <summary>Runbook section 1. A fast-forward is a fact about two counts, not about intent.</summary>
    public static ReleaseCheck Merge(MergeFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var detail = new List<string>();

        if (!f.BaseExists || !f.BranchExists)
        {
            var missing = !f.BaseExists && !f.BranchExists ? $"{f.BaseBranch} and {f.Branch}"
                : !f.BaseExists ? f.BaseBranch : f.Branch;
            return new ReleaseCheck(MergeCheck, ReleaseCheck.Fail,
                $"no such branch: {missing}",
                ["nothing was compared - name the branches with --base and --branch"]);
        }

        if (f.Dirty)
            detail.Add("the working tree has uncommitted changes - commit or stash first, or the merge takes them along");

        // A stale LOCAL base is not a blocker when the branch already carries the remote's commits:
        // the fast-forward simply moves local base past them. Saying so is the difference between a
        // preflight that is trusted and one that is argued with.
        if (f.BaseBehindRemote > 0 && f.BranchBehindRemoteBase == 0)
            detail.Add($"local {f.BaseBranch} is {f.BaseBehindRemote} behind origin/{f.BaseBranch}, and {f.Branch} " +
                       $"already contains all {f.BaseBehindRemote} - the fast-forward carries them; `git pull` first so you see what you merge into");
        if (f.HasRemoteBase)
            detail.Add($"origin/{f.BaseBranch} is read as of your last fetch - this verb does not fetch, and a stale remote ref is a stale verdict");

        if (f.Behind > 0)
        {
            detail.Insert(0, $"git merge --ff-only {f.Branch} would be REFUSED - {f.BaseBranch} carries work {f.Branch} does not");
            detail.Add($"rebase {f.Branch} onto {f.BaseBranch}, or merge with a merge commit and say which you chose");
            return new ReleaseCheck(MergeCheck, ReleaseCheck.Fail,
                $"not a fast-forward: {f.BaseBranch} is {f.Behind} ahead of {f.Branch} ({f.Ahead} the other way)", detail);
        }

        if (f.BranchBehindRemoteBase > 0)
        {
            detail.Insert(0, $"origin/{f.BaseBranch} carries {f.BranchBehindRemoteBase} commit(s) {f.Branch} does not - " +
                             $"the local merge would succeed and the push would be REJECTED");
            detail.Add($"git fetch, then rebase {f.Branch} onto origin/{f.BaseBranch}");
            return new ReleaseCheck(MergeCheck, ReleaseCheck.Fail,
                $"not a fast-forward of the remote: origin/{f.BaseBranch} is {f.BranchBehindRemoteBase} ahead of {f.Branch}", detail);
        }

        if (f.Dirty)
            return new ReleaseCheck(MergeCheck, ReleaseCheck.Fail,
                $"{f.Branch} is {f.Ahead} ahead of {f.BaseBranch} and would fast-forward, but the working tree is not clean", detail);

        if (f.Ahead == 0)
            return new ReleaseCheck(MergeCheck, ReleaseCheck.Ok,
                $"{f.Branch} is already {f.BaseBranch} - nothing to fast-forward", detail);

        detail.Add($"git checkout {f.BaseBranch} && git merge --ff-only {f.Branch} && git push origin {f.BaseBranch}");
        return new ReleaseCheck(MergeCheck, ReleaseCheck.Ok,
            $"fast-forward: {f.Branch} is {f.Ahead} ahead of {f.BaseBranch}, 0 behind", detail);
    }

    /// <summary>Runbook section 2 — the step that was red at KS12.3, still red at DV7.3 one era later
    /// BECAUSE it was never done, and red again today (bug #88). <c>release.yml</c> runs
    /// <c>changelog-section.sh</c> as the first job of a tag build and uses its stdout as the release
    /// body, so a missing section stops a tag before five platforms compile.</summary>
    public static ReleaseCheck Changelog(ChangelogFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);

        if (!f.FileExists)
            return new ReleaseCheck(ChangelogCheck, ReleaseCheck.Fail, "CHANGELOG.md does not exist", []);

        if (string.IsNullOrWhiteSpace(f.Version))
        {
            var undecided = new List<string>
            {
                "the version number is a judgement - MinVer derives a build id, not a release name, and " +
                "single-versus-split is a call about what the world reads on the releases page",
                "re-run with --tag <x.y.z> and this line measures the section instead of naming you",
            };
            if (f.Headings.Count > 0)
                undecided.Add("sections present: " + string.Join(", ", f.Headings.Take(6)));
            return new ReleaseCheck(ChangelogCheck, ReleaseCheck.Owner,
                "no version named - which release this is, is yours to decide", undecided);
        }

        var version = f.Version.TrimStart('v', 'V');

        if (!f.ScriptRan)
            return new ReleaseCheck(ChangelogCheck, ReleaseCheck.Fail,
                $"tools/changelog-section.sh could not be run for {version}",
                [f.ScriptError.Length > 0 ? f.ScriptError : "no error was reported - is `sh` on PATH?",
                 "this script is what release.yml runs first on a tag build; if it cannot run here it will not run there"]);

        if (f.ScriptExit != 0)
        {
            var refused = new List<string>
            {
                $"exit {f.ScriptExit} - release.yml runs this as the first job of a tag build, so the tag would be refused",
                $"rename the heading to '## [{version}] - <date>' and re-run",
            };
            if (f.Headings.Count > 0)
                refused.Add("sections found: " + string.Join(", ", f.Headings.Take(6)));
            if (f.ScriptError.Length > 0)
                refused.Add(f.ScriptError);
            return new ReleaseCheck(ChangelogCheck, ReleaseCheck.Fail,
                $"no CHANGELOG section for {version}", refused);
        }

        var body = f.SectionLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (IsPlaceholder(body))
            return new ReleaseCheck(ChangelogCheck, ReleaseCheck.Fail,
                $"the {version} section exists but says nothing ({body.Count} non-blank line(s))",
                ["this body IS the release notes the world reads - a placeholder ships as the release",
                 .. body.Take(3)]);

        return new ReleaseCheck(ChangelogCheck, ReleaseCheck.Ok,
            $"CHANGELOG has a {version} section and changelog-section.sh exits 0 on it ({body.Count} lines)",
            ["that body is the release notes verbatim - re-read it before tagging",
             "if it quotes a run total or a dollar figure, re-run `conductor budget` and `conductor money`: it is a dated measurement"]);
    }

    /// <summary>A section is a placeholder when it has almost nothing in it, or when what it has is
    /// the scaffold's own apology.</summary>
    private static bool IsPlaceholder(IReadOnlyList<string> body)
    {
        if (body.Count == 0) return true;
        if (body.Count > 4) return false;
        return body.Any(l => l.Contains("Nothing yet", StringComparison.OrdinalIgnoreCase)
                          || l.Contains("entries for the next era", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Runbook section 3. The reinstall overwrites the binary every live run is EXECUTING,
    /// and a run spawns fresh conductor processes throughout a session (every task claim, every
    /// note), so the swap must land on a machine with none.</summary>
    public static ReleaseCheck Processes(ProcessFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var detail = new List<string>();

        foreach (var live in f.Live)
        {
            var mine = f.ConductorPid is { } pid && pid == live.Pid
                ? " <- CONDUCTOR_PID, the run asking this question"
                : "";
            detail.Add($"pid {live.Pid} {live.Path}{mine}");
        }

        if (f.Blockers.Count > 0)
        {
            detail.AddRange(f.Blockers);
            detail.Add("the reinstall belongs AFTER the run has ended - a live engine holding an old image is exactly trap 18's shape");
            return new ReleaseCheck(ProcessesCheck, ReleaseCheck.Fail,
                $"{f.Blockers.Count} reason(s) a binary swap is unsafe right now", detail);
        }

        detail.Add("re-run this immediately before the reinstall: another repository's run may start at any moment and it shares this binary");
        return new ReleaseCheck(ProcessesCheck, ReleaseCheck.Ok,
            "no conductor process is live and no state directory is locked", detail);
    }

    /// <summary>Runbook section 3, second half — trap 18. A fresh build whose schema is ahead of the
    /// installed engine's migrates a store the installed engine then refuses, which killed
    /// <c>task</c>, <c>note</c> and <c>bug</c> for the rest of an era at KS10.1.</summary>
    public static ReleaseCheck Migration(MigrationFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var detail = new List<string>();
        if (f.StorePath is { Length: > 0 })
        {
            var at = f.StoreVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unreadable";
            detail.Add($"store: {f.StorePath} at schema {at}");
        }

        if (string.IsNullOrWhiteSpace(f.InstalledSha))
            return new ReleaseCheck(MigrationCheck, ReleaseCheck.Fail,
                $"the installed engine did not say which commit it is; tree schema is v{f.TreeVersion}",
                [.. detail, "`conductor version --json` on PATH is what answers this - without it the skew cannot be measured"]);

        if (f.InstalledDirty)
            detail.Add($"the installed engine was built from a dirty tree ({f.InstalledSha}+uncommitted), so its commit does not fully identify it");

        if (f.MigrationsSince.Count > 0)
        {
            detail.AddRange(f.MigrationsSince.Take(8).Select(m => "  " + m));
            detail.Add("until the reinstall lands, no fresh build may open the live run.db for WRITE (trap 18)");
            return new ReleaseCheck(MigrationCheck, ReleaseCheck.Fail,
                $"schema skew: {f.MigrationsSince.Count} migration(s) landed since the installed engine ({f.InstalledSha})", detail);
        }

        if (f.StoreVersion is { } sv && sv > f.TreeVersion)
            return new ReleaseCheck(MigrationCheck, ReleaseCheck.Fail,
                $"the store is at schema v{sv} and this tree only knows v{f.TreeVersion} - something newer wrote it", detail);

        return new ReleaseCheck(MigrationCheck, ReleaseCheck.Ok,
            $"no schema skew: tree v{f.TreeVersion}, installed engine {f.InstalledVersion ?? f.InstalledSha} carries the same migrations",
            detail);
    }

    /// <summary>Runbook section 4. Read-only by construction: the scheduler, the presence file and
    /// the settings file. Telegram allows ONE getUpdates consumer per token and the live courier owns
    /// it, so nothing here dials the API.</summary>
    public static ReleaseCheck Courier(CourierFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);
        var detail = new List<string>();
        var red = new List<string>();

        if (!f.TokenSet)
            red.Add("no token: CONDUCTOR_TELEGRAM_TOKEN is unset in this process");
        else if (string.IsNullOrWhiteSpace(f.PersistedScope))
            red.Add("the token is set in this shell but PERSISTED nowhere - a logon-triggered task inherits " +
                    "persisted user/machine variables only, so the courier would come up tokenless after the reinstall");

        if (f.Chats == 0)
            red.Add("no chats are registered - the daemon would have nobody to answer");
        if (!f.RepoAllowed)
            red.Add("this repository is not on the courier's allowlist - its notes park in the dead-letter box (`conductor inbox parked`)");
        if (!f.TaskRegistered)
            red.Add("the scheduled task is not registered - `conductor courier install`");
        else if (!f.Running)
            red.Add($"the task is registered ({f.SchedulerState ?? "state unknown"}) but nothing is polling for this machine");

        var persisted = f.PersistedScope is { Length: > 0 } ? $", persisted at {f.PersistedScope} scope" : ", not persisted";
        detail.Add($"token {(f.TokenSet ? "set" : "unset")}{persisted} - {f.Chats} chat(s), {f.Projects} project(s) allowed");
        var pid = f.Pid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
        detail.Add($"task {(f.TaskRegistered ? f.SchedulerState ?? "registered" : "not installed")}" +
                   (f.Running ? $", running pid {pid}" : ", not running"));
        detail.Add("tools/install.ps1 stops the courier at step 0 and puts it back on the new engine - re-check `conductor courier status` after the reinstall");

        if (red.Count > 0)
            return new ReleaseCheck(CourierCheck, ReleaseCheck.Fail,
                $"the courier would not survive the reinstall: {red.Count} problem(s)", [.. red, .. detail]);

        return new ReleaseCheck(CourierCheck, ReleaseCheck.Ok,
            "the courier is installed, running and reachable, and its token is persisted where the task can see it", detail);
    }

    /// <summary>Runbook section 5. Which run is owed a GitHub record — and it stops there. Whether a
    /// run joins the published corpus is the owner's call, which is why a run with no record is
    /// <see cref="ReleaseCheck.Owner"/> and not <see cref="ReleaseCheck.Fail"/>: the verb's job is to
    /// make the omission impossible to miss, not to decide it.</summary>
    public static ReleaseCheck Backfill(BackfillFacts f)
    {
        ArgumentNullException.ThrowIfNull(f);

        if (string.IsNullOrWhiteSpace(f.Repo))
            return new ReleaseCheck(BackfillCheck, ReleaseCheck.Owner,
                "no GitHub destination is configured, so no run can be owed a record",
                ["set `github.repo` in the plan, or pass --repo, if the board is meant to be mirrored"]);

        var owed = f.Runs.Where(r => !r.InFlight && r.MirroredIssues == 0).ToList();
        var inflight = f.Runs.Where(r => r.InFlight).ToList();

        var detail = new List<string>();
        foreach (var r in inflight)
            detail.Add($"{Short(r.RunId)} {r.PlanName} - still {r.Status}; its own backfill is the closing act, not something owed yet");

        if (owed.Count == 0)
            return new ReleaseCheck(BackfillCheck, ReleaseCheck.Ok,
                $"every finished run in this store has a record on {f.Repo}", detail);

        foreach (var r in owed)
        {
            var claim = r.StoredStatus is { Length: > 0 } s && !s.Equals(r.Status, StringComparison.OrdinalIgnoreCase)
                ? $", though the row still claims {s}"
                : "";
            detail.Add($"{r.RunId} - {r.PlanName} ({r.Status}{claim}) has 0 issues on {f.Repo}");
            detail.Add($"  conductor github sync --backfill {r.RunId} --dry-run   # then once, for real, without --dry-run");
        }
        detail.Add("pass the FULL run id: several eras share one store directory and a slug cannot pick between them");
        detail.Add("run it ONCE - a second pass inside GitHub's replica lag mints the board again (bug #79)");

        return new ReleaseCheck(BackfillCheck, ReleaseCheck.Owner,
            $"{owed.Count} finished run(s) have no GitHub record - whether they join the published corpus is yours", detail);
    }

    private static string Short(string runId) => runId.Length > 12 ? runId[..12] : runId;
}
