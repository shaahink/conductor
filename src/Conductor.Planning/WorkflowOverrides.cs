using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Planning;

/// <summary>Per-stage workflow override — drop QA, or skip gates/commit for one stage (M3.2).</summary>
public sealed class WorkflowOverrides
{
    /// <summary>SF0.1 / bug 6: the only keys this block is allowed to carry. <c>model</c> used to be
    /// declared here and was read by NOTHING, while its three siblings all reach the run loop
    /// (<c>RunLoop.Plumbing</c> for gates/commit, <c>QaPolicyExtensions.EffectiveSkipVerification</c>
    /// for QA) — so one key in four was a lie about which model a stage ran on. Deleted rather than
    /// wired: <c>stage.agent.model</c> is the same scope and already works.</summary>
    public static IReadOnlyList<string> KnownFields { get; } = ["skipVerification", "skipGates", "skipCommit"];

    public bool? SkipVerification { get; set; }
    public bool? SkipGates { get; set; }
    public bool? SkipCommit { get; set; }

    /// <summary>SF0.1 / bug 6: keys this block does not have, kept so plan load can name them.
    /// Reported by <c>PlanConfig.CollectErrors</c>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}
