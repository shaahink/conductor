using Conductor.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Conductor.Ui;

/// <summary>
/// Pure rendering: <see cref="DashboardState"/> → Spectre <see cref="IRenderable"/>. No threads,
/// no IO, no mutable state — so every piece (action bar, cost line, gate chips) is unit-testable.
/// </summary>
public static class DashboardRenderer
{
    private static readonly string[] Spinner = { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };

    public static IRenderable BuildRoot(DashboardState st)
    {
        // Height-aware sizing: fixed regions must never sum to more than the viewport, or the
        // Layout can't fit and Spectre's Live scrolls the terminal (headers stack at the top).
        var h = Math.Max(12, st.Height);

        // Compact mode for short terminals: a single body panel (no stacked/nested panels that
        // each need their own borders) so the layout always fits.
        if (h < 24)
        {
            var cfooter = Math.Clamp(h - 5 - 4, 3, 5);
            var croot = new Layout("root").SplitRows(
                new Layout("header").Size(5),
                new Layout("body"),
                new Layout("footer").Size(cfooter));
            croot["header"].Update(HeaderPanel(st, compact: true));
            croot["body"].Update(AgentPanel(st));
            croot["footer"].Update(FooterPanel(st));
            return croot;
        }

        const int header = 7;
        var footer = Math.Clamp(h - header - 6, 3, FooterHeight(st));
        var wide = st.Width >= 150;

        // The whole layout is one declarative Layout tree (Split + Update leaves) — no scattered
        // Rows/Panel stacking. Every region is sized by the engine, so nothing overflows the viewport.
        Layout root;
        if (wide)
        {
            root = new Layout("root").SplitRows(
                new Layout("header").Size(header),
                new Layout("body").SplitColumns(
                    new Layout("left").Ratio(26),
                    new Layout("center").Ratio(40),
                    new Layout("right").Ratio(34).SplitRows(new Layout("thinking"), new Layout("gates"))),
                new Layout("footer").Size(footer));
            root["center"].Update(AgentPanel(st));
            root["thinking"].Update(ThinkingPanel(st));
            root["gates"].Update(GatePanel(st));
        }
        else
        {
            root = new Layout("root").SplitRows(
                new Layout("header").Size(header),
                new Layout("body").SplitColumns(
                    new Layout("left").Ratio(1),
                    new Layout("right").Ratio(2).SplitRows(
                        new Layout("agent").Ratio(3),
                        new Layout("thinking").Ratio(2))),
                new Layout("footer").Size(footer));
            root["agent"].Update(AgentPanel(st));
            root["thinking"].Update(ThinkingPanel(st));
        }

        root["left"].Update(PlanPanel(st));
        root["header"].Update(HeaderPanel(st, compact: false));
        root["footer"].Update(FooterPanel(st));
        return root;
    }

    private static int FooterHeight(DashboardState st) => Math.Min(11, 5 + Math.Min(5, st.Log.Count));

    // ---------------------------------------------------------------- header

