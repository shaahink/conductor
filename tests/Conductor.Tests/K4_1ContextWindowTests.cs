using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Providers;
using Conductor.Core.Store;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K4.1 — the engine measures context size per turn, not just cumulative tokens.
///
/// <para>Everything the engine recorded before this was an integral. A Karvan session reads as 7.5M
/// tokens; no turn of it ever carried more than ~160k, and the second number is the one that decides
/// where a cap belongs and why 98% of the tokens are cache reads. The measurement is
/// <c>input_tokens + cache_creation_input_tokens + cache_read_input_tokens</c> per DEDUPLICATED
/// assistant message — the prompt the API was actually handed, which is what <c>/context</c> shows.</para>
///
/// <para>The tests that matter are the ones about the two ways this goes wrong: counting a re-emitted
/// content block as a turn (which drags the mean toward a phantom), and reporting a zero where nothing
/// was measured (which would let a later prescription read a cap off no evidence at all).</para>
/// </summary>
public sealed class K4_1ContextWindowTests : IDisposable
{
    private readonly string _tmp;

    public K4_1ContextWindowTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "conductor-k41-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tmp);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_tmp)) TestTemp.DeleteTree(_tmp); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Db(string name = "run.db") => Path.Combine(_tmp, name);

    private static SqliteRunStore Open(string path) => new(path, NullLogger<SqliteRunStore>.Instance);

    /// <summary>One assistant line of a real claude stream: a message id, a content block, and the four
    /// usage fields the measurement is taken from.</summary>
    private static string Assistant(string id, long input, long cacheCreation, long cacheRead, long output) =>
        "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id +
        "\",\"content\":[{\"type\":\"text\",\"text\":\"x\"}],\"usage\":{" +
        "\"input_tokens\":" + input +
        ",\"cache_creation_input_tokens\":" + cacheCreation +
        ",\"cache_read_input_tokens\":" + cacheRead +
        ",\"output_tokens\":" + output + "}}}";

    // ------------------------------------------------------------------ the measurement itself

    [Fact]
    public void Per_turn_context_is_the_prompt_that_call_carried_not_the_running_total()
    {
        var provider = new ClaudeProvider();
        var state = new AgentStreamState((_, _) => { });

        // Three calls of a real session shape: the prefix grows, the fresh input stays small.
        provider.ParseLine(Assistant("msg_01", 119, 0, 98_791, 3), state);
        provider.ParseLine(Assistant("msg_02", 697, 0, 98_908, 2), state);
        provider.ParseLine(Assistant("msg_03", 127, 0, 99_603, 5), state);

        var ctx = state.Context;
        Assert.True(ctx.Measured);
        Assert.Equal(3, ctx.Turns);
        Assert.Equal(99_730, ctx.HighWaterTokens);                       // 127 + 99,603, the last call
        Assert.Equal((98_910 + 99_605 + 99_730) / 3, ctx.MeanTurnTokens);

        // The distinction this checkpoint exists for: the integral is far larger than any turn, and
        // says nothing about how full the window ran.
        Assert.Equal(119 + 697 + 127, state.TokensInput);
        Assert.Equal(98_791 + 98_908 + 99_603, state.TokensCacheRead);
        Assert.True(state.TokensCacheRead + state.TokensInput > ctx.HighWaterTokens * 2);
    }

    [Fact]
    public void Cache_creation_counts_toward_the_prompt_because_it_was_sent_up_too()
    {
        var provider = new ClaudeProvider();
        var state = new AgentStreamState((_, _) => { });

        // The first call of a session: nothing cached yet, a large fresh prefix being written to cache.
        provider.ParseLine(Assistant("msg_01", 2, 11_374, 12_528, 1), state);

        Assert.Equal(2 + 11_374 + 12_528, state.Context.HighWaterTokens);
    }

    [Fact]
    public void A_re_emitted_content_block_is_not_a_second_turn()
    {
        var provider = new ClaudeProvider();
        var state = new AgentStreamState((_, _) => { });

        // claude re-emits one message once per content block carrying the SAME usage. Counted naively
        // that is three turns at one context size — a mean over calls that never happened.
        var line = Assistant("msg_01", 2, 11_374, 12_528, 1);
        provider.ParseLine(line, state);
        provider.ParseLine(line, state);
        provider.ParseLine(line, state);
        provider.ParseLine(Assistant("msg_02", 40, 0, 30_000, 9), state);

        Assert.Equal(2, state.Context.Turns);
        Assert.Equal(30_040, state.Context.HighWaterTokens);            // msg_02, not the tripled msg_01
        Assert.Equal((23_904 + 30_040) / 2, state.Context.MeanTurnTokens);
    }

