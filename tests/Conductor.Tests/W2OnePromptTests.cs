using Conductor.Http;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Conductor.Core;
using Conductor.Hosting;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Conductor.Tests;

/// <summary>
/// W2.3 truth gates — one composition, one instruction. The bytes <c>GET /prompt/blocks</c> serves for
/// a card are the bytes that card contributes to the session prompt on disk (criterion 3: what the
/// card detail shows IS what the agent receives), and the prompt names exactly one claim path instead
/// of instructing a tracker hand-edit in one paragraph and calling it pointless in the next (G7/G8).
/// </summary>
public sealed class W2OnePromptTests
{
    private static int ProbeFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static void Nuke(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CardDetailBytes_AreTheSessionPromptBytes()
    {
        var repo = Path.Combine(Path.GetTempPath(), $"conductor-w23-{Guid.NewGuid():N}");
        using var http = new HttpClient();
        try
        {
            Directory.CreateDirectory(repo);
            ProcResult Git(string a) => ProcessRunner.Run("git",
                a.Split(' ', StringSplitOptions.RemoveEmptyEntries), repo, TimeSpan.FromSeconds(30), CancellationToken.None);
            Git("init -b main");
            Git("config user.email w23@test");
            Git("config user.name W23");
            await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "# r");
            Git("add README.md");
            Git("commit -m init --no-gpg-sign");
            await File.WriteAllTextAsync(Path.Combine(repo, "TRACKER.md"),
                "# Plan\n\n## Handoff\nnone.\n\n| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n| H0.1 | the checkpoint | TODO | | |\n");
            var agentScript = Path.Combine(repo, "fake-agent.cmd");
            await File.WriteAllTextAsync(agentScript, string.Join("\r\n",
                "@echo off",
                "echo {\"type\":\"text\",\"part\":{\"text\":\"working...\"}}",
                "exit /b 0",
                ""));

            var planPath = Path.Combine(repo, "test.plan.json");
            var seed = new PlanConfig
            {
                Name = "w23-live",
                Repo = repo.Replace("\\", "/"),
                Tracker = "TRACKER.md",
                Stages = [new StageConfig { Id = "H0", Title = "Prompt", Sessions = 1 }],
                Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", agentScript, "{prompt}"], Provider = "opencode" },
                Gates = [new GateConfig { Name = "smoke", Command = "echo ok", Tier = "fast", TimeoutMinutes = 1 }],
            };
            seed.Limits.MaxSessions = 1;
            seed.Report.Commit = false;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(seed, PlanConfig.JsonOpts),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var plan = PlanConfig.Load(planPath);

            var state = new RunState { RunId = Guid.NewGuid().ToString("N") };
            using var host = ConductorHost.Build(plan, state, new PlainSink(),
                new RunOptions(DryRun: false, Once: false, MaxSessions: 0,
                    ControlPlane: true, ControlPlanePort: ProbeFreePort(), StartPaused: true), consoleSink: false);
            var server = host.Services.GetRequiredService<Conductor.Http.ControlPlaneServer>();
            Assert.True(server.Start(), "control plane failed to bind");
            http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);
            var baseUrl = $"http://127.0.0.1:{server.Port}";

            using var cts = new CancellationTokenSource();
            var runTask = host.Services.GetRequiredService<Orchestrator>().RunAsync(cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (state.Status != RunStatus.Paused && DateTime.UtcNow < deadline)
                await Task.Delay(50, CancellationToken.None);
            Assert.Equal(RunStatus.Paused, state.Status);

            // The owner authors a card while parked: a sub-task with a multi-line context, which is
            // the shape most likely to differ between two renderings.
            string cardId;
            using (var add = new StringContent("""{"checkpointId":"H0.1","title":"Write the parser"}""",
                       Encoding.UTF8, "application/json"))
            {
                var resp = await http.PostAsync($"{baseUrl}/tasks/add", add, cts.Token);
                Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
                cardId = doc.RootElement.GetProperty("taskId").GetString()!;
            }
            var editBody = JsonSerializer.Serialize(new
            {
                taskId = cardId,
                context = "Start in Parser.cs.\nDo NOT touch the lexer — it is frozen for this stage.",
            });
            using (var edit = new StringContent(editBody, Encoding.UTF8, "application/json"))
                Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync($"{baseUrl}/tasks/edit", edit, cts.Token)).StatusCode);

