using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Core.Orchestration;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SF0.2 — the four bugs the core run filed against its own verdict path.
/// <list type="bullet">
/// <item>#10 a claim made during a Verify or Audit session belonged to no session at all;</item>
/// <item>#10's rider, the dead <c>GateSummary ?? "completed"</c> evidence fallback;</item>
/// <item>#4 a phase-gate RED announced a session kind the dispatcher would not select;</item>
/// <item>#3 a confirmed LAST stage with a queued verify spun the run loop forever;</item>
/// <item>#8 the harness git helper that made every <c>NewCommits</c> assertion vacuous.</item>
/// </list>
/// </summary>
public sealed class SF0_2EvidenceFallbackTests
{
    /// <summary>The measurement that says the old expression was DEAD, not merely unlucky:
    /// <c>SessionRecord.GateSummary</c> is a non-nullable string that defaults to "", so
    /// <c>rec.GateSummary ?? "completed"</c> could never once have produced "completed" — and on a
    /// verify or audit session, which returns before the battery assigns it, "" is what it stays.</summary>
    [Fact]
    public void GateSummary_IsNeverNull_SoTheOldNullCoalescingFallbackWasUnreachable()
    {
        var rec = new SessionRecord { Number = 1, Stage = "H0", Kind = SessionKind.Verify };

        Assert.NotNull(rec.GateSummary);
        Assert.Equal("", rec.GateSummary);

        // The old line, verbatim. It yields "" — never the fallback it was written to yield.
        Assert.Equal("", rec.GateSummary ?? "completed");

        // What replaced it treats an empty battery summary as absent, which is what it means.
        Assert.Equal("completed", string.IsNullOrWhiteSpace(rec.GateSummary) ? "completed" : rec.GateSummary);
    }
}

/// <summary>Bug #4 — the RED line names the kind the DISPATCHER will pick, so both must read the
/// same ranking. <see cref="SessionRunner.PendingToKind"/> is that one ranking; this pins the
/// ordering the announcement now depends on (verify ABOVE fix, which is the whole bug: writing
/// PendingFix does not make the next session a fix).</summary>
public sealed class SF0_2PendingKindRankingTests
{
    [Fact]
    public void APendingVerify_OutranksAPendingFix_WhichIsWhyAnnouncingFixWasALie()
    {
        var fix = new PendingFix { FromSession = 1 };
        var verify = new PendingVerify { FromSession = 1, StageStartHead = "HEAD" };

        Assert.Equal(SessionKind.Fix, SessionRunner.PendingToKind(null, null, null, fix));
        Assert.Equal(SessionKind.Verify, SessionRunner.PendingToKind(null, null, verify, fix));
        Assert.Equal(SessionKind.Audit,
            SessionRunner.PendingToKind(null, new PendingAudit { StageId = "H0" }, verify, fix));
        Assert.Equal(SessionKind.Resume,
            SessionRunner.PendingToKind(new PendingResume { FromSession = 1 }, null, verify, fix));
    }
}

