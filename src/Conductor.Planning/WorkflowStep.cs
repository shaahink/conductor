using System.Text.Json;
using System.Text.Json.Serialization;

namespace Conductor.Planning;

/// <summary>A single step in a workflow: what kind of session, with optional filters
/// and overrides.</summary>
public sealed class WorkflowStep
{
    /// <summary>SF0.1 / bug 6: the only keys a step is allowed to carry. <c>model</c> used to be
    /// declared here and was read by NOTHING — the engine picks a session's model from
    /// <c>pipeline.roles.&lt;role&gt;.model</c> (through <c>SessionAssignment.Model</c>), then the
    /// stage's <c>agent.model</c>, then the plan's, and a workflow step never entered that chain. A
    /// model pinned on a step was therefore inert: the plan claimed one model answered while another
    /// one did. Deleted rather than wired, because the role map already expresses per-kind model
    /// choice and a second key at the same scope is the trap, not the cure.</summary>
    public static IReadOnlyList<string> KnownFields { get; } = ["id", "kind", "runIf", "skipIf", "deliver"];

    public string Id { get; set; } = "";
    public SessionKind Kind { get; set; } = SessionKind.Deliver;
    public string? RunIf { get; set; }
    public string? SkipIf { get; set; }
    public bool? Deliver { get; set; }

    /// <summary>SF0.1 / bug 6: keys a step does not have, kept so plan load can name them instead of
    /// dropping them in silence — the same bucket-and-refuse shape the advisor block uses for bug 7.
    /// Reported by <c>PlanConfig.CollectErrors</c>.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownFields { get; set; }
}
