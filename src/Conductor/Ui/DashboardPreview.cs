using Conductor.Core;
using Conductor.Models;

namespace Conductor.Ui;

/// <summary>
/// Seeds a <see cref="LiveDashboard"/> with the real plan/tracker/state plus representative
/// synthetic session data (agent output, thinking, live gates, cost/tokens) so the whole UI can
/// be verified offline via <c>conductor preview</c>. Nothing here touches disk or the running run.
/// </summary>
public static class DashboardPreview
{
    public static void Seed(LiveDashboard dash, PlanConfig plan, RunState state, TrackerSnapshot track)
    {
        var now = DateTime.UtcNow;
        var gates = new List<GateProgress>
        {
            new("build", "pass", TimeSpan.FromSeconds(28)),
            new("tests", "running", TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(14)),
            new("lint", "pass", TimeSpan.FromSeconds(44)),
            new("security-scan", "pending", TimeSpan.Zero),
            new("integration", "skip", TimeSpan.Zero),
        };

        var snap = SnapshotBuilder.Build(plan, state, track, GateRunner.Summary(
            gates.Select(g => new GateResult(g.Name, g.State == "pass", g.State == "skip", false, 0, g.Elapsed, "")).ToList()))
            with
            {
                Status = "Running",
                AttentionReason = "PREVIEW — synthetic session data (not a live run)",
                SessionNumber = state.SessionCounter > 0 ? state.SessionCounter : 1,
                SessionKind = "Deliver",
                Attempt = 1,
                MaxAttempts = 4,
                AgentActive = true,
                SessionCostUsd = 0.0239m,
                SessionTokensInput = 18400,
                SessionTokensOutput = 3120,
                SessionTokensReasoning = 1650,
                SessionElapsed = TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(57),
                LastActivityAgoSec = 6,
                Gates = gates,
            };
        dash.Snapshot(snap);

        foreach (var (kind, text, agoSec) in SampleAgent)
            dash.AgentEvent(new AgentEvent { Kind = kind, Text = text });
        foreach (var t in SampleThinking)
            dash.AgentEvent(new AgentEvent { Kind = "thinking", Text = t });

        dash.GateProgress(gates);
        dash.Log("[preview] this is a synthetic frame — real runs populate these panels live");
        dash.Log("[preview] press any key to exit");
    }

    private static readonly (string Kind, string Text, int AgoSec)[] SampleAgent =
    {
        ("tool", "bash dotnet build MyProject.slnx", 240),
        ("result", "Build succeeded — 0 Warning(s) 0 Error(s)", 238),
        ("text", "Reading the plan config and stage design before making changes.", 210),
        ("tool", "read src/Core/Engine.cs", 180),
        ("tool", "edit src/Core/Engine.cs", 120),
        ("tool", "bash dotnet test MyProject.slnx --no-build", 70),
        ("result", "Passed! - Failed: 0, Passed: 42, Skipped: 0, Total: 42", 40),
        ("stderr", "warning: analyzer MA0051 method too long (suppressed)", 38),
        ("text", "Now wiring up the controller and adding integration fixtures.", 6),
    };

    private static readonly string[] SampleThinking =
    {
        "Goal: implement the new service layer with proper error handling. Hypothesis: using the existing middleware pipeline will handle edge cases without extra code. Evidence: the pipeline already validates all inputs. Action: start from the existing middleware seam and add the service implementation.",
        "Edge case: concurrent requests could race on the shared cache. Add a distributed lock guard.",
        "Goal: close the implementation gap. Action: add negative test fixtures for the edge cases, then run the full battery.",
    };
}
