using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.Store;
using Conductor.Hosting;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K5.3 — evidence registration driven through a real run, not asserted from source reading.
///
/// <para>The unit tests prove the model, the registry and the watcher. They cannot prove the leg the
/// checkpoint is actually about: an agent producing a screenshot, the run loop noticing at the
/// session boundary, and an event landing in the log that a surface can read. The owner's case is
/// exactly that — conductor builds a website, the agent screenshots it, and a SECOND agent had to be
/// hired to notice the images and forward them.</para>
///
/// <para>So this session does both things an agent really does: it drops a PNG into the evidence
/// directory without mentioning it anywhere, and it claims a checkpoint whose <c>--evidence</c>
/// string names a real file. Both must arrive as artifacts; the free-text claim must survive
/// untouched; and the file that came in BOTH ways must be one artifact, not two.</para>
/// </summary>
public sealed partial class HarnessTests
{
    /// <summary>PNG magic — the kind has to be decided by what a surface would have to send, and a
    /// text file named .png would prove nothing about the case this checkpoint exists for.</summary>
    private static readonly byte[] EvidencePng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02,
    ];

    private const string ClaimEvidenceText = "docs/evidence/H0/H0.1-claim-note.md";

    /// <summary>Copies a screenshot into the evidence directory DURING the session and never mentions
    /// it in its output — the whole point. The sleep is what lets the claim land while the session is
    /// still open, the same shape <c>W1ClaimPathTests</c> uses.</summary>
    private static string EvidenceAgentScript(string source, string destination) => string.Join("\r\n",
        "@echo off",
        "echo {\"type\":\"step_start\"}",
        "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0004,\"tokens\":{\"input\":100,\"output\":50,\"cache\":{\"read\":0}}}}",
        $"copy /y \"{source}\" \"{destination}\" >nul",
        "echo {\"type\":\"text\",\"part\":{\"text\":\"SESSION-RESULT: delivered H0.1\"}}",
        "echo harness done> harness-output.txt",
        "git add harness-output.txt",
        "git commit -m \"feat: deliver harness checkpoint\"",
        "ping -n 5 127.0.0.1 >nul",
        "exit /b 0",
        "");

    [Fact]
    [Trait("Category", "Integration")]
    public async Task FullCycle_RegistersTheScreenshotNobodyMentioned_AndTheClaimsOwnFile()
    {
        // The screenshot the agent will drop, staged outside the watched directories so that it
        // genuinely APPEARS during the session rather than being there all along.
        var staged = Path.Combine(_repo, "staged-shot.png");
        await File.WriteAllBytesAsync(staged, EvidencePng);
        var shot = Path.Combine(_stateDir, "evidence", "H0", "H0.1-shot.png");
        Directory.CreateDirectory(Path.GetDirectoryName(shot)!);

        // The file the claim's free-text --evidence string names.
        var claimNote = Path.Combine(_repo, "docs", "evidence", "H0", "H0.1-claim-note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(claimNote)!);
        await File.WriteAllTextAsync(claimNote, "# H0.1\n\nwhat was measured.\n");

        var script = Path.Combine(_repo, "evidence-agent.cmd");
        await File.WriteAllTextAsync(script, EvidenceAgentScript(staged, shot));

        var plan = new PlanConfig
        {
            Name = "EvidencePlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Harness", Sessions = 1 } },
            Agent = new AgentConfig
            {
                Command = "cmd.exe",
                Args = { "/c", script, "{prompt}" },
                Provider = "opencode",
            },
            GatePolicy = "perSession",
            Gates = { new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 } },
        };
        plan.Report.Commit = false;

        var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
        using var host = ConductorHost.Build(plan, state, new PlainSink(),
            new RunOptions(DryRun: false, Once: true, MaxSessions: 0), consoleSink: false);
        var store = host.Services.GetRequiredService<IRunStore>();
        var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(CancellationToken.None);

        // The claim, made the way `conductor task --done H0.1 --evidence <path>` makes it: a second
        // store on the same run.db, from outside the engine process, while the session is open.
        await ClaimWithEvidenceAsync(store, state, ClaimEvidenceText);

        Assert.Equal(0, await runTask.WaitAsync(TimeSpan.FromSeconds(120)));

        var rec = Assert.Single(state.History);
        Assert.Equal(["H0.1"], rec.NewlyDone);

        // Emit persists via an async drain, so a read taken the instant RunAsync returns can race it
        // (the same wait ControlPlaneServerTests makes for the same reason).
        var registered = await WaitForEvidenceAsync(store, state, expected: 3);

        // 1. The screenshot the agent never mentioned reached the run as an IMAGE, carrying the
        //    session that produced it and the stage it sits in. This is the motivating case.
        var png = Assert.Single(registered, e => e.Path.EndsWith("H0.1-shot.png", StringComparison.Ordinal));
        Assert.Equal(EvidenceKinds.Image, png.Kind);
        Assert.Equal("watcher", png.Source);
        Assert.Equal(rec.Number, png.SessionNumber);
        Assert.Equal("H0.1", png.CheckpointId); // recovered from the name, since no claim named it
        Assert.Equal("H0", png.StageId);
        Assert.Equal(EvidencePng.Length, png.Bytes);
        Assert.Equal(64, png.Sha256.Length);

        // 2. The claim's own file arrived through the CLAIM leg, so it knows its checkpoint from the
        //    claim rather than from a guess — and it is ONE artifact, not one per leg, even though
        //    docs/evidence is also watched.
        var note = Assert.Single(registered, e => e.Path.EndsWith("H0.1-claim-note.md", StringComparison.Ordinal));
        Assert.Equal("claim", note.Source);
        Assert.Equal("H0.1", note.CheckpointId);
        Assert.Equal(EvidenceKinds.Text, note.Kind);

        // 3. The free-text field is untouched: the claim still reads back exactly the string the
        //    agent wrote. An artifact registry that breaks every existing claim is not an improvement.
        var row = Assert.Single(store.GetCheckpoints(state.RunId), c => c.Id == "H0.1");
        Assert.Equal(ClaimEvidenceText, row.Evidence);

        // 3b. KS4.4: the third artifact is the one the ENGINE made — this attempt's own diff. It
        //     carries its own source, so a surface can tell it from a file a session claimed, and NO
        //     checkpoint id, because an attempt is not a claim.
        var attempt = Assert.Single(registered, e => e.Path.Contains("/attempts/", StringComparison.Ordinal));
        Assert.Equal(EvidenceArtifact.AttemptSource, attempt.Source);
        Assert.Null(attempt.CheckpointId);
        Assert.Equal(rec.Number, attempt.SessionNumber);
        Assert.EndsWith($"H0-a{rec.Attempt}-s{rec.Number:000}.diff", attempt.Path, StringComparison.Ordinal);

        // 4. And the registry a surface reads is the FOLD of those events — the same three artifacts,
        //    newest first, without going back to the disk. Only two of them belong to the checkpoint:
        //    the attempt diff is the engine's, not the claim's.
        var registry = EvidenceRegistry.From(store.ReadAllEvents(state.RunId));
        Assert.Equal(3, registry.Count);
        Assert.Equal(["H0.1-claim-note.md", "H0.1-shot.png"],
            registry.ForCheckpoint("H0.1").Select(a => a.Path.Split('/')[^1]).Order(StringComparer.Ordinal));
    }

    private static async Task<List<EvidenceRegistered>> WaitForEvidenceAsync(IRunStore store, RunState state, int expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        List<EvidenceRegistered> found;
        do
        {
            found = [.. store.ReadAllEvents(state.RunId).OfType<EvidenceRegistered>()];
            if (found.Count >= expected) return found;
            await Task.Delay(50, CancellationToken.None);
        } while (DateTime.UtcNow < deadline);
        return found;
    }

    /// <summary>Waits for the session to open, then emits the done-status graph event with agent
    /// provenance and a real evidence path — byte for byte what the task verb writes.</summary>
    private async Task ClaimWithEvidenceAsync(IRunStore engineStore, RunState state, string evidence)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (engineStore.ReadAllEvents(state.RunId).OfType<SessionStarted>().Any()) break;
            await Task.Delay(50, CancellationToken.None);
        }
        // TestState.RunDb, not <repo>/.conductor/run.db: K3.1 moved the store to a machine-level home,
        // so a second connection has to ask the catalogue the same question the engine asked.
        using var cli = new SqliteRunStore(TestState.RunDb(_repo), NullLogger<SqliteRunStore>.Instance);
        cli.UpdateCheckpoint(state.RunId, "H0.1", "DONE", "fake1234", evidence, source: "agent");
    }
}
