using System.Diagnostics;

namespace Conductor.Tests;

/// <summary>
/// K7.2: a mistyped flag must be an error, not silence.
///
/// Spectre's <c>CommandApp</c> defaults <c>StrictParsing</c> to false and <c>Program.cs</c> never set
/// it, so every unrecognised option was dropped on the floor: <c>version --shortt</c> exited 0 and
/// printed the LONG form, and <c>status --no-llm</c> — a flag this engine has never had — appeared to
/// work everywhere it was written down (it was in the troubleshooting cheat sheet and four live
/// scripts). Silence is the wrong answer for a CLI whose flags change what happens. Mistyping
/// <c>update --check</c> installs a new binary instead of looking at one; mistyping <c>gate --full</c>
/// runs the fast tier and reports green, which is a false green on a gate.
///
/// This drives the real binary rather than asserting on <c>Program.cs</c>'s text, because the thing
/// under test is what the parser DOES. The app assembly is in this test project's output directory
/// (Conductor.Tests references Conductor.csproj), so there is no path to guess.
/// </summary>
public class K7_2StrictFlagParsingTests
{
    [Fact]
    public void UnknownOption_IsRejected_AndNamesTheFlag()
    {
        var (exit, output) = RunCli("version", "--shortt");

        Assert.True(exit != 0,
            $"`conductor version --shortt` exited {exit} — a mistyped flag was swallowed. " +
            "Program.cs must call UseStrictParsing(). Output was:\n" + output);
        Assert.Contains("shortt", output, StringComparison.Ordinal);
    }

    /// <summary>The positive control. A rejection test passes just as well against a binary that
    /// rejects everything, so the real flag has to still work — and still do its job: <c>--short</c>
    /// prints one bare version line, which is what scripts consume.</summary>
    [Fact]
    public void DeclaredOption_StillParses_AndStillDoesItsJob()
    {
        var (exit, output) = RunCli("version", "--short");

        Assert.Equal(0, exit);
        Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The escape hatch that must survive. Everything after a literal <c>--</c> is a
    /// remaining argument and is never parsed as an option — this is what lets
    /// <c>bg start --name t -- dotnet test --filter X</c> hand its trailing flags to the child.
    /// Strict parsing tightens the option parser, and it must not reach past that separator.</summary>
    [Fact]
    public void ArgumentsAfterADoubleDash_AreNotParsedAsOptions()
    {
        var (exit, output) = RunCli("version", "--short", "--", "--filter", "Some.Test");

        Assert.True(exit == 0,
            "strict parsing reached past `--` and rejected a passthrough argument; " +
            "`conductor bg start ... -- <cmd> --flag` depends on this. Output was:\n" + output);
    }

    private static (int Exit, string Output) RunCli(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "conductor.dll");
        Assert.True(File.Exists(dll), $"app assembly not next to the tests: {dll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start dotnet");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60_000), "the CLI did not exit within 60s");
        return (p.ExitCode, stdout + stderr);
    }
}
