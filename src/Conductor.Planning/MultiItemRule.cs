namespace Conductor.Planning;

/// <summary>Multi-item session policy (P1): may one session claim several ready items? Disabled by
/// default — one active checkpoint per session, the classic behavior. When enabled, claims must be
/// conflict-free (no overlapping path claims); the assignment policy validates that, purely.</summary>
public sealed class MultiItemRule
{
    public bool Enabled { get; set; }

    /// <summary>Upper bound on items one session may claim. Default 1 (even when Enabled, until a
    /// larger bound is set explicitly).</summary>
    public int MaxItems { get; set; } = 1;
}
