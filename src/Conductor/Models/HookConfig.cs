namespace Conductor.Models;

public sealed class HookConfig
{
    /// <summary>PowerShell command line, run with real exit-code capture. Best-effort: a nonzero exit never blocks the run.</summary>
    public string Command { get; set; } = "";
    /// <summary>Working dir relative to repo root (default: repo root).</summary>
    public string? Cwd { get; set; }
    public int TimeoutMinutes { get; set; } = 3;
}