    /// <summary>
    /// Header as a two-column <see cref="Grid"/>: identity/activity on the left, the live metrics
    /// (checkpoints · cost · tokens) right-aligned in their own column so they line up frame to
    /// frame. A fixed row count that fits the header region — this is what keeps the header-stacking
    /// bug (F-5) retired: the right column is <c>NoWrap</c>, so metrics never wrap onto new lines
    /// that would push the identity block down and out of the region.
    /// </summary>
    private static IRenderable HeaderPanel(DashboardState st, bool compact)
    {
        var s = st.Snap;
        var statusColor = StatusColor(s.Status);
        var title = $"[bold aqua]Conductor[/] — [bold]{Esc(s.PlanName)}[/]   [{statusColor}]● {Esc(s.Status)}[/]" +
                    (s.AttentionReason != null ? $"  [red]{Esc(s.AttentionReason)}[/]" : "");

        // Both columns NoWrap: every grid row is then exactly one line tall, so a long identity
        // string is clipped (acceptable) rather than wrapping and pushing the metrics rows down and
        // out of the fixed header region — the header can never stack or lose its metrics (R4.2/F-5).
        var grid = new Grid();
        grid.AddColumn(new GridColumn().PadRight(2).NoWrap());
        grid.AddColumn(new GridColumn().RightAligned().NoWrap());

        if (compact)
        {
            var stageLine = $"stage [bold]{Esc(s.StageId)}[/] {Esc(Clip(s.StageTitle, 40))}" +
                            (s.SessionNumber > 0 ? $" · #{s.SessionNumber} {Esc(s.SessionKind)}" : "");
            grid.AddRow(new Markup(title), new Markup(CheckpointsLine(s)));
            grid.AddRow(new Markup(ActivityLine(st)), new Markup(CostLine(s)));
            grid.AddRow(new Markup(stageLine), new Markup(TokenLine(s)));
            return new Panel(grid).Expand().Border(BoxBorder.Rounded);
        }

        var line2 = $"stage [bold]{Esc(s.StageId)}[/] {Esc(s.StageTitle)}" +
                    (s.SessionNumber > 0 ? $" · session #{s.SessionNumber} {Esc(s.SessionKind)}" : "") +
                    (s.Attempt > 0 ? $" · attempt {s.Attempt}/{s.MaxAttempts}" : "") +
                    (s.ResumeCount > 0 ? $" · resume {s.ResumeCount}" : "");
        var line3 = !string.IsNullOrEmpty(s.CurrentCheckpoint)
            ? $"[aqua]▸ {Esc(s.CurrentCheckpoint)}[/]" +
              (!string.IsNullOrEmpty(s.CurrentCheckpointTitle) ? $" [silver]{Esc(Clip(s.CurrentCheckpointTitle, 80))}[/]" : "")
            : ProgressLine(s);

        grid.AddRow(new Markup(title), new Markup(CheckpointsLine(s)));
        grid.AddRow(new Markup(line2), new Markup(CostLine(s)));
        grid.AddRow(new Markup(line3), new Markup(TokenLine(s)));
        grid.AddRow(new Markup(ActivityLine(st)), new Markup(""));
        return new Panel(grid).Expand().Border(BoxBorder.Rounded);
    }

    /// <summary>The "who is working?" line — agent vs conductor gates vs paused/backoff — with a live spinner.</summary>
    public static string ActivityLine(DashboardState st)
    {
        var s = st.Snap;
        var spin = Spinner[st.Tick % Spinner.Length];
        switch (s.Status)
        {
            case "Running":
                return s.AgentActive
                    ? $"[green]{spin} agent working[/] · {Esc(s.SessionKind)} · elapsed {Fmt(s.SessionElapsed)} · last output {s.LastActivityAgoSec:0}s ago"
                    : $"[grey]{spin} preparing session…[/]";
            case "VerifyingGates":
                return $"[yellow]{spin} conductor verifying gates[/] (no agent running) " + GateChips(s.Gates);
            case "Backoff":
                var left = s.BackoffUntilUtc is { } u ? Math.Max(0, (u - DateTime.UtcNow).TotalMinutes) : 0;
                return $"[orange1]{spin} usage-limit backoff — ~{left:0}m left[/]";
            case "Paused":
                return "[grey]⏸ paused — press R or `conductor resume` to continue[/]";
            case "NeedsHuman":
                return "[red]■ needs human — resolve, then R / `conductor resume`[/]";
            case "Completed":
                return "[aqua]✔ plan complete[/]";
            case "Aborted":
                return "[red]■ aborted[/]";
            default:
                return $"[grey]{spin} idle[/]";
        }
    }

    private static string ProgressLine(DashboardSnapshot s)
        => $"{CheckpointsLine(s)} · {CostLine(s)} · {TokenLine(s)}";

    /// <summary>Just the checkpoint progress fragment, shared by the header metrics column and the
    /// single-line <see cref="ProgressLine"/> fallback.</summary>
    public static string CheckpointsLine(DashboardSnapshot s)
    {
        var pct = s.TotalCount > 0 ? (int)Math.Round(100.0 * s.DoneCount / s.TotalCount) : 0;
        return $"checkpoints [bold]{s.DoneCount}/{s.TotalCount}[/] ({pct}%)";
    }

