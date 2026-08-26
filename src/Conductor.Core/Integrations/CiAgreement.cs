using System.Globalization;

using Conductor.Models;

namespace Conductor.Core.Integrations;

/// <summary>
/// CH1.3 — whether the battery a run just passed is the battery CI runs, in the DV1.1 shape.
///
/// <para><b>The failure this exists for.</b> For the whole Divan era the local gate battery was
/// green for all 23 checkpoints while <c>CI / windows - full gate battery</c> was red on every
/// commit, and NOTHING COMPARED THEM. The engine's phase gate is what this project trusts a
/// checkpoint against; if that verdict can be green beside a red CI, the trust is misplaced and the
/// run cannot see it. Four tests were failing on every machine on earth except one, for a month.</para>
///
/// <para><b>Why it reuses <see cref="ChannelHealth"/>.</b> Not because CI is an outbound channel —
/// it is not — but because the record IS the vocabulary the loud surfaces already speak: a named
/// thing, a state, why in one sentence, and what the owner does about it, with
/// <see cref="ChannelHealth.IsLoud"/> deciding whether it reaches the report header and the owner
/// queue. Inventing a parallel record would mean a second rendering in three surfaces and a second
/// definition of "loud" to drift from the first. It is deliberately NOT added to
/// <see cref="ChannelHealthProbe.Collect"/>: that list's roll-up line is pinned byte-for-byte by
/// DV1.1's own tests, and CI is not a channel the plan configured.</para>
///
/// <para><b>Derived, never stored</b>, on DV1.1's rule: the answer is recomputed from the workflow
/// files and the plan every time it is asked, so it clears itself the moment either is fixed.</para>
/// </summary>
public static class CiAgreementProbe
{
    /// <summary>Stable names; the owner queue keys on them, so they carry no spaces.</summary>
    public const string BatteryCheck = "ci-battery";

    /// <inheritdoc cref="BatteryCheck"/>
    public const string VerdictCheck = "ci-verdict";

    /// <summary>Every CI-agreement row, in a stable order: does CI run the same battery, and what did
    /// it say about the commit this run is on.</summary>
    /// <param name="platform">Which CI leg to compare against, as a substring of <c>runs-on</c>.
    /// Defaults to the platform this process is on, because that is where the gates just ran: a gate
    /// battery proved something about THIS operating system, and the CI leg that can contradict it
    /// is the one running the same one.</param>
    /// <param name="headSha">The commit the run is on. Defaults to the repo's HEAD.</param>
    public static IReadOnlyList<ChannelHealth> Collect(PlanConfig plan, string? platform = null, string? headSha = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [ProbeBattery(plan, platform ?? CurrentPlatform()), ProbeVerdict(plan, headSha)];
    }

