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
                    new Layout("left").Ratio(24).SplitRows(new Layout("plan"), new Layout("stage")),
                    new Layout("center").Ratio(42),
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
                    new Layout("left").Ratio(1).SplitRows(new Layout("plan"), new Layout("stage")),
                    new Layout("right").Ratio(2).SplitRows(
                        new Layout("agent").Ratio(3),
                        new Layout("thinking").Ratio(2))),
                new Layout("footer").Size(footer));
            root["agent"].Update(AgentPanel(st));
            root["thinking"].Update(ThinkingPanel(st));
        }

        root["plan"].Update(PlanPanel(st));
        root["stage"].Update(StagePanel(st));
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

    /// <summary>Cost broken out so a missing/older cost never reads as a misleading $0.0000.</summary>
    public static string CostLine(DashboardSnapshot s)
    {
        var combined = s.TotalCostUsd + s.SessionCostUsd;
        var txt = $"cost [bold]${combined:0.0000}[/]";
        if (s.SessionCostUsd > 0) txt += $" [grey](session ${s.SessionCostUsd:0.0000})[/]";
        if (s.UntrackedSessions > 0) txt += $" [grey]· {s.UntrackedSessions} untracked[/]";
        return txt;
    }

    public static string TokenLine(DashboardSnapshot s)
    {
        var total = s.TokensInput + s.TokensOutput + s.TokensReasoning +
                    s.SessionTokensInput + s.SessionTokensOutput + s.SessionTokensReasoning;
        if (total == 0) return "[grey]tokens —[/]";
        var parts = $"{Human(s.TokensInput + s.SessionTokensInput)} in · {Human(s.TokensOutput + s.SessionTokensOutput)} out";
        if (s.TokensReasoning + s.SessionTokensReasoning > 0) parts += $" · {Human(s.TokensReasoning + s.SessionTokensReasoning)} think";
        return $"tokens {parts} · [bold]{Human(total)} total[/]";
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

    // ---------------------------------------------------------------- left column (plan + stage)

    private static IRenderable PlanPanel(DashboardState st)
        => new Panel(PlanTable(st)).Header("[aqua]plan[/]").Expand().Border(BoxBorder.Rounded);

    private static IRenderable StagePanel(DashboardState st)
        => new Panel(StageTable(st)).Header($"[aqua]stage {Esc(st.Snap.StageId)}[/]").Expand().Border(BoxBorder.Rounded);

    private static IRenderable PlanTable(DashboardState st)
    {
        var t = new Table().Border(TableBorder.Rounded).Expand();
        t.AddColumn("Stage");
        t.AddColumn("Done");
        t.AddColumn("State");
        foreach (var (id, doneN, total, state) in st.Snap.StageOverview)
        {
            var mark = state switch
            {
                "confirmed" => "[green]✓ done[/]",
                "done" => "[green]done[/]",
                "gating" => "[yellow]gating…[/]",
                "active" => "[bold yellow]← active[/]",
                "skipped" => "[red]skipped[/]",
                _ => "[grey]todo[/]",
            };
            t.AddRow(Esc(id), $"{doneN}/{total}", mark);
        }
        return t;
    }

    private static IRenderable StageTable(DashboardState st)
    {
        var t = new Table().Border(TableBorder.Rounded).Expand();
        t.AddColumn(new TableColumn("#").NoWrap());
        t.AddColumn(new TableColumn("Checkpoint"));
        t.AddColumn(new TableColumn("Status").NoWrap());
        foreach (var (id, title, status) in st.Snap.StageCheckpoints)
        {
            var color = status.StartsWith("DONE", StringComparison.OrdinalIgnoreCase) ? "green"
                : status.StartsWith("BLOCKED", StringComparison.OrdinalIgnoreCase) ? "red"
                : status.StartsWith("IN", StringComparison.OrdinalIgnoreCase) ? "yellow" : "grey";
            var titleColor = color == "grey" ? "grey" : "silver";
            t.AddRow(new Markup(Esc(id)), new Markup($"[{titleColor}]{Esc(Clip(title, 60))}[/]"), new Markup($"[{color}]{Esc(status)}[/]"));
        }
        if (st.Snap.StageCheckpoints.Count == 0) t.AddRow("[grey]—[/]", "[grey](no checkpoints parsed)[/]", "[grey]—[/]");
        return t;
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
        return new Rows(st.Thinking.Select(t =>
            (IRenderable)new Markup($"[grey37]{Local(t.Utc):HH:mm:ss} ~ {Esc(Clip(t.Text, 180))}[/]")).ToArray());
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
        IRenderable body = st.Agent.Count > 0
            ? new Rows(st.Agent.Select(a => (IRenderable)new Markup(AgentLine(a))).ToArray())
            : new Markup("[grey](no agent output yet)[/]");
        return new Panel(body).Header("[aqua]agent[/] [grey](O)[/]").Expand().Border(BoxBorder.Rounded);
    }

    private static string AgentLine(DashboardState.AgentLine a)
    {
        var (glyph, color) = a.Kind switch
        {
            "tool" => ("»", "deepskyblue1"),
            "text" => ("·", "silver"),
            "result" => ("◆", "aqua"),
            "stderr" => ("!", "orange1"),
            "system" => ("○", "grey"),
            _ => (" ", "grey"),
        };
        return $"[{color}]{Local(a.Utc):HH:mm:ss} {glyph} {Esc(a.Text)}[/]";
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
            rows.AddRange(st.Log.Select(l => (IRenderable)new Markup("[grey]" + Esc(l) + "[/]")));
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
        actions.Add("[grey][[T]] think · [[O]] output · [[D]] docs · [[V]] git · [[X]] prompt · [[I]] inject[/]");
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
