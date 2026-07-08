using Conductor.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Conductor.Ui;

/// <summary>Which rows the hierarchical plan tree shows (B4.3). <c>Active</c> = the running stage and
/// any in-progress checkpoint; <c>Failed</c> = BLOCKED rows or stages whose last session outcome was
/// a failure; <c>Todo</c> = anything not yet DONE.</summary>
public enum PlanFilter { All, Todo, Active, Failed }

/// <summary>User's view of the plan tree: the active filter, a free-text search, and which stages are
/// expanded. Immutable so it can live on <see cref="DashboardState"/> and be diffed for tests.</summary>
public sealed record PlanTreeView
{
    public PlanFilter Filter { get; init; } = PlanFilter.All;
    public string Search { get; init; } = "";
    /// <summary>Stage ids the user has explicitly expanded. The active stage is always expanded; any
    /// stage is auto-expanded while a filter/search is narrowing so its matches are visible.</summary>
    public IReadOnlySet<string> Expanded { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public bool ExpandAll { get; init; }

    public bool Narrowing => Filter != PlanFilter.All || !string.IsNullOrWhiteSpace(Search);
}

/// <summary>One flattened row of the rendered tree — a stage header or an indented checkpoint.</summary>
public readonly record struct PlanTreeRow(bool IsStage, string Id, string Title, string Status, StageProgress Stage);

/// <summary>
/// Pure plan-tree logic: given the per-stage roll-up and a <see cref="PlanTreeView"/>, computes the
/// visible rows (honouring filter/search/expand) and renders them as a Spectre table with per-stage
/// columns (done · runs+outcome · cost). No IO, no terminal — fully unit-testable (R4.3).
/// </summary>
public static class PlanTree
{
    /// <summary>Flatten stages → visible rows. A stage is shown when it (or, under a filter/search, one
    /// of its checkpoints) matches; checkpoint rows follow when the stage is expanded.</summary>
    public static IReadOnlyList<PlanTreeRow> VisibleRows(IReadOnlyList<StageProgress> stages, PlanTreeView view)
    {
        var rows = new List<PlanTreeRow>();
        foreach (var s in stages)
        {
            var matching = s.Checkpoints
                .Where(c => MatchesFilter(c.Status, s, view.Filter) && MatchesSearch(c.Id, c.Title, view.Search))
                .ToList();
            var stageSelfMatch = MatchesSearch(s.Id, s.Title, view.Search) && StageMatchesFilter(s, view.Filter);

            var showStage = !view.Narrowing || matching.Count > 0 || stageSelfMatch;
            if (!showStage) continue;

            rows.Add(new PlanTreeRow(true, s.Id, s.Title, s.State, s));

            if (!IsExpanded(s, view)) continue;
            // Narrowing with checkpoint matches → show only those; otherwise (no filter, or the stage
            // itself matched by name) show the whole checkpoint list for context.
            var cps = view.Narrowing && matching.Count > 0 ? matching : s.Checkpoints.ToList();
            foreach (var (id, title, status) in cps)
                rows.Add(new PlanTreeRow(false, id, title, status, s));
        }
        return rows;
    }

    private static bool IsExpanded(StageProgress s, PlanTreeView view)
        => view.ExpandAll || view.Narrowing || s.State == "active" || view.Expanded.Contains(s.Id);

    private static bool MatchesSearch(string id, string title, string search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return id.Contains(search, StringComparison.OrdinalIgnoreCase)
            || title.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesFilter(string status, StageProgress stage, PlanFilter filter) => filter switch
    {
        PlanFilter.All => true,
        PlanFilter.Todo => !status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase),
        PlanFilter.Active => stage.State == "active" || status.StartsWith("IN", StringComparison.OrdinalIgnoreCase),
        PlanFilter.Failed => status.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase) || IsFailedOutcome(stage.LastOutcome),
        _ => true,
    };

    private static bool StageMatchesFilter(StageProgress s, PlanFilter filter) => filter switch
    {
        PlanFilter.All => true,
        PlanFilter.Todo => s.Done < s.Total || s.Total == 0,
        PlanFilter.Active => s.State == "active",
        PlanFilter.Failed => IsFailedOutcome(s.LastOutcome),
        _ => true,
    };

    private static bool IsFailedOutcome(string outcome) => outcome is "GatesRed" or "AgentError" or "Stalled" or "TimedOut";

