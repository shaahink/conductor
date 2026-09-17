using System.Globalization;
using System.Text;

using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Models;

namespace Conductor.Core.Integrations.Messaging;

/// <summary>PK4.2 / D7 — one card the engine posts to a room's observer chat: its composed text and the
/// photos that ride with it (the before/after pair, or none).</summary>
/// <param name="Kind"><see cref="CheckpointKind"/> or <see cref="StageKind"/>.</param>
/// <param name="Id">The checkpoint or stage it is about.</param>
public sealed record CardPush(string Kind, string Id, string Text, IReadOnlyList<string> Photos)
{
    public const string CheckpointKind = "checkpoint";
    public const string StageKind = "stage";
}

/// <summary>What a card counts, from the work graph and the plan.</summary>
/// <param name="Done">Checkpoints the engine CONFIRMED — a claim is not a done.</param>
/// <param name="Total">Every checkpoint that is not skipped.</param>
/// <param name="Live">Confirmed checkpoints of every stage the live rule calls live.</param>
/// <param name="StageLive">Whether the card's own stage is live.</param>
public sealed record CardCounts(int Done, int Total, int Live, bool StageLive);

/// <summary>PK4.2 / D7 — the numbers and the pair a card is composed from, kept pure so the rule is
/// testable without a run: the bar, the counts, the live rule and the before/after pair.
///
/// <para><b>The live rule</b> is the skill's, now the engine's: a stage is live when its LAST checkpoint
/// is done, and then all of its done checkpoints are live. A plan that deploys somewhere other than its
/// last checkpoint names it — <c>stages[].deploys</c> — and that checkpoint decides instead. "Done" here
/// is confirmed, never claimed: the card is posted at the verdict, and it counts what the verdict
/// said.</para></summary>
public static class CardFacts
{
    /// <summary>The counts line when the room names none.</summary>
    public const string DefaultCounters = "{done}/{total} done · {live} live";

    /// <summary>The footer for a live stage when the room names none.</summary>
    public const string DefaultLive = "🚀 Live";

    /// <summary>The footer for a stage that is not live yet when the room names none.</summary>
    public const string DefaultPending = "⏳ Stage {stage}, not live yet";

    /// <summary>How many commit subjects a stage card lists before it says how many more.</summary>
    public const int MaxChanges = 8;

    /// <summary>Ten cells, the same bar the report draws.</summary>
    public static string Bar(int done, int total)
    {
        if (total <= 0) return "";
        const int width = 10;
        var filled = Math.Clamp((int)Math.Round((double)done / total * width), 0, width);
        return new string('█', filled) + new string('░', width - filled);
    }

    /// <summary>The counts for a card about <paramref name="stageId"/>.</summary>
    public static CardCounts Count(PlanConfig plan, TaskGraph graph, string stageId)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(graph);

        var checkpoints = graph.Checkpoints().Where(c => c.Status != "skipped").ToList();
        var done = checkpoints.Count(c => c.Confirmed);
        var live = 0;
        var stageLive = false;
        foreach (var group in checkpoints.GroupBy(c => c.StageId, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsLive(plan, group.Key, [.. group])) continue;
            live += group.Count(c => c.Confirmed);
            if (string.Equals(group.Key, stageId, StringComparison.OrdinalIgnoreCase)) stageLive = true;
        }
        return new CardCounts(done, checkpoints.Count, live, stageLive);
    }

    /// <summary>A room's string with <c>{done}</c>, <c>{total}</c>, <c>{live}</c> and <c>{stage}</c> filled
    /// in — the four names the skill's config strings were written with. Anything else in braces is left
    /// as the room wrote it.</summary>
    public static string Fill(string template, CardCounts counts, string stageId)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(counts);
        return new StringBuilder(template)
            .Replace("{done}", counts.Done.ToString(CultureInfo.InvariantCulture))
            .Replace("{total}", counts.Total.ToString(CultureInfo.InvariantCulture))
            .Replace("{live}", counts.Live.ToString(CultureInfo.InvariantCulture))
            .Replace("{stage}", stageId)
            .ToString();
    }

    /// <summary>The before/after pair registered for a checkpoint: the newest visual artifact whose file
    /// name ends in <c>before</c> and the newest ending in <c>after</c> (<c>S3.1-before.png</c>), both
    /// resolving to a file on this machine. Null unless both halves are there — half a comparison is not
    /// one.</summary>
    public static IReadOnlyList<string>? Pair(EvidenceRegistry registry, string checkpointId, Func<string, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolve);

        var visual = registry.ForCheckpoint(checkpointId).Where(a => EvidenceKinds.IsVisual(a.Kind))
            .OrderByDescending(a => a.CreatedUtc).ToList();
        var before = Half(visual, "before", resolve);
        var after = Half(visual, "after", resolve);
        return before is null || after is null ? null : [before, after];
    }

    /// <summary>A stage card's list of what changed: one line per commit subject, newest last, clipped
    /// with a count of the rest.</summary>
    public static string Changes(IReadOnlyList<string> subjects)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        var shown = subjects.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var lines = shown.Take(MaxChanges).Select(s => "• " + MessageComposer.EscapeHtml(s.Trim())).ToList();
        if (shown.Count > MaxChanges)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"… and {shown.Count - MaxChanges} more"));
        return string.Join("\n", lines);
    }

    private static bool IsLive(PlanConfig plan, string stageId, IReadOnlyList<Models.TaskItem> checkpoints)
    {
        var deploys = plan.Stages.FirstOrDefault(s => string.Equals(s.Id, stageId, StringComparison.OrdinalIgnoreCase))?.Deploys;
        if (deploys is { Length: > 0 })
            return checkpoints.Any(c => string.Equals(c.CheckpointId, deploys, StringComparison.OrdinalIgnoreCase) && c.Confirmed);

        var last = checkpoints.OrderBy(c => c.Order).LastOrDefault();
        return last is { Confirmed: true };
    }

    private static string? Half(IEnumerable<EvidenceArtifact> visual, string suffix, Func<string, string?> resolve) =>
        visual.Where(a => Path.GetFileNameWithoutExtension(a.Path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(a => resolve(a.Path)).FirstOrDefault(p => p is not null);
}
