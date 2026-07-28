namespace Conductor.Planning;

/// <summary>The declarative, engine-agnostic pipeline rules block (P0). Lives on the plan as a plain
/// JSON `pipeline` object any consumer could author. Every field is optional and every default
/// reproduces the classic engine behavior exactly, so a plan with no `pipeline` block runs
/// byte-for-byte unchanged. Policies INTERPRET these rules; nothing hard-codes them.</summary>
public sealed class PipelineRules
{
    /// <summary>P1 (role→agent assignment): maps a session role — "deliver", "verify", "audit",
    /// "fix" — to the agent that should run it. A missing role means "use the stage/plan default
    /// agent", which is exactly today's behavior.</summary>
    public Dictionary<string, RoleAgentRule>? Roles { get; set; }

    /// <summary>P2 (QA frequency dial): off / everySession / phaseGate, resolved onto the existing
    /// workflows. null = the stage's workflow decides, exactly today's behavior.</summary>
    public QaRule? Qa { get; set; }

    /// <summary>P1 (multi-item sessions): whether one session may claim several conflict-free ready
    /// items. null or disabled = one active checkpoint per session, exactly today's behavior.</summary>
    public MultiItemRule? MultiItem { get; set; }
}