    [Fact]
    public void A_stream_that_reports_no_usage_measures_nothing_rather_than_zero()
    {
        var provider = new ClaudeProvider();
        var state = new AgentStreamState((_, _) => { });

        provider.ParseLine("""{"type":"assistant","message":{"id":"msg_01","content":[{"type":"text","text":"hi"}]}}""", state);
        provider.ParseLine("""{"type":"result","subtype":"success","result":"done","total_cost_usd":0.25}""", state);

        Assert.False(state.Context.Measured);
        Assert.Equal(0, state.Context.Turns);
        Assert.Equal("not measured", state.Context.Describe());
    }

    [Fact]
    public void The_result_envelope_does_not_add_a_turn()
    {
        // ReadUsage ASSIGNS the session totals off the terminal envelope rather than accumulating. If
        // that path also observed a context reading, every session would gain one phantom turn whose
        // "prompt" is the whole session's cache-read integral — the single worst way to get this wrong.
        var provider = new ClaudeProvider();
        var state = new AgentStreamState((_, _) => { });

        provider.ParseLine(Assistant("msg_01", 119, 0, 98_791, 3), state);
        provider.ParseLine(
            """{"type":"result","subtype":"success","usage":{"input_tokens":816,"cache_creation_input_tokens":0,"cache_read_input_tokens":297302,"output_tokens":10}}""",
            state);

        Assert.Equal(1, state.Context.Turns);
        Assert.Equal(98_910, state.Context.HighWaterTokens);
    }

    [Fact]
    public void Opencode_steps_are_measured_from_the_same_two_fields()
    {
        var provider = new OpencodeProvider();
        var state = new AgentStreamState((_, _) => { });

        provider.ParseLine("""{"type":"step_finish","part":{"tokens":{"input":100,"output":50,"cache":{"read":9000}},"cost":0.001}}""", state);
        provider.ParseLine("""{"type":"step_finish","part":{"tokens":{"input":40,"output":10,"cache":{"read":21000}},"cost":0.002}}""", state);

        Assert.Equal(2, state.Context.Turns);
        Assert.Equal(21_040, state.Context.HighWaterTokens);
    }

    // ------------------------------------------------------------------ the fold over the event log

    [Fact]
    public void The_same_profile_comes_back_out_of_the_persisted_deltas()
    {
        // Same three calls as the provider test, as the events a run actually stores. The fold has to
        // agree with the stream, or a finished run and a live one disagree about the same session.
        var events = new List<ConductorEvent>
        {
            new TokenDelta { SessionId = "7", Input = 119, Output = 3, CacheRead = 98_791 },
            new TokenDelta { SessionId = "7", Input = 697, Output = 2, CacheRead = 98_908 },
            new TokenDelta { SessionId = "8", Input = 400, Output = 1, CacheRead = 150_000 },
            new TokenDelta { SessionId = "7", Input = 127, Output = 5, CacheRead = 99_603 },
        };

        var seven = LiveMetrics.ContextForSession(events, 7);
        Assert.Equal(3, seven.Turns);
        Assert.Equal(99_730, seven.HighWaterTokens);
        Assert.Equal((98_910 + 99_605 + 99_730) / 3, seven.MeanTurnTokens);

        // Another session's turns are another session's context — the peak must not leak across.
        Assert.Equal(150_400, LiveMetrics.ContextForSession(events, 8).HighWaterTokens);
        Assert.False(LiveMetrics.ContextForSession(events, 9).Measured);
    }

    // ------------------------------------------------------------------ what run.db keeps

    [Fact]
    public void A_recorded_session_carries_its_context_profile_back_out_of_run_db()
    {
        var db = Db();
        using (var store = Open(db))
        {
            store.InitializeRun("run-k41-0001", "core", "C:\\repo", "feat/karvan", new EngineStamp("0.3.0", "aaaaaa", false));
            store.InitializeStage("run-k41-0001", "K4", "token truth");
            store.RecordSession("run-k41-0001", "K4", 1, "work",
                new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 5, 1, 0, 0, DateTimeKind.Utc),
                "advance", agentSessionId: null, resumeCount: 0, attempt: 1,
                gateSummary: null, resultSummary: null, commitCount: 1, newlyDone: null,
                context: new ContextWindowStats(145_774, 94_614, 80));
            // A session the provider never instrumented: three NULLs, not three zeros.
            store.RecordSession("run-k41-0001", "K4", 2, "work",
                new DateTime(2026, 8, 5, 2, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 5, 3, 0, 0, DateTimeKind.Utc),
                "advance", agentSessionId: null, resumeCount: 0, attempt: 1,
                gateSummary: null, resultSummary: null, commitCount: 0, newlyDone: null,
                context: null);
        }
        SqliteConnection.ClearAllPools();

