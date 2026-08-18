using Conductor.Core.Evidence;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Messaging;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS11.4 on the WIRE — the tracker row's exit, measured: <em>an observer pulls a real evidence
/// artifact end-to-end, and the clip constants no longer bound what a reader can reach.</em>
///
/// <para>Nothing is handed in here. A real <see cref="TelegramService"/> resolves the observer's
/// profile from the plan, a real long-poll delivers the command, the real dispatch routes it, and
/// the file that comes back out is read off the disk by the real upload path. The only double is
/// <see cref="RecordingBotApi"/> standing at <see cref="TelegramConfig.ApiBaseUrl"/> — scratch token,
/// scratch chat ids, and no proof in this stage touches a real bot.</para>
/// </summary>
public sealed class KS11_4OnWireTests : IDisposable
{
    private const string ObserverChat = "-100123456";

    private readonly string _repo;

    public KS11_4OnWireTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-ks11p-{Guid.NewGuid():N}", "pull-wire");
        Directory.CreateDirectory(Path.Combine(_repo, ".conductor", "evidence", "KS11"));

        // The token goes in this rig's OWN secrets file rather than in the process environment:
        // xUnit runs classes in parallel, and an env var set by one class is read by every other
        // test in the assembly — including the ones asserting what a run with no token says.
        SecretsStore.WriteTelegramToken(Path.Combine(_repo, ".conductor"), "111111:scratch-bot-token");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(Directory.GetParent(_repo)!.FullName); } catch (Exception) { }
    }

    /// <summary>The checkpoint's own exit. The stakeholder in the group chat types four words and
    /// the artifact that proves a claim arrives in their chat as a file — no forwarding by the
    /// owner, no terminal, and no control surface anywhere near them.</summary>
    [Fact]
    public async Task An_observer_pulls_a_real_artifact_end_to_end()
    {
        var artifact = Path.Combine(".conductor", "evidence", "KS11", "seam.md").Replace('\\', '/');
        await File.WriteAllTextAsync(Path.Combine(_repo, artifact), new string('e', 900));
        await WriteTrackerAsync($"| KS11.1 | the messenger seam | DONE | abc1234 | {artifact} |\n");

        using var bot = new RecordingBotApi();
        bot.QueueCommand(ObserverChat, "/evidence KS11.1");

        using var svc = new TelegramService(Plan(bot.Root), State(), NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        var calls = await WaitForCallsAsync(bot, 1);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var upload = Assert.Single(calls);
        Assert.Equal("sendDocument", upload.Method);
        Assert.Equal(ObserverChat, upload.ChatId);
        Assert.Equal("document", upload.FileField);
        Assert.Equal("seam.md", upload.FileName);
        Assert.Equal(900, upload.FileBytes);
        Assert.Contains("KS11.1", upload.Caption!, StringComparison.Ordinal);
        Assert.Contains(artifact, upload.Caption!, StringComparison.Ordinal);
    }

    /// <summary>The other half of the exit, and the reason CH-6 exists at all.
    ///
    /// <para>A push spends its file budget — <c>EvidenceFilesPerPush</c> = 4 — and ANNOUNCES the
    /// rest. Before this checkpoint that announcement was the end of the road: artifact five existed,
    /// was named in the chat, and could not be obtained from the chat by anybody. Here the same batch
    /// is pushed, artifact five is confirmed to have arrived as text only, and then it is pulled and
    /// arrives whole.</para></summary>
    [Fact]
    public async Task What_the_push_budget_could_only_announce_is_pulled_whole()
    {
        var artifacts = new List<EvidenceArtifact>();
        var rows = "";
        for (var i = 1; i <= 6; i++)
        {
            var rel = Path.Combine(".conductor", "evidence", "KS11", $"note-{i}.md").Replace('\\', '/');
            await File.WriteAllTextAsync(Path.Combine(_repo, rel), new string('e', 100 + i));
            artifacts.Add(new EvidenceArtifact(rel, "text", $"KS11.{i}", "KS11", 7, new string('0', 64),
                100 + i, DateTimeOffset.UnixEpoch, "session"));
            rows += $"| KS11.{i} | claim {i} | DONE | c{i} | {rel} |\n";
        }

        await WriteTrackerAsync(rows);

        using var bot = new RecordingBotApi();
        using var svc = new TelegramService(Plan(bot.Root), State(), NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);

        await ((IRunNotifier)svc).PushEvidenceAsync(artifacts, CancellationToken.None);
        var pushed = await WaitForCallsAsync(bot, 5);

        // Four uploads and one text message: artifact five was named and never sent.
        Assert.Equal(4, pushed.Count(c => c.Method == "sendDocument"));
        var overflow = Assert.Single(pushed, c => c.Method == "sendMessage");
        Assert.Contains("note-5.md", overflow.Text!, StringComparison.Ordinal);
        Assert.DoesNotContain(pushed, c => c.FileName == "note-5.md");

        // The reader asks for it by name, and the bytes the push could not carry arrive.
        bot.QueueCommand(ObserverChat, "/evidence KS11.5");
        var after = await WaitForCallsAsync(bot, 6);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var pulled = after[^1];
        Assert.Equal("sendDocument", pulled.Method);
        Assert.Equal("note-5.md", pulled.FileName);
        Assert.Equal(105, pulled.FileBytes);
        Assert.Equal(ObserverChat, pulled.ChatId);
    }

    /// <summary>An id that names nothing must still ANSWER — a bot that goes silent on a mistyped id
    /// is a bot the reader concludes is broken, and the closed surface means they have no other way
    /// to find out what the ids are.</summary>
    [Fact]
    public async Task A_mistyped_id_is_answered_on_the_wire_rather_than_ignored()
    {
        await WriteTrackerAsync("| KS11.1 | the messenger seam | DONE | abc1234 | missing.md |\n");

        using var bot = new RecordingBotApi();
        bot.QueueCommand(ObserverChat, "/evidence KS11.9");

        using var svc = new TelegramService(Plan(bot.Root), State(), NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        var calls = await WaitForCallsAsync(bot, 1);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var reply = Assert.Single(calls);
        Assert.Equal("sendMessage", reply.Method);
        Assert.Equal(ObserverChat, reply.ChatId);
        Assert.Contains("No checkpoint", reply.Text!, StringComparison.Ordinal);
    }

    // ── the rig ──

    private Task WriteTrackerAsync(string rows) =>
        File.WriteAllTextAsync(Path.Combine(_repo, "TRACKER.md"),
            "# Pull wire\n\n## Checkpoints\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + rows);

    /// <summary>One chat, and an observer: the fan-out is KS11.3's proof, and a second chat here
    /// would only double every count this file asserts.</summary>
    private PlanConfig Plan(string apiRoot)
    {
        var plan = new PlanConfig
        {
            Name = "Karvansara edge",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "KS11", Title = "Chapar — the remote surface", Sessions = 1 } },
            Telegram = new TelegramConfig
            {
                Chats = { new TelegramChatEntry { ChatId = ObserverChat, Profile = "observer" } },
                PollIntervalSeconds = 1,
                ApiBaseUrl = apiRoot,
                EnableTwoWay = true,
            },
        };
        plan.Limits.MaxRunCostUsd = 50m;
        return plan;
    }

    private static RunState State() => new()
    {
        RunId = "ks11-4-wire",
        SessionCounter = 9,
        CurrentStage = "KS11",
        History = { new SessionRecord { Number = 1, CostUsd = 42m } },
    };

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
}
