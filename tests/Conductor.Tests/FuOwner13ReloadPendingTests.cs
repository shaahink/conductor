using Conductor.Http;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Conductor.Core;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// FU-OWNER-13 regression, measured over the real wire. Wiring Telegram into a live run, the owner
/// saw <c>POST /plan/edit</c> return <c>ok:true</c> with the telegram block on disk, and then
/// <c>POST /telegram/token</c> answer "saved, but this run still will not deliver: not configured —
/// add a telegram block to the plan" — advising the edit that had been accepted seconds earlier and
/// instructing a no-op. <c>GET /telegram/status</c> said the same. Both read the live in-memory
/// <see cref="PlanConfig"/>, which by design is not mutated on the HTTP path; the reload is queued
/// and applied at the run loop's next session boundary.
///
/// The behaviour is right and the sentence was wrong, so these tests are about the sentence — and
/// about the flag the Face needs to tell *waiting* from *unconfigured*, which are otherwise
/// byte-identical in the status payload. This is the SC1.3 failure one layer out: a saved thing
/// reporting as if nothing were saved.
/// </summary>
public sealed class FuOwner13ReloadPendingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-fu13-{Guid.NewGuid():N}");
    private readonly string _planPath;
    private readonly PlanConfig _plan;
    private readonly SqliteRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public FuOwner13ReloadPendingTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".conductor"));
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T");
        _store = new SqliteRunStore(Path.Combine(_dir, ".conductor", "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.SetRunId("run-fu13");

        _planPath = Path.Combine(_dir, "test.plan.json");
        // Deliberately NO telegram block: this is the state the owner was in before the edit, and
        // the state the live plan stays in until the loop's boundary after it.
        var seed = new PlanConfig
        {
            Name = "fu13-plan",
            Repo = _dir.Replace("\\", "/"),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [new StageConfig { Id = "S1", Title = "Stage One", Sessions = 1 }],
        };
        File.WriteAllText(_planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _plan = PlanConfig.Load(_planPath);
    }

    public void Dispose()
    {
        foreach (var s in _services) { try { s.Dispose(); } catch (Exception) { /* best effort */ } }
        _http.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>A live <see cref="TelegramService"/> over the block-less plan — the state the owner's
    /// engine was actually in. It is never started, so there is no poll loop to stop; the token
    /// endpoint's ReloadAsync path is what these tests need it for, and that is the path that
    /// produced the reported sentence.</summary>
    private TelegramService LiveService()
    {
        var svc = new TelegramService(_plan, new RunState { RunId = "run-fu13" }, NullLogger<TelegramService>.Instance);
        _services.Add(svc);
        return svc;
    }

    private readonly List<TelegramService> _services = new();

    private (ControlPlaneServer server, int port) StartServer(ITelegramService? telegram = null)
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var probed = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var server = new ControlPlaneServer(_plan, new RunState { RunId = "run-fu13" }, _store, _inbox,
            telegram ?? new NoOpTelegramService(), NullLogger.Instance, probed);
        Assert.True(server.Start(), "control plane failed to bind");
        _http.DefaultRequestHeaders.Remove("X-Conductor-Token");
        _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
        return (server, server.Port);
    }

    private async Task<JsonElement> StatusAsync(int port)
    {
        var body = await _http.GetStringAsync($"http://127.0.0.1:{port}/telegram/status");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    /// <summary>The exact edit the owner made: a telegram block arrives through /plan/edit's
    /// telegram target, which is what creates the block when there is none.</summary>
    private async Task<HttpStatusCode> AddTelegramBlockAsync(int port)
    {
        var req = new PlanEditRequestDto([new PlanEditDto("telegram", "", "allowedchatids", "515151")]);
        using var content = new StringContent(
            JsonSerializer.Serialize(req, ControlPlaneJsonContext.Default.PlanEditRequestDto),
            Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"http://127.0.0.1:{port}/plan/edit", content);
        return resp.StatusCode;
    }

    private async Task<JsonElement> PostTokenAsync(int port)
    {
        var req = new TelegramSetTokenRequestDto("fu13-test-token");
        using var content = new StringContent(
            JsonSerializer.Serialize(req, ControlPlaneJsonContext.Default.TelegramSetTokenRequestDto),
            Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"http://127.0.0.1:{port}/telegram/token", content);
        var body = await resp.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : null;

    // ── the reported failure ────────────────────────────────────────────────────────────────────

    /// <summary>Before the edit the old sentence is the RIGHT one and must be left alone; after it,
    /// status stops telling the owner to do the thing it just accepted.</summary>
    [Fact]
    public async Task TelegramStatus_StopsSayingAddATelegramBlock_OnceTheEditAddingOneIsQueued()
    {
        var (server, port) = StartServer();
        try
        {
            var before = await StatusAsync(port);
            Assert.False(before.GetProperty("reloadPending").GetBoolean());
            Assert.Equal(TelegramReadiness.NoBlock, Str(before, "willDeliverReason"));

            Assert.Equal(HttpStatusCode.Accepted, await AddTelegramBlockAsync(port));

            var after = await StatusAsync(port);
            // The flag the Face needs: every other field of this payload is identical before and
            // after, which is precisely why "waiting" was indistinguishable from "unconfigured".
            Assert.True(after.GetProperty("reloadPending").GetBoolean());
            Assert.Equal(TelegramReadiness.ReloadQueued, Str(after, "willDeliverReason"));
            Assert.DoesNotContain("add a telegram block", Str(after, "willDeliverReason"), StringComparison.Ordinal);
            // Still not delivering — the fix is to the sentence, never to the verdict.
            Assert.False(after.GetProperty("willDeliver").GetBoolean());
        }
        finally { server.Dispose(); }
    }

    /// <summary>The reply that actually burned the owner. "saved, but ... add a telegram block to
    /// the plan" becomes "saved, and a plan reload is queued".</summary>
    [Fact]
    public async Task TelegramToken_SaysTheReloadIsQueued_InsteadOfAdviseTheEditJustAccepted()
    {
        var (server, port) = StartServer(LiveService());
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, await AddTelegramBlockAsync(port));

            var reply = await PostTokenAsync(port);
            Assert.True(reply.GetProperty("ok").GetBoolean());
            var message = Str(reply, "message");
            Assert.Contains(TelegramReadiness.ReloadQueued, message, StringComparison.Ordinal);
            Assert.DoesNotContain("add a telegram block", message, StringComparison.Ordinal);
            // The token save is real either way; only the explanation changed.
            Assert.False(reply.GetProperty("willDeliver").GetBoolean());
        }
        finally { server.Dispose(); }
    }

    // ── the honesty rails: the new sentence must not spread ─────────────────────────────────────

    /// <summary>A queued reload that carries no telegram block fixes nothing about Telegram, so the
    /// old sentence is still the true one. Without this the fix would trade one lie for another —
    /// "a reload is queued" over a plan that will come back just as unconfigured.</summary>
    [Fact]
    public async Task AQueuedReloadWithoutATelegramBlock_LeavesTheOriginalSentenceAlone()
    {
        var (server, port) = StartServer();
        try
        {
            // A limits edit: same code path, same queued reload, nothing to do with Telegram.
            var req = new PlanEditRequestDto([new PlanEditDto("limits", "", "maxsessions", "9")]);
            using var content = new StringContent(
                JsonSerializer.Serialize(req, ControlPlaneJsonContext.Default.PlanEditRequestDto),
                Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.Accepted, (await _http.PostAsync($"http://127.0.0.1:{port}/plan/edit", content)).StatusCode);

            var status = await StatusAsync(port);
            Assert.True(status.GetProperty("reloadPending").GetBoolean());
            Assert.Equal(TelegramReadiness.NoBlock, Str(status, "willDeliverReason"));
        }
        finally { server.Dispose(); }
    }

    /// <summary>The window closes at the boundary. SwapPlan is the run loop's reload point, and it is
    /// the moment "queued" stops being true — a flag that outlived its reload would be the same class
    /// of lie, just later.</summary>
    [Fact]
    public async Task TheQueuedSentenceExpires_WhenTheRunLoopSwapsThePlanIn()
    {
        var (server, port) = StartServer();
        try
        {
            Assert.Equal(HttpStatusCode.Accepted, await AddTelegramBlockAsync(port));
            Assert.True((await StatusAsync(port)).GetProperty("reloadPending").GetBoolean());

            // What RunLoop.ApplyPlanReload does at the session boundary.
            server.SwapPlan(PlanConfig.Load(_planPath));

            var after = await StatusAsync(port);
            Assert.False(after.GetProperty("reloadPending").GetBoolean());
            // The live plan now HAS the block, so the blocker moves on to the next missing half
            // rather than reverting to "not configured".
            Assert.NotEqual(TelegramReadiness.ReloadQueued, Str(after, "willDeliverReason"));
            Assert.NotEqual(TelegramReadiness.NoBlock, Str(after, "willDeliverReason"));
        }
        finally { server.Dispose(); }
    }
}
