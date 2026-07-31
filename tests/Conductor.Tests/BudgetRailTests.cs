using System.Text.Json;

using Conductor.Commands;
using Conductor.Core.Orchestration;

namespace Conductor.Tests;

/// <summary>
/// B13 gate: the rails that make a per-session token budget real rather than decorative.
/// - the budget hook is silent with no signal, speaks once with one
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

    /// <summary>Once per session, not once per tool call — repeating it would nag the agent and spend
    /// a few hundred tokens a turn announcing that tokens are short.</summary>
    [Fact]
    public void HookBudget_SpeaksOncePerSession()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "soft-break"), "finish-subtask-and-handoff:now");
            var (_, first) = RunHook(dir);
            var (_, second) = RunHook(dir);

            Assert.NotEmpty(first);
            Assert.Empty(second);
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
        using var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);
        try
        {
            var cmd = new HookBudgetCommand();
            var exit = cmd.Execute(null!, new HookBudgetCommand.Settings { StateDir = stateDir });
            return (exit, sw.ToString());
        }
        finally { Console.SetOut(original); }
    }
}
