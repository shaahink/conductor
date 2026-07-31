using Conductor.Core;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SC5.4 — `bg` maps cleanly (round-four #4).
///
/// <para>Two defects with one shared cause each, both measured here against a REAL run.db rather
/// than read off the source. (a) `bg status` lists the live agent, so `bg logs &lt;that pid&gt;` is
/// the obvious way to watch a session — and it answered "No log file found" plus 67 unrelated bg log
/// names, because an agent's output never goes to <c>bg-logs/</c>. (b) That same row's Runtime column
/// read <c>-1694s</c> for a process that had been alive half an hour.</para>
///
/// <para>The runtime tests are the ones that matter most, because the bug is invisible from the
/// machine it was written on: <see cref="SqliteRunStore.GetAllPids"/> parsed a <c>…Z</c> stamp with a
/// bare <c>DateTime.Parse</c>, which converts to LOCAL and returns <see cref="DateTimeKind.Local"/>.
/// East of UTC that yields a negative runtime; WEST of UTC it makes
/// <see cref="PidLiveness.Check"/> call every live tracked process <see cref="PidState.Recycled"/>.
/// So the assertions pin the Kind and the exact instant, which are true in every timezone, and not
/// just the sign of a subtraction, which is only true in some.</para>
/// </summary>
public sealed class SC54BgMappingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-sc54-{Guid.NewGuid():N}");
    private readonly SqliteRunStore _store;
    private const string RunId = "run-sc54";

    public SC54BgMappingTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "logs"));
        Directory.CreateDirectory(Path.Combine(_dir, "bg-logs"));
        _store = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.InitializeRun(RunId, "sc54", _dir, "feat/sarban", "1.0");
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- one timezone

    /// <summary>The root cause, pinned where it lives. A pids row goes in as a UTC instant and has to
    /// come back as the SAME instant, stamped UTC — not as the same wall-clock digits reinterpreted
    /// in whatever zone the reader happens to sit in.</summary>
    [Fact]
    public void StartedUtc_RoundTripsAsUtc_NotLocal()
    {
        var started = new DateTime(2026, 7, 30, 23, 42, 17, DateTimeKind.Utc);
        _store.TrackPid(4242, RunId, "bg:dotnet", "SC5", 21, started);

        var row = _store.GetAllPids(RunId).Single(p => p.Pid == 4242);

        Assert.Equal(DateTimeKind.Utc, row.StartedUtc.Kind);
        Assert.Equal(started, row.StartedUtc);
    }

    /// <summary>Same for the exit stamp — the two are subtracted against each other for an exited
    /// row's runtime, so a skew in either is a wrong duration.</summary>
    [Fact]
    public void ExitedUtc_RoundTripsAsUtc_AndYieldsTheRealDuration()
    {
        var started = DateTime.UtcNow.AddMinutes(-7);
        _store.TrackPid(4243, RunId, "bg:dotnet", "SC5", 21, started);
        _store.MarkPidExited(4243, 0);

        var row = _store.GetAllPids(RunId).Single(p => p.Pid == 4243);

        Assert.NotNull(row.ExitedUtc);
        Assert.Equal(DateTimeKind.Utc, row.ExitedUtc!.Value.Kind);
        var duration = row.ExitedUtc.Value - row.StartedUtc;
        Assert.InRange(duration.TotalMinutes, 6.5, 7.5);
    }

    /// <summary>The reported symptom: a live row's runtime is <c>UtcNow - StartedUtc</c>, which is
    /// what `bg status` and MCP `bg_status` both print. It must be the elapsed time, and in
    /// particular it must not be negative.</summary>
    [Fact]
    public void LiveRowRuntime_IsElapsedTime_NotNegative()
    {
        var started = DateTime.UtcNow.AddSeconds(-90);
        _store.TrackPid(4244, RunId, "bg:tests", "SC5", 21, started);

        var row = _store.GetAllPids(RunId).Single(p => p.Pid == 4244);
        var runtime = DateTime.UtcNow - row.StartedUtc;

        Assert.True(runtime > TimeSpan.Zero, $"live runtime was {runtime} — a negative age is round-four #4");
        Assert.InRange(runtime.TotalSeconds, 85, 120);
        Assert.Equal("1m 30s", BgStatusRuntime(row));
    }

    /// <summary>The other half of the same skew, and the expensive one: a pid tracked a moment ago
    /// must still read as ours after a round trip through run.db. West of UTC the stored start read
    /// EARLIER than the OS's real start, so <see cref="PidLiveness.Check"/> declared every live
    /// tracked process recycled — `bg status` prints dead, <see cref="PidLiveness.Sweep"/> buries it,
    /// and SC4.1's battery settle stops waiting for the session's own bg children.</summary>
    [Fact]
    public void TrackedPid_StillReadsAsOurs_AfterAStoreRoundTrip()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        _store.TrackPid(self.Id, RunId, "bg:self", "SC5", 21, DateTime.UtcNow);

        var row = _store.GetAllPids(RunId).Single(p => p.Pid == self.Id);

        Assert.Equal(PidState.Ours, PidLiveness.Check(row.Pid, row.StartedUtc));
        Assert.True(PidLiveness.LooksAlive(row.Pid, row.StartedUtc));
    }

    /// <summary>The bg-log name is reconstructed from the row's start instant, so it is the one place
    /// that was CORRECT before the fix (it called <c>ToUniversalTime()</c> on the local-kind value and
    /// got back the right instant). It has to stay correct now that the value is already UTC.</summary>
    [Fact]
    public void BgLogResolve_StillFindsTheChildLog_AfterTheParseFix()
    {
        var started = DateTime.UtcNow.AddMinutes(-3);
        var logDir = Path.Combine(_dir, "bg-logs");
        var name = BgLogs.NameFor("dotnet", started);
        File.WriteAllText(Path.Combine(logDir, name), "building...\n");
        _store.TrackPid(4245, RunId, "bg:dotnet", "SC5", 21, started);

        var resolved = BgLogs.Resolve(logDir, 4245, _store, RunId);

        Assert.NotNull(resolved);
        Assert.Equal(name, Path.GetFileName(resolved));
    }

    // ---------------------------------------------------------------- bg logs on an agent row

    /// <summary>An agent row written by today's engine carries its session number in the column.</summary>
    [Fact]
    public void AgentRow_ResolvesToItsSessionStream()
    {
        var stream = Path.Combine(_dir, "logs", "session-014.jsonl");
        File.WriteAllText(stream, "{\"type\":\"system\",\"subtype\":\"init\"}\n");
        _store.TrackPid(30244, RunId, "agent:SC5:session#14", "SC5", 14, DateTime.UtcNow);

        var row = _store.GetAllPids(RunId).Single(p => p.Pid == 30244);

        Assert.True(BgLogs.IsAgentRow(row));
        Assert.Equal(14, BgLogs.SessionNumberFor(row));
        Assert.Equal(stream, BgLogs.ResolveAgentStream(_dir, row));
    }

    /// <summary>Every agent row already in a run.db has stage_id AND session_number NULL — that is
    /// what this repo's own run.db holds for all 14 of its sessions. The `session#N` tail of the
    /// purpose is what keeps those rows resolvable, so it is not decoration.</summary>
    [Fact]
    public void LegacyAgentRow_WithNullColumns_StillResolvesFromItsPurpose()
    {
        var stream = Path.Combine(_dir, "logs", "session-002.jsonl");
        File.WriteAllText(stream, "{\"type\":\"system\",\"subtype\":\"init\"}\n");
        _store.TrackPid(18220, RunId, "agent:stage:2:session#2", null, null, DateTime.UtcNow);

        var row = _store.GetAllPids(RunId).Single(p => p.Pid == 18220);

        Assert.Null(row.SessionNumber);
        Assert.Equal(2, BgLogs.SessionNumberFor(row));
        Assert.Equal(stream, BgLogs.ResolveAgentStream(_dir, row));
    }

    /// <summary>A bg child also carries a session number (SC4.1 stamps it), and it must never be
    /// mistaken for the session itself — `bg logs` on it still means the child's own log.</summary>
    [Fact]
    public void BgChildRow_IsNotAnAgentRow_EvenThoughItCarriesASessionNumber()
    {
        _store.TrackPid(4246, RunId, "bg:dotnet", "SC5", 14, DateTime.UtcNow);

        var row = _store.GetAllPids(RunId).Single(p => p.Pid == 4246);

        Assert.False(BgLogs.IsAgentRow(row));
        Assert.Null(BgLogs.SessionNumberFor(row));
        Assert.Null(BgLogs.ResolveAgentStream(_dir, row));
    }

    /// <summary>No stream on disk is a different answer from no session — the caller has to be able
    /// to say which, so it can name the path it expected.</summary>
    [Fact]
    public void AgentRow_WithNoStreamOnDisk_ResolvesToNull_ButKeepsItsNumber()
    {
        _store.TrackPid(30245, RunId, "agent:SC5:session#99", "SC5", 99, DateTime.UtcNow);

        var row = _store.GetAllPids(RunId).Single(p => p.Pid == 30245);

        Assert.Equal(99, BgLogs.SessionNumberFor(row));
        Assert.Null(BgLogs.ResolveAgentStream(_dir, row));
    }

    // ---------------------------------------------------------------- the stream reads like the feed

    /// <summary>Raw NDJSON is not an answer to "what is this session doing" — one claude envelope is
    /// a whole assistant message. The tail is folded by the plan's own provider, so it reads like the
    /// live feed and cannot drift from it.</summary>
    [Fact]
    public void StreamTail_FoldsClaudeEnvelopesToOneLineEach()
    {
        var stream = Path.Combine(_dir, "logs", "session-007.jsonl");
        File.WriteAllLines(stream,
        [
            """{"type":"system","subtype":"init"}""",
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Reading BgLogs.cs"}]}}""",
            """{"type":"assistant","message":{"id":"m2","content":[{"type":"tool_use","name":"Edit","input":{"file":"BgLogs.cs"}}]}}""",
        ]);

        var lines = SessionStreamTail.Render(stream, new ClaudeProvider(), 30);

        Assert.Contains(lines, l => l.StartsWith("system", StringComparison.Ordinal) && l.Contains("init", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("text", StringComparison.Ordinal) && l.Contains("Reading BgLogs.cs", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("tool", StringComparison.Ordinal) && l.Contains("Edit", StringComparison.Ordinal));
        Assert.All(lines, l => Assert.DoesNotContain('\n', l));
    }

    /// <summary>The stream is being appended to by a live agent while this reads it, and its stderr
    /// tee is not JSON. Neither may blank the tail.</summary>
    [Fact]
    public void StreamTail_SurvivesStderrLines_AndAnOpenWriter()
    {
        var stream = Path.Combine(_dir, "logs", "session-008.jsonl");
        File.WriteAllLines(stream,
        [
            "[stderr] warning: something on the wire",
            """{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"still working"}]}}""",
        ]);

        // The writer the agent holds open for append, in the share mode AgentSession uses.
        using var writer = new FileStream(stream, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);

        var lines = SessionStreamTail.Render(stream, new ClaudeProvider(), 30);

        Assert.Contains(lines, l => l.Contains("[stderr] warning", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("still working", StringComparison.Ordinal));
    }

    /// <summary>The tail bound is on DISPLAY lines, and it is the last ones — the point is the end of
    /// the stream, not the beginning.</summary>
    [Fact]
    public void StreamTail_KeepsTheLastNLines()
    {
        var stream = Path.Combine(_dir, "logs", "session-009.jsonl");
        var envelope = """{"type":"assistant","message":{"id":"mN","content":[{"type":"text","text":"step N"}]}}""";
        File.WriteAllLines(stream, Enumerable.Range(1, 50).Select(i =>
            envelope.Replace("N", i.ToString(), StringComparison.Ordinal)));

        var lines = SessionStreamTail.Render(stream, new ClaudeProvider(), 5);

        Assert.Equal(5, lines.Count);
        Assert.Contains("step 50", lines[^1], StringComparison.Ordinal);
        Assert.Contains("step 46", lines[0], StringComparison.Ordinal);
    }

    /// <summary>The same duration formatter `bg status` prints, so the assertion above is about the
    /// column an operator reads and not about a TimeSpan nobody sees.</summary>
    private static string BgStatusRuntime(PidRow row) =>
        Conductor.Commands.BgStatusHandler.FormatDuration(DateTime.UtcNow - row.StartedUtc);
}
