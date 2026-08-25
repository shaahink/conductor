using System.Globalization;
using System.Text.Json;

using Conductor.Core.Inbox;
using Conductor.Models;

using Xunit.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// DV3.3 — speech into words, locally, with the doubt kept.
///
/// <para>Findings §1.6 asks for three things and each one is a failure mode if it is missing: the
/// command is LOCAL and configured (nothing about a run leaves this machine); low-confidence
/// stretches are MARKED in the stored note (an autonomous agent three weeks later cannot hear the
/// audio and has no other way to know which words were guessed); and with no command configured the
/// note still files WITH ITS AUDIO and the sender is told — a silently untranscribed voice note is
/// the §1.2 gap-2 drop wearing a different hat.</para>
///
/// <para>The transcriber under test here is the real one, shelling out to real processes. The
/// commands are two-line batch files rather than a 3 GB speech model: what is being pinned is
/// conductor's half of the contract — substitution, parsing, marking, storing, and every way a
/// command can let it down. The model's half is proven once, live, against a real
/// <c>.ogg</c> — see <c>.conductor/evidence/DV3/dv3-3-transcription.md</c>.</para>
/// </summary>
public sealed class DV3_3TranscriptionTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _envBefore;
    private readonly ITestOutputHelper _out;

    public DV3_3TranscriptionTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"conductor-dv33-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);

        // The env override is process-wide and this class asserts on its absence as well as its
        // presence. Cleared here, restored in Dispose, so a machine that has one set cannot make
        // "no command is configured" quietly untrue.
        _envBefore = Environment.GetEnvironmentVariable(TranscribeConfig.CommandEnvVar);
        Environment.SetEnvironmentVariable(TranscribeConfig.CommandEnvVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(TranscribeConfig.CommandEnvVar, _envBefore);
        try { TestTemp.DeleteTree(_dir); } catch (Exception) { }
    }

    private const string ContractJson =
        """
        {"text":"the courier should refuse a file over twenty megabytes","language":"en",
         "segments":[{"start":0.0,"end":2.5,"text":"the courier should refuse a file","confidence":0.93},
                     {"start":2.5,"end":4.0,"text":"over twenty megabytes","confidence":0.21}]}
        """;

    // ────────────────────────── the transcript itself ──────────────────────────

    /// <summary>The heart of the checkpoint: a stretch the model was unsure of is WRAPPED, and a
    /// stretch it was sure of is not. Anything else and the reader has no way to tell them apart.</summary>
    [Fact]
    public void Only_the_low_confidence_segments_are_marked()
    {
        var t = Transcript.Parse(ContractJson);

        Assert.Equal(2, t.Segments.Count);
        Assert.Equal("en", t.Language);
        Assert.Equal(
            "the courier should refuse a file [?: over twenty megabytes]",
            t.Marked(Transcript.DefaultConfidenceFloor));
        Assert.Equal(1, t.DoubtfulCount(Transcript.DefaultConfidenceFloor));
        _out.WriteLine(t.Marked(Transcript.DefaultConfidenceFloor));
    }

    /// <summary>The floor is a dial, not a constant: the same transcript read strictly marks more.
    /// A different command normalises its numbers differently and the plan has to be able to say so.</summary>
    [Fact]
    public void The_confidence_floor_decides_what_is_doubted()
    {
        var t = Transcript.Parse(ContractJson);

        Assert.DoesNotContain(Transcript.DoubtOpen, t.Marked(0.0), StringComparison.Ordinal);
        Assert.Equal("[?: the courier should refuse a file] [?: over twenty megabytes]", t.Marked(0.99));
    }

    /// <summary>A command that reports no confidence gets NO marks. Null is not confident and it is
    /// not doubtful either — inventing a number for a command whose scale we cannot read would be a
    /// claim about words nobody made.</summary>
    [Fact]
    public void Plain_text_output_is_a_transcript_with_no_marks_and_no_confidence()
    {
        var t = Transcript.Parse("  just the words, no json in sight  ");

        Assert.Equal("just the words, no json in sight", t.Text);
        Assert.Null(t.MeanConfidence);
        Assert.DoesNotContain(Transcript.DoubtOpen, t.Marked(0.9), StringComparison.Ordinal);
        Assert.Equal("no confidence reported", t.ConfidenceLine(0.45));
    }

    /// <summary>faster-whisper's own number. Every wrapper around that library has
    /// <c>avg_logprob</c> to hand and none of them normalise it the same way, so the engine reads it
    /// directly: exp() of a mean log-probability is a probability.</summary>
    [Fact]
    public void An_avg_logprob_is_read_as_a_confidence()
    {
        var t = Transcript.Parse(
            """
            {"segments":[{"start":0,"end":1,"text":"clear","avg_logprob":-0.10},
                         {"start":1,"end":2,"text":"muddy","avg_logprob":-2.30}]}
            """);

        Assert.Equal(0.905, t.Segments[0].Confidence!.Value, 3);
        Assert.Equal(0.100, t.Segments[1].Confidence!.Value, 3);
        Assert.Equal("clear [?: muddy]", t.Marked(Transcript.DefaultConfidenceFloor));
        Assert.Equal("clear muddy", t.Text);   // the text stays what was heard
    }

    /// <summary>Weighted by duration, so one doubtful half-second cannot condemn a two-minute note
    /// and a run of confident "mm"s cannot rescue one.</summary>
    [Fact]
    public void Mean_confidence_is_weighted_by_how_long_each_segment_lasted()
    {
        var t = new Transcript("x", [
            new TranscriptSegment(0, 100, "a long confident stretch", 0.90),
            new TranscriptSegment(100, 101, "a doubtful blip", 0.10),
        ]);

        Assert.Equal(0.892, t.MeanConfidence!.Value, 3);
        Assert.Contains("confidence 89%", t.ConfidenceLine(0.45), StringComparison.Ordinal);
        Assert.Contains("1 unsure stretch", t.ConfidenceLine(0.45), StringComparison.Ordinal);
    }

    // ────────────────────────── the command ──────────────────────────

    /// <summary>No command configured: answered instantly, no process, and NOT an error. The reply
    /// path downstream depends on this being a distinct outcome from a failure.</summary>
    [Fact]
    public async Task With_no_command_configured_nothing_is_run_and_the_outcome_says_so()
    {
        var t = new LocalCommandTranscriber(new TranscribeConfig());

        Assert.False(t.Configured);
        var outcome = await t.TranscribeAsync(Audio("silent.ogg"), CancellationToken.None);

        Assert.Equal(TranscriptionStatus.NotConfigured, outcome.Status);
        Assert.Null(outcome.Transcript);
    }

    /// <summary>The env override, same precedence the bot token already has — and what a rig (and,
    /// at DV4, a machine-level courier with no plan in front of it) points at a command with.</summary>
    [Fact]
    public void The_environment_variable_outranks_the_plan()
    {
        var cfg = new TranscribeConfig { Command = "from-the-plan" };
        Assert.Equal("from-the-plan", cfg.ResolvedCommand());

        Environment.SetEnvironmentVariable(TranscribeConfig.CommandEnvVar, "from-the-env");
        try { Assert.Equal("from-the-env", cfg.ResolvedCommand()); }
        finally { Environment.SetEnvironmentVariable(TranscribeConfig.CommandEnvVar, null); }
    }

    /// <summary>A real process, run over a real file, parsed into marks. The command echoes the path
    /// it was handed, which is how this also proves the substitution ARRIVED rather than that it was
    /// merely formatted.</summary>
    [Fact]
    public async Task A_configured_command_is_run_over_the_audio_and_its_confidences_survive()
    {
        var audio = Audio("note.ogg");
        var t = new LocalCommandTranscriber(new TranscribeConfig { Command = Script("ok", Echo(ContractJson)) });

        Assert.True(t.Configured);
        var outcome = await t.TranscribeAsync(audio, CancellationToken.None);

        Assert.Equal(TranscriptionStatus.Ok, outcome.Status);
        Assert.Equal("the courier should refuse a file [?: over twenty megabytes]",
            outcome.Transcript!.Marked(t.ConfidenceFloor));
    }

    /// <summary>The placeholder is where the author put it, and a path with a space in it survives —
    /// which on Windows is the ordinary case, not the exotic one.</summary>
    [Fact]
    public void The_audio_placeholder_is_substituted_and_quoted_where_it_has_to_be()
    {
        Assert.Equal(("python", "run.py --json C:/a/b.ogg"),
            LocalCommandTranscriber.Split("python run.py --json {audio}", "C:/a/b.ogg"));

        Assert.Equal(("python", "run.py \"C:/a b/c.ogg\""),
            LocalCommandTranscriber.Split("python run.py {audio}", "C:/a b/c.ogg"));

        // No placeholder at all: appended, which is what every ASR CLI expects anyway.
        Assert.Equal(("whisper", "--model tiny C:/a.ogg"),
            LocalCommandTranscriber.Split("whisper --model tiny", "C:/a.ogg"));

        // A quoted executable is one token and a space, not two tokens.
        Assert.Equal(("C:/Program Files/x/y.exe", "-q C:/a.ogg"),
            LocalCommandTranscriber.Split("\"C:/Program Files/x/y.exe\" -q", "C:/a.ogg"));
    }

    /// <summary>A command that fails is a sentence, never an exception: the note has already been
    /// filed by the time this runs, and a throw here would take the poll loop down with it.</summary>
    [Fact]
    public async Task A_failing_command_comes_back_as_a_sentence_naming_the_exit_code()
    {
        var t = new LocalCommandTranscriber(
            new TranscribeConfig { Command = Script("boom", "echo whisper: model not found 1>&2\r\nexit /b 3") });

        var outcome = await t.TranscribeAsync(Audio("note.ogg"), CancellationToken.None);

        Assert.Equal(TranscriptionStatus.Failed, outcome.Status);
        Assert.Contains("exited 3", outcome.Detail!, StringComparison.Ordinal);
        Assert.Contains("model not found", outcome.Detail!, StringComparison.Ordinal);
    }

    /// <summary>An executable that is not on this machine. The Win32Exception this raises is the
    /// single most likely real-world failure — a typo in a plan file — and it must not escape.</summary>
    [Fact]
    public async Task A_command_that_is_not_on_this_machine_is_a_sentence_too()
    {
        var t = new LocalCommandTranscriber(
            new TranscribeConfig { Command = "no-such-transcriber-9f3a --json {audio}" });

        var outcome = await t.TranscribeAsync(Audio("note.ogg"), CancellationToken.None);

        Assert.Equal(TranscriptionStatus.Failed, outcome.Status);
        Assert.Contains("could not be run", outcome.Detail!, StringComparison.Ordinal);
    }

    /// <summary>A command that hangs is killed and named. Without this, one wedged process holds a
    /// voice note hostage for as long as the engine lives.</summary>
    [Fact]
    public async Task A_command_that_never_finishes_is_stopped_and_the_timeout_is_named()
    {
        var t = new LocalCommandTranscriber(new TranscribeConfig
        {
            Command = Script("hang", "ping -n 30 127.0.0.1 >nul"),
            TimeoutSeconds = 1,
        });

        var outcome = await t.TranscribeAsync(Audio("note.ogg"), CancellationToken.None);

        Assert.Equal(TranscriptionStatus.Failed, outcome.Status);
        Assert.Contains("did not finish within 1s", outcome.Detail!, StringComparison.Ordinal);
    }

    /// <summary>A command that runs clean and hears nothing is not a broken command. A silent
    /// recording is a real thing to send by accident and it gets its own sentence.</summary>
    [Fact]
    public async Task Silence_is_not_a_failure()
    {
        var t = new LocalCommandTranscriber(
            new TranscribeConfig { Command = Script("silent", Echo("""{"text":"","segments":[]}""")) });

        var outcome = await t.TranscribeAsync(Audio("note.ogg"), CancellationToken.None);

        Assert.Equal(TranscriptionStatus.NoSpeech, outcome.Status);
        Assert.False(outcome.HasWords);
        Assert.Contains("heard no speech", outcome.Detail!, StringComparison.Ordinal);
    }

    /// <summary>A nonsense dial is refused when the PLAN loads, not when the first voice note lands
    /// on a machine nobody is watching.</summary>
    [Fact]
    public void A_nonsense_transcribe_dial_is_refused_at_plan_load()
    {
        Assert.Contains("timeoutSeconds",
            new TranscribeConfig { TimeoutSeconds = 0 }.Refusal()!, StringComparison.Ordinal);
        Assert.Contains("confidenceFloor",
            new TranscribeConfig { ConfidenceFloor = 1.5 }.Refusal()!, StringComparison.Ordinal);
        Assert.Null(new TranscribeConfig().Refusal());
    }

    // ────────────────────────── the stored note ──────────────────────────

    /// <summary>The whole point, on disk: the marked words in the note, the numbers in a sidecar
    /// BESIDE THE AUDIO, and the audio still there. Findings §1.6 — a garbled transcription is
    /// always recoverable, which is only true if nothing threw the original away.</summary>
    [Fact]
    public void A_transcript_is_attached_beside_the_audio_and_the_audio_stays()
    {
        var store = new InboxStore(_dir);
        Directory.CreateDirectory(Path.Combine(store.Dir, "media"));
        File.WriteAllBytes(Path.Combine(store.Dir, "media", "501-voice.oga"), [1, 2, 3]);
        Assert.True(store.Append(new InboxNote(501, DateTime.UtcNow, "99", "voice", "",
            MediaPath: "media/501-voice.oga")));

        var updated = store.AttachTranscript(501, Transcript.Parse(ContractJson), 0.45);

        Assert.NotNull(updated);
        Assert.Equal("the courier should refuse a file [?: over twenty megabytes]", updated!.Text);
        Assert.Equal("media/501-voice.oga.transcript.json", updated.TranscriptPath);
        Assert.Equal("media/501-voice.oga", updated.MediaPath);
        Assert.True(updated.Transcribed);
        Assert.False(updated.Untranscribed);
        Assert.Equal(0.66, updated.TranscriptConfidence!.Value, 2);

        // The sidecar is beside the audio, and it holds the numbers the marks came from.
        var sidecar = Path.Combine(store.Dir, "media", "501-voice.oga.transcript.json");
        Assert.True(File.Exists(sidecar), sidecar);
        Assert.True(File.Exists(Path.Combine(store.Dir, "media", "501-voice.oga")), "the audio is gone");
        using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
        Assert.Equal(2, doc.RootElement.GetProperty("segments").GetArrayLength());
        Assert.Equal(0.21, doc.RootElement.GetProperty("segments")[1].GetProperty("confidence").GetDouble(), 3);
        Assert.Equal(1, doc.RootElement.GetProperty("doubtful").GetInt32());

        // And it is what a later reader gets, not just what this call returned.
        Assert.Equal(updated.Text, store.All().Single().Text);
        _out.WriteLine(File.ReadAllText(sidecar));
    }

    /// <summary>A caption is the owner's TYPED words and the transcript is their spoken ones. Both
    /// are theirs; neither replaces the other.</summary>
    [Fact]
    public void A_caption_survives_the_transcript_landing_on_top_of_it()
    {
        var store = new InboxStore(_dir);
        store.Append(new InboxNote(7, DateTime.UtcNow, "99", "audio", "listen from 2:00",
            MediaDpath()));

        var updated = store.AttachTranscript(7, Transcript.Parse(ContractJson), 0.45);

        Assert.StartsWith("listen from 2:00", updated!.Text, StringComparison.Ordinal);
        Assert.Contains("the courier should refuse a file", updated.Text, StringComparison.Ordinal);
    }

    /// <summary>Audio with no transcript is a NAMED state, not an absence — it is what
    /// <c>conductor inbox list</c> flags and what the battery says out loud.</summary>
    [Fact]
    public void Audio_with_no_transcript_reports_itself_untranscribed()
    {
        var voice = new InboxNote(1, DateTime.UtcNow, "99", "voice", "", MediaPath: "media/1.oga");
        var photo = new InboxNote(2, DateTime.UtcNow, "99", "photo", "", MediaPath: "media/2.jpg");

        Assert.True(voice.Untranscribed);
        Assert.False(voice.Transcribed);
        Assert.False(photo.Untranscribed);   // a photo was never going to be transcribed
    }

    // ────────────────────────── prune: the only deletion path ──────────────────────────

    /// <summary>Findings §6.1 — deletion is a verb the owner types, and it takes the whole note:
    /// the record, the audio and the transcript, in one act. A prune that left orphan media would
    /// make "the audio is always there" false in the one direction nobody checks.</summary>
    [Fact]
    public void Prune_takes_the_note_its_audio_and_its_transcript_together()
    {
        var store = new InboxStore(_dir);
        Directory.CreateDirectory(Path.Combine(store.Dir, "media"));
        File.WriteAllBytes(Path.Combine(store.Dir, "media", "9-voice.oga"), [4, 5, 6]);
        store.Append(new InboxNote(9, DateTime.UtcNow, "99", "voice", "", MediaPath: "media/9-voice.oga"));
        store.Append(new InboxNote(10, DateTime.UtcNow, "99", "text", "keep me"));
        var withTranscript = store.AttachTranscript(9, Transcript.Parse(ContractJson), 0.45)!;

        Assert.Equal(3, store.FilesOf(withTranscript).Count);
        Assert.Equal(3, store.Prune(withTranscript));

        Assert.False(File.Exists(Path.Combine(store.Dir, "media", "9-voice.oga")));
        Assert.False(File.Exists(Path.Combine(store.Dir, "media", "9-voice.oga.transcript.json")));
        var left = store.All();
        Assert.Equal("keep me", Assert.Single(left).Text);          // nothing else was touched

        // And the id is recorded as pruned rather than erased: "where did note 9 go" has an answer.
        var index = File.ReadAllLines(store.IndexPath);
        Assert.Contains(index, l => l.Contains("\"id\":9", StringComparison.Ordinal)
                                 && l.Contains("\"pruned\":true", StringComparison.Ordinal));
    }

    /// <summary>THE architecture test, in the KS4.1 habit of proving an ABSENCE: nothing in the
    /// engine deletes an inbox file except the prune path. This is what makes the inbox safe to hold
    /// the only copy of something the owner said — and it is the property a well-meaning future
    /// "clean up old media on startup" would silently take away.</summary>
    [Fact]
    public void Prune_is_the_only_code_in_the_engine_that_deletes_an_inbox_file()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var offenders = new List<string>();
        var deleters = new List<string>();

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)) continue;

            var lines = File.ReadAllLines(file);
            var touchesInbox = lines.Any(l => l.Contains("InboxStore", StringComparison.Ordinal)
                                           || l.Contains("InboxNote", StringComparison.Ordinal));
            if (!touchesInbox) continue;

            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("File.Delete(", StringComparison.Ordinal)
                    && !lines[i].Contains("Directory.Delete(", StringComparison.Ordinal)) continue;

                var where = Path.GetFileName(file) + ":" + (i + 1).ToString(CultureInfo.InvariantCulture)
                          + " in " + EnclosingMethod(lines, i);
                deleters.Add(where);

                // Prune deletes a note's files. TryDelete removes a TEMP file this store just wrote
                // and never a note. Anything else is a new way for a note to disappear.
                if (!where.Contains("Prune", StringComparison.Ordinal)
                    && !where.Contains("TryDelete", StringComparison.Ordinal)) offenders.Add(where);
            }
        }

        foreach (var d in deleters) _out.WriteLine(d);
        Assert.NotEmpty(deleters);   // the sweep found the deletions it is supposed to be judging
        Assert.True(offenders.Count == 0,
            "something other than prune deletes inbox files:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>And the CLI half of the same claim: exactly one place in the engine CALLS prune, and
    /// it is the verb a person types.</summary>
    [Fact]
    public void Exactly_one_verb_calls_prune()
    {
        var callers = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs",
                SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("store.Prune", StringComparison.Ordinal)
                     || File.ReadAllText(f).Contains("Sum(store.Prune", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["InboxCommand.cs"], callers);
    }

    // ────────────────────────── the rig ──────────────────────────

    private static string EnclosingMethod(string[] lines, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            var line = lines[i].TrimStart();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if ((line.Contains("void ", StringComparison.Ordinal)
                 || line.Contains("int ", StringComparison.Ordinal)
                 || line.Contains("Task", StringComparison.Ordinal)
                 || line.Contains("bool ", StringComparison.Ordinal))
                && line.Contains('(') && !line.TrimEnd().EndsWith(';')) return line.Trim();
        }
        return "(top level)";
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no repo root above " + AppContext.BaseDirectory);
    }

    private static string MediaDpath() => "media/7-audio.mp3";

    /// <summary>A file that exists, because the transcriber refuses to run a command over a path
    /// that does not — the audio being gone is a different failure and it says so.</summary>
    private string Audio(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0x4F, 0x67, 0x67, 0x53]);
        return path;
    }

    /// <summary>A transcribe command that is two lines of batch. Invoked through <c>cmd /c</c>
    /// explicitly rather than relying on how a .cmd resolves, so the test is about conductor's
    /// substitution and parsing and nothing else.</summary>
    private string Script(string name, string body)
    {
        var path = Path.Combine(_dir, name + ".cmd");
        File.WriteAllText(path, "@echo off\r\n" + body + "\r\n");
        return "cmd /c \"" + path + "\" {audio}";
    }

    /// <summary>One JSON object echoed on one line. The contract is stdout, so the fixture has to
    /// arrive there whole.</summary>
    private static string Echo(string json) =>
        "echo " + string.Join(" ", json.Split('\n', StringSplitOptions.TrimEntries));
}
