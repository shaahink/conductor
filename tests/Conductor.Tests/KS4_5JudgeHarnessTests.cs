using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Evidence;
using Conductor.Core.Store;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// KS4.5's headline exit, driven live: a hostile judge and a flattering one, over the real
/// orchestrator, and neither of them moves a verdict by one bit.
/// </summary>
/// <remarks>
/// <para>The pair is what makes it falsifiable. A judge that screams FAIL at a session the gates
/// passed leaves that session green; a judge that gushes PASS at a session the gates failed leaves it
/// red. Both reviews are RECORDED — as a registered evidence artifact with its own source, as a line
/// in the log, and with the agreement stated — so this is not the absence of a feature, it is the
/// presence of a feature that deliberately decides nothing.</para>
/// <para>The control leg runs the identical rig with no judge configured at all, so "the outcome did
/// not change" is measured against a real baseline rather than against the expectation.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class KS4_5JudgeHarnessTests : IDisposable
{
    /// <summary>One temp root; every LEG gets its own repo underneath it. Two legs of the same test
    /// grade the same work twice, and a shared repo would not let them: the first leg's commit and its
    /// DONE tracker row are exactly the inputs the second leg is supposed to be meeting fresh.</summary>
    private readonly string _root;

    public KS4_5JudgeHarnessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ks45-harness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => TestTemp.DeleteTree(_root);

    /// <summary>A green session and a judge that condemns it. The session stays green — byte for byte
    /// the same outcome the same rig produces with no judge at all — and the condemnation is recorded.</summary>
    [Fact]
    public async Task AJudgeThatCondemnsAGreenSessionCannotTurnItRed()
    {
        var withoutJudge = await RunOnceAsync(gatesPass: true, judge: null);
        var withJudge = await RunOnceAsync(gatesPass: true, judge: ("fail", 0, "this must not ship"));

        Assert.NotEqual(SessionOutcome.GatesRed, withoutJudge.Record.Outcome);
        Assert.Equal(withoutJudge.Record.Outcome, withJudge.Record.Outcome);
        Assert.Equal(withoutJudge.Record.NewlyDone, withJudge.Record.NewlyDone);

        // …and the review really did happen and really did say no.
        var review = ReadReview(withJudge.Record);
        Assert.Equal("fail", review.GetProperty("verdict").GetString());
        Assert.Equal(0, review.GetProperty("score").GetInt32());
        Assert.Equal(nameof(JudgeAgreement.Disagrees), review.GetProperty("agreement").GetString());
        Assert.True(review.GetProperty("deterministic").GetProperty("gatesGreen").GetBoolean());
    }

    /// <summary>The other direction, which is the one that would actually cost something: a red session
    /// and a judge that blesses it. The rig's gate fails; the session is red with or without the judge's
    /// 100/100, and the disagreement is on the record instead of in the verdict.</summary>
    [Fact]
    public async Task AJudgeThatBlessesARedSessionCannotTurnItGreen()
    {
        var withoutJudge = await RunOnceAsync(gatesPass: false, judge: null);
        var withJudge = await RunOnceAsync(gatesPass: false, judge: ("pass", 100, "ship it, this is flawless"));

        Assert.Equal(SessionOutcome.GatesRed, withoutJudge.Record.Outcome);
        Assert.Equal(SessionOutcome.GatesRed, withJudge.Record.Outcome);

        var review = ReadReview(withJudge.Record);
        Assert.Equal("pass", review.GetProperty("verdict").GetString());
        Assert.Equal(nameof(JudgeAgreement.Disagrees), review.GetProperty("agreement").GetString());
        Assert.False(review.GetProperty("deterministic").GetProperty("gatesGreen").GetBoolean());
    }

    /// <summary>The taxonomy join. The review is not a file the engine happens to have written: it is a
    /// registered artifact carrying the judge source, so every surface that reads evidence — the
    /// browser, the digest, the messenger — meets it as evidence and can tell it apart from a
    /// measurement without opening it.</summary>
    [Fact]
    public async Task TheReviewJoinsTheEvidenceTaxonomyWithItsOwnSource()
    {
        var run = await RunOnceAsync(gatesPass: true, judge: ("concerns", 55, "the new test asserts nothing"));

        var artifact = Assert.Single(run.Evidence.Artifacts,
            a => string.Equals(a.Source, EvidenceArtifact.JudgeSource, StringComparison.Ordinal));
        Assert.Equal(run.Record.Number, artifact.SessionNumber);
        Assert.Null(artifact.CheckpointId);      // an opinion is not a claim
        Assert.True(artifact.Bytes > 0);

        // The attempt diff is registered beside it (KS4.4) and the two are NOT the same kind of thing.
        Assert.Contains(run.Evidence.Artifacts, a =>
            string.Equals(a.Source, EvidenceArtifact.AttemptSource, StringComparison.Ordinal));

        // An inconclusive review is still recorded — "no clear opinion" is evidence too.
        var review = ReadReview(run.Record);
        Assert.Equal(nameof(JudgeAgreement.Inconclusive), review.GetProperty("agreement").GetString());
        Assert.Equal("the new test asserts nothing", Assert.Single(review.GetProperty("findings").EnumerateArray()).GetString());
    }

    /// <summary>A judge that answers nothing readable is a judge that said nothing. The session is
    /// graded exactly as it would have been, no artifact is written, and the log says why — the failure
    /// mode being guarded against is a broken judge silently reading as an approval.</summary>
    [Fact]
    public async Task AnUnreadableJudgeCostsTheSessionNothing()
    {
        var run = await RunOnceAsync(gatesPass: true, judge: ("garbage", null, null));

        Assert.NotEqual(SessionOutcome.GatesRed, run.Record.Outcome);
        Assert.Null(run.Record.JudgeReviewPath);
        Assert.DoesNotContain(run.Evidence.Artifacts, a =>
            string.Equals(a.Source, EvidenceArtifact.JudgeSource, StringComparison.Ordinal));
    }

    // ── the rig ──

    private sealed record Run(SessionRecord Record, EvidenceRegistry Evidence);

    private async Task<Run> RunOnceAsync(bool gatesPass, (string Verdict, int? Score, string? Summary)? judge)
    {
        var repo = NewRepo();
        var agent = Path.Combine(repo, "fake-agent.cmd");
        await File.WriteAllTextAsync(agent, AgentScript());

        var plan = new PlanConfig
        {
            Name = "JudgeRig",
            Repo = repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig { Command = "cmd.exe", Args = { "/c", agent, "{prompt}" }, Provider = "opencode" },
            GatePolicy = "perSession",
            Gates =
            {
                new GateConfig
                {
                    Name = "deliverable",
                    Command = gatesPass ? "exit 0" : "exit 1",
                    TimeoutMinutes = 1,
                },
            },
        };
        plan.Report.Commit = false;

        if (judge is { } j)
        {
            var script = Path.Combine(repo, "fake-judge.cmd");
            await File.WriteAllTextAsync(script, JudgeScript(j.Verdict, j.Score, j.Summary));
            plan.Judge = new JudgeConfig
            {
                Enabled = true,
                Command = "cmd.exe",
                Args = ["/c", script, "{prompt}"],
                TimeoutMinutes = 2,
            };
        }

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);

        var code = await host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);
        Assert.Equal(0, code);

        var rec = Assert.Single(state.History);
        var store = host.Services.GetRequiredService<IRunStore>();
        return new Run(rec, await WaitForEvidenceAsync(store, state.RunId, rec).ConfigureAwait(false));
    }

    /// <summary>Evidence registration is written on the session boundary and read back through the
    /// event log, so it lands a beat after the orchestrator returns. Waiting for the ATTEMPT artifact
    /// is what makes the negative assertion honest: it proves the registration leg ran at all, so
    /// "no judge artifact" means the review was not registered rather than that nothing was.</summary>
    private static async Task<EvidenceRegistry> WaitForEvidenceAsync(IRunStore store, string runId, SessionRecord rec)
    {
        var expected = string.IsNullOrEmpty(rec.JudgeReviewPath) ? 1 : 2;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        EvidenceRegistry registry;
        do
        {
            registry = EvidenceRegistry.From(store.ReadAllEvents(runId));
            if (registry.Count >= expected) return registry;
            await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
        } while (DateTime.UtcNow < deadline);
        return registry;
    }

    private static JsonElement ReadReview(SessionRecord rec)
    {
        Assert.False(string.IsNullOrEmpty(rec.JudgeReviewPath), "KS4.5: no judge review was written");
        Assert.True(File.Exists(rec.JudgeReviewPath), $"KS4.5: {rec.JudgeReviewPath} is gone");
        return JsonDocument.Parse(File.ReadAllText(rec.JudgeReviewPath!)).RootElement.Clone();
    }

    /// <summary>An ordinary, honest delivery: it writes the deliverable, marks its checkpoint DONE and
    /// commits. Whether the session is green or red is decided by the gate the rig configures, not by
    /// the agent — so both legs grade the same work.</summary>
    private static string AgentScript() => string.Join("\r\n",
        "@echo off",
        "echo {\"type\":\"text\",\"part\":{\"text\":\"Delivering H0.1.\"}}",
        "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.00042,\"tokens\":{\"input\":350,\"output\":120,\"reasoning\":80,\"cache\":{\"read\":0}}}}",
        "echo shipped> deliverable.txt",
        "copy /y tracker-done.md TRACKER.md > nul",
        "git add -A",
        "git commit -m \"feat: deliver H0.1\" --no-gpg-sign",
        "exit /b 0",
        "");

    /// <summary>The seeded judge. A score of 0 or 100 is not a subtlety — the point is that the most
    /// extreme opinion available moves nothing.</summary>
    private static string JudgeScript(string verdict, int? score, string? summary) => string.Join("\r\n",
        "@echo off",
        score is { } s
            ? $"echo {{\"verdict\":\"{verdict}\",\"score\":{s},\"findings\":[\"{summary}\"],\"summary\":\"{summary}\"}}"
            : "echo I have thoughts but I will not be putting them in JSON.",
        "exit /b 0",
        "");

    private static string Tracker(string status) =>
        "# Harness Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
        "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
        $"| H0.1 | harness checkpoint | {status} | | |\n";

    /// <summary>A fresh repo for one leg: the same starting tree every time, so the only difference
    /// between two legs is the one the test is varying.</summary>
    private string NewRepo()
    {
        var repo = Path.Combine(_root, $"leg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "-b", "main");
        Git(repo, "config", "user.email", "harness@test");
        Git(repo, "config", "user.name", "Harness Test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "# KS4.5 judge rig");
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".conductor/\n");
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"), Tracker("TODO"));
        File.WriteAllText(Path.Combine(repo, "tracker-done.md"), Tracker("DONE"));
        Git(repo, "add", "-A");
        Git(repo, "commit", "-m", "chore: initial commit", "--no-gpg-sign");
        return repo;
    }

    private static void Git(string repo, params string[] args)
    {
        var r = ProcessRunner.Run("git", args, repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed ({r.ExitCode}): {r.Output} {r.StdErr}");
    }
}