/// <summary>
/// Bug #8, measured rather than asserted from reading: the helper <c>HarnessTests</c> used until
/// this checkpoint split its argument string on spaces before handing the pieces to
/// <see cref="ProcessRunner"/>'s ArgumentList. This drives BOTH shapes against a real git repo and
/// shows what the harness was actually testing against — a repo with no commits at all, where
/// <see cref="Git.CommitsSince"/> short-circuits and every NewCommits assertion passes for free.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SF0_2VacuousHarnessGitTests : IDisposable
{
    private readonly string _repo;

    public SF0_2VacuousHarnessGitTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-sf02git-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception) { }
    }

    private ProcResult SplitOnSpaces(string args) => ProcessRunner.Run("git",
        args.Split(' ', StringSplitOptions.RemoveEmptyEntries), _repo,
        TimeSpan.FromSeconds(30), CancellationToken.None);

    private ProcResult OneArgPerParameter(params string[] args) =>
        ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(30), CancellationToken.None);

    [Fact]
    public void TheOldSpaceSplittingHelper_LeftTheHarnessRepoWithZeroCommits()
    {
        OneArgPerParameter("init", "-b", "main");
        OneArgPerParameter("config", "user.email", "sf02@test");
        OneArgPerParameter("config", "user.name", "SF02 Test");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# sf0.2");
        SplitOnSpaces("add README.md");

        // The exact call HarnessTests made. Six space-separated tokens: git reads `"chore:` as the
        // whole message and `initial`, `commit"` as pathspecs that match nothing.
        var commit = SplitOnSpaces("commit -m \"chore: initial commit\" --no-gpg-sign");
        Assert.True(commit.ExitCode != 0,
            $"expected the space-split commit to FAIL; it exited 0: {commit.Output} {commit.StdErr}");

        // Nothing checked that exit code, so this is the repo every harness assertion ran against:
        // no commits at all. `git rev-parse HEAD` cannot resolve, so Git.Head echoes back the
        // unresolved rev — the literal "HEAD" — and CommitsSince("HEAD") asks git for the range
        // `HEAD..HEAD`, which errors, hits the `ExitCode != 0` guard and yields an empty list.
        // Whichever way a session's start head was recorded, the answer was "no new commits", so
        // every harness assertion about NewCommits passed for free.
        Assert.Equal("HEAD", Git.Head(_repo));
        Assert.Empty(Git.CommitsSince(_repo, Git.Head(_repo)));
        Assert.Empty(Git.CommitsSince(_repo, ""));

        // One argument per parameter is all it takes.
        var fixedCommit = OneArgPerParameter("commit", "-m", "chore: initial commit", "--no-gpg-sign");
        Assert.True(fixedCommit.ExitCode == 0,
            $"one-arg-per-parameter commit failed ({fixedCommit.ExitCode}): {fixedCommit.Output} {fixedCommit.StdErr}");
        Assert.NotEqual("", Git.Head(_repo));

        var head = Git.Head(_repo);
        File.WriteAllText(Path.Combine(_repo, "work.txt"), "work");
        OneArgPerParameter("add", "-A");
        OneArgPerParameter("commit", "-m", "feat: real work", "--no-gpg-sign");

        // …and only now does CommitsSince have anything to say, which is the assertion the harness
        // believed it was making all along.
        var since = Git.CommitsSince(_repo, head);
        Assert.Single(since);
        Assert.Contains("feat: real work", since[0], StringComparison.Ordinal);
    }
}

/// <summary>A sink that keeps every narrated line, so a test can assert on what the RUN SAID —
/// bug #4 is a defect in a sentence, and the sentence is the artifact.</summary>
internal sealed class RecordingSink : IProgressSink
{
    private readonly List<string> _lines = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Lines { get { lock (_gate) { return [.. _lines]; } } }

    public void Log(string line) { lock (_gate) { _lines.Add(line); } }
    public void AgentEvent(AgentEvent ev) { }
    public void Snapshot(DashboardSnapshot snap) { }
    public ControlCommand? PollControl() => null;
}