    /// <summary>Log entries with severity colour + glyph prefix so the footer log distinguishes
    /// warnings, errors, successes, and human-in-the-loop messages from noise (B4.4).</summary>
    public static (string Color, string Glyph) SeverityGlyph(LogSeverity s) => s switch
    {
        LogSeverity.Info => ("grey", "·"),
        LogSeverity.Warn => ("orange1", "!"),
        LogSeverity.Error => ("red", "✗"),
        LogSeverity.Success => ("green", "✓"),
        LogSeverity.Waiting => ("yellow", "…"),
        LogSeverity.Human => ("bold aqua", "§"),
        _ => ("grey", "·"),
    };

    /// <summary>Converts a <see cref="LogSeverity"/> to a Spectre colour for use in markup.</summary>
    public static string SeverityColor(LogSeverity s) => SeverityGlyph(s).Color;
    public static string CostLine(DashboardSnapshot s)
    {
        var combined = s.TotalCostUsd + s.SessionCostUsd;
        var txt = $"cost [bold]${combined:0.0000}[/]";
        if (s.SessionCostUsd > 0) txt += $" [grey](session ${s.SessionCostUsd:0.0000})[/]";
        if (s.UntrackedSessions > 0) txt += $" [grey]· {s.UntrackedSessions} sessions unreported[/]";
        return txt;
    }

    public static string TokenLine(DashboardSnapshot s)
    {
        var sessionTotal = s.SessionTokensInput + s.SessionTokensOutput + s.SessionTokensReasoning;
        var total = s.TokensInput + s.TokensOutput + s.TokensReasoning + sessionTotal;
        if (total == 0) return "[grey]tokens —[/]";
        var parts = $"{Human(s.TokensInput + s.SessionTokensInput)} in · {Human(s.TokensOutput + s.SessionTokensOutput)} out";
        if (s.TokensReasoning + s.SessionTokensReasoning > 0) parts += $" · {Human(s.TokensReasoning + s.SessionTokensReasoning)} think";
        var txt = $"tokens {parts} · [bold]{Human(total)} total[/]";
        // Live-consistent with the cost line (B4.7 / F-3): break out the running session's live delta
        // explicitly — the same shape the cost line uses for its `(session $…)` — so tokens and cost
        // always agree and an AFK operator can see current burn, not just an all-time total.
        if (sessionTotal > 0) txt += $" [grey](session {Human(sessionTotal)})[/]";
        return txt;
    }

    private static string GateChips(IReadOnlyList<GateProgress> gates)
    {
        if (gates.Count == 0) return "";
        var now = DateTime.UtcNow;
        return string.Join("  ", gates.Select(g =>
        {
            var (color, glyph) = g.State switch
            {
                "pass" => ("green", "✓"),
                "fail" => ("red", "✗"),
                "running" => ("yellow", "…"),
                "warn" => ("orange1", "!"),
                "skip" => ("grey", "-"),
                _ => ("grey35", "·"),
            };
            var el = g.LiveElapsed(now);
            var t = el > TimeSpan.Zero ? $" {Fmt(el)}" : "";
            return $"[{color}]{Esc(g.Name)}{glyph}{t}[/]";
        }));
    }

    // ---------------------------------------------------------------- left column (hierarchical plan tree)

    private static IRenderable PlanPanel(DashboardState st)
    {
        var stages = StagesFor(st.Snap);
        var v = st.Tree;
        var chips = new[] { PlanFilter.All, PlanFilter.Todo, PlanFilter.Active, PlanFilter.Failed }
            .Select(f => f == v.Filter ? $"[bold aqua]{PlanTree.FilterLabel(f)}[/]" : $"[grey]{PlanTree.FilterLabel(f)}[/]");
        var header = $"[aqua]plan[/] [grey](F/↑↓/D)[/] " + string.Join("[grey]/[/]", chips) +
                     (string.IsNullOrWhiteSpace(v.Search) ? "" : $" [grey]search:[/][silver]{Esc(v.Search)}[/]");
        return new Panel(PlanTree.Build(stages, v)).Header(header).Expand().Border(BoxBorder.Rounded);
    }

