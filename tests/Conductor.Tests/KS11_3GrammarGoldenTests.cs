using Conductor.Core;
using Conductor.Core.Evidence;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS11.3 — CHAPAR CH-4 and CH-5, pinned for BOTH profiles.
///
/// <para>Two claims live here. CH-4: a chat is told what this run is, what will arrive and exactly
/// what it may ask, before its first push — in its own profile's voice. CH-5: every push reads as
/// headline / proof / telemetry, with the figures in monospace, and a session-end push reads
/// standalone: what landed, what proves it, what it cost, with no other message for context.</para>
///
/// <para>The goldens live beside KS11.1's rather than inside them because they pin something
/// different. KS11.1's pin the wire — the real service, the real Bot API calls, byte-identical
/// through the seam. These pin the TEXT, per profile, driven through the fake channel, so a
/// composition change shows up as a readable diff of what a person would actually see.</para>
/// </summary>
public sealed class KS11_3GrammarGoldenTests : IDisposable
{
    private const string AdminChat = "99205495";
    private const string ObserverChat = "-100123456";

    private readonly string _repo;
    private readonly FakeChannel _channel = new();

    public KS11_3GrammarGoldenTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-ks11g-{Guid.NewGuid():N}", "grammar-rig");
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# Grammar rig\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + "| KS11.1 | the seam | DONE | abc1234 | seam.md |\n"
            + "| KS11.2 | profiles | DONE | def5678 | profiles.md |\n"
            + "| KS11.3 | the grammar | IN PROGRESS | | |\n"
            + "| KS11.4 | evidence on demand | TODO | | |\n");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(Directory.GetParent(_repo)!.FullName); } catch (Exception) { }
    }

    // ────────────────────────── CH-4: onboarding, per profile ──────────────────────────

    /// <summary>The bot's first message, in each profile's voice. The two differ in exactly the
    /// place they should: what this chat IS, and what it may ask.</summary>
    [Theory]
    [InlineData(ChatProfile.Admin, "onboarding-admin")]
    [InlineData(ChatProfile.Observer, "onboarding-observer")]
    public async Task Onboarding_reads_in_the_profiles_own_voice(ChatProfile profile, string golden)
    {
        var body = await Composer().OnboardingAsync(profile, twoWay: true);

        AssertGolden(golden, body);
    }

    /// <summary>CH-4's three questions, checked as facts rather than as a shape: what this run is,
    /// what will arrive, and what this chat may ask. A golden alone would go on passing if the
    /// message quietly stopped answering one of them.</summary>
    [Theory]
    [InlineData(ChatProfile.Admin)]
    [InlineData(ChatProfile.Observer)]
    public async Task Onboarding_answers_all_three_of_CH4s_questions(ChatProfile profile)
    {
        var body = await Composer().OnboardingAsync(profile, twoWay: true);

        Assert.Contains("Karvansara edge", body, StringComparison.Ordinal);          // what run
        Assert.Contains("KS11 → KS12", body, StringComparison.Ordinal);              // the stage map
        Assert.Contains("budget $42.00 of $50.00", body, StringComparison.Ordinal);  // the ceiling
        Assert.Contains("What arrives here", body, StringComparison.Ordinal);        // and when
        Assert.Contains("What you can ask", body, StringComparison.Ordinal);         // the surface
    }

    /// <summary>The promise and the gate that enforces it are the SAME list. An onboarding message
    /// that offers an observer a verb the router refuses is worse than no onboarding at all, and
    /// hand-writing the list a second time is exactly how that happens.</summary>
    [Fact]
    public async Task What_an_observer_is_offered_is_what_the_gate_actually_allows()
    {
        var body = await Composer().OnboardingAsync(ChatProfile.Observer, twoWay: true);
        var router = Router();

        var offered = SurfaceCommands.BrowseList.Split(", ", StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(offered);

        foreach (var verb in offered)
        {
            Assert.Contains(verb, body, StringComparison.Ordinal);
            var outcome = router.Route(verb, ChatProfile.Observer, twoWay: true, injectionArmed: false);
            Assert.Equal(SurfaceAction.Reply, outcome.Action);
        }

        // And nothing that moves the run is offered to a reader.
        foreach (var cmd in SurfaceCommands.All.Where(c => c.Scope != SurfaceScope.Browse))
            Assert.DoesNotContain(cmd.Verb, body, StringComparison.Ordinal);
    }

    // ────────────────────────── CH-5: the push grammar ──────────────────────────

    /// <summary>Every push type, pinned. Composition does not vary by profile — CH-3 gives an
    /// observer the run's whole story and closes only what they may ASK — so one golden per push
    /// type is the truthful pin, and <see cref="Both_profiles_receive_the_same_story"/> is what
    /// makes that a measurement instead of an assumption.</summary>
    [Theory]
    [InlineData("push-session-end")]
    [InlineData("push-session-end-rollover")]
    [InlineData("push-run-complete")]
    [InlineData("push-evidence")]
    public async Task Every_push_type_reads_as_headline_proof_telemetry(string name)
    {
        AssertGolden(name, await ComposePushAsync(name));
    }

    /// <summary>The claim CH-3 makes and CH-5 must not quietly break: the observer's copy of the
    /// run's story is the owner's copy, byte for byte. A "filtered for the stakeholder" version is
    /// a second thing to maintain and a second thing to be wrong.</summary>
    [Fact]
    public async Task Both_profiles_receive_the_same_story()
    {
        _channel.SetTargets(new ChatTarget(AdminChat, ChatProfile.Admin),
                            new ChatTarget(ObserverChat, ChatProfile.Observer));

        await Surface().PushSessionEndAsync(SessionEnd(), CancellationToken.None);

        Assert.Equal(2, _channel.Queued.Count);
        Assert.Equal(AdminChat, _channel.Queued[0].ChatId);
        Assert.Equal(ObserverChat, _channel.Queued[1].ChatId);
        Assert.Equal(_channel.Queued[0].Text, _channel.Queued[1].Text, StringComparer.Ordinal);
    }

    /// <summary>KS11.3's headline exit, as a fact rather than a golden: ONE message, read with no
    /// other message for context, answers what landed, what proves it, and what it cost.</summary>
    [Fact]
    public async Task A_checkpoint_push_reads_standalone()
    {
        var body = await Composer().SessionEndAsync(SessionEnd());
        var lines = body.Split('\n');

        // what landed — the outcome in bold, and the checkpoint it claimed
        Assert.StartsWith("<b>Advanced</b>", lines[0], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.StartsWith("landed:", StringComparison.Ordinal)
                                    && l.Contains("claimed KS11.3", StringComparison.Ordinal));

        // what proves it — the gate verdict AND the artifact, on one line
        var proof = Assert.Single(lines, l => l.StartsWith("proof:", StringComparison.Ordinal));
        Assert.Contains("gates build:OK gates:9/9", proof, StringComparison.Ordinal);
        Assert.Contains("evidence .conductor/evidence/KS11/KS11.3-grammar.md", proof, StringComparison.Ordinal);

        // what it cost — money AND tokens, in monospace, on one line
        var telemetry = Assert.Single(lines, l => l.StartsWith("<code>", StringComparison.Ordinal));
        Assert.Contains("progress: 2/4 checkpoints", telemetry, StringComparison.Ordinal);
        Assert.Contains("cost: $1.25 · run $42.00 of $50.00 (84%, $8.00 left)", telemetry, StringComparison.Ordinal);
        Assert.Contains("tokens 3.5M", telemetry, StringComparison.Ordinal);
        Assert.EndsWith("</code>", telemetry, StringComparison.Ordinal);
    }

    /// <summary>Tokens are the figure the run's own era is measured in, and the three totals on
    /// <c>RunState</c> that predate this one exclude cache reads — about 98% of what is actually
    /// spent. A telemetry line built from those would report a fiftieth of the truth.</summary>
    [Fact]
    public void The_token_figure_counts_cache_reads()
    {
        var state = State();

        Assert.Equal(3_500_000, state.TotalTokens);
        Assert.Equal(60_000, state.TotalTokensInput + state.TotalTokensOutput + state.TotalTokensReasoning);
    }

    // ────────────────────────── the command replies, per profile ──────────────────────────

    /// <summary>What each profile is TOLD when it asks for something it may not have. Pinned
    /// because a refusal is a piece of writing: it has to name the verb, say what this chat is, and
    /// leave the reader knowing what they CAN do.</summary>
    [Theory]
    [InlineData("/pause", "cmd-pause-observer")]
    [InlineData("/inject re-run the gate", "cmd-inject-observer")]
    [InlineData("/chat", "cmd-chat-observer")]
    public void An_observers_refusal_is_pinned(string text, string golden)
    {
        var outcome = Router().Route(text, ChatProfile.Observer, twoWay: true, injectionArmed: false);

        Assert.Equal(SurfaceAction.Refuse, outcome.Action);
        AssertGolden(golden, outcome.Text!);
    }

    // ────────────────────────── the rig ──────────────────────────

    private async Task<string> ComposePushAsync(string name)
    {
        var composer = Composer();
        return name switch
        {
            "push-session-end" => await composer.SessionEndAsync(SessionEnd()),
            "push-session-end-rollover" => await composer.SessionEndAsync(SessionEnd() with
            {
                Outcome = "RolledOver", GateSummary = null, IsRollover = true, Score = null, CostUsd = 0.4242m,
            }),
            "push-run-complete" => await composer.RunCompleteAsync(new RunCompletePush(
                CheckpointsDone: 22, CheckpointsTotal: 24, Sessions: 9,
                Duration: TimeSpan.FromHours(9) + TimeSpan.FromMinutes(30), SkippedStages: [])),
            "push-evidence" => await composer.EvidenceCaptionAsync(
                new EvidenceArtifact(".conductor/evidence/KS11/KS11.3-grammar.md", "text",
                    CheckpointId: "KS11.3", StageId: "KS11", SessionNumber: 7, Sha256: "0f1e2d3c",
                    Bytes: 2048, CreatedUtc: DateTimeOffset.UnixEpoch, Source: "claim"), 2),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such push in the rig"),
        };
    }

    private static SessionEndPush SessionEnd() => new(
        Number: 7,
        Outcome: "Advanced",
        Stage: "KS11",
        GateSummary: "build:OK gates:9/9",
        ResultSummary:
            "SESSION-RESULT: the push grammar and per-profile onboarding\n"
            + "- every push type recomposed to headline / proof / telemetry\n"
            + "- onboarding composed from the same list the gate enforces\n"
            + "artefacts: MessageComposer.cs, NotifyDefaults.cs\n"
            + "evidence: .conductor/evidence/KS11/KS11.3-grammar.md\n"
            + "gaps: none",
        CostUsd: 1.25m,
        Score: 88m,
        Duration: TimeSpan.FromHours(1) + TimeSpan.FromMinutes(23),
        Commits: 2,
        CommitShas: ["a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"],
        NewlyDone: ["KS11.3"],
        IsRollover: false);

    private PlanConfig Plan() => new()
    {
        Name = "Karvansara edge",
        Repo = _repo,
        Tracker = "TRACKER.md",
        Limits = { MaxRunCostUsd = 50m },
        Stages =
        {
            new StageConfig { Id = "KS11", Title = "Chapar — the remote surface", Sessions = 6 },
            new StageConfig { Id = "KS12", Title = "The record", Sessions = 3 },
        },
    };

    /// <summary>A run that has spent real money and real tokens — 3.5M of them, cache reads
    /// included, which is the number this era is actually measured in.</summary>
    private static RunState State() => new()
    {
        RunId = "ks11-3-grammar",
        SessionCounter = 7,
        CurrentStage = "KS11",
        History =
        {
            new SessionRecord
            {
                Number = 1, CostUsd = 42m,
                TokensInput = 20_000, TokensOutput = 30_000, TokensReasoning = 10_000,
                TokensCacheRead = 3_440_000,
            },
        },
    };

    private MessageComposer Composer()
    {
        var plan = Plan();
        return new MessageComposer(plan, State(), ProgressProviderFactory.Create(plan), null, _ => { });
    }

    private CommandRouter Router() => new(Composer(), Plan());

    private RemoteSurface Surface()
    {
        var plan = Plan();
        var state = State();
        var composer = new MessageComposer(plan, state, ProgressProviderFactory.Create(plan), null, _ => { });
        return new RemoteSurface(_channel, composer, new CommandRouter(composer, plan), state, null,
            (_, _, _) => Task.CompletedTask, (_, _) => { });
    }

    // ────────────────────────── the goldens ──────────────────────────

    /// <summary>Strict, like KS11.1's: a missing golden FAILS rather than being written, because a
    /// golden that writes itself on first run pins whatever the code happened to do that day.
    /// <c>CONDUCTOR_GOLDEN_REBASELINE=1</c> is the deliberate way to move one.</summary>
    private static void AssertGolden(string name, string actual)
    {
        var path = Path.Combine(GoldenDir(), name + ".txt");
        var normalised = actual.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

        if (string.Equals(Environment.GetEnvironmentVariable("CONDUCTOR_GOLDEN_REBASELINE"), "1",
                StringComparison.Ordinal))
        {
            Directory.CreateDirectory(GoldenDir());
            File.WriteAllText(path, normalised);
            return;
        }

        Assert.True(File.Exists(path),
            $"golden {name}.txt is missing — regenerate with CONDUCTOR_GOLDEN_REBASELINE=1 and READ the diff");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), normalised,
            StringComparer.Ordinal);
    }

    private static string GoldenDir() =>
        Path.Combine(RepoRoot(), "tests", "Conductor.Tests", "testdata", "ks11-3");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>CH-1's fake, with two chats of different profiles.</summary>
    private sealed class FakeChannel : IMessageChannel
    {
        public string Name => "fake";
        public bool IsLive => true;
        public bool AllowsControl => true;
        public IReadOnlyList<ChatTarget> Targets { get; private set; } = [new ChatTarget(AdminChat, ChatProfile.Admin)];
        public List<OutboundMessage> Queued { get; } = [];

        public void SetTargets(params ChatTarget[] targets) => Targets = targets;

        public Task EnqueueAsync(OutboundMessage message, CancellationToken ct)
        {
            Queued.Add(message);
            return Task.CompletedTask;
        }

        public Task SendAsync(OutboundMessage message, CancellationToken ct)
        {
            Queued.Add(message);
            return Task.CompletedTask;
        }
    }
}
