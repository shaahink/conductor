using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Models;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// CH1.3 — the local gate battery and CI stop being able to disagree in silence.
///
/// <para><b>What was wrong.</b> For the whole Divan era the phase gate passed 23 checkpoints while
/// <c>CI / windows - full gate battery</c> was red on every commit of the era, and NOTHING COMPARED
/// THEM. Four tests were failing on every machine on earth except the author's — one because a raw
/// string literal inherited a CRLF checkout (CH1.1), three because the plans named an absolute
/// machine path (CH1.2) — and the surfaces a run reads said nothing at all, for a month. A run's
/// phase gate is what this project trusts a checkpoint against; if that verdict can be green beside
/// a red CI, the trust is misplaced and the run cannot see it.</para>
///
/// <para><b>What is asserted here.</b> Not "CI is green" — that is a fact about a server. The
/// checkable half is the one that decides what a green CI MEANS: <b>does CI run the same battery the
/// gates just ran?</b> It is answered from the workflow files and the plan, with no network, so it
/// answers identically inside the engine, in <c>conductor doctor</c>, and in a report read
/// afterwards.</para>
///
/// <para><b>Silence must never read as agreement.</b> The reader is deliberately naive — it scans
/// for job keys, <c>runs-on</c> and <c>run:</c> and understands no other YAML — so the tests that
/// matter most are the ones where it finds nothing:
/// <see cref="Workflows_that_do_not_cover_this_platform_are_a_finding_not_a_silence"/> and
/// <see cref="A_repo_with_no_workflows_is_quiet_because_there_is_no_second_battery"/>. Returning an
/// empty list that renders as "fine" is the exact failure this checkpoint exists to end.</para>
/// </summary>
public sealed class CH1_3CiAgreementTests
{
    private readonly ITestOutputHelper _out;

    public CH1_3CiAgreementTests(ITestOutputHelper output) => _out = output;

    // ───────────────────────── one command, reduced to what makes it that command ─────────────────

    /// <summary>The two batteries are written in different dialects and must still be comparable. A
    /// gate carries the repo's absolute path inside a <c>cmd /c</c> wrapper; the CI step is a bare
    /// line in a YAML block. Same step, and this is what makes them the same string.</summary>
    [Theory]
    // the gate dialect
    [InlineData("dotnet build Conductor.slnx -clp:ErrorsOnly", "dotnet build")]
    [InlineData("dotnet test Conductor.slnx", "dotnet test")]
    [InlineData("cmd /c \"cd /d C:\\code\\conductor\\face-go && go build ./... && go vet ./...\"", "go build|go vet")]
    // the CI dialect — same steps, different words
    [InlineData("dotnet build Conductor.slnx --configuration Debug", "dotnet build")]
    [InlineData("dotnet test Conductor.slnx --configuration Debug --no-build", "dotnet test")]
    [InlineData("go build ./...\ngo vet ./...\ngo test ./... -count=1", "go build|go vet|go test")]
    // a script IS the step: two powershell invocations are not interchangeable
    [InlineData("powershell -File tools/gates/ratchet.ps1", "powershell tools/gates/ratchet.ps1")]
    [InlineData("./src/Conductor/bin/Debug/net10.0/conductor demo", "conductor demo")]
    // noise on both sides
    [InlineData("cd face-go", "")]
    [InlineData("", "")]
    public void A_command_reduces_to_the_step_it_runs(string command, string expected)
    {
        Assert.Equal(expected, string.Join("|", CiBatterySignature.Of(command)));
    }

    // ───────────────────────────────── the four answers ───────────────────────────────────────────

