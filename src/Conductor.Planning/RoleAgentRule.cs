namespace Conductor.Planning;

/// <summary>Which agent runs a given session role (P1). Any field left null falls back to the
/// stage/plan default — the rule only overrides what it names.</summary>
public sealed class RoleAgentRule
{
    /// <summary>Model id for this role (e.g. audit → a stronger model than deliver).</summary>
    public string? Model { get; set; }

    /// <summary>Persona name for this role (resolved by the consumer's persona registry).</summary>
    public string? Persona { get; set; }

    /// <summary>Agent command override for this role (a different CLI entirely).</summary>
    public string? Command { get; set; }
}
