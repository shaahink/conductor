using System.Text;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core.Courier;
using Conductor.Core.Integrations;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>PK4.2 / D7 — the card, through a REAL run: <c>RunCommand</c>, a fake agent that claims with the
/// real <c>conductor task --done --tell</c> binary, real gates, the evidence watcher, and a recording Bot
/// API standing in for Telegram. The room's observer chat is a room file in this process's state home;
/// the plan's own telegram block names the admin chat only — the shape every field plan has, where the
/// group was fed by <c>report.ps1</c> and never by the engine.
///
/// <para>Each test writes what the observer chat received to <c>%TEMP%/conductor-pk42-rig/</c>, so the
/// evidence file can show the card rather than describe it.</para></summary>
public sealed partial class HarnessTests
{
    private const string CardAdminChat = "99205495";
    private const string CardGroupChat = "-1004242424242";
    private const string CardWords = "Cards leave at the verdict | The engine posted this card after the gates went green. The session only wrote these words.";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Card_GreenClaim_PostsExactlyOneCard_WithTheBarTheCountsAndThePair()
    {
        using var bot = new RecordingBotApi();
        var room = CardRig(bot, ["H0.1", "H0.2"], gate: "echo ok");
        try
        {
            Assert.Equal(0, await RunCardRigAsync(once: true, maxSessions: 0));

            var group = GroupCalls(bot);
            Transcript(nameof(Card_GreenClaim_PostsExactlyOneCard_WithTheBarTheCountsAndThePair), group);
            var card = Assert.Single(group);
            Assert.Equal("sendMediaGroup", card.Method);
            Assert.Equal(2, card.FileCount);

            var caption = FirstCaption(card);
            Assert.Contains("<b>Cards leave at the verdict</b>", caption, StringComparison.Ordinal);
            // One of two confirmed, H0.2 still open: half the bar, nothing live, the pending footer.
            Assert.Contains("█████░░░░░  1/2 fixed · 0 live", caption, StringComparison.Ordinal);
            Assert.Contains("The engine posted this card after the gates went green. The session only wrote these words.", caption, StringComparison.Ordinal);
            Assert.Contains("<i>Lands with H0</i>", caption, StringComparison.Ordinal);
            // The pair, in order: the stub keeps the LAST file part, and in a two-photo group that is the
            // after shot - one byte longer than the before shot on purpose, so the bytes say which is which.
            Assert.Equal("H0.1-after.png", card.FileName);
            Assert.Equal(EvidencePng.Length + 1, card.FileBytes);

            // The words went to the room and nowhere else.
            Assert.DoesNotContain(bot.Snapshot(), c => c.ChatId != CardGroupChat && (c.Text ?? c.Caption ?? c.Media ?? "").Contains("Cards leave at the verdict", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(room);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Card_RedClaim_PostsNothing_AndTheNextPromptCarriesTheHeldWords()
    {
        using var bot = new RecordingBotApi();
        var room = CardRig(bot, ["H0.1", "H0.2"], gate: "cmd /c exit 1");
        try
        {
            await RunCardRigAsync(once: false, maxSessions: 2);

            var group = GroupCalls(bot);
            var next = Path.Combine(_stateDir, "logs", "session-002.prompt.md");
            var prompt = File.Exists(next) ? await File.ReadAllTextAsync(next) : "(no second prompt)";
            Transcript(nameof(Card_RedClaim_PostsNothing_AndTheNextPromptCarriesTheHeldWords), group,
                prompt.Split('\n').Where(l => l.Contains("HELD", StringComparison.Ordinal) || l.StartsWith("- H0.1:", StringComparison.Ordinal)
                    || l.StartsWith("The engine posts each card", StringComparison.Ordinal)));

            Assert.Empty(group);
            Assert.True(File.Exists(next), "the run never composed a second prompt");
            Assert.Contains("Words for the room are HELD", prompt, StringComparison.Ordinal);
            Assert.Contains("- H0.1: " + CardWords, prompt, StringComparison.Ordinal);
            // The first prompt had nothing to hold: the words are the claim's, and the claim came later.
            Assert.DoesNotContain("HELD", await File.ReadAllTextAsync(Path.Combine(_stateDir, "logs", "session-001.prompt.md")), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(room);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Card_StageConfirm_PostsOneStageCard_AfterTheCheckpointCard()
    {
        using var bot = new RecordingBotApi();
        var room = CardRig(bot, ["H0.1"], gate: "echo ok", gatePolicy: "perPhase");
        try
        {
            Assert.Equal(0, await RunCardRigAsync(once: false, maxSessions: 1));

            var group = GroupCalls(bot);
            Transcript(nameof(Card_StageConfirm_PostsOneStageCard_AfterTheCheckpointCard), group);
            Assert.Equal(2, group.Count);

            // The checkpoint card: its stage's last checkpoint is confirmed, so it is live.
            Assert.Equal("sendMediaGroup", group[0].Method);
            Assert.Contains("██████████  1/1 fixed · 1 live", FirstCaption(group[0]), StringComparison.Ordinal);
            Assert.Contains("<i>Live now</i>", FirstCaption(group[0]), StringComparison.Ordinal);

            // The stage card, once, with what changed - the agent's commit, not the engine's bookkeeping.
            Assert.Equal("sendMessage", group[1].Method);
            Assert.Contains("<b>H0 · Cards</b>", group[1].Text, StringComparison.Ordinal);
            Assert.Contains("• feat: deliver the card checkpoint", group[1].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("chore(conductor)", group[1].Text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(room);
        }
    }

    // -- the rig ---------------------------------------------------------------------------------

    /// <summary>Tracker, plan, token, room and the claiming agent. Returns the room file to delete.</summary>
    private string CardRig(RecordingBotApi bot, string[] checkpoints, string gate, string gatePolicy = "perSession")
    {
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# Tracker\n\n## Checkpoints\n\n| ID | Title | Status | Commit | Evidence |\n|---|---|---|---|---|\n"
            + string.Concat(checkpoints.Select(id => $"| {id} | card checkpoint {id} | TODO | | |\n")));

        Directory.CreateDirectory(_stateDir);
        SecretsStore.WriteTelegramToken(_stateDir, "111111:pk42-rig-token");

        var staged = Path.Combine(_repo, "staged");
        Directory.CreateDirectory(staged);
        File.WriteAllBytes(Path.Combine(staged, "before.png"), EvidencePng);
        File.WriteAllBytes(Path.Combine(staged, "after.png"), [.. EvidencePng, 0x01]);
        var shots = Path.Combine(_stateDir, "evidence", "H0");
        Directory.CreateDirectory(shots);
        var note = Path.Combine(_repo, "docs", "evidence", "H0", "H0.1-claim-note.md");
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        File.WriteAllText(note, "# H0.1\n\nwhat was measured.\n");

        var conductor = Path.Combine(AppContext.BaseDirectory, "conductor.exe");
        var script = Path.Combine(_repo, "card-agent.cmd");
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            "echo {\"type\":\"step_start\"}",
            "echo {\"type\":\"step_finish\",\"part\":{\"cost\":0.0004,\"tokens\":{\"input\":100,\"output\":50,\"cache\":{\"read\":0}}}}",
            // Only the first session delivers; a later one finds the claim already made.
            $"if exist \"{Path.Combine(_stateDir, "claim-output.txt")}\" goto done",
            $"copy /y \"{Path.Combine(staged, "before.png")}\" \"{Path.Combine(shots, "H0.1-before.png")}\" >nul",
            $"copy /y \"{Path.Combine(staged, "after.png")}\" \"{Path.Combine(shots, "H0.1-after.png")}\" >nul",
            "echo card done> card-output.txt",
            "git add card-output.txt",
            "git commit -m \"feat: deliver the card checkpoint\" >nul",
            $"\"{conductor}\" task --done H0.1 --evidence docs/evidence/H0/H0.1-claim-note.md --tell \"{CardWords}\" > \"{Path.Combine(_stateDir, "claim-output.txt")}\" 2>&1",
            ":done",
            "echo {\"type\":\"text\",\"part\":{\"text\":\"SESSION-RESULT: delivered H0.1\"}}",
            "ping -n 3 127.0.0.1 >nul",
            "exit /b 0",
            ""));

        var plan = new PlanConfig
        {
            Name = "CardRigPlan",
            Repo = _repo,
            Tracker = "TRACKER.md",
            Stages = { new StageConfig { Id = "H0", Title = "Cards", Sessions = 2 } },
            Agent = new AgentConfig { Command = "cmd.exe", Args = { "/c", script, "{prompt}" }, Provider = "opencode" },
            // perPhase is what a field plan runs, and the only policy under which a stage is CONFIRMED.
            GatePolicy = gatePolicy,
            VerifyEachDelivery = false,
            Gates = { new GateConfig { Name = "smoke", Command = gate, Tier = "fast", TimeoutMinutes = 1 } },
            Telegram = new TelegramConfig
            {
                Chats = [new TelegramChatEntry { ChatId = CardAdminChat, Profile = "admin" }],
                PollIntervalSeconds = 1,
                ApiBaseUrl = bot.Root,
            },
        };
        plan.Report.Commit = false;
        File.WriteAllText(Path.Combine(_repo, "card.plan.json"), JsonSerializer.Serialize(plan, PlanConfig.JsonOpts));

        return Rooms.Save(new Room
        {
            Project = "pk42-card-rig-" + Path.GetFileName(_repo),
            Repo = _repo,
            Chats = { Admin = CardAdminChat, Observer = CardGroupChat },
            Footer = { Counters = "{done}/{total} fixed · {live} live", Live = "Live now", Pending = "Lands with {stage}" },
        });
    }

    private async Task<int> RunCardRigAsync(bool once, int maxSessions) =>
        await new RunCommand().ExecuteAsync(null!, new RunCommand.Settings
        {
            Plan = Path.Combine(_repo, "card.plan.json"),
            Once = once,
            MaxSessions = maxSessions,
            Headless = true,
            NoFace = true,
            NoControlPlane = true,
        }).WaitAsync(TimeSpan.FromMinutes(4));

    private static List<BotCall> GroupCalls(RecordingBotApi bot) =>
        [.. bot.Snapshot().Where(c => c.ChatId == CardGroupChat)];

    private static string FirstCaption(BotCall call)
    {
        using var media = JsonDocument.Parse(call.Media ?? "[]");
        return media.RootElement.EnumerateArray().Select(m => m.TryGetProperty("caption", out var c) ? c.GetString() : null)
            .FirstOrDefault(c => c is not null) ?? "";
    }

    private void Transcript(string test, IReadOnlyList<BotCall> group, IEnumerable<string>? promptLines = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "conductor-pk42-rig");
        Directory.CreateDirectory(dir);
        var sb = new StringBuilder();
        sb.Append("observer chat received ").Append(group.Count).AppendLine(" call(s)");
        foreach (var call in group)
        {
            sb.Append("- ").Append(call.Method);
            if (call.FileCount > 0) sb.Append(" (").Append(call.FileCount).Append(" photos, the last ").Append(call.FileName).Append(", ").Append(call.FileBytes).Append(" B)");
            sb.AppendLine().AppendLine((call.Media is not null ? FirstCaption(call) : call.Text ?? "").Replace("\n", "\n    ", StringComparison.Ordinal).Insert(0, "    "));
        }
        if (promptLines is not null)
        {
            sb.AppendLine("next prompt (session-002.prompt.md), the held-words lines:");
            foreach (var line in promptLines) sb.Append("    ").AppendLine(line.TrimEnd());
        }
        var claim = Path.Combine(_stateDir, "claim-output.txt");
        if (File.Exists(claim)) sb.AppendLine("claim verb output:").Append("    ").AppendLine(File.ReadAllText(claim).Trim().Replace("\n", "\n    ", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(dir, test + ".txt"), sb.ToString());
    }
}
