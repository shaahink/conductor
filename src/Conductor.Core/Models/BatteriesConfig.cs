namespace Conductor.Models;

/// <summary>Opt-in prompt batteries that inject bounded context into every session prompt (B8.5).
/// Each battery is a named, deterministic, byte-capped section. Batteries compose in order.</summary>
public sealed class BatteriesConfig
{
    /// <summary>Include the rolling lessons brief from .conductor/lessons.md.</summary>
    public bool Lessons { get; set; } = true;
    /// <summary>Include a recent-failure digest when the last session didn't verify.</summary>
    public bool RecentFailure { get; set; } = true;
    /// <summary>M7.1: inject recent knowledge-ledger entries (conductor note) into the next prompt.</summary>
    public bool Ledger { get; set; } = true;
    /// <summary>M7.2: inject the run's open tracked bugs (conductor bug new) into the next prompt.</summary>
    public bool Bugs { get; set; } = true;
    /// <summary>KS7.5: inject a bounded map of the repo's top-level source directories. OPT-IN, and
    /// deliberately so: this battery makes the prompt BIGGER on a bet that it prevents exploration
    /// turns. That bet is about session behaviour, not arithmetic, so it is not switched on for
    /// everyone by default (see <see cref="RepoMapBattery"/> for the measurement behind the caution).</summary>
    public bool RepoMap { get; set; }
    /// <summary>KS7.5: recap the checkpoint in flight and the exact claim command, pre-filled with its
    /// id. OPT-IN, and for a measured reason rather than timidity: the prompt is not only a token cost,
    /// it is an ARGUMENT, and doctor's argv lint (KS1.4, bugs #15/#21) puts a minimal plan on the
    /// built-in templates at 7.8k of the 8191-char ceiling a cmd/bat-shimmed agent has. ~280 more bytes
    /// on by default would spend a fifth of the headroom every plan has left, so a plan opts in when it
    /// has the room. Turn it on: sessions here have repeatedly written DONE somewhere that moves
    /// nothing, and this is the one line that names the id and the verb together.</summary>
    public bool DefinitionOfDone { get; set; }
    /// <summary>Max top-level directories the repo map lists (default 12).</summary>
    public int RepoMapMaxEntries { get; set; } = 12;
    /// <summary>Max entries to include from lessons (default 3).</summary>
    public int LessonsMaxEntries { get; set; } = 3;
    /// <summary>Max ledger entries to inject (default 8).</summary>
    public int LedgerMaxEntries { get; set; } = 8;
    /// <summary>Total byte cap for the combined battery section in the prompt.</summary>
    public int MaxBytes { get; set; } = 2048;
}
