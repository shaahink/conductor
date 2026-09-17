using System.Globalization;

using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Courier;

namespace Conductor.Tests;

/// <summary>PK2.1 / D2(a,b) - the courier's heartbeat, the four-word liveness a status prints from it,
/// and the death record a new courier journals when its predecessor left a record behind. Pure seams
/// and a scratch state home; the scheduler is a recorded runner, never the machine's.</summary>
public sealed class PK2_1CourierHeartbeatTests : IDisposable
{
    private static readonly DateTimeOffset Started = new(2026, 9, 17, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StaleAfter = CourierVitals.StaleAfter(4);

    private readonly string _home = Path.Combine(Path.GetTempPath(), "pk21-" + Guid.NewGuid().ToString("N"));

    public PK2_1CourierHeartbeatTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CourierPresence Record(DateTimeOffset? lastPoll = null, string? task = "pk21-scratch") =>
        new(CourierProtocol.Version, 4242, "0.5.1", @"C:\scratch\conductor-courier.exe", task, Started, 47001, lastPoll);

    // ── the heartbeat on disk ──────────────────────────────────────────────────────────────

    [Fact]
    public void ABeatIsWrittenDownAndReadBack()
    {
        var record = Record();
        record.Write(_home);
        Assert.Null(CourierPresence.Read(_home)!.LastPollUtc);

        var polled = Started.AddMinutes(3);
        var served = record.Beat(polled, _home);

        Assert.Equal(polled, served.LastPollUtc);
        Assert.Equal(served, CourierPresence.Read(_home));
        Assert.Contains("\"lastPollUtc\"", File.ReadAllText(CourierHome.PresencePathFor(_home)), StringComparison.Ordinal);
    }

    [Fact]
    public void ARecordFromBeforeTheHeartbeatStillReads()
    {
        Directory.CreateDirectory(CourierHome.DirFor(_home));
        File.WriteAllText(CourierHome.PresencePathFor(_home),
            "{ \"protocol\": 2, \"pid\": 15, \"engine\": \"0.0.0.0\", \"startedUtc\": \"2026-09-16T19:48:00+00:00\" }");

        var read = CourierPresence.Read(_home);
        Assert.NotNull(read);
        Assert.Null(read!.LastPollUtc);
        Assert.Equal(15, read.Pid);
    }

    [Fact]
    public void Bug97_ThePresenceNamesTheBuildNotTheAssemblyVersion()
    {
        var engine = CourierPresence.Current().Engine;
        Assert.Equal(EngineStamp.Current.Full, engine);
        Assert.DoesNotMatch(@"^\d+\.0\.0\.0$", engine ?? "");
    }

    [Fact]
    public async Task TheLoopBeatsEveryTimeItComesRound_FailedPollsIncluded()
    {
        var source = new ScriptedSource(failEvery: 2);
        var beats = new List<DateTimeOffset>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var daemon = new CourierDaemon(source, new CourierSettings { PollIntervalSeconds = 1 }, _home,
            beat: t => { beats.Add(t); if (beats.Count == 3) stop.Cancel(); });

        await daemon.RunAsync(stop.Token);

        Assert.Equal(3, beats.Count);
        Assert.True(source.Polls >= 3, "polls " + source.Polls.ToString(CultureInfo.InvariantCulture));
        Assert.True(source.Failures >= 1, "the scripted source never failed, so the failure path went unproven");
        Assert.True(beats.Zip(beats.Skip(1)).All(p => p.Second >= p.First), "beats went backwards");
    }

    [Fact]
    public async Task AHeartbeatThatCannotBeWrittenDoesNotStopTheLoop()
    {
        var source = new ScriptedSource(failEvery: 0);
        var said = new List<string>();
        var attempts = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var daemon = new CourierDaemon(source, new CourierSettings { PollIntervalSeconds = 1 }, _home, log: said.Add,
            beat: _ => { if (++attempts == 3) stop.Cancel(); throw new IOException("disk said no"); });

        await daemon.RunAsync(stop.Token);

        Assert.Equal(3, attempts);
        Assert.Single(said, l => l.Contains("heartbeat not written", StringComparison.Ordinal));
    }

    // ── the four words ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void LivenessFollowsThePollAge_AtEveryAge()
    {
        // A property over the age, not three examples: the threshold is the only thing that decides.
        for (var seconds = 0; seconds <= StaleAfter.TotalSeconds * 3; seconds += 7)
        {
            var polled = Started.AddMinutes(1);
            var now = polled.AddSeconds(seconds);
            var record = Record(polled);

            var live = CourierVitals.Of(record, record, StaleAfter, now);
            var expected = TimeSpan.FromSeconds(seconds) > StaleAfter ? CourierLife.Stale : CourierLife.Alive;
            Assert.Equal(expected, live.Life);
            Assert.Equal(polled, live.LastSeenUtc);

            var gone = CourierVitals.Of(record, live: null, StaleAfter, now);
            Assert.Equal(CourierLife.Dead, gone.Life);
            Assert.Equal(polled, gone.LastSeenUtc);
        }
    }

    [Fact]
    public void NoRecordIsAbsent_AndADeadRecordWithoutAPollIsLastSeenAtItsStart()
    {
        Assert.Equal(CourierLife.Absent, CourierVitals.Of(null, null, StaleAfter, Started).Life);

        var dead = CourierVitals.Of(Record(), live: null, StaleAfter, Started.AddHours(7));
        Assert.Equal(CourierLife.Dead, dead.Life);
        Assert.Equal(Started, dead.LastSeenUtc);
    }

    [Fact]
    public void ALivePidWithNoHeartbeatIsInItsFirstPoll_ThenUnmetered()
    {
        var record = Record(lastPoll: null);
        Assert.Equal(CourierLife.Alive, CourierVitals.Of(record, record, StaleAfter, Started.AddSeconds(20)).Life);

        var old = CourierVitals.Of(record, record, StaleAfter, Started.AddHours(2));
        Assert.Equal(CourierLife.Unmetered, old.Life);
        Assert.Contains("writes no heartbeat", old.Describe(Started.AddHours(2)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWordsAreTheOnesTheSpecNames()
    {
        var polled = Started.AddMinutes(1);
        var record = Record(polled);

        Assert.StartsWith("alive (last poll 30 s ago",
            CourierVitals.Of(record, record, StaleAfter, polled.AddSeconds(30)).Describe(polled.AddSeconds(30)), StringComparison.Ordinal);
        Assert.StartsWith("stale (last poll 7 min ago",
            CourierVitals.Of(record, record, StaleAfter, polled.AddMinutes(7)).Describe(polled.AddMinutes(7)), StringComparison.Ordinal);
        Assert.StartsWith("dead (last seen 2026-09-17 10:01:00Z, 3 h 5 min ago",
            CourierVitals.Of(record, null, StaleAfter, polled.AddMinutes(185)).Describe(polled.AddMinutes(185)), StringComparison.Ordinal);
        Assert.StartsWith("absent", CourierVitals.Of(null, null, StaleAfter, polled).Describe(polled), StringComparison.Ordinal);

        foreach (var life in Enum.GetValues<CourierLife>())
            Assert.Equal(life.ToString().ToLowerInvariant(), new CourierVitals(life, record, polled).Word);
    }

    [Fact]
    public void StaleNeverFiresInsideOneHealthyIteration_AtAnyInterval()
    {
        // The longest healthy gap between beats: a request that hangs to the transport ceiling, then
        // the longer of the interval and the conflict backoff. Stale must sit beyond it at every interval.
        foreach (var interval in new[] { 1, 2, 4, 30, 59, 60, 61, 300, 3600 })
        {
            var longest = CourierVitals.RequestCeilingSeconds + Math.Max(interval, CourierVitals.BackoffCeilingSeconds);
            Assert.True(CourierVitals.StaleAfter(interval).TotalSeconds > longest,
                $"interval {interval.ToString(CultureInfo.InvariantCulture)}");
            Assert.True(CourierDaemon.ConflictBackoff(1000).TotalSeconds <= CourierVitals.BackoffCeilingSeconds);
        }
    }

    [Fact]
    public void AStaleRecordReadFromDiskWithALivePidIsStale()
    {
        Record(Started.AddMinutes(1)).Write(_home);
        var vitals = CourierVitals.Read(StaleAfter, Started.AddMinutes(30), _home, probe: _ => Started);
        Assert.Equal(CourierLife.Stale, vitals.Life);
        Assert.Equal(CourierLife.Dead, CourierVitals.Read(StaleAfter, Started.AddMinutes(30), _home, probe: _ => null).Life);
    }

    // ── what the scheduler remembers ──────────────────────────────────────────────────────

    /// <summary>The shape measured on the owner's machine on 2026-09-17 (host name replaced).</summary>
    private const string MeasuredVerbose =
        "\"HOST\",\"\\Conductor Courier\",\"N/A\",\"Running\",\"Interactive only\",\"17/09/2026 10:30:42\",\"267009\","
      + "\"conductor\",\"C:\\conductor\\conductor.exe courier run\",\"N/A\",\"conductor courier - one bot, always awake, "
      + "outliving the run. Started at logon; restarts on failure.\",\"Enabled\"\r\n";

    [Fact]
    public void TheVerboseQueryIsReadByPosition()
    {
        var run = CourierTask.VerboseLastRun(MeasuredVerbose);
        Assert.NotNull(run);
        Assert.Equal("17/09/2026 10:30:42", run!.LastRunTime);
        Assert.Equal(CourierTaskRun.Running, run.LastResult);
        Assert.StartsWith("267009 (0x00041301), running now", run.Describe(), StringComparison.Ordinal);

        // One row per trigger: the first answers.
        Assert.Equal(1, CourierTask.VerboseLastRun(MeasuredVerbose.Replace("267009", "1", StringComparison.Ordinal) + MeasuredVerbose)!.LastResult);
        Assert.Equal("-1073741510 (0xC000013A) at N/A",
            new CourierTaskRun("N/A", -1073741510).Describe());
        Assert.Null(CourierTask.VerboseLastRun("ERROR: The system cannot find the file specified.\r\n"));
    }

    [Fact]
    public async Task LastRunAsksTheSchedulerVerbosely_AndSaysWhyWhenItCannot()
    {
        var asked = new List<string>();
        var task = new CourierTask("pk21-scratch", (exe, args) =>
        {
            asked.Add(exe + " " + args);
            return Task.FromResult(new ShellResult(0, MeasuredVerbose.Replace("267009", "3", StringComparison.Ordinal), ""));
        });
        var (run, unread) = await task.LastRunAsync();
        Assert.Equal(3, run!.LastResult);
        Assert.Null(unread);
        Assert.Equal("schtasks.exe /Query /TN \"pk21-scratch\" /V /FO CSV /NH", Assert.Single(asked));

        var missing = new CourierTask("pk21-missing", (_, _) =>
            Task.FromResult(new ShellResult(1, "", "ERROR: The system cannot find the file specified.")));
        var (none, why) = await missing.LastRunAsync();
        Assert.Null(none);
        Assert.Contains("cannot find", why, StringComparison.Ordinal);
    }

    // ── the death record ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDeathRecordIsTheSpecsSentence()
    {
        Assert.Equal(
            "previous courier pid 4242 died silently; last poll 2026-09-17 10:04:00Z; "
          + "task last-run result 1 (0x00000001) at 17/09/2026 10:30:42 [task pk21-scratch]",
            CourierVitals.DeathRecord(Record(Started.AddMinutes(4)), "pk21-scratch", new CourierTaskRun("17/09/2026 10:30:42", 1)));

        Assert.Equal(
            "previous courier pid 4242 died silently; last poll none recorded (started 2026-09-17 10:00:00Z); "
          + "task last-run result none (no task started it)",
            CourierVitals.DeathRecord(Record(task: null), null, null));
    }

    [Fact]
    public async Task StartupFindsNothingToSayAfterACleanStop()
    {
        Assert.Null(await CourierProgram.DeathRecordAsync(_home, "pk21-scratch",
            _ => throw new InvalidOperationException("a clean stop must not ask the scheduler")));
    }

    [Fact]
    public async Task StartupJournalsTheDeathWithTheDeadCouriersTaskResult()
    {
        Record(Started.AddMinutes(4), task: "pk21-dead-task").Write(_home);
        string? askedFor = null;

        var line = await CourierProgram.DeathRecordAsync(_home, "pk21-this-task", name =>
        {
            askedFor = name;
            return new CourierTask(name, (_, _) =>
                Task.FromResult(new ShellResult(0, MeasuredVerbose.Replace("267009", "0", StringComparison.Ordinal), "")));
        });

        Assert.Equal("pk21-dead-task", askedFor);
        Assert.Equal("previous courier pid 4242 died silently; last poll 2026-09-17 10:04:00Z; "
                   + "task last-run result 0 (0x00000000) at 17/09/2026 10:30:42 [task pk21-dead-task]", line);
    }

    [Fact]
    public async Task AHandStartedPredecessorIsAskedAboutUnderThisCouriersTask_OrNotAtAll()
    {
        Record(Started.AddMinutes(4), task: null).Write(_home);

        var unread = await CourierProgram.DeathRecordAsync(_home, "pk21-this-task", name =>
            new CourierTask(name, (_, _) => Task.FromResult(new ShellResult(1, "", "ERROR: no such task"))));
        Assert.EndsWith("task last-run result unread [task pk21-this-task]: ERROR: no such task", unread, StringComparison.Ordinal);

        var byHand = await CourierProgram.DeathRecordAsync(_home, taskName: null,
            _ => throw new InvalidOperationException("no task to ask"));
        Assert.EndsWith("task last-run result none (no task started it)", byHand, StringComparison.Ordinal);
    }

    /// <summary>A source whose every <paramref name="failEvery"/>th poll throws; zero never throws.</summary>
    private sealed class ScriptedSource(int failEvery) : ICourierSource
    {
        public int Polls { get; private set; }
        public int Failures { get; private set; }

        public string Describe => "scripted";

        public Task<IReadOnlyList<CourierDelivery>> FetchAsync(long offset, CancellationToken ct)
        {
            Polls++;
            if (failEvery > 0 && Polls % failEvery == 0)
            {
                Failures++;
                throw new HttpRequestException("scripted network error");
            }
            return Task.FromResult<IReadOnlyList<CourierDelivery>>([]);
        }

        public Task ReplyAsync(string chatId, string text, long? threadId, CancellationToken ct,
            IReadOnlyList<CourierButton>? buttons = null) => Task.CompletedTask;

        public Task<string?> SendAsync(CourierPush push, CancellationToken ct) => Task.FromResult<string?>(null);
    }
}