        var sessions = RunArchive.TryOpen(db)!.Sessions("run-k41-0001");
        Assert.Equal(145_774, sessions[0].Context!.HighWaterTokens);
        Assert.Equal(94_614, sessions[0].Context!.MeanTurnTokens);
        Assert.Equal(80, sessions[0].Context!.Turns);
        Assert.Null(sessions[1].Context);
    }

    [Fact]
    public void A_run_recorded_before_v12_answers_from_its_own_event_log()
    {
        // This is the case in hand, not a hypothetical: this repo's run.db holds ~4,800 TokenDelta rows
        // written months before the context columns existed. Their history is not lost — Input (which
        // already carries cache-creation) plus CacheRead is the prompt each call re-sent — so `history`
        // reports a profile for runs that finished long before anything measured one.
        var db = Db("legacy.db");
        using (var store = Open(db))
        {
            store.InitializeRun("run-k41-0002", "core", "C:\\repo", "master", new EngineStamp("0.2.0", "bbbbbb", false));
            store.InitializeStage("run-k41-0002", "S1", "stage one");
            store.RecordSession("run-k41-0002", "S1", 3, "work",
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 7, 1, 1, 0, 0, DateTimeKind.Utc),
                "advance", agentSessionId: null, resumeCount: 0, attempt: 1,
                gateSummary: null, resultSummary: null, commitCount: 1, newlyDone: null);
            store.SetRunId("run-k41-0002");
            store.Emit(new TokenDelta { SessionId = "3", Input = 119, Output = 3, CacheRead = 98_791 });
            store.Emit(new TokenDelta { SessionId = "3", Input = 127, Output = 5, CacheRead = 99_603 });
            store.FlushEvents();
        }
        SqliteConnection.ClearAllPools();

        var session = Assert.Single(RunArchive.TryOpen(db)!.Sessions("run-k41-0002"));
        Assert.Equal(2, session.Context!.Turns);
        Assert.Equal(99_730, session.Context!.HighWaterTokens);
        Assert.Equal((98_910 + 99_730) / 2, session.Context!.MeanTurnTokens);
    }

    [Fact]
    public void A_v11_database_migrates_to_v12_and_keeps_its_sessions()
    {
        // Taken back to v11 by dropping the columns the migration adds, so the upgrade under test is
        // the shipped .sql file rather than a hand-written approximation of it (K3.3's pattern).
        var db = Db("upgrade.db");
        using (var store = Open(db))
        {
            store.InitializeRun("run-k41-0003", "core", "C:\\repo", "master", new EngineStamp("0.3.0", "aaaaaa", false));
            store.InitializeStage("run-k41-0003", "S1", "stage one");
            store.RecordSession("run-k41-0003", "S1", 1, "work",
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc), null,
                "advance", agentSessionId: null, resumeCount: 0, attempt: 1,
                gateSummary: null, resultSummary: "kept", commitCount: 1, newlyDone: null);
        }
        SqliteConnection.ClearAllPools();

        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "ALTER TABLE sessions DROP COLUMN context_high_water;" +
                "ALTER TABLE sessions DROP COLUMN context_mean_turn;" +
                "ALTER TABLE sessions DROP COLUMN context_turns;" +
                "UPDATE schema_version SET version = 11;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        using (var store = Open(db))   // opening runs the migration
            store.RecordRunEnd("run-k41-0003", "completed");
        SqliteConnection.ClearAllPools();

        var archive = RunArchive.TryOpen(db)!;
        Assert.Equal(12, SqliteRunStore.CurrentSchemaVersion);
        Assert.Equal((long)SqliteRunStore.CurrentSchemaVersion,
            Convert.ToInt64(archive.Query("SELECT version FROM schema_version")[0]["version"]));
        var session = Assert.Single(archive.Sessions("run-k41-0003"));
        Assert.Equal("kept", session.ResultSummary);
        Assert.Null(session.Context);   // the column is back; the pre-migration row never had a value
    }

    // ------------------------------------------------------------------ how it reads

    [Fact]
    public void The_profile_reads_as_a_sentence_an_operator_can_act_on()
    {
        Assert.Equal("95k mean · 135k high water · 78 turns",
            new ContextWindowStats(135_000, 95_000, 78).Describe());
        Assert.Equal("not measured", ContextWindowStats.None.Describe());
    }
}
