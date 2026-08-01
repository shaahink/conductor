using System.Globalization;
using System.Text;
using Conductor.Models;

namespace Conductor.Core;

/// <summary>
/// SF4.1 — one thing only the owner can do. The shape is copied from the hand-written `SHAHIN.md`
/// the owner kept during the sk-platform round and asked for by name: <i>"i liked the manual list
/// created for me… feels like conductor could do with this, displaying what human need to do."</i>
/// Three fields carry the whole idea — what it is, what it UNBLOCKS, and the exact command that
/// clears it. An entry without those last two is a status line, not a queue item.
/// </summary>
/// <param name="Kind">Machine-readable source, one of <c>park</c>, <c>human</c>, <c>ownerGate</c>,
/// <c>wait</c>, <c>checkpoint</c>, <c>skippedStage</c>. The face groups on it; the markdown does not.</param>
/// <param name="Command">The literal command that clears the entry, or <c>""</c> when NOTHING the
/// owner types clears it (a blocked-until wait wakes itself). Empty is a fact, not a gap: inventing
/// a command for a wait would send the owner to a keyboard for nothing.</param>
/// <param name="SinceUtc">When the obligation opened, or null when the source cannot date it — the
/// tracker's markdown rows carry no timestamp. Null renders as "age unknown", never as "just now".</param>
public sealed record OwnerQueueItem(
    string Id,
    string Kind,
    string Title,
    string Unblocks,
    string Command,
    DateTime? SinceUtc,
    int Rank,
    string? Detail = null)
{
    /// <summary>Age in whole seconds, or null when the source could not date the obligation. Clock
    /// skew (a stamp in the future) reads 0, matching <see cref="Staleness.Age"/>.</summary>
    public long? AgeSeconds(DateTime nowUtc)
        => SinceUtc is { } s ? (long)Math.Max(0, (nowUtc - s).TotalSeconds) : null;
}

/// <summary>
/// SF4.1 — collects every open owner obligation the engine already knows about into one list, and
/// renders it to <c>.conductor/OWNER-QUEUE.md</c>. Six sources, all of them state the engine holds
/// anyway: the park it is sitting in, <c>HUMAN:</c> lines an agent wrote in the tracker handoff,
/// owner-gated stages nobody has approved, a live blocked-until wait, checkpoints a session parked
/// with <c>task --blocked</c>, and stages skipped for human review.
/// <para>Every entry is DERIVED, never stored. That is the whole clearing mechanism: approve the
/// gate and the gate entry is gone on the next render; delete the HUMAN: line and its entry goes
/// with it. There is no queue file to garbage-collect and no way for an entry to outlive the
/// condition that raised it — which is exactly how the hand-written list went stale.</para>
/// </summary>
public static class OwnerQueue
{
    // Urgency ranks. Lower sorts first: what is stopping the run RIGHT NOW, then what will stop it,
    // then what is merely owed. A park and a HUMAN: line are the same stop from the owner's side —
    // the run is standing still — so they lead.
    private const int RankPark = 0;
    private const int RankHuman = 1;
    private const int RankGateNow = 2;
    private const int RankWait = 3;
    private const int RankCheckpoint = 4;
    private const int RankSkipped = 5;
    private const int RankGateAhead = 6;

    public static string QueuePath(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Path.Combine(plan.StateDir, "OWNER-QUEUE.md");
    }

    /// <summary>Every open owner obligation, most urgent first.</summary>
    public static IReadOnlyList<OwnerQueueItem> Collect(PlanConfig plan, RunState state, TrackerSnapshot track, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(track);

        var items = new List<OwnerQueueItem>();
        CollectHumanLines(plan, state, track, items);
        CollectOwnerGates(plan, state, items);
        CollectPark(state, items);
        CollectWait(state, nowUtc, items);
        CollectBlockedCheckpoints(track, items);
        CollectSkippedStages(plan, state, items);
        return [.. items.OrderBy(i => i.Rank).ThenBy(i => i.Id, StringComparer.Ordinal)];
    }

    // ---- sources -----------------------------------------------------------------------------

    /// <summary>`HUMAN:` lines in the tracker handoff — the agent's own escalation. The run parks on
    /// one (RunLoop → NeedsHuman), so this is nearly always the reason the loop is standing still.</summary>
    private static void CollectHumanLines(PlanConfig plan, RunState state, TrackerSnapshot track, List<OwnerQueueItem> items)
    {
        var token = plan.Conventions.HumanToken;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(track.HandoffBlock)) return;

