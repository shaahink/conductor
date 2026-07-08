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
            new("pnpm-check", "pass", TimeSpan.FromSeconds(44)),
            new("mcp-qa", "pending", TimeSpan.Zero),
            new("loom-guards", "skip", TimeSpan.Zero),
        };

        var snap = SnapshotBuilder.Build(plan, state, track, GateRunner.Summary(
            gates.Select(g => new GateResult(g.Name, g.State == "pass", g.State == "skip", false, 0, g.Elapsed, "")).ToList()))
            with
            {
                Status = "Running",
                AttentionReason = "PREVIEW — synthetic session data (not a live run)",
                SessionNumber = state.SessionCounter > 0 ? state.SessionCounter : 5,
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
        ("tool", "bash git -C C:/code/DevContext2-ui status --porcelain", 240),
        ("result", " M src/DevContext.Core/Graph/SymbolTable.cs", 238),
        ("result", "?? tests/DevContext.Tests/SymbolRefTests.cs", 237),
        ("text", "Reading the L1 stage section and loom-graph-design.md before touching identity code.", 210),
        ("tool", "read src/DevContext.Core/Graph/SymbolTable.cs", 180),
        ("tool", "edit src/DevContext.Core/Graph/SymbolId.cs", 120),
        ("tool", "bash dotnet build DevContext.slnx", 70),
        ("result", "build succeeded — 0 warnings", 40),
        ("stderr", "warning: analyzer MA0051 method too long (suppressed)", 38),
        ("text", "Now adding ambiguity fixtures and wiring SymbolRef resolution tiers.", 6),
    };

    private static readonly string[] SampleThinking =
    {
        "Goal: implement L1.1 SymbolId/SymbolRef with resolution tiers. Hypothesis: exact-then-fuzzy tiering is safe because the dogfood repo has duplicate short names. Evidence: SymbolTable already exposes a seam. Action: start from the SymbolTable seam and add ambiguity fixtures.",
        "The dogfood repo has duplicate short names across services, so exact-then-fuzzy tiering is the safe order.",
        "Goal: close the audit gap. Action: add negative fixtures for the service-libs case, then run the truth gate.",
    };
}
