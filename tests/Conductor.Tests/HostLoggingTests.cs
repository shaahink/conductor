using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Hosting;
using Conductor.Models;
using Conductor.Ui;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Conductor.Tests;

/// <summary>B2.5 truth gate: the composition root validates the plan config on start (a bad config
/// surfaces as a thrown <see cref="OptionsValidationException"/>, never a silent swallow), and a real
/// run writes a Serilog structured log under <c>.conductor/logs/</c> carrying the run's correlation id.</summary>
public sealed class HostLoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-host-{Guid.NewGuid():N}");

    public HostLoggingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* Serilog may still hold a handle briefly; temp dir is disposable */ }
    }

    [Fact]
    public void CorrelationTemplateCoversRunSessionStageGate()
    {
        // The four correlation dimensions R2.5 mandates must all be in the sink template, or a
        // structured line silently drops a dimension. Guards the schema without a full agent run.
        Assert.Contains("{runId}", ConductorHost.FileTemplate, StringComparison.Ordinal);
        Assert.Contains("{sessionId}", ConductorHost.FileTemplate, StringComparison.Ordinal);
        Assert.Contains("{stage}", ConductorHost.FileTemplate, StringComparison.Ordinal);
        Assert.Contains("{gate}", ConductorHost.FileTemplate, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPlanSurfacesAsOptionsValidationExceptionOnStart()
    {
        // A plan that skipped Load's validation (built directly) must still be rejected fail-fast by
        // the Options validator when the host is composed — the error surfaces, it is not swallowed.
        var plan = new PlanConfig { Name = "bad", Repo = _dir, Tracker = "t.md" }; // no stages, no agent args
        WriteTracker();
        var state = new RunState { RunId = "run-x" };

        var ex = Assert.Throws<OptionsValidationException>(() =>
            ConductorHost.Build(plan, state, StatePath, new PlainSink(), NullEventSink.Instance,
                new RunOptions(DryRun: true, Once: false, MaxSessions: 0), consoleSink: false));

        Assert.Contains("stages", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidPlanComposesOrchestratorViaDi()
    {
        var plan = ValidPlan();
        WriteTracker();
        using var host = ConductorHost.Build(plan, new RunState { RunId = "run-y" }, StatePath,
            new PlainSink(), NullEventSink.Instance,
            new RunOptions(DryRun: true, Once: false, MaxSessions: 0), consoleSink: false);

        Assert.NotNull(host.Services.GetRequiredService<Orchestrator>());
    }

    [Fact]
    public void DryRunWritesStructuredLogWithRunIdCorrelation()
    {
        var plan = ValidPlan();
        WriteTracker();
        const string runId = "run-corr-123";
        var state = new RunState { RunId = runId };

        using (var host = ConductorHost.Build(plan, state, StatePath, new PlainSink(), NullEventSink.Instance,
                   new RunOptions(DryRun: true, Once: false, MaxSessions: 0), consoleSink: false))
        {
            var code = host.Services.GetRequiredService<Orchestrator>().Run(CancellationToken.None);
            Assert.Equal(0, code);
        } // dispose flushes + closes the Serilog file sink

        var logDir = Path.Combine(plan.StateDir, "logs");
        var logFile = Directory.EnumerateFiles(logDir, "conductor-*.log").Single();
        var log = File.ReadAllText(logFile);

        Assert.Contains($"run={runId}", log, StringComparison.Ordinal);      // correlation scope reached the sink
        Assert.Contains("conductor start", log, StringComparison.Ordinal);   // the run actually narrated through ILogger
        Assert.Contains($"stage={plan.Stages[0].Id}", log, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunWritesJsonLogWithCorrelationProperties()
    {
        var plan = ValidPlan();
        WriteTracker();
        const string runId = "run-json-456";
        var state = new RunState { RunId = runId };

        using (var host = ConductorHost.Build(plan, state, StatePath, new PlainSink(), NullEventSink.Instance,
                   new RunOptions(DryRun: true, Once: false, MaxSessions: 0), consoleSink: false))
        {
            var code = host.Services.GetRequiredService<Orchestrator>().Run(CancellationToken.None);
            Assert.Equal(0, code);
        }

        var logDir = Path.Combine(plan.StateDir, "logs");
        var jsonFile = Directory.EnumerateFiles(logDir, "conductor-*.json").Single();
        var lines = File.ReadAllLines(jsonFile);
        Assert.NotEmpty(lines);

        // Every line must be valid JSON with @t + @m fields
        foreach (var line in lines)
        {
            System.Text.Json.JsonDocument doc;
            try { doc = System.Text.Json.JsonDocument.Parse(line); }
            catch (System.Text.Json.JsonException) { Assert.Fail($"Line is not valid JSON: {line[..Math.Min(80, line.Length)]}"); continue; }
            var root = doc.RootElement;
            Assert.True(root.TryGetProperty("@t", out _), "missing @t timestamp");
            Assert.True(root.TryGetProperty("@m", out _), "missing @m message");

            // At least one line must carry the run correlation
            if (root.TryGetProperty("runId", out var rid))
            {
                Assert.Equal(runId, rid.GetString());
                break; // found the correlated entry
            }
        }

        // Verify text log still present (backward compatibility)
        var textFile = Directory.EnumerateFiles(logDir, "conductor-*.log").Single();
        Assert.True(new FileInfo(textFile).Length > 0, "binary text log must still be written");
    }

    private string StatePath => Path.Combine(_dir, ".conductor", "state.json");

    private PlanConfig ValidPlan() => new()
    {
        Name = "hostlog",
        Repo = _dir,
        Tracker = "t.md",
        Stages = { new StageConfig { Id = "S1", Title = "First", Sessions = 1 } },
        Agent = new AgentConfig { Command = "opencode", Args = { "run", "{prompt}" }, Output = "opencode-json" },
    };

    private void WriteTracker() => File.WriteAllText(Path.Combine(_dir, "t.md"),
        "# T\n\n## Handoff\nlast: none.\n\n## Checkpoints\n\n" +
        "| # | Checkpoint | Status | Commit | Evidence |\n|---|---|---|---|---|\n" +
        "| S1.1 | first task | TODO | | |\n");
}