        var n = 0;
        foreach (var raw in track.HandoffBlock.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            // Handoffs are markdown: the line may arrive as `- **HUMAN:** …` or `> HUMAN: …`. Strip
            // the decoration before testing, or a bulleted escalation is invisible to the queue.
            var line = raw.Trim().TrimStart('-', '*', '>', '#', ' ', '\t').TrimStart('*', ' ');
            if (!line.StartsWith(token, StringComparison.OrdinalIgnoreCase)) continue;

            var text = line[token.Length..].Trim().TrimStart('*', ' ').TrimEnd('*', ' ');
            if (text.Length == 0) text = "(the handoff asks for a decision but does not say which)";
            n++;
            items.Add(new OwnerQueueItem(
                Id: FormattableString.Invariant($"human-{n}"),
                Kind: "human",
                Title: Clip(text, 220),
                // A HUMAN: line stops the whole loop, not one stage — the engine has nothing else to run.
                Unblocks: state.CurrentStage is { Length: > 0 } s
                    ? $"the run — every remaining session on {s} and after it"
                    : "the run — no session is scheduled while it stands",
                Command: "conductor resume",
                // The line is the queue entry AND the park condition, so answering it is two acts:
                // decide, then take the line out of the handoff, or the next session re-parks on it.
                SinceUtc: state.Status == RunStatus.NeedsHuman ? state.AttentionSinceUtc : null,
                Rank: RankHuman,
                Detail: $"answer it, delete the {token} line from {plan.Tracker}'s handoff block, then resume"));
        }
    }

    /// <summary>Owner-gated stages. One that is parked right now is the most urgent thing on the
    /// board; the rest are on the list so the owner can see the approvals coming before they land at
    /// 3am. Approving removes the entry, because the source is <c>OwnerApprovedStages</c> itself.</summary>
    private static void CollectOwnerGates(PlanConfig plan, RunState state, List<OwnerQueueItem> items)
    {
        foreach (var stage in plan.Stages.Where(s => s.OwnerGate))
        {
            if (state.OwnerApprovedStages.Contains(stage.Id)) continue;
            if (state.SkippedStages.Contains(stage.Id)) continue;

            var parkedHere = state.Status == RunStatus.AwaitingOwner
                && state.AwaitingOwnerReason == AwaitingOwnerReason.OwnerGate
                && string.Equals(state.CurrentStage, stage.Id, StringComparison.OrdinalIgnoreCase);

            items.Add(new OwnerQueueItem(
                Id: $"gate-{stage.Id}",
                Kind: "ownerGate",
                Title: parkedHere
                    ? $"approve {stage.Id} — {stage.Title} (the run is parked on it now)"
                    : $"approve {stage.Id} — {stage.Title} (ahead of the run)",
                Unblocks: $"stage {stage.Id} and everything after it",
                Command: "conductor approve",
                SinceUtc: parkedHere ? state.AttentionSinceUtc : null,
                Rank: parkedHere ? RankGateNow : RankGateAhead,
                Detail: parkedHere
                    ? "green gates are not enough for this stage — it advances only when you say so"
                    : "not blocking yet; it will park here even when every gate is green"));
        }
    }

    /// <summary>The park the run is actually sitting in, with its reason and its age. Skipped when an
    /// owner-gate or HUMAN: entry already says the same thing in more detail — the queue names each
    /// obligation once, or the owner reads two lines and clears one.</summary>
    private static void CollectPark(RunState state, List<OwnerQueueItem> items)
    {
        if (state.Status is not (RunStatus.Paused or RunStatus.NeedsHuman or RunStatus.AwaitingOwner)) return;
        if (state.AwaitingOwnerReason == AwaitingOwnerReason.OwnerGate && items.Exists(i => i.Kind == "ownerGate")) return;
        if (state.Status == RunStatus.NeedsHuman && items.Exists(i => i.Kind == "human")) return;

        var (command, what) = state.Status switch
        {
            RunStatus.AwaitingOwner when state.AwaitingOwnerReason == AwaitingOwnerReason.Budget
                => ("conductor approve", "approving resets the budget window and the run continues"),
            RunStatus.AwaitingOwner
                => ("conductor approve", "this run asks for an approval at every stage boundary"),
            RunStatus.NeedsHuman
                => ("conductor resume", "the engine stopped itself and wants a human to look before it spends more"),
            _ => ("conductor resume", "the run is paused and will not schedule a session until you resume"),
        };

        var reason = state.AttentionReason is { Length: > 0 } r ? r : $"the run is {state.Status}";
        items.Add(new OwnerQueueItem(
            Id: "park",
            Kind: "park",
            Title: Clip(reason, 220),
            Unblocks: state.CurrentStage is { Length: > 0 } s ? $"the run, currently on {s}" : "the run",
            Command: command,
            SinceUtc: state.AttentionSinceUtc,
            Rank: RankPark,
            Detail: what));
    }

    /// <summary>A live blocked-until wait. The only entry on the board with NO command: the engine
    /// wakes itself and spawns exactly one session (RunLoop). Saying "conductor resume" here would be
    /// a lie — resume clears a park, and the wait outlives it.</summary>
    private static void CollectWait(RunState state, DateTime nowUtc, List<OwnerQueueItem> items)
    {
        if (state.BlockedUntilUtc is not { } until || until <= nowUtc) return;
        var when = until.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        items.Add(new OwnerQueueItem(
            Id: "wait",
            Kind: "wait",
            Title: state.BlockedReason is { Length: > 0 } r
                ? $"waiting until {when}Z — {Clip(r, 180)}"
                : $"waiting until {when}Z",
            Unblocks: "nothing you can hurry — the next session spawns by itself when the window opens",
            Command: "",
            SinceUtc: state.BlockedSinceUtc,
            Rank: RankWait,
            Detail: "no command clears this; it is here so a sleeping run does not read as a dead one"));
    }

    /// <summary>Checkpoints a session parked with <c>conductor task --blocked</c> — the explicit
    /// "I cannot proceed on this" marker. The stage cannot complete while one is open, so the card is
    /// owed work with a human's name on it.</summary>
    private static void CollectBlockedCheckpoints(TrackerSnapshot track, List<OwnerQueueItem> items)
    {
        foreach (var row in track.Checkpoints.Where(c => c.IsBlocked))
        {
            items.Add(new OwnerQueueItem(
                Id: $"checkpoint-{row.Id}",
                Kind: "checkpoint",
                Title: $"{row.Id} is BLOCKED — {Clip(row.Title, 180)}",
                Unblocks: $"stage {row.StageId} — a blocked card holds it open",
                Command: $"conductor task --todo {row.Id}",
                SinceUtc: null,
                Rank: RankCheckpoint,
                Detail: "unblock it to put it back in front of an agent, or retire it with task --skipped"));
        }
    }

    /// <summary>Stages the operator skipped. <c>conductor skip</c> flags them for human review by
    /// design, and nothing else in the engine ever comes back to them.</summary>
    private static void CollectSkippedStages(PlanConfig plan, RunState state, List<OwnerQueueItem> items)
    {
        foreach (var id in state.SkippedStages.OrderBy(s => s, StringComparer.Ordinal))
        {
            var title = plan.Stages.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))?.Title;
            items.Add(new OwnerQueueItem(
                Id: $"skipped-{id}",
                Kind: "skippedStage",
                Title: $"{id} was skipped and flagged for review{(title is { Length: > 0 } t ? $" — {t}" : "")}",
                Unblocks: "nothing is waiting on it; the run moved on without the work",
                Command: $"conductor goto {id}",
                SinceUtc: null,
                Rank: RankSkipped,
                Detail: "review what was skipped: go back to the stage, or accept the gap deliberately"));
        }
    }

    // ---- rendering ---------------------------------------------------------------------------

    /// <summary>The owner-facing markdown. Voice copied from `SHAHIN.md`: second person, the things
    /// only you can do, each with what it unblocks and the command underneath it.</summary>
    public static string Render(PlanConfig plan, RunState state, IReadOnlyList<OwnerQueueItem> items, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(items);

        var sb = new StringBuilder();
        sb.AppendLine(FormattableString.Invariant($"# Owner queue — {plan.Name}"));
        sb.AppendLine();
        sb.AppendLine(FormattableString.Invariant(
            $"_The things only you can do. Regenerated at every session boundary · {nowUtc:yyyy-MM-dd HH:mm} UTC · run is {state.Status}_"));
        sb.AppendLine();

        if (items.Count == 0)
        {
            // Said out loud, never implied. A file that simply stops after the header is
            // indistinguishable from a stale one, and that ambiguity is what the whole surface exists
            // to kill: the owner must be able to read "nothing" and believe it.
            sb.AppendLine("**Nothing is waiting on you.** The run has no park, no `HUMAN:` line, no unapproved");
            sb.AppendLine("owner gate, no blocked checkpoint and no skipped stage. This file is rewritten every");
            sb.AppendLine("session boundary, so an empty list here is current, not forgotten.");
            sb.AppendLine();
            return sb.ToString();
        }

        sb.AppendLine(FormattableString.Invariant(
            $"**{items.Count} item{(items.Count == 1 ? "" : "s")} need you.** Most urgent first."));
        sb.AppendLine();

        var n = 0;
        foreach (var item in items)
        {
            n++;
            sb.AppendLine(FormattableString.Invariant($"### {n}. {item.Title}"));
            sb.AppendLine();
            sb.AppendLine($"- **Unblocks:** {item.Unblocks}");
            sb.AppendLine($"- **Age:** {AgeText(item, nowUtc)}");
            if (item.Detail is { Length: > 0 } d) sb.AppendLine($"- **Why you:** {d}");
            sb.AppendLine(item.Command.Length > 0
                ? $"- **Clears with:** `{item.Command}`"
                : "- **Clears with:** nothing to type — it clears itself");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string AgeText(OwnerQueueItem item, DateTime nowUtc)
        => item.SinceUtc is { } s
            ? FormattableString.Invariant(
                $"{Staleness.Age(nowUtc - s)} (since {s.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}Z)")
            : "unknown — this source carries no timestamp";

    /// <summary>Collect + render + write <c>.conductor/OWNER-QUEUE.md</c>. Tolerant of I/O failure the
    /// same way the report is (A15): the queue is a convenience surface and must never take a run down.</summary>
    public static void Write(PlanConfig plan, RunState state, TrackerSnapshot track, Action<string> log, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(log);
        var now = nowUtc ?? DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(plan.StateDir);
            var items = Collect(plan, state, track, now);
            File.WriteAllText(QueuePath(plan), Render(plan, state, items, now), Reporter.Utf8Bom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"owner queue write failed: {ex.Message}");
        }
    }

    private static string Clip(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }
}