    /// <summary>Prefer the full per-stage roll-up (<see cref="DashboardSnapshot.Stages"/>); fall back to
    /// deriving it from the legacy <c>StageOverview</c>/<c>StageCheckpoints</c> pair so older snapshots
    /// (and focused tests) still render a tree. Public so the dashboard can drive plan-tree selection
    /// (B4.7) off exactly the stages it renders.</summary>
    public static IReadOnlyList<StageProgress> StagesFor(DashboardSnapshot s)
    {
        if (s.Stages.Count > 0) return s.Stages;
        return s.StageOverview.Select(o => new StageProgress
        {
            Id = o.StageId,
            Title = "",
            Done = o.Done,
            Total = o.Total,
            State = o.State,
            Checkpoints = o.StageId.Equals(s.StageId, StringComparison.OrdinalIgnoreCase)
                ? s.StageCheckpoints
                : Array.Empty<(string, string, string)>(),
        }).ToList();
    }

    // ---------------------------------------------------------------- right column (thinking + gates)

    private static IRenderable GatePanel(DashboardState st)
        => new Panel(GateBody(st)).Header("[aqua]gates[/]").Expand().Border(BoxBorder.Rounded);

    private static IRenderable ThinkingPanel(DashboardState st)
        => new Panel(ThinkingBody(st)).Header("[aqua]thinking[/] [grey](T)[/]").Expand().Border(BoxBorder.Rounded);

    private static IRenderable ThinkingBody(DashboardState st)
    {
        if (st.Thinking.Count == 0)
            return new Markup("[grey](no thinking captured yet)[/]");
        return new Rows(st.Thinking.Select(t => ThinkingRow(t)).ToArray());
    }

    /// <summary>Renders one reasoning block as a Goal/Hypothesis/Evidence/Action digest when the
    /// parser finds structure, else the raw single line — so no reasoning is ever dropped (B4.5).</summary>
    private static IRenderable ThinkingRow(DashboardState.ThinkingLine t)
    {
        var ts = $"[grey37]{Local(t.Utc):HH:mm:ss}[/]";
        var parsed = StructuredThinking.Parse(t.Text);
        if (!parsed.HasStructure)
            return new Markup($"{ts} [grey37]~ {Esc(Clip(parsed.Raw, 180))}[/]");

        var facets = new List<IRenderable>();
        void Facet(string glyph, string color, string label, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            facets.Add(new Markup($"  [{color}]{glyph} {label}[/] [grey]{Esc(Clip(value!, 150))}[/]"));
        }
        Facet("◎", "aqua", "goal", parsed.Goal);
        Facet("?", "yellow", "hyp", parsed.Hypothesis);
        Facet("✎", "green", "evidence", parsed.Evidence);
        Facet("→", "deepskyblue1", "action", parsed.Action);
        return new Rows(new IRenderable[] { new Markup($"{ts}") }.Concat(facets).ToArray());
    }

    private static IRenderable GateBody(DashboardState st)
    {
        var gates = st.Snap.Gates;
        if (gates.Count == 0)
            return new Markup(string.IsNullOrEmpty(st.Snap.GateSummary)
                ? "[grey](no gate run yet)[/]"
                : Esc(st.Snap.GateSummary));
        var t = new Table().Border(TableBorder.None).Expand();
        t.AddColumn("gate");
        t.AddColumn("state");
        var now = DateTime.UtcNow;
        foreach (var g in gates)
        {
            var (color, label) = g.State switch
            {
                "pass" => ("green", "✓ pass"),
                "fail" => ("red", "✗ fail"),
                "running" => ("yellow", "… running"),
                "warn" => ("orange1", "! warn"),
                "skip" => ("grey", "- skip"),
                _ => ("grey35", "pending"),
            };
            var live = g.LiveElapsed(now);
            var el = live > TimeSpan.Zero ? $" [grey]{Fmt(live)}[/]" : "";
            t.AddRow(Esc(g.Name), $"[{color}]{label}[/]{el}");
        }
        return t;
    }

