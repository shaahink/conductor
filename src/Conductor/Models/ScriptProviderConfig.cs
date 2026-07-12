namespace Conductor.Models;

/// <summary>Config for the script progress provider: a plan-owned command whose stdout is a JSON array
/// of checkpoint objects (<c>{ id, title, status, commit, evidence }</c>). Resilient by contract —
/// a missing script or malformed JSON surfaces as a clear error, never a crash (B1.3 trap).</summary>
public sealed class ScriptProviderConfig
{
    /// <summary>PowerShell command line, run in the repo root with real exit-code capture.</summary>
    public string Command { get; set; } = "";
    /// <summary>Working dir relative to repo root (default: repo root).</summary>
    public string? Cwd { get; set; }
    public int TimeoutMinutes { get; set; } = 2;
}
