using System.Text.Json;
using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// The design rules, as executable tests — a debt ratchet, not a cliff.
/// </summary>
/// <remarks>
/// "Separation of concerns" and "no god classes" are worth nothing as prose: the last three eras of this
/// codebase all carried that doc, and Orchestrator.cs still reached 2,334 lines while Commands.cs reached
/// 2,574. A rule not enforced by a failing build is a suggestion.
/// <para/>
/// The violations that exist today are recorded in <c>architecture-baseline.json</c>. These tests allow
/// exactly one direction of travel: a listed file may shrink, and it may leave the list. A file may never
/// grow past its recorded size, and a file not on the list may never appear on it. Emptying that file is
/// stage M1's definition of done.
/// <para/>
/// The obvious cheat — edit the baseline instead of the code — is denied by tools/gates/ratchet.ps1, which
/// fails if the baseline's totals rise. The other obvious cheat — delete these tests — is denied by the same
/// gate, which fails if the test count falls. The interlock is deliberate.
/// <para/>
/// If a ceiling is genuinely wrong, raising it is a HUMAN decision: write <c>HUMAN:</c> in the handoff and
/// stop. Do not raise it to make your own session pass.
/// </remarks>
public class ArchitectureTests
{
    private sealed record Baseline(
        int LineCeiling,
        int MaxTypesPerFile,
        Dictionary<string, int> FilesOverLineCeiling,
        Dictionary<string, int> FilesOverTypeCeiling);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static Baseline LoadBaseline()
    {
        var path = Path.Combine(RepoRoot(), "tests", "Conductor.Tests", "architecture-baseline.json");
        var json = File.ReadAllText(path);
        var b = JsonSerializer.Deserialize<Baseline>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(b);
        return b!;
    }

    private static List<FileInfo> EngineSources() =>
        new DirectoryInfo(Path.Combine(RepoRoot(), "src"))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

