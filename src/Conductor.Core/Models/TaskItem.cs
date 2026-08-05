namespace Conductor.Models;

/// <summary>
/// A single work item in the unified graph (B9.1 sub-tasks; W1.1 checkpoints joined the same
/// fold). Kind "checkpoint" items are the verified contract the engine schedules; "subtask"
/// items are advisory break-points beneath one. Anti-pattern A16: keep lightweight.
/// </summary>
public sealed class TaskItem
{
    public string TaskId { get; set; } = "";
    public string CheckpointId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "todo";

    /// <summary>Provenance: plan | tracker | import | human | agent (W1.1 vocabulary).</summary>
    public string Source { get; set; } = "";
    public int Order { get; set; }

    /// <summary>W1.1: checkpoint | subtask (the <c>WorkItemKinds</c> vocabulary).</summary>
    public string Kind { get; set; } = "subtask";

    /// <summary>W1.1: owning stage id (checkpoint-kind items; derived for subtasks).</summary>
    public string StageId { get; set; } = "";

    /// <summary>W1.1: commit sha attributed by the latest done-claim ("-" = none yet).</summary>
    public string Commit { get; set; } = "-";

    /// <summary>W1.1: evidence carried by the latest done-claim ("-" = none yet).</summary>
    public string Evidence { get; set; } = "-";

    /// <summary>W1.1: true once the engine confirmed the claim (M4.1 — gates + verify evidence,
    /// folded from <c>CheckpointConfirmed</c>). Claims flip Status; only the verdict engine
    /// confirms.</summary>
    public bool Confirmed { get; set; }

    /// <summary>P3: owner-provided extra context for this task — structured task data that becomes
    /// the editable "extra context" block of the task's prompt composition. Empty = none.</summary>
    public string Context { get; set; } = "";

    /// <summary>PF3: repo-relative paths this card is DECLARED to touch — the real task data behind
    /// <c>ReadyItem.PathClaims</c>, so a multi-item session refuses to co-claim checkpoints whose
    /// open cards declare overlapping paths. Empty = no declared claims (the common case).</summary>
    public List<string> Paths { get; set; } = new();

    /// <summary>W4.4: per-item QA override — "" (inherit, the common case), "verify", or "off".
    /// Consulted by <c>DefaultQaPolicy</c> ABOVE the stage and plan dials when a session claims
    /// this item.</summary>
    public string Qa { get; set; } = "";

    /// <summary>SF3.2: the instant this card last CHANGED status, taken from the event envelope, so
    /// a board can say how long it has sat where it is. null = it has not moved since it was added,
    /// or the log's events carry no stamp (hand-built events in tests).</summary>
    public DateTimeOffset? StatusSinceUtc { get; set; }

    /// <summary>SF3.2: the session whose work last moved this card — the session in flight when the
    /// fold saw the status change. A status-change event carries no session of its own, so the graph
    /// tracks the last <c>SessionStarted</c> and stamps it here; an engine-side confirmation landing
    /// after the session ends therefore names the session that just finished, which is the session
    /// whose work it is. 0 = never moved inside a session (a seeded card).</summary>
    public int SessionNumber { get; set; }

    /// <summary>SF3.2: how many times this card has been PICKED UP — entered in_progress. NOT the
    /// stage's attempt counter: a card reopened after a failed session reads 2 here while its stage
    /// may be on an entirely different number.</summary>
    public int Attempts { get; set; }
}
