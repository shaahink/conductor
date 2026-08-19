using System.Diagnostics;

namespace Conductor.Tests;

/// <summary>
/// KS6.2 — the analyzer-debt referee, driven against a scratch repo.
/// </summary>
/// <remarks>
/// <c>tools/gates/analyzer-debt.ps1</c> is the thing that decides whether a session lowered the bar, so
/// "it looked right when I read it" is not a standard it gets to be held to. These tests build a throwaway
/// git repo, seed each attack into it, and assert the gate goes RED for the named reason — the same seeded
/// attacks captured once by hand in <c>.conductor/evidence/KS6/KS6.2-seeded-attacks.log</c>, made
/// permanent so they run on every battery instead of on the day they were written.
/// <para/>
/// The one that matters is <see cref="CommittingTheSuppressionDoesNotMoveTheBar"/>. The gate's first
/// version anchored on <c>origin/&lt;branch&gt;</c>, which is what ratchet.ps1 does, and that is worth
/// nothing here: a session commits and pushes BEFORE conductor runs the battery, so origin/&lt;branch&gt;
/// is HEAD and the comparison is the tree against itself. The bar is a minimum over a window of history
/// for exactly that reason, and this test is what keeps it one.
/// <para/>
/// Windows-only, like every .ps1 in tools/gates: the gate is Windows PowerShell 5.1 by design and the
/// suite runs on Windows. Elsewhere these skip rather than fail — a red test on a platform the gate never
/// claimed would teach the next session to ignore it.
/// </remarks>
public sealed class KS6_2AnalyzerDebtRatchetTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "ks62-" + Guid.NewGuid().ToString("N")[..10]);
    private static bool OnWindows => OperatingSystem.IsWindows();

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

    /// <summary>A scratch repo whose history is clean: three commits, one justified pragma, one enforced
    /// rule. Every attack below starts from here, so a RED verdict can only have come from the seed.</summary>
    private void SeedCleanHistory()
    {
        Directory.CreateDirectory(_repo);
        Git("init --quiet");
        Write(".editorconfig", "[*.cs]\ndotnet_diagnostic.MA0045.severity = error # sync-over-async\ndotnet_diagnostic.CA1031.severity = suggestion # boundary catches are legitimate here\n");
        Write("src/A.cs", "#pragma warning disable MA0045 // sync file I/O at the CLI boundary, not a hot path\nclass A { }\n");
        Commit("one");
        Write("src/B.cs", "class B { }\n");
        Commit("two");
        Write("src/C.cs", "class C { }\n");
        Commit("three");
    }

    private (int Exit, string Out) Gate()
    {
        var script = Path.Combine(RepoRoot(), "tools", "gates", "analyzer-debt.ps1");
        // Files must be tracked for 'git grep' to see them, and a seed that is only staged is exactly the
        // uncommitted case the gate has to catch, so intent-to-add is enough.
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

        Assert.Contains("analyzer-debt: OK", output, StringComparison.Ordinal);
        Assert.Equal(0, exit);
    }

    [Fact]
    public void AnUnjustifiedPragmaIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/D.cs", "#pragma warning disable MA0045\nclass D { }\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("UNJUSTIFIED SUPPRESSIONS", output, StringComparison.Ordinal);
        Assert.Contains("pragma-src", output, StringComparison.Ordinal);
    }

    /// <summary>The attack the old gate scored as an improvement: the pragma count goes DOWN while a whole
    /// rule goes quiet.</summary>
    [Fact]
    public void LaunderingAPragmaIntoASeverityDowngradeIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/A.cs", "class A { }\n");   // the pragma is gone - the count improves
        Write(".editorconfig", "[*.cs]\ndotnet_diagnostic.MA0045.severity = none # tidied up\ndotnet_diagnostic.CA1031.severity = suggestion # boundary catches are legitimate here\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("RULES QUIETLY UN-ENFORCED", output, StringComparison.Ordinal);
        Assert.Contains("MA0045", output, StringComparison.Ordinal);
    }

    /// <summary>One line that silences every rule nobody listed by name. Never curation: an explicit
    /// per-rule severity beats a blanket whatever the order, so a blanket only reaches the defaults.</summary>
    [Fact]
    public void ABlanketCategoryDowngradeIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write(".editorconfig", "[*.cs]\ndotnet_diagnostic.MA0045.severity = error # sync-over-async\ndotnet_diagnostic.CA1031.severity = suggestion # boundary catches are legitimate here\ndotnet_analyzer_diagnostic.severity = none # quieten the noise\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("severity-blanket", output, StringComparison.Ordinal);
    }

    /// <summary>The old PathSpec looked at src/ only, so the same pragma parked under tests/ was free.</summary>
    [Fact]
    public void APragmaParkedUnderTestsIsRefused()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("tests/T.cs", "#pragma warning disable MA0045 // a reason long enough to look legitimate\nclass T { }\n");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("pragma-tests-tools", output, StringComparison.Ordinal);
    }

    /// <summary>Declining a rule this repo never enforced is how a curated ruleset is written — KS6.1's
    /// whole deliverable did it twice. It must not read as debt, or the gate makes curation illegal.</summary>
    [Fact]
    public void DecliningARuleThatWasNeverEnforcedIsAllowed()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write(".editorconfig", "[*.cs]\ndotnet_diagnostic.MA0045.severity = error # sync-over-async\ndotnet_diagnostic.CA1031.severity = suggestion # boundary catches are legitimate here\ndotnet_diagnostic.RCS1234.severity = none # measured: 40 hits, all false positives here\n");

        var (exit, output) = Gate();

        Assert.Contains("analyzer-debt: OK", output, StringComparison.Ordinal);
        Assert.Equal(0, exit);
    }

    /// <summary>
    /// The one the design turns on. Anchored on origin/&lt;branch&gt; — which is what ratchet.ps1 does —
    /// this passes, because a session pushes before the battery runs and the gate ends up comparing the
    /// tree against itself. Anchored on a window minimum it cannot: the twenty-odd commits behind this one
    /// still measure 1, and a minimum does not move for a single commit.
    /// </summary>
    [Fact]
    public void CommittingTheSuppressionDoesNotMoveTheBar()
    {
        if (!OnWindows) return;
        SeedCleanHistory();
        Write("src/D.cs", "#pragma warning disable MA0045 // a justification long enough to pass the reason check\nclass D { }\n");
        Commit("the suppression, committed and (as far as the gate can tell) pushed");

        var (exit, output) = Gate();

        Assert.Equal(1, exit);
        Assert.Contains("SUPPRESSIONS ROSE", output, StringComparison.Ordinal);
        Assert.Contains("committing this does not move it", output, StringComparison.Ordinal);
    }
}
