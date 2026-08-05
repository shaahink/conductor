using Conductor.Http;
using System.Text.Json;
using Conductor.Core;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Providers;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// K1.3 (untruth 1) — <c>costs.tokens_think</c> is 0 on all 125 rows this project has ever written,
/// and a permanent 0 in a money column is a claim: "no thinking happened". It is not one conductor
/// can make. Every one of those rows came from claude, whose <c>usage</c> object has no thinking
/// field at all (<c>ClaudeProvider.ReadUsage</c> leaves reasoning unset on purpose, because that
/// spend is already inside <c>output_tokens</c>).
/// <para>The column was LABELLED rather than dropped, and this file is the measurement behind that
/// decision: <c>OpencodeProvider</c> really does fold <c>tokens.reasoning</c>, so for that backend
/// the number is a fact. The wire therefore carries <c>null</c> — not 0 — for a run whose provider
/// has no such concept, and the real count for one that does.</para>
/// </summary>
public sealed class K1_3ThinkingTokensTests : IDisposable
{
    private const string RunId = "run-k13-think";
    private readonly string _dir;
    private readonly SqliteRunStore _store;
    private readonly System.Collections.Concurrent.ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();

    public K1_3ThinkingTokensTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "conductor-k13-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_dir, ".conductor"));
        _store = new SqliteRunStore(Path.Combine(_dir, ".conductor", "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId(RunId);
        _store.InitializeRun(RunId, "k13", _dir, null, null);
        _store.RecordSession(RunId, "K1", 1, "Deliver", DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow,
            "Delivered", agentSessionId: null, resumeCount: 0, attempt: 1,
            gateSummary: "gates ok", resultSummary: "did the thing", commitCount: 1, newlyDone: null);
    }

    public void Dispose()
    {
        _http.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temp dir */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>The capability is the adapter's own answer, not a lookup table a surface keeps in
    /// parallel — measured against the parse behaviour each provider actually has.</summary>
    [Fact]
    public void OnlyTheProviderThatParsesReasoning_ClaimsToReportIt()
    {
        Assert.False(new ClaudeProvider().ReportsReasoningTokens);
        Assert.False(new GenericTextProvider().ReportsReasoningTokens);
        Assert.True(new OpencodeProvider().ReportsReasoningTokens);

        // And the plan-level question resolves through the same inference the run uses to pick a
        // parser (B2.4): most plans leave `provider` unset and are inferred from `output`.
        Assert.False(AgentProviderFactory.ReportsReasoningTokens(new AgentConfig { Output = "stream-json" }));
        Assert.True(AgentProviderFactory.ReportsReasoningTokens(new AgentConfig { Output = "opencode-json" }));
        Assert.True(AgentProviderFactory.ReportsReasoningTokens(new AgentConfig { Provider = " OpenCode " }));
    }

    /// <summary>OpencodeProvider is the reason the column survives: it folds a real
    /// <c>tokens.reasoning</c> off <c>step_finish</c>. If this ever stops being true the honest move
    /// is to drop the column, so the claim is pinned rather than assumed.</summary>
    [Fact]
    public void OpencodeStream_FoldsAReasoningCount()
    {
        var state = new AgentStreamState((_, _) => { });
        new OpencodeProvider().ParseLine(
            """{"type":"step_finish","tokens":{"input":10,"output":20,"reasoning":37,"cache":{"read":5}}}""",
            state);
        Assert.Equal(37, state.TokensReasoning);
    }

    /// <summary>The wire, over a real socket. A claude run serves <c>null</c> — JSON null, not 0 —
    /// even though the stored column holds 0, because 0 there means "nothing was ever written",
    /// which is exactly what the surfaces were reading as a measurement.</summary>
    [Fact]
    public async Task ClaudeRun_ServesNullThinkTokens_NotZero()
    {
        _store.RecordCost(RunId, 1, "agent", tokensIn: 41213, tokensOut: 3187,
            tokensThink: 0, tokensCache: 188420, costUsd: 0.12m, wallMs: 1000);

        var row = await SessionRow(new AgentConfig { Output = "stream-json" });

        Assert.Equal(JsonValueKind.Null, row.GetProperty("tokensThink").ValueKind);
        // The columns that ARE measurable for this provider still carry their numbers, so a null
        // think cell reads as "not applicable", not as "this endpoint is broken".
        Assert.Equal(41213, row.GetProperty("tokensIn").GetInt64());
        Assert.Equal(3187, row.GetProperty("tokensOut").GetInt64());
        Assert.Equal(188420, row.GetProperty("tokensCache").GetInt64());
    }

    /// <summary>An opencode run serves the number — including a genuine 0, which for that provider
    /// IS a measurement. This is the half that makes dropping the column the wrong fix.</summary>
    [Fact]
    public async Task OpencodeRun_ServesTheRealThinkCount()
    {
        _store.RecordCost(RunId, 1, "agent", tokensIn: 100, tokensOut: 200,
            tokensThink: 2310, tokensCache: 0, costUsd: 0.01m, wallMs: 1000);

        var row = await SessionRow(new AgentConfig { Output = "opencode-json" });

        Assert.Equal(JsonValueKind.Number, row.GetProperty("tokensThink").ValueKind);
        Assert.Equal(2310, row.GetProperty("tokensThink").GetInt64());
    }

    // ---------------------------------------------------------------- helpers

    private async Task<JsonElement> SessionRow(AgentConfig agent)
    {
        var plan = new PlanConfig
        {
            Name = "k13",
            Repo = _dir,
            Tracker = "TRACKER.md",
            Agent = agent,
            Stages = { new StageConfig { Id = "K1", Title = "Stage One", Sessions = 1 } },
        };
        var server = new ControlPlaneServer(plan, new RunState { RunId = RunId, CurrentStage = "K1" },
            _store, _inbox, new NoOpTelegramService(), NullLogger.Instance, FreeLoopbackPort());
        Assert.True(server.Start(), "control plane failed to bind");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/sessions");
            req.Headers.Add("X-Conductor-Token", server.Token);
            using var resp = await _http.SendAsync(req);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("sessions").EnumerateArray().Single().Clone();
        }
        finally { server.Dispose(); }
    }

    private static int FreeLoopbackPort()
    {
        using var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((System.Net.IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }
}
