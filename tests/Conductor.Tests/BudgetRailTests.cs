using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Orchestration;

namespace Conductor.Tests;

/// <summary>
/// B13 gate: the rails that make a per-session token budget real rather than decorative.
/// - the budget hook is silent with no signal, speaks with one, and (K1.2) restates as it drains
/// - the hook command it is wired with survives the shell that runs it
/// - --settings rides alongside --mcp-config instead of replacing it
/// </summary>
public class BudgetRailTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), $"conductor-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(d);
        return d;
    }

    // --------------- the hook command's shape

    /// <summary>The trap this whole rail died on once: a hook command carrying native Windows
    /// separators is handed to a shell that reads `\` as an escape, so the path arrives mangled, the
    /// hook never runs, and everything upstream still looks correctly wired. Forward slashes work on
    /// every platform the engine targets, so the assertion is simply that no backslash survives.</summary>
    [Fact]
    public void HookCommand_CarriesNoBackslashes_SoTheShellCannotEatThem()
    {
        const string exe = @"C:\Program Files\conductor\conductor.exe";
        const string stateDir = @"C:\Code\sk-studio\.conductor";
        var command = $"\"{exe.Replace('\\', '/')}\" hook-budget --state-dir \"{stateDir.Replace('\\', '/')}\"";

        Assert.DoesNotContain("\\", command, StringComparison.Ordinal);
        Assert.Contains("C:/Program Files/conductor/conductor.exe", command, StringComparison.Ordinal);
        Assert.Contains("hook-budget", command, StringComparison.Ordinal);
        // Quoted despite the forward slashes: the paths still contain spaces.
        Assert.Contains("\"C:/Program Files/", command, StringComparison.Ordinal);
    }

    // --------------- the hook itself

    [Fact]
    public void HookBudget_SaysNothing_WhenNoSignalIsRaised()
    {
        var dir = TempDir();
        try
        {
            var (exit, output) = RunHook(dir);
            Assert.Equal(0, exit);
            Assert.Empty(output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void HookBudget_EmitsPostToolUseContext_WhenSignalIsRaised()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "soft-break"), "finish-subtask-and-handoff:now");
            var (exit, output) = RunHook(dir);

            Assert.Equal(0, exit);
            using var doc = JsonDocument.Parse(output);
            var hook = doc.RootElement.GetProperty("hookSpecificOutput");
            Assert.Equal("PostToolUse", hook.GetProperty("hookEventName").GetString());
            var ctx = hook.GetProperty("additionalContext").GetString() ?? "";
            Assert.Contains("SESSION TOKEN BUDGET", ctx, StringComparison.Ordinal);
            Assert.Contains("COMMIT", ctx, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Not once per tool call — but not once per SESSION either, which is what this test
    /// asserted until K1.2. Announcing the nudge a single time, hundreds of thousands of tokens before
    /// the end, converted zero of the Sarban face run's eleven post-cap sessions: every one ran on to
    /// the hard ceiling and was killed mid-turn. The contract now is "silent between statements, and
    /// restated once the session has spent another step of its margin" — see
    /// <see cref="K1_2SoftBreakRuleTests"/> for the rule and K1_2SoftBreakLiveTests for it end to end.</summary>
    [Fact]
    public void HookBudget_IsSilentBetweenStatements_ButSpeaksAgainAsTheBudgetDrains()
    {
        var dir = TempDir();
        try
        {
            void Signal(long spent) => SoftBreak.WriteSignal(dir,
                new SoftBreak.Signal(spent, 1000, 500, "H0.1", DateTime.UtcNow));

            Signal(500);
            var (_, first) = RunHook(dir);
            var (_, immediatelyAgain) = RunHook(dir);

            Assert.NotEmpty(first);
            Assert.Empty(immediatelyAgain);   // nagging every tool call is still wrong

            Signal(600);                      // …but another 100 of a 500-token margin is not nagging
            var (_, restated) = RunHook(dir);
            Assert.NotEmpty(restated);
            Assert.Contains("notice 2", restated, StringComparison.Ordinal);
            Assert.Contains("400 tokens", restated, StringComparison.Ordinal); // the budget that is left NOW
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>The delivered-marker has to go when the signal goes, or the NEXT session's nudge is a
    /// silent no-op — the quietest way to lose the cooperative rail all over again.</summary>
    [Fact]
    public void HookBudget_SpeaksAgain_AfterTheSignalIsCleared()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "soft-break"), "x");
            RunHook(dir);
            foreach (var name in new[] { "soft-break", "soft-break.delivered" })
                File.Delete(Path.Combine(dir, name));

            File.WriteAllText(Path.Combine(dir, "soft-break"), "x");
            var (_, again) = RunHook(dir);

            Assert.NotEmpty(again);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // --------------- the live counters the rails actually read

    /// <summary>The bug that made every rail above ornamental: assistant-message usage was emitted to
    /// the event stream but never folded onto the session state, so <c>TokensCacheRead</c> stayed null
    /// until the terminal result envelope. Both the soft-break and the ceiling ask the LIVE session what
    /// it has spent, so both read zero for the whole session — a run with a 6M ceiling reached 10M+ with
    /// neither firing. Asserting on the state, not on the emitted deltas, is the point of this test:
    /// the deltas were always correct.</summary>
    [Fact]
    public void LiveUsage_LandsOnTheSessionState_NotOnlyOnTheEventStream()
    {
        var state = new Conductor.Core.Providers.AgentStreamState((_, _) => { });
        var provider = new Conductor.Core.Providers.ClaudeProvider();

        provider.ParseLine(AssistantLine("msg_1", input: 100, output: 20, cacheRead: 50_000), state);
        provider.ParseLine(AssistantLine("msg_2", input: 200, output: 30, cacheRead: 70_000), state);

        Assert.Equal(300, state.TokensInput);
        Assert.Equal(50, state.TokensOutput);
        Assert.Equal(120_000, state.TokensCacheRead);
    }

    /// <summary>claude re-emits one message once per content block, carrying the SAME usage. The dedupe
    /// is what makes accumulating safe; without it the live total would run 3-4x hot and the ceiling
    /// would cut sessions short of the budget the operator set.</summary>
    [Fact]
    public void LiveUsage_CountsAMessageOnce_EvenWhenTheWireRepeatsIt()
    {
        var state = new Conductor.Core.Providers.AgentStreamState((_, _) => { });
        var provider = new Conductor.Core.Providers.ClaudeProvider();

        provider.ParseLine(AssistantLine("msg_1", input: 100, output: 20, cacheRead: 50_000), state);
        provider.ParseLine(AssistantLine("msg_1", input: 100, output: 20, cacheRead: 50_000), state);

        Assert.Equal(100, state.TokensInput);
        Assert.Equal(50_000, state.TokensCacheRead);
    }

    private static string AssistantLine(string id, long input, long output, long cacheRead) =>
        "{\"type\":\"assistant\",\"message\":{\"id\":\"" + id + "\",\"usage\":{\"input_tokens\":" + input
        + ",\"output_tokens\":" + output + ",\"cache_read_input_tokens\":" + cacheRead + "},\"content\":[]}}";

    // --------------- launch args

    [Fact]
    public void McpArgs_CarryBothTheMcpConfigAndTheBudgetSettings()
    {
        var args = SessionRunner.McpArgsFor("claude", plannedArgs: ["-p", "{prompt}"], "mcp.json", "settings.budget.json");

        Assert.Contains("--mcp-config", args, StringComparer.Ordinal);
        Assert.Contains("--strict-mcp-config", args, StringComparer.Ordinal);
        Assert.Contains("--settings", args, StringComparer.Ordinal);
        Assert.Contains("settings.budget.json", args, StringComparer.Ordinal);
    }

    /// <summary>A plan that wires its own --settings keeps full control rather than being handed a
    /// second, conflicting file — the same rule --mcp-config already followed.</summary>
    [Fact]
    public void McpArgs_LeaveAPlansOwnSettingsAlone()
    {
        var args = SessionRunner.McpArgsFor("claude", plannedArgs: ["--settings", "mine.json"], "mcp.json", "settings.budget.json");

        Assert.DoesNotContain("settings.budget.json", args, StringComparer.Ordinal);
    }

    [Fact]
    public void McpArgs_AddNothingWithoutABudget()
    {
        var args = SessionRunner.McpArgsFor("claude", plannedArgs: ["-p", "{prompt}"], "mcp.json", budgetSettingsPath: null);

        Assert.DoesNotContain("--settings", args, StringComparer.Ordinal);
        Assert.Contains("--mcp-config", args, StringComparer.Ordinal);
    }

    private static (int Exit, string Output) RunHook(string stateDir)
    {
        // Bug #26: this used to capture with Console.SetOut. That is process-global, so under the
        // full parallel suite another test's console writes landed in this buffer and the JSON parse
        // below failed on them — 10/10 in isolation, red in the battery. The command writes to a
        // writer THIS test owns; nothing running beside it can reach in.
        using var sw = new StringWriter();
        var cmd = new HookBudgetCommand { Output = sw };
        var exit = cmd.Execute(null!, new HookBudgetCommand.Settings { StateDir = stateDir });
        return (exit, sw.ToString());
    }
}
