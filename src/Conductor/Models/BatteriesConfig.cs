namespace Conductor.Models;

/// <summary>Opt-in prompt batteries that inject bounded context into every session prompt (B8.5).
/// Each battery is a named, deterministic, byte-capped section. Batteries compose in order.</summary>
public sealed class BatteriesConfig
{
    /// <summary>Include the rolling lessons brief from .conductor/lessons.md.</summary>
    public bool Lessons { get; set; } = true;
    /// <summary>Include a recent-failure digest when the last session didn't verify.</summary>
    public bool RecentFailure { get; set; } = true;
    /// <summary>Max entries to include from lessons (default 3).</summary>
    public int LessonsMaxEntries { get; set; } = 3;
    /// <summary>Total byte cap for the combined battery section in the prompt.</summary>
    public int MaxBytes { get; set; } = 2048;
}
