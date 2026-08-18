using System.Globalization;

using Conductor.Core;
using Conductor.Core.Evidence;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS11.1, the replay half — the bytes the remote surface puts on the wire TODAY, pinned before
/// anything moves.
///
/// <para>CH-1 makes composition, profiles and browsing channel-agnostic and turns
/// <c>TelegramService</c> into the transport adapter behind that seam. Every previous extraction in
/// this repo was argued from a doc comment; the one thing that can settle whether an extraction
/// changed what a reader SEES is the reader's own bytes, so these goldens were generated from the
/// pre-seam engine and are not permitted to move when the seam lands. A golden that has to be
/// regenerated to make the refactor pass IS the refactor's bug report.</para>
///
/// <para>Everything is recorded through <see cref="RecordingBotApi"/> — the same stub K5.4 asserts
/// against — so a case captures the whole call: the method, whether it buzzed, what it threaded
/// onto, an uploaded file's name and size, and the full text after stamping and chunking. Inbound
/// commands are delivered by the same stub, so one exchange is driven and captured by one double.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class KS11_1SeamGoldenTests : IDisposable
{
    private const string ChatId = "838383";
    private const string PlanName = "Karvansara edge";
    private const string Remote = "https://github.com/acme/widgets";

    /// <summary>The repo's LEAF name rides every context line (<c>RunHistory.RepoLabel</c>), so it is
    /// fixed and the randomness lives in the parent directory instead.</summary>
    private const string RepoLeaf = "karvansara-golden";

    private readonly string _parent;
    private readonly string _repo;
    private readonly ITestOutputHelper _out;

    public KS11_1SeamGoldenTests(ITestOutputHelper output)
    {
        _out = output;
        _parent = Path.Combine(Path.GetTempPath(), $"conductor-ks11-{Guid.NewGuid():N}");
        _repo = Path.Combine(_parent, RepoLeaf);
        Directory.CreateDirectory(_repo);

        Git("init", "-b", "feat/karvansara-edge");
        Git("config", "user.email", "ks11@test");
        Git("config", "user.name", "KS11 Test");
        Git("remote", "add", "origin", Remote + ".git");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "# KS11.1");
        Git("add", "README.md");
        Git("commit", "-m", "chore: initial commit", "--no-gpg-sign");

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# Karvansara edge\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| KS11.1 | the messenger seam | DONE | abc1234 | seam.md |\n" +
            "| KS11.2 | profiles admin and observer | IN PROGRESS | | |\n" +
            "| KS11.3 | onboarding and the push grammar | TODO | | |\n" +
            "| KS11.4 | evidence on demand | TODO | | |\n");

        Directory.CreateDirectory(Path.Combine(_repo, ".conductor"));
        SecretsStore.WriteTelegramToken(Path.Combine(_repo, ".conductor"), "ks11-golden-token");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_parent); } catch (Exception) { }
    }

    private void Git(params string[] args)
    {
        var r = ProcessRunner.Run("git", args, _repo, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.True(r.ExitCode == 0, $"git {string.Join(" ", args)} failed: {r.Output} {r.StdErr}");
    }

    // ── the cases ──

    /// <summary>Every shape the surface can emit: a bare push, both session-end variants, both
    /// completion variants, an evidence batch that overflows its file budget, and a keyboard.</summary>
    [Theory]
    [InlineData("push-plain")]
    [InlineData("push-session-end")]
    [InlineData("push-session-end-rollover")]
    [InlineData("push-run-complete")]
    [InlineData("push-run-complete-skipped")]
    [InlineData("push-evidence-batch")]
    [InlineData("push-keyboard")]
    public async Task Outbound_pushes_render_exactly_as_they_did_before_the_seam(string name)
    {
        using var bot = new RecordingBotApi();
        var state = State();

        using var svc = new TelegramService(Plan(bot.Root, twoWay: name == "push-keyboard"), state,
            NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        await DrivePushAsync(name, svc);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        AssertGolden(name, bot.Snapshot());
    }

    /// <summary>Every command the surface answers today, admin-shaped — which is the only shape
    /// there is before KS11.2. Driven through the stub's own long-poll, so the dispatch, the
    /// composition and the reply are all the engine's real ones.</summary>
    [Theory]
    [InlineData("cmd-status")]
    [InlineData("cmd-tasks")]
    [InlineData("cmd-start")]
    [InlineData("cmd-daily")]
    [InlineData("cmd-chat")]
    [InlineData("cmd-inject")]
    [InlineData("cmd-pause")]
    [InlineData("cmd-abort")]
    public async Task Inbound_commands_answer_exactly_as_they_did_before_the_seam(string name)
    {
        var text = name switch
        {
            "cmd-status" => "/status",
            "cmd-tasks" => "/tasks",
            "cmd-start" => "/start",
            "cmd-daily" => "/daily",
            "cmd-chat" => "/chat",
            "cmd-inject" => "/inject re-run the ratchet gate",
            "cmd-pause" => "/pause",
            "cmd-abort" => "/abort",
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        using var bot = new RecordingBotApi();
        bot.QueueCommand(ChatId, text);

        var twoWay = name is "cmd-pause" or "cmd-abort";
        using var svc = new TelegramService(Plan(bot.Root, twoWay: twoWay, pollSeconds: 1), State(),
            NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        var calls = await WaitForCallsAsync(bot, 1);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        AssertGolden(name, calls);
    }

    private async Task DrivePushAsync(string name, TelegramService svc)
    {
        switch (name)
        {
            case "push-plain":
                await svc.PushAsync("the run resumed after the owner approved past the cap");
                break;

            case "push-session-end":
                await svc.PushSessionEndAsync(new SessionEndPush(
                    7, "KS11", "Advanced", "build:OK gates:6/6", StructuredResult, 1.25m, 88m, 2,
                    ["KS11.1"], false,
                    ["a1b2c3d4e5f60718293a4b5c6d7e8f9012345678 feat: the seam",
                     "0f1e2d3c4b5a69788796a5b4c3d2e1f012345678 test: the goldens"],
                    TimeSpan.FromMinutes(83)));
                break;

            case "push-session-end-rollover":
                await svc.PushSessionEndAsync(new SessionEndPush(
                    8, "KS11", "Rollover", null, null, 0.4242m, null, 0, [], true, null,
                    TimeSpan.FromSeconds(47)));
                break;

            case "push-run-complete":
                await svc.PushRunCompleteAsync(new RunCompletePush(20, 38, 40, TimeSpan.FromHours(9.5), []));
                break;

            case "push-run-complete-skipped":
                await svc.PushRunCompleteAsync(new RunCompletePush(4, 6, 9, null, ["KS4", "KS7"]));
                break;

            case "push-evidence-batch":
                await svc.PushEvidenceAsync(EvidenceBatch());
                break;

            case "push-keyboard":
                await svc.PushWithKeyboardAsync("the run has parked for approval",
                    [("Approve", "approve:deadbeef:confirmed"), ("Cancel", "cancel:deadbeef")]);
                break;

            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    /// <summary>Six artifacts against a budget of four: the first four ride as uploads (one of them
    /// a photo, because the kind decides the method) and the rest are announced as text.</summary>
    private List<EvidenceArtifact> EvidenceBatch()
    {
        var dir = Path.Combine(_repo, ".conductor", "evidence", "KS11");
        Directory.CreateDirectory(dir);
        var artifacts = new List<EvidenceArtifact>();
        for (var i = 1; i <= 6; i++)
        {
            var visual = i == 2;
            var rel = Path.Combine(".conductor", "evidence", "KS11", visual ? $"shot-{i}.png" : $"note-{i}.md")
                .Replace('\\', '/');
            File.WriteAllText(Path.Combine(_repo, rel), new string('e', 40 * i));
            artifacts.Add(new EvidenceArtifact(rel, visual ? EvidenceKinds.Image : "text",
                $"KS11.{i}", "KS11", 7, new string('0', 64), 40 * i,
                new DateTimeOffset(2026, 8, 18, 9, 0, 0, TimeSpan.Zero), "session"));
        }
        return artifacts;
    }

    private const string StructuredResult =
        "SESSION-RESULT: the messenger seam extracted, composition proven byte-identical\n"
        + "- composition, profiles and browsing now sit behind one channel-agnostic seam\n"
        + "- a fake channel drives the whole surface with no Telegram type in the test\n"
        + "artefacts: src/Conductor.Core/Integrations/Messaging/RemoteSurface.cs\n"
        + "evidence: .conductor/evidence/KS11/ks11-1-seam.md\n"
        + "gaps: raised #412 for the observer profile";

    private RunState State()
    {
        var state = new RunState
        {
            RunId = "ks11-golden-run",
            SessionCounter = 12,
            CurrentStage = "KS11",
            Status = RunStatus.Running,
            AttemptsThisStage = 1,
        };
        state.History.Add(new SessionRecord { Number = 1, CostUsd = 42m });
        return state;
    }

    private PlanConfig Plan(string apiRoot, bool twoWay = false, int pollSeconds = 60)
    {
        var plan = new PlanConfig
        {
            Name = PlanName,
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages =
            {
                new StageConfig { Id = "KS11", Title = "Chapar — the remote surface", Sessions = 1 },
                new StageConfig { Id = "KS12", Title = "The era closes", Sessions = 1 },
            },
            Telegram = new TelegramConfig
            {
                AllowedChatIds = { ChatId },
                PollIntervalSeconds = pollSeconds,
                ApiBaseUrl = apiRoot,
                EnableTwoWay = twoWay,
            },
        };
        plan.Limits.MaxRunCostUsd = 50m;
        return plan;
    }

    /// <summary>A long-poll answers on its own schedule, so the assertion waits for the reply rather
    /// than for a fixed sleep — a sleep tuned to a fast machine is how this suite acquires a flake.</summary>
    private static async Task<List<BotCall>> WaitForCallsAsync(RecordingBotApi bot, int atLeast)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var calls = bot.Snapshot();
            if (calls.Count >= atLeast) return calls;
            await Task.Delay(50);
        }
        return bot.Snapshot();
    }

    // ── the goldens ──

    /// <summary>The recorded calls, rendered the way <see cref="BotCall.Describe"/> renders them —
    /// method, loudness, threading, upload and full text — which is every field the wire carried.</summary>
    private void AssertGolden(string name, IReadOnlyList<BotCall> calls)
    {
        var actual = string.Join("\n\n", calls.Select(c => c.Describe())).ReplaceLineEndings("\n").TrimEnd() + "\n";
        _out.WriteLine(actual);

        var path = Path.Combine(GoldenDir(), name + ".txt");
        if (Environment.GetEnvironmentVariable("CONDUCTOR_GOLDEN_REBASELINE") == "1")
        {
            Directory.CreateDirectory(GoldenDir());
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path),
            $"golden {name}.txt is missing. It is a RECORD, not an output: regenerate deliberately with "
            + "CONDUCTOR_GOLDEN_REBASELINE=1 in its own commit, never as a side effect of a change.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static string GoldenDir() =>
        Path.Combine(RepoRoot(), "tests", "Conductor.Tests", "testdata", "ks11");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
