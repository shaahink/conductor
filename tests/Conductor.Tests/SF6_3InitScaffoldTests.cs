using System.Text.RegularExpressions;
using Conductor.Commands;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SF6.3 — <c>conductor init</c> scaffolds the refreshed set, wires telegram + supervisor hints, and
/// its output passes doctor clean.
///
/// <para>Three things are pinned here, each because its absence was measured on the shipped scaffold
/// rather than suspected:</para>
/// <list type="number">
/// <item>init wrote TWO of the eight templates, so the templates directory lied by omission about
/// what "templates as content" covers.</item>
/// <item>a commented hint block is only worth having if it PARSES when uncommented. These tests strip
/// the comment markers and load the result, so a typo'd key or a stray comma in the scaffold fails
/// here instead of at 3am in someone else's repo.</item>
/// <item>the scaffold shipped with no spend cap at all, which doctor warns about — the one warn on a
/// fresh scaffold that was a real gap rather than a fact of life.</item>
/// </list>
/// </summary>
public sealed class SF6_3InitScaffoldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sf63-{Guid.NewGuid():N}");

    public SF6_3InitScaffoldTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    // ---- (1) the whole bank is on disk, and it is the bank the renderer reads -------------------

    [Fact]
    public void ScaffoldWritesEveryBuiltInTemplate()
    {
        var templatesDir = Path.Combine(_dir, "templates");
        WriteScaffold(templatesDir);

        foreach (var name in PromptBuilder.BuiltInNames)
        {
            var path = Path.Combine(templatesDir, name);
            Assert.True(File.Exists(path), $"init did not scaffold {name}");
            Assert.Equal(PromptBuilder.BuiltIn(name), File.ReadAllText(path));
        }
    }

    [Fact]
    public void ScaffoldWritesNothingBesidesTheBuiltIns()
    {
        var templatesDir = Path.Combine(_dir, "templates");
        WriteScaffold(templatesDir);

        var written = Directory.GetFiles(templatesDir).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(PromptBuilder.BuiltInNames.Order(StringComparer.Ordinal), written);
    }

    /// <summary>The list is only load-bearing if it matches the switch it claims to enumerate. A name
    /// added to <c>BuiltIn</c> and forgotten in <c>BuiltInNames</c> would ship a half-scaffold in
    /// silence, so the switch's own case labels are read back out of the source and compared.</summary>
    [Fact]
    public void BuiltInNamesEnumeratesEveryCaseOfTheBuiltInSwitch()
    {
        var source = ReadRepoFile(Path.Combine("src", "Conductor", "Core", "PromptBuilder.cs"));
        var body = source[source.IndexOf("internal static string BuiltIn(string name) => name switch", StringComparison.Ordinal)..];
        var cases = Regex.Matches(body, @"^\s{8}""(?<n>[a-z]+\.md)"" =>", RegexOptions.Multiline, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups["n"].Value)
            .ToArray();

        Assert.NotEmpty(cases);
        Assert.Equal(cases.Order(StringComparer.Ordinal), PromptBuilder.BuiltInNames.Order(StringComparer.Ordinal));
    }

    /// <summary>The claim a scaffolded template makes is "edit me and the prompt changes". Proven by
    /// editing each one and rendering through the real entry points — a file init writes into a
    /// directory nothing reads would pass every other test in this class.</summary>
    [Fact]
    public void EveryScaffoldedTemplateIsTheOneTheRendererReads()
    {
        var plan = ScaffoldedPlan();
        WriteScaffold(Path.Combine(_dir, "templates"));

        foreach (var name in PromptBuilder.BuiltInNames)
        {
            var sentinel = $"SENTINEL-{name}";
            File.WriteAllText(Path.Combine(_dir, "templates", name), sentinel);
            Assert.Equal(Path.Combine(_dir, "templates", name), new PromptBuilder(plan).ResolveTemplatePath(name));
            Assert.Equal(sentinel, RenderKind(new PromptBuilder(plan), plan.Stages[0], name));
        }
    }

    private static string RenderKind(PromptBuilder p, StageConfig s, string template) => template switch
    {
        "session.md" => p.Deliver(s, 1, 1, 1),
        "fix.md" => p.Fix(s, 1, 1, 1, new PendingFix { FromSession = 1, GateFailures = "g", ProgressSummary = "p" }),
        "resume.md" => p.Resume(s, 1, 1, 1, new PendingResume { FromSession = 1, Reason = "r" }),
        "verify.md" => p.Verify(s, 1, new PendingVerify { FromSession = 1, StageStartHead = "HEAD" }),
        "review.md" => p.Review(s, 1, 1, 1, "review.md"),
        "audit.md" => p.Audit(s, 1, new PendingAudit { StageId = s.Id, StageStartHead = "HEAD" }, "HEAD"),
        "advisor.md" => p.Advisor(s, "o", "g", "c", "h", "t", 1, 1),
        "chat.md" => p.Chat("how much has this run cost?"),
        _ => throw new ArgumentException($"SF6.3: {template} is scaffolded but this test knows no way to " +
            "render it — wire it to an entry point or drop it from BuiltInNames", nameof(template)),
    };

    // ---- (2) the commented hints parse when uncommented -----------------------------------------

    [Fact]
    public void ScaffoldCarriesTelegramAndSupervisorHints()
    {
        var json = InitCommand.BuildPlanJson("Demo", _dir, RepoKind.Dotnet);
        Assert.Contains("// \"telegram\": {", json, StringComparison.Ordinal);
        Assert.Contains("// \"supervisor\": {", json, StringComparison.Ordinal);
        Assert.Contains("CONDUCTOR_TELEGRAM_TOKEN", json, StringComparison.Ordinal);  // the token is never written into the plan
        Assert.Contains("// \"packs\":", json, StringComparison.Ordinal);
    }

    /// <summary>Packs are named in the scaffold and left OFF. SF6.2 measured the two shipped packs at
    /// 5974 characters against the ~8k command line Windows will accept; a scaffold that switched them
    /// on would hand a new user a first run whose agent never starts.</summary>
    [Fact]
    public void ScaffoldDoesNotEnablePacks() => Assert.Empty(ScaffoldedPlan().Packs ?? []);

    [Theory]
    [InlineData("telegram")]
    [InlineData("supervisor")]
    [InlineData("advisor")]
    public void UncommentingAHintYieldsAPlanThatLoads(string block)
    {
        var plan = LoadWithBlockUncommented(block);

        switch (block)
        {
            case "telegram":
                Assert.NotNull(plan.Telegram);
                Assert.True(plan.Telegram!.EnableTwoWay);
                Assert.Single(plan.Telegram.AllowedChatIds);
                Assert.Equal(4, plan.Telegram.PollIntervalSeconds);
                break;
            case "supervisor":
                Assert.NotNull(plan.Supervisor);
                Assert.True(plan.Supervisor!.Enabled);
                Assert.Contains("night watch", plan.Supervisor.Command, StringComparison.Ordinal);
                Assert.Equal(6, plan.Supervisor.MaxPerHour);
                Assert.False(string.IsNullOrWhiteSpace(plan.Supervisor.StandingOrders));
                break;
            default:
                Assert.NotNull(plan.Advisor);
                Assert.True(plan.Advisor!.Enabled);
                Assert.Equal("json", plan.Advisor.Output);
                break;
        }
    }

    /// <summary>Uncomments exactly one hint block — the run of <c>// </c> lines whose first line names
    /// the key — and loads the result, the way a reader deleting two slashes would.</summary>
    private PlanConfig LoadWithBlockUncommented(string key)
    {
        var lines = InitCommand.BuildPlanJson("Demo", _dir, RepoKind.Dotnet).Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimStart().StartsWith($"// \"{key}\": {{", StringComparison.Ordinal));
        Assert.True(start >= 0, $"no commented \"{key}\" block in the scaffold");

        for (var i = start; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("//", StringComparison.Ordinal)) break;
            var indent = lines[i][..(lines[i].Length - trimmed.Length)];
            lines[i] = indent + trimmed[2..].TrimStart();
        }

        var planPath = Path.Combine(_dir, "conductor.plan.json");
        File.WriteAllText(planPath, string.Join('\n', lines));
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), InitCommand.BuildTrackerMd("Demo"));
        return PlanConfig.Load(planPath);
    }

    // ---- (3) doctor clean ------------------------------------------------------------------------

    [Fact]
    public void ScaffoldShipsASpendCapSoDoctorDoesNotWarnOnBudget()
    {
        var plan = ScaffoldedPlan();

        Assert.NotNull(plan.Limits);
        Assert.NotNull(plan.Limits!.MaxRunCostUsd);
        Assert.True(plan.Limits.MaxRunCostUsd > 0);
    }

    /// <summary>Whatever else init writes, the scaffold must still load — the self-check in Execute
    /// deletes it otherwise, and a scaffold that deletes itself is the worst first impression there is.</summary>
    [Fact]
    public void ScaffoldWithAllHintsStillLoadsAndKeepsItsGates()
    {
        var plan = ScaffoldedPlan();
        Assert.Equal("Demo", plan.Name);
        Assert.Single(plan.Stages);
        Assert.Contains(plan.Gates, g => g.Name == "build");
    }

    private PlanConfig ScaffoldedPlan()
    {
        var planPath = Path.Combine(_dir, "conductor.plan.json");
        File.WriteAllText(planPath, InitCommand.BuildPlanJson("Demo", _dir, RepoKind.Dotnet));
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), InitCommand.BuildTrackerMd("Demo"));
        return PlanConfig.Load(planPath);
    }

    /// <summary>Writes the scaffold exactly as <c>InitCommand.Execute</c> does — same pairs, same
    /// destination — so these tests exercise the shipped list and not a copy of it.</summary>
    private static void WriteScaffold(string templatesDir)
    {
        Directory.CreateDirectory(templatesDir);
        foreach (var (path, content) in InitCommand.TemplateScaffold(templatesDir))
            File.WriteAllText(path, content);
    }

    private static string ReadRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Conductor.slnx")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