    private static readonly Regex TypeDeclaration = new(
        @"^\s*(?:public|internal)\s+(?:sealed\s+|abstract\s+|static\s+|partial\s+)*(?:class|record|interface|enum)\s",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    /// <summary>No file may grow past the ceiling — and a file already over it may only shrink.</summary>
    [Fact]
    public void NoFileGrowsPastItsLineCeilingOrItsRecordedDebt()
    {
        var baseline = LoadBaseline();
        var failures = new List<string>();

        foreach (var file in EngineSources())
        {
            var lines = File.ReadAllLines(file.FullName).Length;
            var allowed = baseline.FilesOverLineCeiling.TryGetValue(file.Name, out var debt)
                ? debt                       // known offender: today's size is its ceiling, it may only fall
                : baseline.LineCeiling;      // everything else: the real rule

            if (lines > allowed)
            {
                failures.Add(baseline.FilesOverLineCeiling.ContainsKey(file.Name)
                    ? $"  {file.Name,-30} {lines} lines — grew past its recorded debt of {allowed}. Known god classes may only shrink."
                    : $"  {file.Name,-30} {lines} lines — over the {baseline.LineCeiling}-line ceiling. Split it by responsibility.");
            }
        }

        Assert.True(failures.Count == 0, "Architecture ratchet — file size went the wrong way:\n" + string.Join("\n", failures));
    }

    /// <summary>One file, one job. A file holding a dozen types is a filing cabinet, not a module — that is
    /// how Commands.cs came to hold 54 of them and nobody could find anything.</summary>
    [Fact]
    public void NoFileGrowsPastItsTypeCeilingOrItsRecordedDebt()
    {
        var baseline = LoadBaseline();
        var failures = new List<string>();

        foreach (var file in EngineSources())
        {
            var types = TypeDeclaration.Matches(File.ReadAllText(file.FullName)).Count;
            var allowed = baseline.FilesOverTypeCeiling.TryGetValue(file.Name, out var debt)
                ? debt
                : baseline.MaxTypesPerFile;

            if (types > allowed)
                failures.Add($"  {file.Name,-30} declares {types} types (allowed {allowed}). Give each type its own file.");
        }

        Assert.True(failures.Count == 0, "Architecture ratchet — type count went the wrong way:\n" + string.Join("\n", failures));
    }

    /// <summary>The baseline must describe reality. Fix a file and you MUST remove/lower its entry — that is
    /// what makes progress visible in the diff, and what stops the list from quietly becoming fiction.</summary>
    [Fact]
    public void BaselineDoesNotListDebtThatIsAlreadyPaid()
    {
        var baseline = LoadBaseline();
        var byName = EngineSources().ToDictionary(f => f.Name, f => f, StringComparer.Ordinal);
        var stale = new List<string>();

        foreach (var (name, debt) in baseline.FilesOverLineCeiling)
        {
            if (!byName.TryGetValue(name, out var file))
            {
                stale.Add($"  {name} is in the baseline but no longer exists — delete its entry.");
                continue;
            }
            var lines = File.ReadAllLines(file.FullName).Length;
            if (lines <= baseline.LineCeiling)
                stale.Add($"  {name} is now {lines} lines (under the {baseline.LineCeiling} ceiling) — delete its entry.");
            else if (lines < debt)
                stale.Add($"  {name} is now {lines} lines but the baseline still says {debt} — lower it to {lines} and bank the win.");
        }

        Assert.True(stale.Count == 0,
            "Architecture baseline is out of date. Debt you have paid must be removed from the ledger:\n" + string.Join("\n", stale));
    }

    /// <summary>P0: the planning library is pure and standalone — the engine references
    /// Conductor.Planning, NEVER the reverse. A reverse reference (or any IO/engine using creeping in)
    /// would silently re-couple the planner to the app and kill the standalone-usable goal. Checked
    /// both at the assembly level (compiled references) and at source level (using directives).</summary>
    [Fact]
    public void PlanningLibraryDoesNotReferenceTheEngine()
    {
        // Compiled truth: the Conductor.Planning assembly must not reference the engine assembly
        // ("conductor" is the engine's AssemblyName). This is what the build actually linked.
        var refs = typeof(Conductor.Planning.WorkflowEngine).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(refs, r =>
            string.Equals(r.Name, "conductor", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.Name, "Conductor", StringComparison.Ordinal));

        // Source truth: no file in the library may even name an engine namespace (catches a
        // same-solution reference someone might add without changing the csproj).
        var libDir = Path.Combine(RepoRoot(), "src", "Conductor.Planning");
        var violations = new List<string>();
        foreach (var file in EngineSources().Where(f => f.FullName.StartsWith(libDir, StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file.FullName);
            foreach (var forbidden in new[] { "using Conductor.Core", "using Conductor.Models", "using Conductor.Commands", "using Spectre" })
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                    violations.Add($"  {file.Name} -> {forbidden}");
            }
        }
        Assert.True(violations.Count == 0,
            "Conductor.Planning must stay engine-free (one-way dependency):\n" + string.Join("\n", violations));
    }

    /// <summary>The engine must not depend on the CLI or on any UI. The Face is a disposable client over the
    /// control plane and the CLI is merely one of three ingresses — if Core reaches back into either, the run
    /// can no longer outlive its UI, which is the entire point of the split.</summary>
    [Fact]
    public void CoreDoesNotDependOnTheCliOrAnyUi()
    {
        var coreDir = Path.Combine(RepoRoot(), "src", "Conductor", "Core");
        var violations = new List<string>();

        foreach (var file in EngineSources().Where(f => f.FullName.StartsWith(coreDir, StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file.FullName);
            foreach (var forbidden in new[] { "using Conductor.Commands", "using Spectre.Console" })
            {
                if (text.Contains(forbidden, StringComparison.Ordinal))
                    violations.Add($"  {file.Name} -> {forbidden}");
            }
        }

        Assert.True(violations.Count == 0,
            "Core must not depend on the CLI or a UI — the engine outlives every face:\n" + string.Join("\n", violations));
    }
}
