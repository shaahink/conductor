namespace Conductor.Commands;

/// <summary>Which of the four the reader picked. An enum rather than a string so the dispatch cannot
/// quietly gain a fifth case by someone typing a new label.</summary>
public enum HubActionKind
{
    /// <summary>Open the Face on a run that is already going.</summary>
    Attach,

    /// <summary>Launch a run from a plan discoverable here.</summary>
    Start,

    /// <summary>Scaffold a plan in this directory.</summary>
    PlanNew,

    /// <summary>Browse what this machine remembers.</summary>
    History,
}

/// <summary>One offer on the hub's menu: what it is called, and the sentence that says why you would
/// pick it.</summary>
public sealed record HubAction(HubActionKind Kind, string Label, string Hint);

/// <summary>
/// KS2.1 — the four things a person can do from the front door, in one list because the number is
/// part of the design.
///
/// <para>The verb list has forty-one entries and that is the right size for a reference and the wrong
/// size for a door. Four is what fits in a glance: look at a run that is going, start one, make a plan
/// if there is none, or look at what already happened. Everything else is still one <c>--help</c>
/// away, and quitting is not on this list because quitting is the way out, not a thing to do.</para>
/// </summary>
public static class HubActions
{
    public static readonly HubAction Attach =
        new(HubActionKind.Attach, "attach", "open the Face on a run that is already going");

    public static readonly HubAction Start =
        new(HubActionKind.Start, "start", "launch a run from a plan in this directory");

    public static readonly HubAction PlanNew =
        new(HubActionKind.PlanNew, "plan new", "scaffold a plan here, then start it");

    public static readonly HubAction History =
        new(HubActionKind.History, "history", "browse what this machine remembers");

    /// <summary>Exactly four, in the order a reader most often wants them.</summary>
    public static readonly IReadOnlyList<HubAction> All = [Attach, Start, PlanNew, History];

    /// <summary>The escape. Offered beside the four so a prompt has a way out that exits 0, and kept
    /// out of <see cref="All"/> so "the hub offers four actions" stays a countable claim.</summary>
    public const string QuitLabel = "quit";
}
