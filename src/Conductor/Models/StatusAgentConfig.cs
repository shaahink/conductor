namespace Conductor.Models;

/// <summary>On-demand "what's the status?" agent (dashboard `G` key + <c>conductor status</c> CLI).
/// Runs read-only: all context is embedded in the prompt and it executes in a scratch cwd, so it
/// can't touch the working repo.</summary>
public sealed class StatusAgentConfig
{
    public bool Enabled { get; set; } = true;
    public string Command { get; set; } = "opencode";
    /// <summary>Placeholders: {prompt}</summary>
    public List<string> Args { get; set; } = new() { "run", "{prompt}", "-m", "deepseek/deepseek-v4-pro" };
    public int TimeoutMinutes { get; set; } = 5;
    /// <summary>LLM model override for status reports (e.g. "deepseek/deepseek-chat").
    /// When set, replaces the -m arg in <see cref="Args"/>. null = use the args as-is.</summary>
    public string? Model { get; set; }
    /// <summary>Max status invocations per rolling hour. 0 = unlimited. Default 12.</summary>
    public int MaxPerHour { get; set; } = 12;
}
