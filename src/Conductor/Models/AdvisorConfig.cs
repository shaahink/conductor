namespace Conductor.Models;

public sealed class AdvisorConfig
{
    public bool Enabled { get; set; } = true;
    public string Command { get; set; } = "claude";
    public List<string> Args { get; set; } = new();
    /// <summary>"text" or "json" (claude -p --output-format json envelope)</summary>
    public string Output { get; set; } = "text";
    public int TimeoutMinutes { get; set; } = 6;
    /// <summary>P3: optional shell command run when the advisor returns ApplyFix.
    /// Example: "taskkill /f /im opencode.exe" or "git clean -fdx". Executed via
    /// the default shell with a 5-minute timeout; runs in the repo root.</summary>
    public string? RemediationScript { get; set; }
}
