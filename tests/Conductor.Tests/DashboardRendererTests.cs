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
        Log = new[] { "session #5 start" },
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
        var bar = DashboardRenderer.ActionBar(status);
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
        Assert.Contains("$0.0239", line);      // combined shows the live session cost, not $0.0000
        Assert.Contains("session $0.0239", line);
        Assert.Contains("3 untracked", line);
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

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
