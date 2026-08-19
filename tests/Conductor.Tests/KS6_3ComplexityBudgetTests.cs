using System.Diagnostics;

namespace Conductor.Tests;

/// <summary>
/// KS6.3 — the complexity-budget referee, driven against a scratch repo, plus one canary that compiles.
/// </summary>
/// <remarks>
/// <c>tools/gates/complexity-budget.ps1</c> decides whether a session was allowed to give itself more room,
/// so "it read correctly" is not a standard it gets to be held to. These tests build a throwaway git repo,
/// seed each loosening into it, and assert the gate goes RED for the named reason.
/// <para/>
/// Two of them carry the whole design. <see cref="CommittingTheLooseningDoesNotMoveTheBar"/> is the KS6.2
/// lesson restated: a session commits AND PUSHES before conductor runs the battery, so any bar phrased
/// against <c>origin/&lt;branch&gt;</c> compares the tree with itself. The bar is the strictest value over a
/// window of history, and one commit cannot move a minimum.
/// <para/>
/// <see cref="TheBudgetIsLiveAndNotMerelyConfigured"/> is the other one, and it exists because of what this
/// checkpoint measured: ONE unparseable line in a <c>CodeMetricsConfig.txt</c> disables CA1502, CA1505 and
/// CA1506 for the whole project in total silence — no AD0001, and not even CA1509, the diagnostic that
/// exists for exactly this. Reading the config proves nothing, so that test copies this repo's real
/// analyzer wiring into a scratch project and compiles a method that must break the budget. If the wiring
/// ever comes apart, that build goes green and the test goes red.
/// <para/>
/// Windows-only, like every .ps1 in tools/gates: the gate is Windows PowerShell 5.1 by design and the suite
/// runs on Windows. Elsewhere these skip rather than fail — a red test on a platform the gate never claimed
/// would teach the next session to ignore it.
/// </remarks>
public sealed class KS6_3ComplexityBudgetTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "ks63-" + Guid.NewGuid().ToString("N")[..10]);
    private static bool OnWindows => OperatingSystem.IsWindows();

    /// <summary>The scratch repo's starting budget. Every attack below moves one of these three.</summary>
    private const string CleanBudget = "# a budget\nCA1502: 20\nCA1505: 30\nCA1506: 40\n";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (int Exit, string Out) Run(string exe, string args, string cwd)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // A scratch repo must not inherit the operator's identity, hooks or signing config: a global
        // commit.gpgsign would hang the commit on a passphrase prompt with nobody to answer it.
        psi.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(cwd, ".gitconfig-none");
        psi.Environment["GIT_CONFIG_SYSTEM"] = Path.Combine(cwd, ".gitconfig-none");
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout + stderr);
    }

    private void Git(string args) => Run("git", args, _repo);

    private void Commit(string message)
    {
        Git("add -A");
        Git($"-c user.name=t -c user.email=t@t -c commit.gpgsign=false commit --quiet -m \"{message}\"");
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A scratch repo whose history is clean: three commits, one project, one budget at 20/30/40,
    /// all three rules enforced and wired. Every attack starts from here, so a RED verdict can only have
    /// come from the seed.</summary>
    private void SeedCleanHistory()
    {
        Directory.CreateDirectory(_repo);
        Git("init --quiet");
        Write(".editorconfig",
            "[*.cs]\ndotnet_diagnostic.CA1502.severity = error\ndotnet_diagnostic.CA1505.severity = error\ndotnet_diagnostic.CA1506.severity = error\n");
        Write("Directory.Build.props",
            "<Project>\n  <ItemGroup>\n    <AdditionalFiles Include=\"$(MSBuildProjectDirectory)/CodeMetricsConfig.txt\" />\n  </ItemGroup>\n</Project>\n");
        Write("src/Thing/Thing.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        Write("src/Thing/CodeMetricsConfig.txt", CleanBudget);
        Commit("one");
        Write("src/Thing/A.cs", "class A { }\n");
        Commit("two");
        Write("src/Thing/B.cs", "class B { }\n");
        Commit("three");
    }

    private (int Exit, string Out) Gate()
    {
        var script = Path.Combine(RepoRoot(), "tools", "gates", "complexity-budget.ps1");
        // Files must be tracked for 'git ls-files' to see them, and a seed that is only staged is exactly
        // the uncommitted case the gate has to catch, so intent-to-add is enough.
        Git("add -AN");
        return Run("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"", _repo);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_repo))
            {
                // git marks its object files read-only; a plain recursive delete trips over them.
                foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_repo, recursive: true);
            }
        }
        catch (IOException) { /* a scratch dir the OS still has open is not a test failure */ }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void CleanTreePasses()
    {
        if (!OnWindows) return;
        SeedCleanHistory();

        var (exit, output) = Gate();

        Assert.Contains("complexity-budget: OK", output, StringComparison.Ordinal);
        Assert.Equal(0, exit);
    }

    /// <summary>The cheapest loosening there is: no file at all, and the analyzer quietly reverts to
    /// 25/10/95 — looser than this repo's budget on every axis.</summary>
    [Fact]
    public void ADeletedBudgetFileIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        File.Delete(Path.Combine(_repo, "src", "Thing", "CodeMetricsConfig.txt"));

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("NO COMPLEXITY BUDGET", output, StringComparison.Ordinal);
    }

    /// <summary>The finding this checkpoint was built around. A symbol name in the parentheses is the
    /// obvious way to exempt one method; the analyzer cannot parse it and answers by switching all three
    /// rules off for the project without saying so. The gate refuses the line rather than the intent.</summary>
    [Fact]
    public void AnUnparseableLineIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/Thing/CodeMetricsConfig.txt", CleanBudget + "CA1502(ClaimItems): 90\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("UNPARSEABLE BUDGET LINE", output, StringComparison.Ordinal);
        Assert.Contains("CA1502(ClaimItems): 90", output, StringComparison.Ordinal);
    }

    /// <summary>A per-SymbolKind entry IS legal grammar, so it must not be reported as a typo — but it is a
    /// second budget hiding behind the first, so it is ratcheted under its own name.</summary>
    [Fact]
    public void APerSymbolKindEntryParsesAndIsRatchetedSeparately()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/Thing/CodeMetricsConfig.txt", CleanBudget + "CA1502(Method): 15\n");
        Commit("tighter for methods");
        Write("src/Thing/CodeMetricsConfig.txt", CleanBudget + "CA1502(Method): 60\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.DoesNotContain("UNPARSEABLE", output, StringComparison.Ordinal);
        Assert.Contains("CA1502(Method)", output, StringComparison.Ordinal);
        Assert.Contains("COMPLEXITY BUDGET LOOSENED", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ABudgetMissingARuleIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/Thing/CodeMetricsConfig.txt", "CA1502: 20\nCA1506: 40\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("BUDGET MISSING A RULE", output, StringComparison.Ordinal);
        Assert.Contains("CA1505", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RaisingACeilingIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/Thing/CodeMetricsConfig.txt", "CA1502: 45\nCA1505: 30\nCA1506: 40\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("COMPLEXITY BUDGET LOOSENED", output, StringComparison.Ordinal);
        Assert.Contains("20 -> 45", output, StringComparison.Ordinal);
    }

    /// <summary>CA1505 runs the other way — the index is 0-100 and the rule fires BELOW the number, so
    /// LOWERING it is the loosening. A gate that compared all three the same way would wave this through
    /// and refuse the tightening instead, which is the one failure mode a per-rule direction has.</summary>
    [Fact]
    public void LoweringTheMaintainabilityFloorIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/Thing/CodeMetricsConfig.txt", "CA1502: 20\nCA1505: 12\nCA1506: 40\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("COMPLEXITY BUDGET LOOSENED", output, StringComparison.Ordinal);
        Assert.Contains("30 -> 12", output, StringComparison.Ordinal);
    }

    /// <summary>The other direction, on all three at once: tightening is always free and must never be
    /// mistaken for tampering, or the next session learns to leave the numbers alone.</summary>
    [Fact]
    public void TighteningEveryBudgetPasses()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/Thing/CodeMetricsConfig.txt", "CA1502: 12\nCA1505: 44\nCA1506: 31\n");

        var (exit, output) = Gate();

        Assert.Contains("complexity-budget: OK", output, StringComparison.Ordinal);
        Assert.Equal(0, exit);
    }

    /// <summary>Removing the line is the same loosening as raising the number, and quieter.</summary>
    [Fact]
    public void DroppingALineThatUsedToExistIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/Thing/CodeMetricsConfig.txt", "CA1502: 20\nCA1505: 30\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("BUDGET MISSING A RULE", output, StringComparison.Ordinal);
        Assert.Contains("BUDGET DROPPED", output, StringComparison.Ordinal);
    }

    /// <summary>THE ONE THAT MATTERS. A session commits and pushes before conductor runs the battery, so a
    /// bar anchored on origin/&lt;branch&gt; is the tree compared with itself. Here the loosening is
    /// committed first — the state the gate actually meets — and the window minimum still refuses it.</summary>
    [Fact]
    public void CommittingTheLooseningDoesNotMoveTheBar()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/Thing/CodeMetricsConfig.txt", "CA1502: 90\nCA1505: 30\nCA1506: 40\n");
        Commit("give myself room");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("COMPLEXITY BUDGET LOOSENED", output, StringComparison.Ordinal);
        Assert.Contains("20 -> 90", output, StringComparison.Ordinal);
    }

    /// <summary>The budget can also be voided from the other end: leave the numbers alone and stop the rule
    /// being able to fail a build.</summary>
    [Fact]
    public void UnEnforcingARuleIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write(".editorconfig",
            "[*.cs]\ndotnet_diagnostic.CA1502.severity = error\ndotnet_diagnostic.CA1505.severity = error\ndotnet_diagnostic.CA1506.severity = suggestion\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("COMPLEXITY RULE UN-ENFORCED", output, StringComparison.Ordinal);
        Assert.Contains("CA1506", output, StringComparison.Ordinal);
    }

    /// <summary>A path-scoped 'none' exempts a whole tree in one line while the root section still reads
    /// enforced — the shape that makes a severity map worth walking in full rather than stopping at the
    /// first hit.</summary>
    [Fact]
    public void APathScopedNoneIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write(".editorconfig",
            "[*.cs]\ndotnet_diagnostic.CA1502.severity = error\ndotnet_diagnostic.CA1505.severity = error\n"
            + "dotnet_diagnostic.CA1506.severity = error\n\n[src/**/*.cs]\ndotnet_diagnostic.CA1506.severity = none\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("COMPLEXITY RULE UN-ENFORCED", output, StringComparison.Ordinal);
    }

    /// <summary>And from the third end: leave every number and every severity in place, and stop handing the
    /// budget files to the compiler. Nothing about the repo looks different.</summary>
    [Fact]
    public void UnwiringAdditionalFilesIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("Directory.Build.props", "<Project>\n</Project>\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("BUDGETS NOT WIRED", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// The canary: this repo's own analyzer wiring, compiled, with a method that must break the budget.
    /// </summary>
    /// <remarks>
    /// Every other test here reads configuration. This one refuses to, because reading it is precisely what
    /// this checkpoint proved unsafe — a malformed line leaves CA1502, CA1505 and CA1506 switched off with
    /// no diagnostic anywhere, so a repo whose budgets are dead looks exactly like a repo whose budgets are
    /// met. The real <c>Directory.Build.props</c>, <c>Directory.Packages.props</c> and <c>.editorconfig</c>
    /// are copied into a scratch project next to a <c>CodeMetricsConfig.txt</c> of 1, and a method with
    /// three branches has to make the build red. Green here means the budget mechanism is gone.
    /// </remarks>
    [Fact]
    public void TheBudgetIsLiveAndNotMerelyConfigured()
    {
        if (!OnWindows) return;
        Directory.CreateDirectory(_repo);
        var root = RepoRoot();
        foreach (var f in new[] { "Directory.Build.props", "Directory.Packages.props", ".editorconfig" })
            File.Copy(Path.Combine(root, f), Path.Combine(_repo, f));

        Write("Canary.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><EnableDefaultCompileItems>true</EnableDefaultCompileItems></PropertyGroup></Project>\n");
        Write("CodeMetricsConfig.txt", "CA1502: 1\nCA1505: 0\nCA1506: 200\n");
        Write("Canary.cs",
            "namespace Canary;\ninternal static class C\n{\n"
            + "    public static int F(int a, int b)\n    {\n"
            + "        if (a > 0) { return 1; }\n        if (b > 0) { return 2; }\n"
            + "        if (a == b) { return 3; }\n        return 0;\n    }\n}\n");

        var (exit, output) = Run("dotnet", "build Canary.csproj --nologo -v:m", _repo);

        // A machine with no SDK or no restored packages is a machine that cannot answer the question; that
        // is not the same as an answer of "green", so it says so rather than passing quietly.
        Assert.False(
            output.Contains("NU1101", StringComparison.Ordinal) || output.Contains("MSB4236", StringComparison.Ordinal),
            "the canary could not be restored, so nothing was proven: " + output);
        Assert.Contains("error CA1502", output, StringComparison.Ordinal);
        Assert.NotEqual(0, exit);
    }
}