    // ---------------------------------------------------------------- center (agent)

    private static IRenderable AgentPanel(DashboardState st)
    {
        var rows = AgentFold.Build(st.Agent, st.AgentExpanded);
        IRenderable body = rows.Count > 0
            ? new Rows(rows.Select(r => (IRenderable)new Markup(AgentLine(r))).ToArray())
            : new Markup("[grey](no agent output yet)[/]");
        var hint = st.AgentExpanded ? "[grey](C fold)[/]" : "[grey](C expand)[/]";
        return new Panel(body).Header($"[aqua]agent[/] [grey](O)[/] {hint}").Expand().Border(BoxBorder.Rounded);
    }

    /// <summary>Renders a folded agent row: tool headers show a "(N lines)" badge for hidden output,
    /// expanded output lines are indented under their tool call (B4.5).</summary>
    private static string AgentLine(AgentFold.Row r)
    {
        var (glyph, color) = r.Kind switch
        {
            "tool" => ("»", "deepskyblue1"),
            "text" => ("·", "silver"),
            "result" => ("◆", "aqua"),
            "stderr" => ("!", "orange1"),
            "system" => ("○", "grey"),
            _ => (" ", "grey"),
        };
        var indent = r.Indent ? "  " : "";
        var badge = r.IsToolHeader && r.FoldedCount > 0 ? $" [grey37]▸ ({r.FoldedCount} lines)[/]" : "";
        return $"{indent}[{color}]{Local(r.Utc):HH:mm:ss} {glyph} {Esc(r.Text)}[/]{badge}";
    }

    // ---------------------------------------------------------------- footer (log + actions)

    private static IRenderable FooterPanel(DashboardState st)
    {
        // Action bar first so it survives when a short viewport crops the region from the bottom;
        // the confirm prompt and gate summary sit next (both are attention-worthy), and the scrolling
        // log lives below a Rule so the boundary between "controls" and "history" is unambiguous.
        var rows = new List<IRenderable> { new Markup(ActionBar(st.Snap.Status)) };
        if (st.ConfirmPrompt != null)
            rows.Add(new Markup($"[bold yellow]⚠ {Esc(st.ConfirmPrompt)}[/]"));
        if (!string.IsNullOrEmpty(st.Snap.GateSummary))
            rows.Add(new Markup("[bold]gates:[/] " + Esc(st.Snap.GateSummary)));
        if (st.Log.Count > 0)
        {
            rows.Add(new Rule("[grey37]log[/]").LeftJustified().RuleStyle("grey37"));
            rows.AddRange(st.Log.Select(l =>
            {
                var (color, glyph) = SeverityGlyph(l.Severity);
                return (IRenderable)new Markup($"[{color}]{glyph}[/] [grey]{Esc(l.Text)}[/]");
            }));
        }
        return new Panel(new Rows(rows)).Header("[aqua]conductor[/]").Expand().Border(BoxBorder.Rounded);
    }