/// <summary>
/// SF0.2 live — a real orchestrator, a real git repo, and a stand-in agent that claims through the
/// REAL freshly-built <c>conductor task --done</c> in its own process, exactly as an agent does.
/// Nothing below is asserted from source reading: the claims here are engine behaviour, so they are
/// measured against a run.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SF0_2VerdictLiveTests : IDisposable
{
    private readonly string _repo;

    public SF0_2VerdictLiveTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-sf02-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repo);
        GitRun("init", "-b", "main");
        GitRun("config", "user.email", "sf02@test");
        GitRun("config", "user.name", "SF02 Test");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); }
        catch (Exception) { }
    }

    private void GitRun(params string[] args)
    {
        var r = ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
    }

    private static string ConductorExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "conductor.exe");
        Assert.True(File.Exists(exe), $"the freshly-built CLI must sit beside the test assembly: {exe}");
        return exe;
    }

    private Task WriteTrackerAsync(params string[] rows) =>
        File.WriteAllTextAsync(Path.Combine(_repo, "TRACKER.md"),
            "# SF0.2 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            string.Join("\n", rows) + "\n", CancellationToken.None);

    private PlanConfig BuildPlan(string agentScript, string gatePolicy, params GateConfig[] gates)
    {
        var plan = new PlanConfig
        {
            Name = "SF02Plan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "SF02", Sessions = 4 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                // Three arguments, never one string: cmd needs /c, the absolute script path and the
                // prompt as separate argv entries or it silently runs nothing.
                Args = { "/c", agentScript, "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = gatePolicy,
            // everySession: the dial that makes a green delivery queue a VERIFY, which is the
            // session all three engine bugs here need in flight.
            Pipeline = new PipelineRules { Qa = new QaRule { Mode = "everySession" } },
        };
        foreach (var g in gates) plan.Gates.Add(g);
        plan.Report.Commit = false;
        return plan;
    }

    private async Task<string> WritePlanFileAsync(PlanConfig plan, string name)
    {
        var path = Path.Combine(_repo, name);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(plan, PlanConfig.JsonOpts),
            CancellationToken.None);
        return path;
    }

    /// <summary>Bug #10, end to end. Session 1 delivers and claims H0.1 with NO evidence flag;
    /// session 2 is the VERIFY, and while it holds the run it claims H0.2 with an evidence path —
    /// the owner-runs-<c>task --done</c>-from-another-shell case, reproduced from inside the session
    /// that was swallowing it. Before this checkpoint the verify branch of ComputeVerdict returned
    /// before the work graph was ever read, so H0.2 appeared in no session's NewlyDone, reached no
    /// confirmation, and got no engine stamp.</summary>
    [Fact]
    public async Task ClaimDuringAVerifySession_IsCountedAgainstThatSession_StampedAndConfirmed()
    {
        await WriteTrackerAsync("| H0.1 | first checkpoint | TODO | | |",
                                "| H0.2 | claimed mid-verify | TODO | | |");
        await File.WriteAllTextAsync(Path.Combine(_repo, "README.md"), "# SF0.2 live repo", CancellationToken.None);
        GitRun("add", "-A");
        GitRun("commit", "-m", "chore: initial commit", "--no-gpg-sign");

        var agentScript = Path.Combine(_repo, "sf02-agent.cmd");
        var plan = BuildPlan(agentScript, "perSession",
            new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 });
        var planPath = await WritePlanFileAsync(plan, "sf02.plan.json");
        var cli = "\"" + ConductorExe() + "\" task --plan \"" + planPath + "\"";

        await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
        [
            "@echo off",
            "if exist verify.marker goto verify",
            "echo verified> verify.marker",
            // ---- session 1: DELIVER. Claims H0.1 with no --evidence, so the CLI's own default
            // wording is what the row carries — and the engine must not overwrite it with the
            // battery token, which is exactly what `GateSummary ?? completed` used to do.
            "echo {\"type\":\"text\",\"part\":{\"text\":\"SESSION-RESULT: delivered H0.1.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
            "echo deliverable> deliverable.md",
            "git add deliverable.md",
            "git commit -m \"feat: deliver H0.1\" --no-gpg-sign -- deliverable.md",
            cli + " --done H0.1",
            ">deliver-exit.txt echo exit=%ERRORLEVEL%",
            "exit /b 0",
            ":verify",
            // ---- session 2: VERIFY. It claims H0.2 mid-session, with an evidence path, and returns
            // a passing verdict. Both halves of bug #10 ride on this one session.
            cli + " --done H0.2 -e .conductor/evidence/H0/mid-verify-proof.md",
            ">verify-exit.txt echo exit=%ERRORLEVEL%",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"{\\\"score\\\": 95, \\\"verdict\\\": \\\"PASS\\\", \\\"findings\\\": []}\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
            "exit /b 0",
            "",
        ]), CancellationToken.None);

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: false, MaxSessions: 2), consoleSink: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
        Assert.Equal(0, code);

        // The CLI accepted both claims in its OWN process — without that nothing below means anything.
        foreach (var f in new[] { "deliver-exit.txt", "verify-exit.txt" })
        {
            var p = Path.Combine(_repo, f);
            Assert.True(File.Exists(p), $"the stand-in agent never reached the CLI call for {f}");
            Assert.Equal("exit=0", (await File.ReadAllTextAsync(p, CancellationToken.None)).Trim());
        }

        Assert.Equal(2, state.History.Count);
        var deliver = state.History[0];
        var verify = state.History[1];
        Assert.Equal(SessionKind.Deliver, deliver.Kind);
        Assert.Equal(SessionKind.Verify, verify.Kind);

        // THE BUG. H0.2 was claimed while the verify session held the run; it belongs to that session.
        Assert.Contains("H0.1", deliver.NewlyDone);
        Assert.DoesNotContain("H0.2", deliver.NewlyDone);
        Assert.Contains("H0.2", verify.NewlyDone);

        using var store = new SqliteRunStore(Path.Combine(plan.StateDir, "run.db"),
            NullLogger<SqliteRunStore>.Instance);

        // …and the session row agrees, which is what history, the report and the timeline read.
        var verifyRow = Assert.Single(store.QuerySessions(state.RunId), s => s.Number == verify.Number);
        Assert.Equal("Verify", verifyRow.Kind);

        var cps = store.GetCheckpoints(state.RunId).ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith("DONE", cps["H0.2"].Status, StringComparison.OrdinalIgnoreCase);

        // The engine-side stamp RAN for the mid-verify claim — the write bug #10 says never happened,
        // identified by its source rather than by its payload. (Its payload for the commit column is
        // legitimately "-": this verify session committed nothing, so there is no sha to attribute,
        // and "-" reaches the fold as "leave unchanged" rather than as a value.)
        var stamps = store.ReadAllEvents(state.RunId).OfType<TaskStatusChanged>()
            .Where(e => string.Equals(e.TaskId, "H0.2", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(stamps, e => e.Source == "engine" && e.Status == "done");

        // THE RIDER. The agent's evidence survives the engine's stamp on BOTH rows — the path it
        // passed on the mid-verify claim, and the CLI's own default on the claim that passed none.
        // The old `rec.GateSummary ?? "completed"` wrote the battery token over each of them.
        Assert.Equal(".conductor/evidence/H0/mid-verify-proof.md", cps["H0.2"].Evidence);
        Assert.Equal("marked done via CLI", cps["H0.1"].Evidence);

        // Confirmed like any other claim — the verifier passed on the tree that contains it.
        var confirmed = store.ReadAllEvents(state.RunId).OfType<CheckpointConfirmed>()
            .Select(e => e.CheckpointId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("H0.1", confirmed);
        Assert.Contains("H0.2", confirmed);
    }

    /// <summary>Bug #3 — the only outright hang on the core run's list. A perPhase plan whose LAST
    /// (here: only) stage is CONFIRMED while a verify is still queued used to spin the run loop at
    /// full speed forever: completion declined because the verify was owed, and the very next branch
    /// re-scheduled a phase gate for the already-confirmed stage and `continue`d, so the verify it
    /// was waiting on could never be dispatched. The assertion is simply that the run RETURNS.</summary>
    [Fact]
    public async Task ConfirmedLastStage_WithAQueuedVerify_RunsItAndCompletes_InsteadOfSpinning()
    {
        await WriteTrackerAsync("| H0.1 | the only checkpoint | TODO | | |");
        await File.WriteAllTextAsync(Path.Combine(_repo, "README.md"), "# SF0.2 hang repo", CancellationToken.None);
        GitRun("add", "-A");
        GitRun("commit", "-m", "chore: initial commit", "--no-gpg-sign");

        var agentScript = Path.Combine(_repo, "sf02-hang-agent.cmd");
        var plan = BuildPlan(agentScript, "perPhase",
            new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 });
        var planPath = await WritePlanFileAsync(plan, "sf02-hang.plan.json");
        var cli = "\"" + ConductorExe() + "\" task --plan \"" + planPath + "\"";

        await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
        [
            "@echo off",
            "if exist verify.marker goto verify",
            "echo verified> verify.marker",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"SESSION-RESULT: delivered H0.1.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
            "echo deliverable> deliverable.md",
            "git add deliverable.md",
            "git commit -m \"feat: deliver the only checkpoint\" --no-gpg-sign -- deliverable.md",
            cli + " --done H0.1 -e .conductor/evidence/H0/proof.md",
            "exit /b 0",
            ":verify",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"{\\\"score\\\": 95, \\\"verdict\\\": \\\"PASS\\\", \\\"findings\\\": []}\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
            "exit /b 0",
            "",
        ]), CancellationToken.None);

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: false, MaxSessions: 0), consoleSink: false);

        // A bounded wait, because the regression this pins is an infinite loop: on the old code the
        // orchestrator never returns and the delay wins.
        using var cts = new CancellationTokenSource();
        var run = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(4), CancellationToken.None));
        if (finished != run)
        {
            await cts.CancelAsync();
            Assert.Fail("the run never returned — the confirmed last stage is still spinning on its queued verify (bug #3)");
        }
        Assert.Equal(0, await run);

        Assert.Contains("H0", state.ConfirmedStages);
        Assert.Null(state.PendingVerify);

        // It did not merely stop: the queued verify actually got its turn, which is the whole point
        // of excluding PendingVerify from the completion guard in the first place (W5.1).
        Assert.Contains(state.History, h => h.Kind == SessionKind.Verify);
    }

    /// <summary>Bug #4 — the announcement and the dispatcher agree. A perPhase stage whose delivery
    /// queued a verify, then failed the full battery: the RED line used to read "queuing fix session"
    /// and the very next line "session #N start — Verify". The attempt number agreed; the kind did
    /// not. This asserts the two against each other rather than against a hardcoded word.</summary>
    [Fact]
    public async Task APhaseGateRed_NamesTheSessionKindTheDispatcherActuallySelects()
    {
        await WriteTrackerAsync("| H0.1 | the only checkpoint | TODO | | |");
        await File.WriteAllTextAsync(Path.Combine(_repo, "README.md"), "# SF0.2 red repo", CancellationToken.None);
        GitRun("add", "-A");
        GitRun("commit", "-m", "chore: initial commit", "--no-gpg-sign");

        var agentScript = Path.Combine(_repo, "sf02-red-agent.cmd");
        // A fast gate that passes (so the DELIVERY session goes green and the workflow queues the
        // verify) and a full-tier gate that fails (so the phase battery, which runs both, is RED).
        var plan = BuildPlan(agentScript, "perPhase",
            new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 },
            new GateConfig { Name = "deep", Command = "exit 1", Tier = "full", TimeoutMinutes = 1 });
        var planPath = await WritePlanFileAsync(plan, "sf02-red.plan.json");
        var cli = "\"" + ConductorExe() + "\" task --plan \"" + planPath + "\"";

        await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
        [
            "@echo off",
            "if exist claimed.marker goto later",
            "echo claimed> claimed.marker",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"SESSION-RESULT: delivered H0.1.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
            "echo deliverable> deliverable.md",
            "git add deliverable.md",
            "git commit -m \"feat: deliver H0.1\" --no-gpg-sign -- deliverable.md",
            cli + " --done H0.1 -e .conductor/evidence/H0/proof.md",
            "exit /b 0",
            ":later",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"SESSION-RESULT: nothing further.\"}}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0001,\"tokens\":{\"input\":10,\"output\":5}}}",
            "exit /b 0",
            "",
        ]), CancellationToken.None);

        var sink = new RecordingSink();
        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, sink,
            new RunOptions(DryRun: false, Once: false, MaxSessions: 2), consoleSink: false);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);

        var red = Assert.Single(sink.Lines, l =>
            l.Contains("full battery", StringComparison.Ordinal) &&
            l.Contains("queuing", StringComparison.Ordinal));

        Assert.Equal(2, state.History.Count);
        var announced = state.History[1].Kind.ToString().ToLowerInvariant();
        Assert.Contains($"queuing {announced} session", red, StringComparison.Ordinal);

        // And be explicit about which case this run exercised, so a future change that stops queuing
        // the verify cannot quietly turn this into a test of nothing.
        Assert.Equal(SessionKind.Verify, state.History[1].Kind);
        Assert.DoesNotContain("queuing fix session", red, StringComparison.Ordinal);
    }
}