            // What the card detail promises the agent will receive.
            using var blocks = JsonDocument.Parse(
                await http.GetStringAsync($"{baseUrl}/prompt/blocks?task={cardId}", cts.Token));
            Assert.True(blocks.RootElement.GetProperty("ok").GetBoolean());
            var promised = blocks.RootElement.GetProperty("promptSection").GetString()!;
            Assert.Contains("Write the parser", promised, StringComparison.Ordinal);
            Assert.Contains("frozen for this stage", promised, StringComparison.Ordinal);

            // Let session 1 run, then compare against the prompt actually written to disk.
            using (var resume = new StringContent("""{"command":"resume"}""", Encoding.UTF8, "application/json"))
                Assert.Equal(HttpStatusCode.Accepted, (await http.PostAsync($"{baseUrl}/control", resume, cts.Token)).StatusCode);

            var promptPath = Path.Combine(repo, ".conductor", "logs", "session-001.prompt.md");
            var prompt = await ReadPromptWhenCompleteAsync(promptPath, TimeSpan.FromSeconds(90));

            // THE GATE: the card detail's bytes appear verbatim in the prompt the agent was handed.
            Assert.Contains(promised, prompt, StringComparison.Ordinal);
            Assert.Contains(Conductor.Planning.PromptBlockRenderer.SectionHeading, prompt, StringComparison.Ordinal);

            // ONE claim path: the prompt names the verb as the only channel, and no longer tells the
            // agent to fill in tracker checkpoint rows it also calls a no-op.
            Assert.Contains("conductor task --done", prompt, StringComparison.Ordinal);
            Assert.Contains("THIS IS THE ONLY WAY TO REPORT PROGRESS", prompt, StringComparison.Ordinal);
            Assert.DoesNotContain("fill checkpoint rows", prompt, StringComparison.Ordinal);

            await cts.CancelAsync();
            try { await runTask.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None); }
            catch (OperationCanceledException) { }
        }
        finally { Nuke(repo); }
    }

    [Fact]
    public void ToolContract_NamesOneClaimPath_AndKeepsTheHandoffBlockWritable()
    {
        var plan = new PlanConfig
        {
            Name = "p",
            Repo = ".",
            Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "H0", Title = "s", Sessions = 1 }],
        };
        var text = ToolContract.Render(plan);

        Assert.Contains("THIS IS THE ONLY WAY TO REPORT PROGRESS", text, StringComparison.Ordinal);
        Assert.Contains("There is no second mechanism", text, StringComparison.Ordinal);
        // The handoff block is genuinely read back (RunLoop.Plumbing writes it to run.db from the
        // tracker), so the contract must NOT tell the agent that writing the tracker is pointless
        // wholesale — only its generated checkpoint rows are.
        Assert.Contains("handoff block", text, StringComparison.Ordinal);
        Assert.DoesNotContain("editing its rows by hand achieves nothing", text, StringComparison.Ordinal);
    }

    /// <summary>Reads the session prompt once the engine has finished writing it.
    /// <c>File.Exists</c> flips the instant the file is CREATED, while the writer still holds the
    /// handle — and <c>File.ReadAllTextAsync</c> opens with <c>FileShare.Read</c>, so it loses that
    /// race with "the process cannot access the file". A local SSD hides the window; the W6.2 CI
    /// runner did not. Open shared, and wait for the section heading that proves the write
    /// completed rather than for mere existence (a partial read would fail the byte comparison for
    /// the wrong reason). Same shape as <c>HostLoggingTests.ReadLogWhenFlushedAsync</c>.</summary>
    private static async Task<string> ReadPromptWhenCompleteAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        var text = "";
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                try
                {
                    await using var stream = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    text = await reader.ReadToEndAsync();
                    if (text.Contains(Conductor.Planning.PromptBlockRenderer.SectionHeading, StringComparison.Ordinal))
                        return text;
                }
                catch (IOException) { /* writer still holds it exclusively — try again */ }
            }
            await Task.Delay(100, CancellationToken.None);
        }
        Assert.Fail($"session 1 never wrote a complete prompt to {path}. Last content:\n{text}");
        return text;
    }
}