    public static IRenderable Build(IReadOnlyList<StageProgress> stages, PlanTreeView view)
    {
        var rows = VisibleRows(stages, view);
        var t = new Table().Border(TableBorder.None).Expand();
        t.AddColumn(new TableColumn("#").NoWrap());
        t.AddColumn(new TableColumn("item"));
        t.AddColumn(new TableColumn("meta").RightAligned().NoWrap());

        if (rows.Count == 0)
        {
            t.AddRow(Esc("—"), $"[grey](no stages match {FilterLabel(view.Filter).ToLowerInvariant()}" +
                (string.IsNullOrWhiteSpace(view.Search) ? "" : $"/“{Esc(view.Search)}”") + ")[/]", "");
            return t;
        }

        foreach (var r in rows)
        {
            if (r.IsStage)
            {
                var s = r.Stage;
                var glyph = IsExpanded(s, view) ? "▾" : "▸";
                var (color, badge) = StageMark(s.State);
                var id = $"[{color}]{glyph} {Esc(s.Id)}[/]";
                var title = $"[{color}]{Esc(Clip(s.Title, 30))}[/]{badge}";
                t.AddRow(new Markup(id), new Markup(title), new Markup(StageMeta(s, color)));
            }
            else
            {
                var color = StatusColor(r.Status);
                t.AddRow(
                    new Markup($"[grey]  ↳[/] [{color}]{Esc(r.Id)}[/]"),
                    new Markup($"[silver]{Esc(Clip(r.Title, 40))}[/]"),
                    new Markup($"[{color}]{Esc(ShortStatus(r.Status))}[/]"));
            }
        }
        return t;
    }

    /// <summary>Compact per-stage columns folded into one right-aligned cell: done · runs+outcome · cost.</summary>
    private static string StageMeta(StageProgress s, string color)
    {
        var parts = new List<string> { $"[{color}]{s.Done}/{s.Total}[/]" };
        if (s.Attempts > 0)
            parts.Add($"[grey]{s.Attempts}×{(s.LastOutcome.Length > 0 ? " " + OutcomeAbbr(s.LastOutcome) : "")}[/]");
        if (s.CostUsd > 0) parts.Add($"[grey]${s.CostUsd:0.00}[/]");
        return string.Join(" [grey37]·[/] ", parts);
    }

    public static string FilterLabel(PlanFilter f) => f switch
    {
        PlanFilter.All => "All",
        PlanFilter.Todo => "Todo",
        PlanFilter.Active => "Active",
        PlanFilter.Failed => "Failed",
        _ => "All",
    };

    public static PlanFilter NextFilter(PlanFilter f) => f switch
    {
        PlanFilter.All => PlanFilter.Todo,
        PlanFilter.Todo => PlanFilter.Active,
        PlanFilter.Active => PlanFilter.Failed,
        PlanFilter.Failed => PlanFilter.All,
        _ => PlanFilter.All,
    };

    private static (string Color, string Badge) StageMark(string state) => state switch
    {
        "confirmed" => ("green", " [green]✓[/]"),
        "done" => ("green", ""),
        "gating" => ("yellow", " [yellow]gating…[/]"),
        "active" => ("bold yellow", " [bold yellow]← active[/]"),
        "skipped" => ("red", " [red]skipped[/]"),
        _ => ("grey", ""),
    };

    private static string StatusColor(string status)
        => status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase) ? "green"
            : status.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase) ? "red"
            : status.StartsWith("IN", StringComparison.OrdinalIgnoreCase) ? "yellow" : "grey";

    private static string ShortStatus(string status)
    {
        if (status.StartsWith("IN", StringComparison.OrdinalIgnoreCase)) return "active";
        if (status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase)) return "done";
        return status.ToLowerInvariant();
    }

    /// <summary>Compact outcome tag for the "runs" column (SessionOutcome names are long).</summary>
    public static string OutcomeAbbr(string outcome) => outcome switch
    {
        "Advanced" => "adv",
        "Progress" => "prog",
        "NoProgress" => "noop",
        "GatesRed" => "red",
        "Stalled" => "stall",
        "TimedOut" => "t/o",
        "AgentError" => "err",
        "LimitBackoff" => "limit",
        "KilledByUser" => "kill",
        "Interrupted" => "intr",
        "" => "",
        _ => outcome.Length > 5 ? outcome[..5].ToLowerInvariant() : outcome.ToLowerInvariant(),
    };

    private static string Clip(string s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    private static string Esc(string s) => Markup.Escape(s ?? "");
}