    /// <summary>State-machine action bar: only the actions valid in the current status are shown.</summary>
    public static string ActionBar(string status)
    {
        var actions = new List<string>();
        void Add(string key, string label) => actions.Add($"[[{key}]] {label}");

        switch (status)
        {
            case "Running":
            case "VerifyingGates":
            case "Idle":
                Add("P", "pause"); Add("K", "kill"); Add("S", "skip");
                Add("I", "inject"); Add("G", "status"); Add("Q", "quit"); Add("A", "abort");
                break;
            case "Backoff":
                Add("R", "resume now"); Add("G", "status"); Add("A", "abort");
                break;
            case "Paused":
                Add("R", "resume"); Add("S", "skip"); Add("G", "status"); Add("A", "abort");
                break;
            case "NeedsHuman":
                Add("R", "resume"); Add("S", "skip"); Add("I", "inject"); Add("G", "status"); Add("A", "abort");
                break;
            case "AwaitingOwner":
                Add("R", "approve"); Add("S", "skip"); Add("G", "status"); Add("A", "abort");
                break;
            case "Completed":
            case "Aborted":
                Add("Q", "quit");
                break;
            default:
                Add("Q", "quit"); Add("A", "abort");
                break;
        }
        // Pop-out viewers + inject are available whenever a session/buffer exists.
        actions.Add("[grey][[T]] think · [[O]] history · [[L]] timeline · [[C]] fold · [[↑↓]] select · [[D]] docs · [[V]] git · [[X]] prompt · [[F]] filter · [[E]] expand · [[I]] inject[/]");
        return "[grey]" + string.Join("  ", actions) + "[/]";
    }

    // ---------------------------------------------------------------- modal (scrollable pager)

    /// <summary>Full-screen scrollable pager for T/O/D/V/X pop-outs. <paramref name="lines"/> are the
    /// full content; <paramref name="offset"/> is the top visible line.</summary>
    public static IRenderable BuildModal(string title, IReadOnlyList<string> lines, int offset, int width, int height)
    {
        var inner = Math.Max(3, height - 4);       // rows available for content inside the panel
        var total = lines.Count;
        var maxOffset = Math.Max(0, total - inner);
        offset = Math.Clamp(offset, 0, maxOffset);
        var window = lines.Skip(offset).Take(inner).Select(l => (IRenderable)new Markup(Esc(TrimTo(l, width - 6)))).ToArray();

        var body = window.Length > 0 ? new Rows(window) : (IRenderable)new Markup("[grey](empty)[/]");
        var pos = total == 0 ? "empty" : $"{offset + 1}-{Math.Min(offset + inner, total)} / {total}";
        var footer = $"[grey]{Esc(pos)}   ↑/↓ PgUp/PgDn Home/End scroll · Esc/q close[/]";
        return new Panel(new Rows(body, new Markup(""), new Markup(footer)))
            .Header($"[bold]{Esc(title)}[/]")
            .Expand()
            .Border(BoxBorder.Rounded);
    }

    private static string TrimTo(string s, int max) { s = (s ?? "").Replace("\t", "    "); return max > 0 && s.Length > max ? s[..(max - 1)] + "…" : s; }

    /// <summary>Full-screen input box for the `I` inject action.</summary>
    public static IRenderable BuildInput(string buffer, int width, int height)
    {
        var body = new Rows(
            new Markup("[grey]Type an instruction for the agent. It is queued to `.conductor/queue/` and injected into the next session prompt (linked as the next workflow step).[/]"),
            new Markup(""),
            new Markup($"[bold]▸[/] {Esc(buffer)}[blink]▌[/]"),
            new Markup(""),
            new Markup("[grey]Enter submit · Esc cancel · Backspace edit[/]"));
        return new Panel(body).Header("[bold]inject instruction[/]").Expand().Border(BoxBorder.Double);
    }


    public static string StatusColor(string status) => status switch
    {
        "Running" => "green",
        "VerifyingGates" => "yellow",
        "Backoff" => "orange1",
        "Paused" => "grey",
        "NeedsHuman" => "red",
        "Completed" => "aqua",
        "Aborted" => "red",
        _ => "silver",
    };

    private static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m{t.Seconds:00}s" : $"{t.Seconds}s";

    private static string Clip(string s, int max) { s = OneLine(s); return s.Length <= max ? s : s[..(max - 1)] + "…"; }

    private static string OneLine(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

    public static string Human(long n) => n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" : n >= 1000 ? $"{n / 1000.0:0.0}k" : n.ToString();

    private static DateTime Local(DateTime utc) => utc.Kind == DateTimeKind.Utc ? utc.ToLocalTime() : utc;

    private static string Esc(string s) => Markup.Escape(s ?? "");
}
