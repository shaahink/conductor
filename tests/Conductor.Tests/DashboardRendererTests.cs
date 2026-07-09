using Conductor.Core;
using Conductor.Models;
using Conductor.Ui;
using Spectre.Console;

namespace Conductor.Tests;

public class DashboardRendererTests
{
    private static string Render(DashboardState st, int width = 160, int height = 40)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = width;
        console.Profile.Height = height;
        console.Write(DashboardRenderer.BuildRoot(st with { Width = width, Height = height }));
        return writer.ToString();
    }

    private static DashboardState SampleState() => new()
    {
        Snap = new DashboardSnapshot
        {
            PlanName = "Loom",
            Status = "Running",
            StageId = "L1",
            StageTitle = "Identity spine",
            SessionNumber = 5,
            SessionKind = "Deliver",
            Attempt = 1,
            MaxAttempts = 4,
            AgentActive = true,
            DoneCount = 3,
            TotalCount = 35,
            CurrentCheckpoint = "L1.1",
            CurrentCheckpointTitle = "SymbolId/SymbolRef/tiers",
            StageCheckpoints = new[] { ("L1.1", "SymbolId/SymbolRef/tiers", "TODO"), ("L1.2", "Service node kinds", "TODO") },
            StageOverview = new[] { ("L0", 3, 3, "confirmed"), ("L1", 0, 5, "active"), ("L2", 0, 4, "todo") },
        },
        Agent = new[] { new DashboardState.AgentLine("tool", "bash git status", DateTime.UtcNow) },
        Thinking = new[] { new DashboardState.ThinkingLine(DateTime.UtcNow, "thinking about L1.1") },
        Log = new LogEntry[] { new("session #5 start", DateTime.UtcNow, LogSeverity.Info) },
    };

    [Fact]
    public void RendersWithoutThrowingAndShowsKeyContent()
    {
        var outp = Render(SampleState());
        Assert.Contains("Conductor", outp);
        Assert.Contains("L1", outp);
        Assert.Contains("Identity spine", outp);
        Assert.Contains("SymbolId", outp); // checkpoint title is now visible
    }

    [Theory]
    [InlineData(13)]
    [InlineData(20)]
    [InlineData(60)]
    public void RendersOnShortTerminalsWithoutThrowing(int height)
    {
        // Regression: fixed header+footer must never exceed the viewport (headers-stacking bug).
        var outp = Render(SampleState(), width: 120, height: height);
        Assert.Contains("Conductor", outp);
    }

    [Fact]
    public void NarrowTerminalStillRenders()
    {
        var outp = Render(SampleState(), width: 100);
        Assert.Contains("Conductor", outp);
    }

    [Theory]
    [InlineData("Paused", "resume", true)]
    [InlineData("Paused", "kill", false)]
    [InlineData("Paused", "pause", false)]
    [InlineData("Running", "kill", true)]
    [InlineData("Running", "pause", true)]
    [InlineData("NeedsHuman", "resume", true)]
    [InlineData("AwaitingOwner", "approve", true)]
    [InlineData("AwaitingOwner", "abort", true)]
    [InlineData("AwaitingOwner", "skip", true)]
    [InlineData("Completed", "quit", true)]
    [InlineData("Completed", "abort", false)]
    [InlineData("Backoff", "resume now", true)]
    public void ActionBarIsStateMachineAware(string status, string label, bool present)
    {
        var bar = DashboardRenderer.ActionBar(status, false);
        Assert.Equal(present, bar.Contains(label, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CostLineSeparatesTotalSessionAndUntracked()
    {
        var line = DashboardRenderer.CostLine(new DashboardSnapshot
        {
            TotalCostUsd = 0m,
            SessionCostUsd = 0.0239m,
            UntrackedSessions = 3,
        });
        Assert.Contains("$0.0239", line);              // combined shows the live session cost, not $0.0000
        Assert.Contains("session $0.0239", line);
        Assert.Contains("3 sessions unreported", line); // B4.4: reworded from cryptic "untracked"
    }

    [Theory]
    [InlineData(LogSeverity.Info, "grey", "·")]
    [InlineData(LogSeverity.Warn, "orange1", "!")]
    [InlineData(LogSeverity.Error, "red", "✗")]
    [InlineData(LogSeverity.Success, "green", "✓")]
    [InlineData(LogSeverity.Waiting, "yellow", "…")]
    [InlineData(LogSeverity.Human, "bold aqua", "§")]
    public void SeverityGlyphMapsEveryLevelToDistinctColorAndGlyph(LogSeverity s, string color, string glyph)
    {
        // B4.4: the severity model must map each level to a consistent (colour · glyph) pair
        // so the footer log, activity line, and status header all speak the same visual language.
        var (c, g) = DashboardRenderer.SeverityGlyph(s);
        Assert.Equal(color, c);
        Assert.Equal(glyph, g);
    }

    [Fact]
    public void LogRendersWithSeverityPrefix()
    {
        // B4.4: log entries with non-Info severity render a coloured glyph before the text.
        var st = SampleState() with
        {
            Log = new LogEntry[]
            {
                new("build started", DateTime.UtcNow, LogSeverity.Info),
                new("gate failed: tests red", DateTime.UtcNow, LogSeverity.Error),
                new("waiting for owner approval", DateTime.UtcNow, LogSeverity.Waiting),
            },
        };
        var outp = Render(st, width: 160, height: 24);
        Assert.Contains("gate failed", outp);
        Assert.Contains("waiting for owner approval", outp);
    }

    [Fact]
    public void CostLineOmitsSessionCostWhenZero()
    {
        var line = DashboardRenderer.CostLine(new DashboardSnapshot
        {
            TotalCostUsd = 0m,
            SessionCostUsd = 0m,
            UntrackedSessions = 0,
        });
        Assert.DoesNotContain("session", line);
        Assert.DoesNotContain("unreported", line);
    }

    [Fact]
    public void SeverityColorMatchesGlyph()
    {
        foreach (LogSeverity s in Enum.GetValues<LogSeverity>())
        {
            var (color, _) = DashboardRenderer.SeverityGlyph(s);
            Assert.Equal(color, DashboardRenderer.SeverityColor(s));
        }
    }

    [Fact]
    public void TokenLineShowsTotal()
    {
        var line = DashboardRenderer.TokenLine(new DashboardSnapshot
        {
            TokensInput = 58000,
            TokensOutput = 12100,
            TokensReasoning = 7165,
        });
        Assert.Contains("77.3k total", line);
    }

    [Fact]
    public void TokenLineIncludesLiveSessionTokens()
    {
        // B2.6 — live session tokens are added to historical totals (same pattern as cost)
        var line = DashboardRenderer.TokenLine(new DashboardSnapshot
        {
            TokensInput = 5000,            // historical from finished sessions
            TokensOutput = 2000,
            SessionTokensInput = 1500,     // live, still burning
            SessionTokensOutput = 500,
        });
        Assert.Contains("6.5k in", line);  // 5000 + 1500
        Assert.Contains("2.5k out", line); // 2000 + 500
        Assert.Contains("9.0k total", line);
    }

    [Fact]
    public void TokenLineBreaksOutLiveSessionDeltaLikeCostLine()
    {
        // B4.7: the token line must present the running session's live delta the same way the cost
        // line does — a "(session …)" fragment — so tokens and cost are visually consistent (F-3).
        var snap = new DashboardSnapshot
        {
            TokensInput = 5000,
            TokensOutput = 2000,
            SessionTokensInput = 1500,
            SessionTokensOutput = 500,
            TotalCostUsd = 0m,
            SessionCostUsd = 0.0200m,
        };
        var tokens = DashboardRenderer.TokenLine(snap);
        var cost = DashboardRenderer.CostLine(snap);
        Assert.Contains("(session 2.0k)", tokens);   // live delta broken out
        Assert.Contains("9.0k total", tokens);        // and still folded into the grand total
        Assert.Contains("(session $0.0200)", cost);   // matching shape on the cost line
    }

    [Fact]
    public void TokenLineOmitsSessionDeltaWhenNoLiveSession()
    {
        // Consistency with CostLineOmitsSessionCostWhenZero: no live burn → no "(session …)".
        var line = DashboardRenderer.TokenLine(new DashboardSnapshot { TokensInput = 5000, TokensOutput = 2000 });
        Assert.DoesNotContain("(session", line);
    }

    [Fact]
    public void ConfirmPromptIsRenderedInFooter()
    {
        var st = SampleState() with { ConfirmPrompt = "Press A again to confirm ABORT (any other key cancels)" };
        var outp = Render(st);
        Assert.Contains("ABORT", outp);
    }

    [Fact]
    public void ConfirmPromptDoesNotCrashOnShortTerminal()
    {
        var st = SampleState() with { ConfirmPrompt = "Press K again to confirm KILL (any other key cancels)" };
        var outp = Render(st, width: 120, height: 13);
        Assert.Contains("Conductor", outp);
    }

    [Fact]
    public void HeaderGridShowsIdentityAndLiveMetricsInSeparateColumns()
    {
        // B4.2: the header is a two-column Grid — identity on the left, live metrics right-aligned.
        // Both columns must survive the split; a regression would drop one (all-metrics or all-identity).
        var s = SampleState();
        var st = s with
        {
            Snap = s.Snap with
            {
                DoneCount = 3,
                TotalCount = 35,
                SessionCostUsd = 0.0239m,
                TokensInput = 5000,
                TokensOutput = 2000,
            },
        };
        var outp = Render(st, width: 160, height: 40);
        Assert.Contains("Conductor", outp);          // identity column
        Assert.Contains("Identity spine", outp);      // identity column (stage title)
        Assert.Contains("checkpoints 3/35", outp);    // metrics column: progress
        Assert.Contains("$0.0239", outp);             // metrics column: cost
        Assert.Contains("7.0k total", outp);          // metrics column: tokens
    }

    [Theory]
    [InlineData(13)]  // compact
    [InlineData(23)]  // compact boundary
    [InlineData(24)]  // full boundary
    [InlineData(40)]  // full
    public void HeaderTitleRendersExactlyOnce_NoStacking(int height)
    {
        // B4.2 regression guard (F-5): the header must occupy its region once, never stack. A single
        // BuildRoot frame therefore contains the "Conductor" title exactly once across every mode.
        var outp = Render(SampleState(), width: 160, height: height);
        Assert.Equal(1, CountOccurrences(outp, "Conductor"));
    }

    [Fact]
    public void PlanTreeRendersSubCheckpointsAndPerStageColumnsThroughBuildRoot()
    {
        // B4.3: the left column is now the hierarchical plan tree. Through the real BuildRoot path
        // (not just PlanTree.Build) an active stage shows its sub-checkpoints and per-stage columns.
        var s = SampleState();
        var st = s with
        {
            Snap = s.Snap with
            {
                Stages = new[]
                {
                    new StageProgress { Id = "L0", Title = "Bootstrap", Done = 3, Total = 3, State = "confirmed", Attempts = 2, LastOutcome = "Advanced", CostUsd = 0.30m,
                        Checkpoints = new[] { ("L0.1", "seed", "DONE") } },
                    new StageProgress { Id = "L1", Title = "Identity spine", Done = 0, Total = 2, State = "active", Attempts = 1, LastOutcome = "Progress", CostUsd = 0.12m,
                        Checkpoints = new[] { ("L1.1", "SymbolId/SymbolRef/tiers", "IN PROGRESS"), ("L1.2", "Service node kinds", "TODO") } },
                },
            },
        };
        var outp = Render(st, width: 160, height: 40);
        Assert.Contains("L1.1", outp);            // active stage's sub-checkpoint is visible
        Assert.Contains("0/2", outp);             // per-stage done column
        Assert.DoesNotContain("L0.1", outp);      // collapsed (confirmed) stage hides its checkpoints
    }

    [Fact]
    public void PlanTreeFilterNarrowsRowsThroughBuildRoot()
    {
        // B4.3: applying the Active filter drops non-active stages from the rendered tree.
        var s = SampleState();
        var stages = new[]
        {
            new StageProgress { Id = "L0", Title = "Bootstrap", Done = 3, Total = 3, State = "confirmed",
                Checkpoints = new[] { ("L0.1", "seed", "DONE") } },
            new StageProgress { Id = "L1", Title = "Identity spine", Done = 0, Total = 2, State = "active",
                Checkpoints = new[] { ("L1.1", "SymbolId", "IN PROGRESS") } },
        };
        var st = s with { Snap = s.Snap with { Stages = stages } };

        var all = Render(st with { Tree = new PlanTreeView() }, width: 160, height: 40);
        Assert.Contains("Bootstrap", all);          // L0 shown under All

        var active = Render(st with { Tree = new PlanTreeView { Filter = PlanFilter.Active } }, width: 160, height: 40);
        Assert.DoesNotContain("Bootstrap", active); // L0 dropped under Active
        Assert.Contains("Identity spine", active);  // L1 (active) kept
    }

    [Fact]
    public void ThinkingPaneShowsStructuredFacetsThroughBuildRoot()
    {
        // B4.5: structured reasoning is parsed into a Goal/Action digest in the thinking pane.
        var s = SampleState();
        var st = s with
        {
            Thinking = new[]
            {
                new DashboardState.ThinkingLine(DateTime.UtcNow,
                    "Goal: wire SymbolRef tiers. Action: add ambiguity fixtures then gate."),
            },
        };
        var outp = Render(st, width: 160, height: 40);
        Assert.Contains("goal", outp);                        // facet label rendered
        Assert.Contains("wire SymbolRef tiers", outp);        // facet value
        Assert.Contains("action", outp);
        Assert.Contains("add ambiguity fixtures", outp);
    }

    [Fact]
    public void AgentPaneFoldsToolOutputThroughBuildRoot()
    {
        // B4.5: folded (default) shows the tool header with a "(N lines)" badge, not the raw output;
        // expanded reveals the output lines.
        var s = SampleState();
        var now = DateTime.UtcNow;
        var st = s with
        {
            Agent = new[]
            {
                new DashboardState.AgentLine("tool", "bash git status", now),
                new DashboardState.AgentLine("result", "modified SymbolTable.cs", now),
                new DashboardState.AgentLine("result", "untracked SymbolRefTests.cs", now),
            },
        };
        var folded = Render(st with { AgentExpanded = false }, width: 160, height: 40);
        Assert.Contains("bash git status", folded);           // tool header visible
        Assert.Contains("2 lines", folded);                   // fold badge
        Assert.DoesNotContain("modified SymbolTable.cs", folded); // output hidden when folded

        var expanded = Render(st with { AgentExpanded = true }, width: 160, height: 40);
        Assert.Contains("modified SymbolTable.cs", expanded); // output shown when expanded
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
