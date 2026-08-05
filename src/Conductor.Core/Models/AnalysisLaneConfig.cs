namespace Conductor.Models;

/// <summary>Read-only analysis lane that runs concurrently with sessions (B12.1 Tier A).
/// Spawns an agent in a scratch temp directory — it can never write the working tree.
/// Output is captured as an artifact and injected into the next session's prompt.</summary>
public sealed class AnalysisLaneConfig
{
    /// <summary>Unique lane id used for artifact naming.</summary>
    public string Id { get; set; } = "";
    /// <summary>Analysis kind: "architecture", "design", "qa", "research", "analysis".</summary>
    public string Kind { get; set; } = "analysis";
    /// <summary>Human-readable name for logs and prompts.</summary>
    public string Name { get; set; } = "";
    /// <summary>The question or topic to analyze — embedded in the lane prompt.</summary>
    public string Prompt { get; set; } = "";
    /// <summary>Run when this stage becomes active. null = run on every stage.</summary>
    public string? StageTrigger { get; set; }
    /// <summary>Agent timeout in minutes.</summary>
    public int TimeoutMinutes { get; set; } = 15;
    /// <summary>When false the lane is skipped. Default true.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Maximum lines of agent output captured as the artifact. Default 200.</summary>
    public int MaxOutputLines { get; set; } = 200;
}

/// <summary>Tier B isolated-worktree mutating lane (B12.3). Runs in its own <c>git worktree</c>
/// on a scratch branch so it can freely mutate files. A merge gate runs the full battery on the
/// integrated tree before the lane's changes are accepted — red battery → rejected, never merged.</summary>
public sealed class MutatingLaneConfig
{
    /// <summary>Unique lane id used for branch/worktree naming.</summary>
    public string Id { get; set; } = "";
    /// <summary>Lane kind: "delivery", "fix", "refactor".</summary>
    public string Kind { get; set; } = "delivery";
    /// <summary>Human-readable name for logs.</summary>
    public string Name { get; set; } = "";
    /// <summary>The work prompt — injected into the agent session.</summary>
    public string Prompt { get; set; } = "";
    /// <summary>Run when this stage becomes active. null = run on every stage.</summary>
    public string? StageTrigger { get; set; }
    /// <summary>Agent timeout in minutes. Default 30 (mutating lanes may need more time).</summary>
    public int TimeoutMinutes { get; set; } = 30;
    /// <summary>When false the lane is skipped. Default true.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Per-lane agent override (persona, model, etc.). null = use plan default.</summary>
    public AgentConfig? Agent { get; set; }
    /// <summary>Gates to run on the merged tree for merge verification. null = use plan-level gates.</summary>
    public List<GateConfig>? MergeGates { get; set; }
}
