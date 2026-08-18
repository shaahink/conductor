using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
/// K5.4, transport half — what actually goes on the wire.
///
/// <para>Before this the engine made exactly one Bot API call, <c>sendMessage</c>, with three fields
/// in it. Four consequences, each of which is a silent failure rather than a visible one:</para>
///
/// <list type="number">
/// <item>a message over Telegram's 4096-character limit was answered with HTTP 400 and DROPPED —
///   the owner saw nothing at all, and the run log carried one warning;</item>
/// <item>K5.3's evidence reached the chat as a file PATH on a machine the owner is not sitting at,
///   which for the motivating case — conductor takes a screenshot — is useless;</item>
/// <item>every push buzzed the phone equally, so a routine progress line woke the owner exactly as
///   hard as a run that had parked waiting for them;</item>
/// <item>and a run's messages were N loose lines interleaved with whatever else was in the chat.</item>
/// </list>
///
/// <para>Every assertion here is made on the bytes that left the process: a real
/// <see cref="TelegramService"/>, its real send queue and send loop, POSTing over a real loopback
/// socket to a stub standing in for api.telegram.org — including the multipart bodies, which is the
/// only way to prove a PNG was uploaded rather than named.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class K5_4TransportTests : IDisposable
{
    private const string ChatId = "737373";
    private const string PlanName = "K54Plan";

    private readonly string _repo;
    private readonly string _stateDir;
    private readonly ITestOutputHelper _out;

    public K5_4TransportTests(ITestOutputHelper output)
    {
        _out = output;
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-k54-{Guid.NewGuid():N}");
        _stateDir = Path.Combine(_repo, ".conductor");
        Directory.CreateDirectory(_stateDir);

        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"),
            "# K5.4 Plan\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
            "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
            "| K9.1 | first checkpoint | DONE | abc1234 | e.md |\n" +
            "| K9.2 | second checkpoint | TODO | | |\n");

        SecretsStore.WriteTelegramToken(_stateDir, "k54-test-token");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_repo); } catch (Exception) { }
    }

    private PlanConfig Plan(string apiRoot, long? topic = null) => new()
    {
        Name = PlanName,
        Repo = _repo,
        Tracker = "TRACKER.md",
        Stages = { new StageConfig { Id = "K9", Title = "The result contract and the channels", Sessions = 1 } },
        Telegram = new TelegramConfig
        {
            AllowedChatIds = { ChatId },
            PollIntervalSeconds = 60,
            ApiBaseUrl = apiRoot,
            MessageThreadId = topic,
        },
    };

    /// <summary>Pushes through the REAL queue and send loop, then stops the service — StopAsync
    /// drains the backlog, which is what makes the final push observable.</summary>
    private async Task<List<BotCall>> SendAsync(RecordingBotApi bot, Func<TelegramService, Task> push,
        long? topic = null)
    {
        using var svc = new TelegramService(Plan(bot.Root, topic), new RunState { SessionCounter = 4 },
            NullLogger<TelegramService>.Instance);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);
        await push(svc);
        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var calls = bot.Snapshot();
        _out.WriteLine("---- verbatim Bot API calls ----");
        foreach (var c in calls) _out.WriteLine(c.Describe());
        _out.WriteLine("---- end ----");
        return calls;
    }

    // ── defect 1: a long message was rejected whole and delivered as nothing ──

    [Fact]
    public async Task A_message_over_the_limit_is_chunked_instead_of_being_dropped()
    {
        using var bot = new RecordingBotApi();

        // Twelve thousand characters of ordinary prose: three chunks' worth, and a guaranteed HTTP
        // 400 before K5.4 — Telegram does not truncate, it refuses.
        var body = string.Join("\n", Enumerable.Range(0, 300)
            .Select(i => $"line {i.ToString(CultureInfo.InvariantCulture)} of a very long engine notification"));
        Assert.True(body.Length > 3 * TelegramLimits.MaxMessageChars - 4000);

        var calls = await SendAsync(bot, svc => svc.PushAsync(body));

        Assert.True(calls.Count > 1, $"a {body.Length}-character message left as {calls.Count} call(s)");
        Assert.All(calls, c => Assert.Equal("sendMessage", c.Method));
        Assert.All(calls, c => Assert.True(c.Text!.Length <= TelegramLimits.MaxMessageChars,
            $"a chunk of {c.Text!.Length} characters would be refused by Telegram"));

        // Nothing was lost on the way: the first and last lines both arrived.
        var joined = string.Join("", calls.Select(c => c.Text));
        Assert.Contains("line 0 of a very long", joined, StringComparison.Ordinal);
        Assert.Contains("line 299 of a very long", joined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_chunk_leaves_an_html_tag_or_an_entity_split_across_the_boundary()
    {
        using var bot = new RecordingBotApi();

        // Markup all the way down, with no long runs of plain text to cut in: every candidate cut
        // point is inside a tag, inside an entity, or between an open tag and its close.
        var body = string.Concat(Enumerable.Repeat("<b>bold &amp; brittle</b> <i>italic &lt;here&gt;</i> ", 260));
        Assert.True(body.Length > TelegramLimits.MaxMessageChars);

        var calls = await SendAsync(bot, svc => svc.PushAsync(body));

        Assert.True(calls.Count > 1);
        foreach (var c in calls)
        {
            var t = c.Text!;
            Assert.True(t.Length <= TelegramLimits.MaxMessageChars, $"chunk of {t.Length}");
            // A chunk Telegram's HTML parser would reject: an unbalanced tag, a bare '<' with no
            // '>' after it, or an entity cut in half.
            Assert.Equal(Count(t, "<b>"), Count(t, "</b>"));
            Assert.Equal(Count(t, "<i>"), Count(t, "</i>"));
            Assert.Equal(t.Count(ch => ch == '<'), t.Count(ch => ch == '>'));
            Assert.DoesNotMatch(new Regex(@"&[a-z]*$", RegexOptions.None, TimeSpan.FromSeconds(1)), t);
        }
    }

    private static int Count(string s, string needle) =>
        s.Split(needle, StringSplitOptions.None).Length - 1;

    [Fact]
    public void The_chunker_returns_a_short_message_untouched_and_never_exceeds_the_limit()
    {
        Assert.Equal(new[] { "short" }, HtmlChunker.Split("short", TelegramLimits.MaxMessageChars));

        var exact = new string('x', TelegramLimits.MaxMessageChars);
        Assert.Same(exact, Assert.Single(HtmlChunker.Split(exact, TelegramLimits.MaxMessageChars)));

        // One character over: two chunks, not a refusal.
        var over = new string('x', TelegramLimits.MaxMessageChars + 1);
        var chunks = HtmlChunker.Split(over, TelegramLimits.MaxMessageChars);
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= TelegramLimits.MaxMessageChars));
    }

    // ── defect 2: evidence as a path instead of the artifact ──

    [Fact]
    public async Task A_screenshot_arrives_as_a_photo_rather_than_as_its_path()
    {
        using var bot = new RecordingBotApi();
        var png = WriteArtifact("shot.png", 2048);

        var calls = await SendAsync(bot, svc => svc.PushEvidenceAsync([Artifact("shot.png", EvidenceKinds.Image, png)]));

        var call = Assert.Single(calls);
        Assert.Equal("sendPhoto", call.Method);
        Assert.Equal("photo", call.FileField);
        Assert.Equal("shot.png", call.FileName);
        Assert.Equal(2048, call.FileBytes);

        // The line K5.3 used to push is still there — as the caption on the image itself.
        Assert.Contains("evidence", call.Caption!, StringComparison.Ordinal);
        Assert.Contains("shot.png", call.Caption!, StringComparison.Ordinal);
        Assert.Contains("K9.2", call.Caption!, StringComparison.Ordinal);
        // FU-OWNER-11's stamp rides the caption too — a photo must not be the one anonymous push.
        Assert.StartsWith($"<i>{PlanName} · s", call.Caption!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_visual_artifact_arrives_as_a_document()
    {
        using var bot = new RecordingBotApi();
        var md = WriteArtifact("K5.4-evidence.md", 64);

        var calls = await SendAsync(bot, svc => svc.PushEvidenceAsync([Artifact("K5.4-evidence.md", EvidenceKinds.Text, md)]));

        var call = Assert.Single(calls);
        Assert.Equal("sendDocument", call.Method);
        Assert.Equal("document", call.FileField);
        Assert.Equal("K5.4-evidence.md", call.FileName);
    }

    [Fact]
    public async Task An_artifact_whose_path_no_longer_resolves_is_announced_rather_than_throwing()
    {
        using var bot = new RecordingBotApi();

        var calls = await SendAsync(bot, svc => svc.PushEvidenceAsync(
            [Artifact("gone.png", EvidenceKinds.Image, Path.Combine(_repo, "gone.png"))]));

        var call = Assert.Single(calls);
        Assert.Equal("sendMessage", call.Method);
        Assert.Contains("not attached", call.Text!, StringComparison.Ordinal);
        Assert.Contains("gone.png", call.Text!, StringComparison.Ordinal);
    }

    /// <summary>Telegram refuses a photo over 10 MB and a document over 50 MB outright. Both are
    /// decided before a byte is uploaded, because the failure mode being avoided is a 400 nobody
    /// reads — not a slow upload.</summary>
    [Theory]
    [InlineData(true, 1024, "sendPhoto")]
    [InlineData(true, 11L * 1024 * 1024, "sendDocument")]   // too big to be a photo, still sendable
    [InlineData(false, 1024, "sendDocument")]
    [InlineData(true, 60L * 1024 * 1024, null)]             // beyond every call Telegram has
    [InlineData(false, 60L * 1024 * 1024, null)]
    public void The_bot_api_method_follows_the_kind_and_the_size(bool visual, long bytes, string? expected)
        => Assert.Equal(expected, TelegramLimits.MethodFor(visual, bytes));

    [Fact]
    public async Task A_batch_sends_the_first_few_as_files_and_announces_the_rest()
    {
        using var bot = new RecordingBotApi();
        var many = Enumerable.Range(0, 7)
            .Select(i => { var n = $"shot{i}.png"; return Artifact(n, EvidenceKinds.Image, WriteArtifact(n, 32)); })
            .ToList();

        var calls = await SendAsync(bot, svc => svc.PushEvidenceAsync(many));

        // Four files, then ONE text message naming the rest — a watcher sweep that finds thirty
        // captures must not send thirty photos.
        Assert.Equal(4, calls.Count(c => c.Method == "sendPhoto"));
        var summary = Assert.Single(calls, c => c.Method == "sendMessage");
        Assert.Contains("3 further artifacts, not attached", summary.Text!, StringComparison.Ordinal);
        Assert.Contains("shot6.png", summary.Text!, StringComparison.Ordinal);
    }

    // ── defect 3: everything buzzed equally ──

    [Fact]
    public async Task A_push_the_owner_must_act_on_buzzes_and_a_routine_one_does_not()
    {
        using var bot = new RecordingBotApi();

        var calls = await SendAsync(bot, async svc =>
        {
            await svc.PushAsync("progress: nothing needs you", PushSeverity.Quiet);
            await svc.PushAsync("needs attention — the run has parked", PushSeverity.Alert);
        });

        Assert.Equal(2, calls.Count);
        var quiet = Assert.Single(calls, c => c.Text!.Contains("nothing needs you", StringComparison.Ordinal));
        var loud = Assert.Single(calls, c => c.Text!.Contains("has parked", StringComparison.Ordinal));

        Assert.True(quiet.DisableNotification, "a progress line must not buzz the owner's phone");
        Assert.False(loud.DisableNotification, "a parked run is the one message that has earned a buzz");
    }

    [Fact]
    public async Task A_session_that_ended_needing_the_owner_buzzes_and_one_that_advanced_does_not()
    {
        using var bot = new RecordingBotApi();

        var calls = await SendAsync(bot, async svc =>
        {
            await svc.PushSessionEndAsync(new SessionEndPush(
                1, "K9", "Advanced", "engine-fast:OK", null, 0.1m, null, 1, [], false));
            await svc.PushSessionEndAsync(new SessionEndPush(
                2, "K9", "NeedsAttention", "engine-fast:FAIL", null, 0.1m, null, 0, [], false));
        });

        Assert.True(calls.Single(c => c.Text!.Contains("Advanced", StringComparison.Ordinal)).DisableNotification);
        Assert.False(calls.Single(c => c.Text!.Contains("NeedsAttention", StringComparison.Ordinal)).DisableNotification);
    }

    // ── defect 4: a run's messages were loose lines in a shared chat ──

    [Fact]
    public async Task Every_message_after_the_first_replies_to_the_runs_own_anchor()
    {
        using var bot = new RecordingBotApi();

        var calls = await SendAsync(bot, async svc =>
        {
            await svc.PushAsync("first message of the run");
            await svc.PushAsync("second");
            await svc.PushAsync("third");
        });

        Assert.Equal(3, calls.Count);
        Assert.Null(calls[0].ReplyToMessageId);       // nothing to reply to yet — this IS the anchor
        Assert.Equal(RecordingBotApi.AssignedMessageId, calls[1].ReplyToMessageId);
        Assert.Equal(RecordingBotApi.AssignedMessageId, calls[2].ReplyToMessageId);
        // A deleted anchor must not silence the rest of the run.
        Assert.True(calls[1].AllowSendingWithoutReply);
    }

    [Fact]
    public async Task A_configured_forum_topic_is_used_instead_of_the_reply_anchor()
    {
        using var bot = new RecordingBotApi();

        var calls = await SendAsync(bot, async svc =>
        {
            await svc.PushAsync("first");
            await svc.PushAsync("second");
        }, topic: 991);

        Assert.All(calls, c => Assert.Equal(991L, c.MessageThreadId));
        Assert.All(calls, c => Assert.Null(c.ReplyToMessageId));
    }

    // ── helpers ──

    private string WriteArtifact(string name, int bytes)
    {
        var path = Path.Combine(_repo, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static EvidenceArtifact Artifact(string relPath, string kind, string absolute)
    {
        var info = new FileInfo(absolute);
        return new EvidenceArtifact(relPath, kind, "K9.2", "K9", 4, new string('a', 64),
            info.Exists ? info.Length : 0, DateTimeOffset.UnixEpoch, "claim");
    }
}