    [Fact]
    public void The_same_battery_on_both_sides_is_quiet()
    {
        var plan = Rig(
            workflow: Workflow("windows-latest", "dotnet build Conductor.slnx --configuration Debug",
                                                 "dotnet test Conductor.slnx --no-build"),
            gates: ["dotnet build Conductor.slnx -clp:ErrorsOnly", "dotnet test Conductor.slnx"]);
        try
        {
            var row = Row(plan);

            Assert.Equal(ChannelState.Ready, row.State);
            Assert.False(row.IsLoud);
            Assert.Contains("ci.yml:windows", row.Detail, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>The seeded divergence, in the direction this repo actually has: CI runs a step the
    /// plan's gates do not, so a checkpoint passes here and the branch goes red there.</summary>
    [Fact]
    public void A_step_CI_runs_that_the_gates_do_not_is_loud_and_names_it()
    {
        var plan = Rig(
            workflow: Workflow("windows-latest", "dotnet test Conductor.slnx",
                                                 "powershell -File tools/gates/ratchet.ps1"),
            gates: ["dotnet test Conductor.slnx"]);
        try
        {
            var row = Row(plan);

            Assert.Equal(ChannelState.Degraded, row.State);
            Assert.True(row.IsLoud);
            Assert.Contains("powershell tools/gates/ratchet.ps1", row.Detail, StringComparison.Ordinal);
            Assert.Contains("CI runs", row.Detail, StringComparison.Ordinal);
            Assert.Contains("plan.gates", row.Fix, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>And the other direction, which is the half that costs a green CI its meaning: the run
    /// proves something CI never re-runs, so "CI is green" stops covering it.</summary>
    [Fact]
    public void A_gate_CI_does_not_run_is_loud_and_names_it()
    {
        var plan = Rig(
            workflow: Workflow("windows-latest", "dotnet build Conductor.slnx"),
            gates: ["dotnet build Conductor.slnx", "dotnet test Conductor.slnx"]);
        try
        {
            var row = Row(plan);

            Assert.Equal(ChannelState.Degraded, row.State);
            Assert.Contains("'dotnet test'", row.Detail, StringComparison.Ordinal);
            Assert.Contains("this run's gates run", row.Detail, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>Workflows exist and none of them runs where the gates ran. This is the trap-16 shape:
    /// a branch reads green because the workflow that would have failed never ran on this platform at
    /// all. It is a FAIL, not a silence — the gates a checkpoint is judged by are proven nowhere
    /// else.</summary>
    [Fact]
    public void Workflows_that_do_not_cover_this_platform_are_a_finding_not_a_silence()
    {
        var plan = Rig(
            workflow: Workflow("ubuntu-latest", "dotnet build Conductor.slnx"),
            gates: ["dotnet build Conductor.slnx"]);
        try
        {
            var row = Row(plan);

            Assert.Equal(ChannelState.Dead, row.State);
            Assert.True(row.IsLoud);
            Assert.Contains("no CI job runs on windows", row.Detail, StringComparison.Ordinal);
            Assert.Contains("ubuntu-latest", row.Detail, StringComparison.Ordinal);   // what it DID find
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>No workflows at all is <b>off</b>, not broken: there is no second battery to disagree
    /// with, and a project that does not use CI is not a project with a fault. Quiet, but still a row
    /// — "the report does not mention CI" and "CI runs the same battery" must not look the same.</summary>
    [Fact]
    public void A_repo_with_no_workflows_is_quiet_because_there_is_no_second_battery()
    {
        var plan = Rig(workflow: null, gates: ["dotnet test Conductor.slnx"]);
        try
        {
            var row = Row(plan);

            Assert.Equal(ChannelState.Off, row.State);
            Assert.False(row.IsLoud);
            Assert.Equal("ci-battery off", row.Summary);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    // ──────────────────── and the other question: what did CI say about THIS commit ────────────────

    /// <summary>Nobody has asked, so nothing is claimed — the <c>telegramStarted: null</c> rule. A
    /// probe that has not measured must not report a verdict, and must not report agreement
    /// either.</summary>
    [Fact]
    public void With_no_recorded_verdict_the_row_is_off_and_says_how_to_ask()
    {
        var plan = Rig(workflow: null, gates: []);
        try
        {
            var row = Verdict(plan);

            Assert.Equal(ChannelState.Off, row.State);
            Assert.False(row.IsLoud);
            Assert.Equal("conductor github ci", row.FixCommand);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>The state this project actually held for 23 checkpoints: the gates passed here and CI
    /// is red on the same commit.</summary>
    [Fact]
    public void A_red_CI_on_the_commit_the_gates_just_passed_is_dead()
    {
        var plan = Rig(workflow: null, gates: []);
        try
        {
            Record(plan, "deadbee", ("CI", "active", "deadbee", "completed", "failure"));
            var row = Verdict(plan, "deadbee");

            Assert.Equal(ChannelState.Dead, row.State);
            Assert.Contains("CI is red on deadbee", row.Detail, StringComparison.Ordinal);
            Assert.Contains("while this run's gates passed", row.Detail, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>Trap 16's shape, and the difference between two claims that are read as one: "CI is
    /// green" and "CI is green ON THIS COMMIT". A green verdict for a different sha says nothing
    /// about the tree this run is building on, and it must not be allowed to read as if it did.</summary>
    [Fact]
    public void A_green_verdict_for_a_different_commit_is_not_a_verdict_for_this_one()
    {
        var plan = Rig(workflow: null, gates: []);
        try
        {
            Record(plan, "0000001", ("CI", "active", "0000001", "completed", "success"));
            var row = Verdict(plan, "9999999");

            Assert.Equal(ChannelState.Degraded, row.State);
            Assert.True(row.IsLoud);
            Assert.Contains("CI has no verdict for 9999999", row.Detail, StringComparison.Ordinal);
            Assert.Contains("newest run is for 0000001", row.Detail, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>A workflow that has never run on this branch is a row saying so, not an absence. This
    /// is the half a commit's check-run list cannot see at all.</summary>
    [Fact]
    public void A_workflow_that_never_ran_on_this_branch_is_named()
    {
        var plan = Rig(workflow: null, gates: []);
        try
        {
            Record(plan, "abc1234",
                ("CI", "active", "abc1234", "completed", "success"),
                ("Nightly", "active", "", "", ""));
            var row = Verdict(plan, "abc1234");

            Assert.Equal(ChannelState.Degraded, row.State);
            Assert.Contains("Nightly has never run on", row.Detail, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>Every active workflow green on this very sha. The only shape that is quiet.</summary>
    [Fact]
    public void Green_on_this_commit_is_the_only_quiet_verdict()
    {
        var plan = Rig(workflow: null, gates: []);
        try
        {
            Record(plan, "abc1234",
                ("CI", "active", "abc1234", "completed", "success"),
                ("Release", "disabled_manually", "", "", ""));
            var row = Verdict(plan, "abc1234");

            Assert.Equal(ChannelState.Ready, row.State);
            Assert.False(row.IsLoud);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>Every workflow switched off is not "green": there is nothing on the server re-running
    /// these gates at all, and a disabled workflow's last run can be a year old.</summary>
    [Fact]
    public void A_repo_whose_workflows_are_all_disabled_is_dead_not_green()
    {
        var plan = Rig(workflow: null, gates: []);
        try
        {
            Record(plan, "abc1234", ("CI", "disabled_manually", "abc1234", "completed", "success"));
            var row = Verdict(plan, "abc1234");

            Assert.Equal(ChannelState.Dead, row.State);
            Assert.Contains("no ACTIVE workflow", row.Detail, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>A tag-triggered workflow is SUPPOSED never to run on a feature branch, and calling
    /// that a finding on every branch forever is how a check earns the right to be ignored. Measured
    /// on this repo: the first live run of <c>conductor github ci</c> raised exactly this about
    /// Release. The observation still RECORDS "never ran" — only the derived health stops calling it
    /// a fault.</summary>
    [Fact]
    public void A_workflow_that_cannot_fire_on_a_branch_push_is_not_a_finding()
    {
        var plan = Rig(
            workflow: Workflow("windows-latest", "dotnet test Conductor.slnx"),
            gates: ["dotnet test Conductor.slnx"]);
        try
        {
            // A second workflow file that only a tag can start.
            File.WriteAllText(Path.Combine(plan.Repo, ".github", "workflows", "release.yml"),
                "name: Release\non:\n  workflow_dispatch:\n  release:\n    types: [published]\njobs:\n  publish:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ship\n");

            Record(plan, "abc1234",
                ("ci", "active", "abc1234", "completed", "success"),
                ("release", "active", "", "", ""));

            // …and the shape this repo's own release.yml has: a push trigger that names only tags.
            File.WriteAllText(Path.Combine(plan.Repo, ".github", "workflows", "tagonly.yml"),
                "name: Tag\non:\n  push:\n    tags: [\"v*\"]\n  workflow_dispatch:\njobs:\n  x:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo ship\n");

            Assert.False(CiWorkflows.BranchTriggered(plan.Repo, ".github/workflows/release.yml"));
            Assert.False(CiWorkflows.BranchTriggered(plan.Repo, ".github/workflows/tagonly.yml"));
            Assert.True(CiWorkflows.BranchTriggered(plan.Repo, ".github/workflows/ci.yml"));
            Assert.Null(CiWorkflows.BranchTriggered(plan.Repo, ".github/workflows/gone.yml"));

            var row = Verdict(plan, "abc1234");
            Assert.Equal(ChannelState.Ready, row.State);
            Assert.Equal(2, CiStatus.Read(plan)!.Workflows.Count);   // recorded, just not counted against
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>A run still going is not a green. It is "no verdict yet for this commit", which is
    /// loud for the same reason a verdict for a different sha is: the gates being green here is not
    /// yet corroborated by anything. It clears itself the moment CI lands.</summary>
    [Fact]
    public void A_run_still_in_progress_on_this_commit_is_not_a_green()
    {
        var plan = Rig(workflow: null, gates: []);
        try
        {
            Record(plan, "abc1234", ("CI", "active", "abc1234", "in_progress", ""));
            var row = Verdict(plan, "abc1234");

            Assert.Equal(ChannelState.Degraded, row.State);
            Assert.Contains("has not finished judging abc1234", row.Detail, StringComparison.Ordinal);
            Assert.Contains("CI in_progress", row.Detail, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>The observation survives a round trip through the file the surfaces read.</summary>
    [Fact]
    public void The_observation_is_read_back_exactly_as_it_was_recorded()
    {
        var plan = Rig(workflow: null, gates: []);
        try
        {
            Record(plan, "abc1234", ("CI", "active", "abc1234", "completed", "failure"));

            var back = CiStatus.Read(plan)!;
            Assert.Equal("abc1234", back.HeadSha);
            Assert.Equal("feat/x", back.Branch);
            var w = Assert.Single(back.Workflows);
            Assert.Equal("failure", w.Conclusion);
            Assert.True(File.Exists(Path.Combine(plan.StateDir, CiStatus.FileName)));
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    // ─────────────────────── it reaches the two surfaces a run actually reads ──────────────────────

    /// <summary>The report header. A finding that only a unit test can see is the Divan era
    /// again.</summary>
    [Fact]
    public void The_divergence_reaches_the_report_header()
    {
        var plan = Rig(
            workflow: Workflow("windows-latest", "dotnet test Conductor.slnx", "powershell -File tools/gates/ratchet.ps1"),
            gates: ["dotnet test Conductor.slnx"]);
        try
        {
            var report = Reporter.Build(plan, new RunState { RunId = "ch13" }, new TrackerSnapshot(), null);
            _out.WriteLine(report.Split('\n').First(l => l.Contains("CI ", StringComparison.Ordinal)));

            Assert.Contains("**CI battery:** ci-battery DEGRADED", report, StringComparison.Ordinal);
            Assert.Contains("powershell tools/gates/ratchet.ps1", report, StringComparison.Ordinal);
            // Above the fold, with the run's other facts — not in a footer nobody scrolls to.
            Assert.True(report.IndexOf("**CI battery:**", StringComparison.Ordinal) < report.Length / 2,
                "the CI line belongs in the header block");
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>And the owner queue, because only the owner can edit a workflow or a plan's
    /// gates.</summary>
    [Fact]
    public void The_divergence_reaches_the_owner_queue()
    {
        var plan = Rig(
            workflow: Workflow("windows-latest", "dotnet test Conductor.slnx", "powershell -File tools/gates/ratchet.ps1"),
            gates: ["dotnet test Conductor.slnx"]);
        try
        {
            var items = OwnerQueue.Collect(plan, new RunState { RunId = "ch13" }, new TrackerSnapshot(),
                new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc));

            var item = Assert.Single(items, i => i.Kind == "ci");
            Assert.Equal("ci-battery", item.Id);
            Assert.Contains("DEGRADED", item.Title, StringComparison.Ordinal);
            // Named precisely: it does not unblock a stage, and saying it did is the lie that costs
            // the queue its credibility.
            Assert.Contains("nothing in the run", item.Unblocks, StringComparison.Ordinal);
            Assert.Contains("plan.gates", item.Detail!, StringComparison.Ordinal);
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    /// <summary>A quiet row never reaches either surface — the DV1.1 rule that keeps the surfaces
    /// readable. An operator who is shouted at about a healthy thing learns to skip the block.</summary>
    [Fact]
    public void An_agreeing_battery_puts_nothing_in_the_owner_queue()
    {
        var plan = Rig(
            workflow: Workflow("windows-latest", "dotnet test Conductor.slnx"),
            gates: ["dotnet test Conductor.slnx"]);
        try
        {
            var items = OwnerQueue.Collect(plan, new RunState { RunId = "ch13" }, new TrackerSnapshot(),
                new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc));

            Assert.DoesNotContain(items, i => i.Kind == "ci");
        }
        finally { TestTemp.DeleteTree(plan.Repo); }
    }

    // ───────────────────────────── against this repo's real workflow ──────────────────────────────

    /// <summary>The reader, against the file it was written for. Pinned on the steps rather than on
    /// the verdict: a test that asserts "this repo currently diverges" is a test that fails the day
    /// somebody fixes it, which is the wrong way round.</summary>
    [Fact]
    public void This_repos_own_windows_leg_is_read_correctly()
    {
        var root = RepoRoot();
        if (root is null) return; // not a full checkout — soft skip, as the other repo sweeps do

        var jobs = CiWorkflows.Read(root);
        Assert.NotEmpty(jobs);

        var windows = jobs.Where(j => j.RunsOn.Contains("windows", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(windows);

        var signatures = windows.SelectMany(j => j.Steps).SelectMany(CiBatterySignature.Of).Distinct(StringComparer.Ordinal).ToList();
        _out.WriteLine("windows leg runs: " + string.Join(", ", signatures));

        foreach (var expected in new[] { "dotnet build", "dotnet test", "go build", "go vet", "go test" })
            Assert.Contains(expected, signatures, StringComparer.Ordinal);
    }

    // ────────────────────────────────────────── fixtures ──────────────────────────────────────────

    private static ChannelHealth Row(PlanConfig plan) =>
        CiAgreementProbe.Collect(plan, "windows").First(r => r.Channel == CiAgreementProbe.BatteryCheck);

    private static ChannelHealth Verdict(PlanConfig plan, string? head = null) =>
        CiAgreementProbe.Collect(plan, "windows", head).First(r => r.Channel == CiAgreementProbe.VerdictCheck);

    /// <summary>Record an observation the way <c>conductor github ci</c> does, without the network.</summary>
    private static void Record(PlanConfig plan, string head,
        params (string Workflow, string State, string RunSha, string Status, string Conclusion)[] workflows) =>
        CiStatus.Write(plan, new CiStatus("2026-08-26 12:00:00Z", "owner/repo", "feat/x", head,
            [.. workflows.Select(w => new CiWorkflowVerdict(
                w.Workflow, ".github/workflows/" + w.Workflow.ToLowerInvariant() + ".yml", w.State,
                w.RunSha, w.Status, w.Conclusion,
                w.RunSha.Length == 0 ? "" : "https://github.com/owner/repo/actions/runs/1"))]));

    private static string Workflow(string runsOn, params string[] steps)
    {
        var lines = new List<string> { "name: CI", "on: [push]", "jobs:", "  windows:", $"    runs-on: {runsOn}", "    steps:" };
        foreach (var s in steps)
        {
            if (!s.Contains('\n', StringComparison.Ordinal)) { lines.Add($"      - run: {s}"); continue; }
            lines.Add("      - run: |");
            foreach (var l in s.Split('\n')) lines.Add("          " + l);
        }
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>A repo with a tracker, a plan holding <paramref name="gates"/>, and optionally one
    /// workflow file. Nothing else — the probe reads exactly these two things.</summary>
    private static PlanConfig Rig(string? workflow, string[] gates)
    {
        var repo = Path.Combine(Path.GetTempPath(), "conductor-ch13-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"), "# tracker\n");
        if (workflow is not null)
        {
            var dir = Path.Combine(repo, ".github", "workflows");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ci.yml"), workflow);
        }

        return new PlanConfig
        {
            Name = "ch13",
            Repo = repo,
            Tracker = "TRACKER.md",
            Gates = [.. gates.Select((g, i) => new GateConfig { Name = "g" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), Command = g })],
        };
    }

    private static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".github", "workflows"))) return d.FullName;
        return null;
    }
}
