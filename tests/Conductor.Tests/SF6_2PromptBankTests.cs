using System.Globalization;
using System.Text.RegularExpressions;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SF6.2 — the prompt bank under <c>plans/</c> is pruned, enriched and indexed. The index
/// (<c>plans/README.md</c>) is the only thing standing between a choosable bank and an archaeological
/// one, and an index is exactly the artifact that rots first: these tests pin it to the filesystem in
/// both directions, and pin its stated sizes to the real files, because the whole point of the sizes
/// is that someone budgets a prompt against them (bug #15, bug #21).
/// </summary>
public class SF6_2PromptBankTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string PlansDir() => Path.Combine(RepoRoot(), "plans");

    /// <summary>Line endings differ between checkouts; the argv budget does not care about the \r.</summary>
    private static int Chars(string path) => File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Length;

    private static string Index() => File.ReadAllText(Path.Combine(PlansDir(), "README.md"));

    /// <summary>Rows of the form <c>| `personas/qa.md` | 605 | ... |</c>.</summary>
    private static Dictionary<string, int> IndexedItems()
    {
        var rows = Regex.Matches(
            Index(),
            @"^\|\s*`(?<rel>(?:personas|packs)/[^`]+\.md)`\s*\|\s*(?<chars>\d+)\s*\|",
            RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(5));
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in rows)
            map[m.Groups["rel"].Value] = int.Parse(m.Groups["chars"].Value, CultureInfo.InvariantCulture);
        return map;
    }

    private static List<string> BankFiles()
    {
        var plans = PlansDir();
        var files = new List<string>();
        foreach (var sub in new[] { "personas", "packs" })
            foreach (var f in Directory.GetFiles(Path.Combine(plans, sub), "*.md"))
                files.Add(sub + "/" + Path.GetFileName(f));
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    [Fact]
    public void Index_ListsEveryBankFileOnDisk()
    {
        var indexed = IndexedItems().Keys;
        var missing = BankFiles().Where(f => !indexed.Contains(f)).ToList();
        Assert.True(missing.Count == 0,
            "plans/README.md has no row for: " + string.Join(", ", missing) +
            " — a bank item nobody can find is a bank item nobody chooses.");
    }

    [Fact]
    public void Index_NamesNoFileThatDoesNotExist()
    {
        var onDisk = BankFiles();
        var phantom = IndexedItems().Keys.Where(k => !onDisk.Contains(k, StringComparer.Ordinal)).ToList();
        Assert.True(phantom.Count == 0,
            "plans/README.md advertises files that do not exist: " + string.Join(", ", phantom));
    }

    [Fact]
    public void Index_StatesEachItemsRealSize()
    {
        // 10% (floor 25 chars) — loose enough that a typo fix is not a build break, tight enough that
        // the number stays usable for adding up a prompt budget.
        var plans = PlansDir();
        var wrong = new List<string>();
        foreach (var (rel, stated) in IndexedItems())
        {
            var actual = Chars(Path.Combine(plans, rel.Replace('/', Path.DirectorySeparatorChar)));
            var tolerance = Math.Max(25, actual / 10);
            if (Math.Abs(actual - stated) > tolerance)
                wrong.Add($"{rel}: index says {stated}, file is {actual}");
        }
        Assert.True(wrong.Count == 0,
            "plans/README.md sizes are stale — " + string.Join("; ", wrong) +
            ". These numbers are what a plan author budgets against; update the row.");
    }

    [Fact]
    public void EveryPersonaNamedByAPlanResolves()
    {
        var plans = PlansDir();
        var registry = new PersonaRegistry(Path.Combine(plans, "personas"));
        var referenced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var planFile in Directory.GetFiles(plans, "*.plan.json"))
            foreach (Match m in Regex.Matches(
                         File.ReadAllText(planFile),
                         "\"persona\"\\s*:\\s*\"(?<name>[^\"]+)\"",
                         RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
                         TimeSpan.FromSeconds(5)))
                referenced.Add(m.Groups["name"].Value);

        Assert.NotEmpty(referenced);
        var unresolved = referenced.Where(p => string.IsNullOrWhiteSpace(registry.ResolveSystemPrompt(p))).ToList();
        Assert.True(unresolved.Count == 0,
            "plans reference personas with no file and no built-in: " + string.Join(", ", unresolved) +
            " — the reference is silently dropped at render time, so the stage runs with no persona at all.");
    }

    [Fact]
    public void EnrichedPatterns_AreInTheBankWhereTheAgentThatNeedsThemWillRead()
    {
        var plans = PlansDir();
        // Each pattern lives with the role it is for, not in a lump nobody loads.
        Assert.Contains("proof-note", File.ReadAllText(Path.Combine(plans, "packs", "agent-pitfalls.md")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anchor", File.ReadAllText(Path.Combine(plans, "packs", "agent-pitfalls.md")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("owner", File.ReadAllText(Path.Combine(plans, "personas", "planner.md")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("acceptance", File.ReadAllText(Path.Combine(plans, "personas", "planner.md")), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("what it unblocks", File.ReadAllText(Path.Combine(plans, "personas", "docs.md")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Personas_DoNotRepeatTheConductorContract()
    {
        // The contract block renders immediately after the persona and wins. A persona that restates it
        // buys nothing and spends chars from a budget that is already over (see the index's budget note).
        var duplicated = new[] { "HUMAN:", "never weaken", "conductor bg", "--evidence" };
        var offenders = new List<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(PlansDir(), "personas"), "*.md"))
        {
            var text = File.ReadAllText(f);
            foreach (var phrase in duplicated)
                if (text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{Path.GetFileName(f)} repeats '{phrase}'");
        }
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    // --- pack resolution: era-first, shared-second (the reason both packs were unreachable) ---

    private static PlanConfig PlanIn(string dir, string? templatesDir, params string[] packs)
    {
        var plan = new PlanConfig
        {
            Name = "bank",
            Repo = dir,
            Tracker = "T.md",
            PlanDoc = "T.md",
            TemplatesDir = templatesDir,
            Packs = packs.ToList(),
        };
        // PlanDir is derived from the plan file's location — the same way a real run gets it.
        plan.PlanFilePath = Path.Combine(dir, "bank.plan.json");
        return plan;
    }

    private static readonly StageConfig BankStage = new() { Id = "S1", Title = "Stage", Sessions = 1 };

    private static string DeliverWith(PlanConfig plan) => new PromptBuilder(plan).Deliver(BankStage, 1, 1, 1);

    [Fact]
    public void Pack_LoadsFromTheSharedBankWhenTheEraSetHasNone()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "packs"));
        File.WriteAllText(Path.Combine(tmp.Path, "packs", "house.md"), "SHARED-PACK-BODY");

        Assert.Contains("SHARED-PACK-BODY", DeliverWith(PlanIn(tmp.Path, "era-templates", "house")), StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_PrefersTheEraSetWhenBothHaveIt()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "packs"));
        Directory.CreateDirectory(Path.Combine(tmp.Path, "era-templates", "packs"));
        File.WriteAllText(Path.Combine(tmp.Path, "packs", "house.md"), "SHARED-PACK-BODY");
        File.WriteAllText(Path.Combine(tmp.Path, "era-templates", "packs", "house.md"), "ERA-PACK-BODY");

        var prompt = DeliverWith(PlanIn(tmp.Path, "era-templates", "house"));
        Assert.Contains("ERA-PACK-BODY", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("SHARED-PACK-BODY", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_NameThatEscapesThePlanDirectoryIsRefused()
    {
        using var tmp = new TempDir();
        var outside = Path.Combine(tmp.Path, "secret.md");
        File.WriteAllText(outside, "ESCAPED-PACK-BODY");
        Directory.CreateDirectory(Path.Combine(tmp.Path, "nested"));

        var plan = PlanIn(Path.Combine(tmp.Path, "nested"), templatesDir: null, "../secret");
        Assert.DoesNotContain("ESCAPED-PACK-BODY", DeliverWith(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void BothShippedPacksResolveForAPlanThatDeclaresThem()
    {
        // The measured staleness SF6.2 fixed: before the shared fallback these two lived under
        // maestro-templates and no other plan in plans/ could load them even by naming them.
        var prompt = DeliverWith(PlanIn(PlansDir(), "sarban-templates", "agent-pitfalls", "dotnet-engineer"));
        Assert.Contains("mistakes agents keep making here", prompt, StringComparison.Ordinal);
        Assert.Contains("house style for this codebase", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void CurrentEraTemplates_RenderThePacksPlaceholder()
    {
        // A file template replaces the built-in wholesale, so it also decides which placeholders exist.
        // Omit {packs} and every pack the plan declared is dropped with no error anywhere.
        var era = Path.Combine(PlansDir(), "sarban-templates");
        foreach (var name in new[] { "session.md", "fix.md" })
            Assert.Contains("{packs}", File.ReadAllText(Path.Combine(era, name)), StringComparison.Ordinal);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("sf62-bank-").FullName;
        public void Dispose()
        {
            try { TestTemp.DeleteTree(Path); }
            catch (IOException) { /* a locked temp dir is not a test failure */ }
            catch (UnauthorizedAccessException) { /* ditto */ }
        }
    }
}
