using Conductor.Core.Integrations;
using Conductor.Core.Inbox;
using Conductor.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV3.3 ON THE WIRE — a voice note arrives, and what the owner hears back is the truth about what
/// happened to it.
///
/// <para>Three journeys, each one end to end through a real <see cref="TelegramService"/>
/// long-polling a loopback stand-in: transcribed (two messages — the receipt, then the words with
/// their marks), not configured (one message that NAMES the setting), and a command that failed (the
/// receipt, then a sentence saying why). In all three the note is on disk with its audio, because
/// the file is what survives a transcript going wrong.</para>
///
/// <para>Scratch token, scratch chat ids, a stub at <see cref="TelegramConfig.ApiBaseUrl"/> and a
/// temp repo per test — no proof here touches a real bot, a real chat or this repo's state.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class DV3_3TranscriptionWireTests : IDisposable
{
    private const string AdminChat = "99205495";

    private readonly string _repo;
    private readonly string _stateDir;
    private readonly string? _envBefore;
    private readonly ITestOutputHelper _out;

    public DV3_3TranscriptionWireTests(ITestOutputHelper output)
    {
        _out = output;
        _repo = Path.Combine(Path.GetTempPath(), $"conductor-dv33w-{Guid.NewGuid():N}", "inbox-rig");
        _stateDir = Path.Combine(_repo, ".conductor");
        Directory.CreateDirectory(_stateDir);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# DV3.3 rig\n");
        SecretsStore.WriteTelegramToken(_stateDir, "111111:dv33-scratch-token");

        _envBefore = Environment.GetEnvironmentVariable(TranscribeConfig.CommandEnvVar);
        Environment.SetEnvironmentVariable(TranscribeConfig.CommandEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TranscribeConfig.CommandEnvVar, _envBefore);
        try { TestTemp.DeleteTree(Directory.GetParent(_repo)!.FullName); } catch (Exception) { }
    }

    private const string VoiceUpdate =
        """
        {"message_id":601,"chat":{"id":ADMIN,"type":"private"},
         "voice":{"file_id":"voice-1","file_unique_id":"u1","duration":11,
                  "mime_type":"audio/ogg","file_size":3000}}
        """;

    private const string TranscriptJson =
        """
        {"text":"the courier should refuse a file over twenty megabytes","language":"en",
         "segments":[{"start":0.0,"end":2.5,"text":"the courier should refuse a file","confidence":0.93},
                     {"start":2.5,"end":4.0,"text":"over twenty megabytes","confidence":0.21}]}
        """;

    /// <summary>The whole checkpoint in one journey. The receipt goes out at once (transcription is
    /// GPU minutes and silence reads as a drop), the words follow, the doubtful stretch is marked in
    /// BOTH the message and the stored note, and the audio is still on disk beside its transcript.</summary>
    [Fact]
    public async Task A_voice_note_comes_back_transcribed_with_its_doubtful_stretch_marked()
    {
        using var bot = new RecordingBotApi();
        bot.AddFile("voice-1", "voice/file_7.oga", Bytes(3_000));
        Send(bot, VoiceUpdate);

        var calls = await DriveAsync(bot, Script("ok", "echo " + OneLine(TranscriptJson)), expected: 2);

        Assert.Contains("Voice note received", calls[0], StringComparison.Ordinal);
        Assert.Contains("Transcribing it locally", calls[0], StringComparison.Ordinal);

        Assert.Contains("Transcript", calls[1], StringComparison.Ordinal);
        Assert.Contains("confidence 66%", calls[1], StringComparison.Ordinal);
        Assert.Contains("[?: over twenty megabytes]", calls[1], StringComparison.Ordinal);

        var note = new InboxStore(_stateDir).All().Single();
        Assert.Equal("the courier should refuse a file [?: over twenty megabytes]", note.Text);
        Assert.Equal("media/601-voice.oga.transcript.json", note.TranscriptPath);
        Assert.True(File.Exists(Path.Combine(_stateDir, "inbox", "media", "601-voice.oga")),
            "the audio was not kept beside its transcript");
        Assert.True(File.Exists(Path.Combine(_stateDir, "inbox", "media", "601-voice.oga.transcript.json")));
        _out.WriteLine(note.Text);
    }

    /// <summary>Findings §1.6's fallback, and the one the owner will meet first on a fresh machine:
    /// no command configured. The note files, the audio is kept, and the reply NAMES the setting —
    /// "not transcribed" without the fix is a dead end.</summary>
    [Fact]
    public async Task With_no_command_configured_the_note_is_kept_and_the_reply_says_untranscribed()
    {
        using var bot = new RecordingBotApi();
        bot.AddFile("voice-1", "voice/file_7.oga", Bytes(1_500));
        Send(bot, VoiceUpdate);

        var calls = await DriveAsync(bot, command: null, expected: 1);

        Assert.Contains("Voice note received", calls[0], StringComparison.Ordinal);
        Assert.Contains("Not transcribed", calls[0], StringComparison.Ordinal);
        Assert.Contains("courier.transcribe.command", calls[0], StringComparison.Ordinal);
        Assert.Contains(TranscribeConfig.CommandEnvVar, calls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("Transcribing it locally", calls[0], StringComparison.Ordinal);

        var note = new InboxStore(_stateDir).All().Single();
        Assert.Null(note.TranscriptPath);
        Assert.True(note.Untranscribed);
        Assert.True(File.Exists(Path.Combine(_stateDir, "inbox", "media", "601-voice.oga")),
            "the audio must be kept when the words are not");
    }

    /// <summary>A command that fails costs the transcript and nothing else: the note is still filed,
    /// the audio is still there, and the sender is told what went wrong instead of being left with a
    /// receipt and silence.</summary>
    [Fact]
    public async Task A_failing_command_still_leaves_the_note_and_says_why()
    {
        using var bot = new RecordingBotApi();
        bot.AddFile("voice-1", "voice/file_7.oga", Bytes(1_200));
        Send(bot, VoiceUpdate);

        var calls = await DriveAsync(bot,
            Script("boom", "echo CUDA out of memory 1>&2\r\nexit /b 2"), expected: 2);

        Assert.Contains("Transcribing it locally", calls[0], StringComparison.Ordinal);
        Assert.Contains("Not transcribed", calls[1], StringComparison.Ordinal);
        Assert.Contains("exited 2", calls[1], StringComparison.Ordinal);
        Assert.Contains("CUDA out of memory", calls[1], StringComparison.Ordinal);

        var note = new InboxStore(_stateDir).All().Single();
        Assert.Null(note.TranscriptPath);
        Assert.True(File.Exists(Path.Combine(_stateDir, "inbox", "media", "601-voice.oga")));
    }

    // ── the rig ──

    /// <summary>Starts the service, waits for the replies the stub's long-poll provokes, stops it,
    /// and hands back what the bot was told to say, in order.</summary>
    private async Task<List<string>> DriveAsync(RecordingBotApi bot, string? command, int expected)
    {
        var log = new CapturingLogger();
        using var svc = new TelegramService(Plan(bot.Root, command),
            new RunState { RunId = "dv33", SessionCounter = 3 }, log);
        await ((IHostedService)svc).StartAsync(CancellationToken.None);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (DateTime.UtcNow < deadline && bot.Snapshot().Count < expected)
            await Task.Delay(50);

        await ((IHostedService)svc).StopAsync(CancellationToken.None);

        var calls = bot.Snapshot();
        foreach (var c in calls) _out.WriteLine(c.Describe());
        foreach (var l in log.Lines.Where(l => l.Contains("transcribe", StringComparison.OrdinalIgnoreCase)))
            _out.WriteLine("log: " + l);

        Assert.Equal(expected, calls.Count);
        return [.. calls.Select(c => c.Text ?? "")];
    }

    private PlanConfig Plan(string apiRoot, string? command) => new()
    {
        Name = "Divan",
        Repo = _repo,
        Tracker = "TRACKER.md",
        Stages = { new StageConfig { Id = "DV3", Title = "The inbox", Sessions = 1 } },
        Courier = new CourierConfig
        {
            Transcribe = new TranscribeConfig { Command = command, TimeoutSeconds = 30 },
        },
        Telegram = new TelegramConfig
        {
            PollIntervalSeconds = 1,
            ApiBaseUrl = apiRoot,
            EnableTwoWay = true,
            Chats = { new TelegramChatEntry { ChatId = AdminChat, Profile = "admin" } },
        },
    };

    private static void Send(RecordingBotApi bot, string messageJson) =>
        bot.QueueMessage(messageJson.Replace("ADMIN", AdminChat, StringComparison.Ordinal));

    private string Script(string name, string body)
    {
        var path = Path.Combine(_repo, name + ".cmd");
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        return "cmd /c \"" + path + "\" {audio}";
    }

    private static string OneLine(string json) =>
        string.Join(" ", json.Split('\n', StringSplitOptions.TrimEntries));

    private static byte[] Bytes(int count)
    {
        var b = new byte[count];
        Array.Fill(b, (byte)0x4F);
        return b;
    }

    private sealed class CapturingLogger : ILogger<TelegramService>
    {
        private readonly Lock _gate = new();
        private readonly List<string> _lines = new();

        public List<string> Lines { get { lock (_gate) return new List<string>(_lines); } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (_gate) _lines.Add(formatter(state, exception));
        }
    }
}