    /// <summary>What CI said about the commit this run is on, from the recorded observation.
    ///
    /// <para>Four answers again, and the second is the one this project has already paid for.
    /// <b>Off</b>: nobody has asked — the honest word for "no measurement", and a probe that has not
    /// measured does not claim, exactly as <c>telegramStarted: null</c> does not claim.
    /// <b>Degraded</b>: there is a verdict and it is for a DIFFERENT commit. That is trap 16's shape
    /// — a branch reads green because the workflow that would have failed never ran on this head —
    /// and it is the difference between "CI is green" and "CI is green ON THIS COMMIT", which are
    /// different claims. <b>Dead</b>: CI is red on this commit while the gates just passed. That
    /// exact state held for 23 checkpoints and nothing said so. <b>Ready</b>: every active workflow
    /// concluded success on this very sha.</para></summary>
    private static ChannelHealth ProbeVerdict(PlanConfig plan, string? headSha)
    {
        if (CiStatus.Read(plan) is not { } status)
            return new ChannelHealth(VerdictCheck, ChannelState.Off,
                "no CI verdict has been fetched for this repo", "", "conductor github ci");

        var head = string.IsNullOrWhiteSpace(headSha) ? Git.Head(plan.Repo) : headSha.Trim();

        // A tag- or release-triggered workflow is SUPPOSED never to run on a feature branch, and
        // calling that a finding on every branch forever is how a check earns the right to be
        // ignored. Measured: the first live run of `conductor github ci` on this repo raised exactly
        // that about Release. Unknown (the file is not on disk) still counts as active — an unknown
        // is reported, not hidden.
        var active = status.Workflows
            .Where(w => string.Equals(w.State, "active", StringComparison.OrdinalIgnoreCase)
                     && CiWorkflows.BranchTriggered(plan.Repo, w.Path) != false)
            .ToList();

        if (active.Count == 0)
            return new ChannelHealth(VerdictCheck, ChannelState.Dead,
                $"{status.Repo} has no ACTIVE workflow that a push to {status.Branch} can fire, so nothing "
                + "on the server re-runs these gates"
                + (status.Workflows.Count > 0 ? FormattableString.Invariant($" ({status.Workflows.Count} workflow(s) found, none of them eligible)") : ""),
                "re-enable a workflow in the repository's Actions tab, or add this branch to one's push "
                + "trigger, or accept that the local battery is the only one",
                "conductor github ci");

        var red = active.Where(w => w.Conclusion.Length > 0
                                 && !string.Equals(w.Conclusion, "success", StringComparison.OrdinalIgnoreCase)
                                 && !string.Equals(w.Conclusion, "skipped", StringComparison.OrdinalIgnoreCase)).ToList();
        if (red.Count > 0)
            return new ChannelHealth(VerdictCheck, ChannelState.Dead,
                $"CI is red on {Short(red[0].RunSha)} while this run's gates passed: "
                + string.Join(", ", red.Select(w => $"{w.Workflow} {w.Conclusion}"))
                + $" (asked {status.FetchedUtc})",
                $"read {red[0].Url} - a green local battery beside a red CI is a finding, not a footnote",
                "");

        var stale = active.Where(w => !string.Equals(w.RunSha, head, StringComparison.OrdinalIgnoreCase)).ToList();
        if (stale.Count > 0 && head.Length > 0)
            return new ChannelHealth(VerdictCheck, ChannelState.Degraded,
                $"CI has no verdict for {Short(head)}, the commit this run is on: "
                + string.Join(", ", stale.Select(w => w.RunSha.Length == 0
                    ? $"{w.Workflow} has never run on {status.Branch}"
                    : $"{w.Workflow}'s newest run is for {Short(w.RunSha)}"))
                + " - a branch reads green when the workflow that would have failed never ran on this head",
                "push the commit, or re-ask once CI has run: conductor github ci",
                "conductor github ci");

        // A run that is still going is not a green. It is "no verdict yet for the commit this run is
        // on", which is the same class of answer as a verdict for a different sha and is loud for the
        // same reason: the gates being green here is not yet corroborated by anything. It clears
        // itself the moment CI finishes.
        var running = active.Where(w => w.Conclusion.Length == 0).ToList();
        if (running.Count > 0)
            return new ChannelHealth(VerdictCheck, ChannelState.Degraded,
                $"CI has not finished judging {Short(head)}: "
                + string.Join(", ", running.Select(w => $"{w.Workflow} {(w.Status.Length > 0 ? w.Status : "has no run")}"))
                + $" (asked {status.FetchedUtc})",
                "re-ask once it lands: conductor github ci", "conductor github ci");

        return new ChannelHealth(VerdictCheck, ChannelState.Ready,
            $"{active.Count} active workflow(s) green on {Short(head)} (asked {status.FetchedUtc})", "", "");
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>The <c>runs-on</c> substring for the platform this process is on.</summary>
    public static string CurrentPlatform() =>
        OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "macos"
        : "ubuntu";

    /// <summary>Do the two batteries run the same commands?
    ///
    /// <para>Four answers, and the middle two are the point. <b>Off</b>: no
    /// <c>.github/workflows</c> — there is no second battery, so there is nothing to disagree with,
    /// and a project without CI is not a project with a fault. <b>Dead</b>: workflows exist but none
    /// of them runs on this platform — the gates a checkpoint is judged by are proven nowhere else,
    /// which is worse than drift because there is no verdict to compare at all. <b>Degraded</b>: both
    /// batteries exist and they are not the same battery. <b>Ready</b>: every gate signature appears
    /// in CI and every CI step appears in the gates.</para>
    ///
    /// <para>Both directions of drift are reported and neither is dismissed. A step CI runs that the
    /// gates do not is how a checkpoint passes here and the branch goes red there — this repo's own
    /// case. A gate CI does not run is the other half: CI stops being evidence for that gate, so a
    /// green CI stops meaning what the phase gate thinks it means.</para></summary>
    private static ChannelHealth ProbeBattery(PlanConfig plan, string platform)
    {
        var jobs = CiWorkflows.Read(plan.Repo);
        if (jobs.Count == 0)
            return new ChannelHealth(BatteryCheck, ChannelState.Off,
                $"no {CiWorkflows.WorkflowDir} jobs in {plan.Repo} - the gate battery is the only battery",
                "", "");

        var mine = jobs.Where(j => j.RunsOn.Contains(platform, StringComparison.OrdinalIgnoreCase)).ToList();
        if (mine.Count == 0)
            return new ChannelHealth(BatteryCheck, ChannelState.Dead,
                $"no CI job runs on {platform}, so nothing re-runs the gates a checkpoint is judged by "
                + $"({jobs.Count} job(s) found: {string.Join(", ", jobs.Select(j => j.Job + " on " + (j.RunsOn.Length == 0 ? "?" : j.RunsOn)))})",
                $"add a {platform} job to {CiWorkflows.WorkflowDir} that runs the plan's gate commands, "
                + "or accept in writing that the local battery is the only one",
                "");

        var ci = Signatures(mine.SelectMany(j => j.Steps));
        var gates = Signatures(plan.Gates.Select(g => g.Command));

        var ciOnly = ci.Where(s => !gates.Contains(s, StringComparer.Ordinal)).ToList();
        var gatesOnly = gates.Where(s => !ci.Contains(s, StringComparer.Ordinal)).ToList();

        if (ciOnly.Count == 0 && gatesOnly.Count == 0)
            return new ChannelHealth(BatteryCheck, ChannelState.Ready,
                FormattableString.Invariant(
                    $"{gates.Count} gate step(s) match {string.Join(" + ", mine.Select(j => j.File + ":" + j.Job))} on {platform}"),
                "", "");

        var why = new List<string>();
        if (ciOnly.Count > 0) why.Add("CI runs " + List(ciOnly) + " that this run's gates do not");
        if (gatesOnly.Count > 0) why.Add("this run's gates run " + List(gatesOnly) + " that CI does not");

        return new ChannelHealth(BatteryCheck, ChannelState.Degraded,
            string.Join("; ", why) + " - a checkpoint can pass one battery and fail the other",
            (ciOnly.Count > 0
                ? $"add {List(ciOnly)} to plan.gates, or drop it from {string.Join("/", mine.Select(j => j.File).Distinct(StringComparer.Ordinal))}. "
                : "")
            + (gatesOnly.Count > 0
                ? $"add {List(gatesOnly)} to the {platform} job, or accept that CI is not evidence for it."
                : "").TrimEnd(),
            "");
    }

    /// <summary>Ordered, de-duplicated signatures for a set of command lines.</summary>
    private static List<string> Signatures(IEnumerable<string?> commands)
    {
        var all = new List<string>();
        foreach (var c in commands)
            foreach (var s in CiBatterySignature.Of(c))
                if (!all.Contains(s, StringComparer.Ordinal)) all.Add(s);
        return all;
    }

    /// <summary>Names them, and says how many it did not name. A detail line that silently shows the
    /// first three of nine reads as "three problems".</summary>
    private static string List(IReadOnlyList<string> signatures)
    {
        const int shown = 3;
        var head = string.Join(", ", signatures.Take(shown).Select(s => "'" + s + "'"));
        return signatures.Count <= shown
            ? head
            : head + FormattableString.Invariant($" (+{signatures.Count - shown} more)");
    }
}
