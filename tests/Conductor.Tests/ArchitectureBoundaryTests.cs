using System.Reflection;
using System.Text.RegularExpressions;

namespace Conductor.Tests;

/// <summary>
/// K2.2: the layering rules, as tests that fail the build and name the offending type.
/// </summary>
/// <remarks>
/// K2.1 split the engine into <c>Conductor.Core</c> (domain, orchestration, store) and <c>Conductor</c>
/// (CLI + hosting), so the dependency DIRECTION is now a link error. These tests exist because a project
/// reference alone does not say the rest of it: nothing in a csproj stops the store from printing to the
/// console, a command from driving <c>SessionRunner</c> directly, or an event record from being declared
/// next to the thing that raises it.
/// <para/>
/// They land WITH the extraction on purpose (see the K2 spec): a boundary asserted in a design doc is a
/// suggestion — <c>AGENTS.md</c> has documented a Command/Query/Event layering rule for three eras and
/// nothing ever checked it. Every rule here names the offending TYPE or FILE and the rule it broke, so the
/// failure tells the next session what to do rather than that something is wrong.
/// <para/>
/// Deliberately no architecture-DSL dependency. Every rule below is either "what did the compiler actually
/// link" (<see cref="Assembly.GetReferencedAssemblies"/>) or "what does the source actually say" — the two
/// questions a fluent DSL would answer for us, at the cost of a third-party package in the test project and
/// a second, differently-shaped way to write a rule in a repo that already has
/// <see cref="ArchitectureTests"/>. The rules, not the syntax, are the deliverable.
/// </remarks>
public class ArchitectureBoundaryTests
{
    /// <summary>The assembly holding the domain, orchestration and store.</summary>
    private static readonly Assembly Core = typeof(Conductor.Core.PromptBuilder).Assembly;

    /// <summary>The CLI + hosting shell. Its AssemblyName is lowercase <c>conductor</c> — that is the
    /// name a reference to it carries, and the name these rules look for.</summary>
    private static readonly Assembly Shell = typeof(Conductor.Hosting.ConductorHost).Assembly;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static List<FileInfo> SourcesUnder(params string[] parts) =>
        new DirectoryInfo(Path.Combine([RepoRoot(), .. parts]))
            .EnumerateFiles("*.cs", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();

    /// <summary>Comments are not dependencies. <c>DoctorCommand</c> may say the word "SessionRunner" in a
    /// doc-comment explaining why it does NOT use one; a rule that cannot tell prose from code teaches the
    /// next session to delete the explanation, which is the opposite of what these tests are for.</summary>
    private static readonly Regex Comments = new(
        @"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(5));

    private static string CodeOnly(FileInfo file) => Comments.Replace(File.ReadAllText(file.FullName), " ");

    /// <summary>The engine must not link the CLI or any UI. This is the compiled truth — what the build
    /// actually resolved — and it is the rule the whole K2.1 extraction exists to make checkable: before
    /// the split there was one assembly, so this question could not even be asked.</summary>
    [Fact]
    public void CoreDoesNotLinkTheCliOrAnyUiAssembly()
    {
        var forbidden = Core.GetReferencedAssemblies()
            .Where(r => r.Name is not null && (
                r.Name.Equals("conductor", StringComparison.OrdinalIgnoreCase) ||
                r.Name.StartsWith("Spectre", StringComparison.OrdinalIgnoreCase)))
            .Select(r => $"  {Core.GetName().Name} -> {r.Name} — core may not depend on the CLI or a UI; the run outlives every face.")
            .ToList();

        Assert.True(forbidden.Count == 0,
            "K2.2 layering — Conductor.Core linked something it may not:\n" + string.Join("\n", forbidden));
    }

    /// <summary>Core may not host HTTP. <c>ControlPlaneServer</c> is hosting and lives in the shell; the wire
    /// CONTRACT (<c>ControlPlaneDto</c>) stays in core because the fleet scan reads other runs' discovery
    /// files. ASP.NET Core is checked at the assembly level and <see cref="System.Net.HttpListener"/> at the
    /// source level, because the BCL listener needs no package reference to sneak back in.</summary>
    [Fact]
    public void CoreDoesNotHostHttp()
    {
        var violations = Core.GetReferencedAssemblies()
            .Where(r => r.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase) == true)
            .Select(r => $"  {Core.GetName().Name} -> {r.Name} — core does not serve HTTP; hosting belongs in Conductor.")
            .ToList();

        foreach (var file in SourcesUnder("src", "Conductor.Core"))
        {
            if (CodeOnly(file).Contains("HttpListener", StringComparison.Ordinal))
                violations.Add($"  {file.Name} uses HttpListener — an HTTP server in core; move it to src/Conductor/Http.");
        }

        Assert.True(violations.Count == 0,
            "K2.2 layering — core started hosting HTTP:\n" + string.Join("\n", violations));
    }

    /// <summary>Source truth for the same boundary, and the one that catches a violation EARLIEST: a
    /// <c>using</c> naming the shell cannot compile today, but a fully-qualified reference in a future
    /// merge would, and this rule reads the text rather than the link table.</summary>
    [Fact]
    public void CoreSourceNeverNamesTheShell()
    {
        var forbidden = new[] { "Conductor.Commands", "Conductor.Hosting", "Conductor.Http", "Spectre.Console" };
        var violations = new List<string>();

        foreach (var file in SourcesUnder("src", "Conductor.Core"))
        {
            var code = CodeOnly(file);
            foreach (var name in forbidden)
            {
                if (code.Contains(name, StringComparison.Ordinal))
                    violations.Add($"  {file.Name} names {name} — core may not reach up into the CLI/hosting shell or a UI.");
            }
        }

        Assert.True(violations.Count == 0,
            "K2.2 layering — core reached up into the shell:\n" + string.Join("\n", violations));
    }

    /// <summary>The store persists; it does not present. A store that writes to the console cannot be used
    /// by the control plane, the Face or a test without polluting somebody's output — and
    /// <c>Console.SetOut</c> in a test project is how bug #26's order-dependent flake happened.</summary>
    [Fact]
    public void TheStoreDoesNotWriteToTheConsole()
    {
        var violations = new List<string>();

        foreach (var file in SourcesUnder("src", "Conductor.Core", "Store"))
        {
            var code = CodeOnly(file);
            foreach (var writer in new[] { "Console.Write", "Console.Error", "Console.Out", "AnsiConsole" })
            {
                if (code.Contains(writer, StringComparison.Ordinal))
                    violations.Add($"  {file.Name} calls {writer} — the store persists, it does not present. Return the data and let a caller render it.");
            }
        }

        Assert.True(violations.Count == 0,
            "K2.2 layering — the store started printing:\n" + string.Join("\n", violations));
    }

    /// <summary>A command parses arguments and calls ONE seam — <c>Orchestrator</c> / <c>ConductorHost</c>.
    /// The moment a command drives <c>SessionRunner</c> or <c>RunLoop</c> itself, the run loop's invariants
    /// (single-threaded state mutation, one claim path, the settle-and-retry rails) acquire a second caller
    /// that no test covers, and the CLI stops being one of three equal ingresses.</summary>
    [Fact]
    public void CommandsDoNotReachIntoOrchestrationInternals()
    {
        var internals = new[] { "SessionRunner", "RunLoop", "VerdictEngine", "GateOrchestrator", "RunContext" };
        var violations = new List<string>();

        foreach (var file in SourcesUnder("src", "Conductor", "Commands"))
        {
            var code = CodeOnly(file);
            foreach (var type in internals)
            {
                if (Regex.IsMatch(code, $@"\b{type}\b", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2)))
                    violations.Add($"  {file.Name} uses {type} — commands go through Orchestrator/ConductorHost, not into the run loop.");
            }
        }

        Assert.True(violations.Count == 0,
            "K2.2 layering — a command reached into orchestration internals:\n" + string.Join("\n", violations));
    }

    /// <summary>Every event record lives in <c>Conductor.Core.Events</c>, in both assemblies. The event log is
    /// the run's only durable truth and every read endpoint folds it; an event declared next to the code that
    /// raises it is invisible to the fold, to the projection, and to anyone looking for "what can happen".</summary>
    [Fact]
    public void EventTypesStayInTheEventNamespace()
    {
        const string EventNamespace = "Conductor.Core.Events";
        var strays = new[] { Core, Shell }
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(Conductor.Core.Events.ConductorEvent).IsAssignableFrom(t)
                     && t != typeof(Conductor.Core.Events.ConductorEvent)
                     && t.Namespace?.StartsWith(EventNamespace, StringComparison.Ordinal) != true)
            .Select(t => $"  {t.FullName} is a ConductorEvent outside {EventNamespace} — move it there.")
            .ToList();

        Assert.True(strays.Count == 0,
            $"K2.2 layering — event types must live in {EventNamespace}:\n" + string.Join("\n", strays));
    }

    /// <summary>The split must not quietly un-split. If the shell assembly declares a type in a
    /// <c>Conductor.Core.*</c> namespace, someone has started re-growing the engine inside the CLI — the
    /// same failure mode <see cref="ArchitectureTests.NoPlanningDomainTypeRemainsInTheEngineAssembly"/>
    /// guards for the planning library, one layer up.</summary>
    [Fact]
    public void NoCoreDomainTypeRemainsInTheShellAssembly()
    {
        var strays = Shell.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Conductor.Core", StringComparison.Ordinal) == true)
            .Select(t => $"  {t.FullName} is declared in {Shell.GetName().Name} — domain types belong in Conductor.Core.")
            .ToList();

        Assert.True(strays.Count == 0,
            "K2.2 layering — core domain is re-growing inside the CLI:\n" + string.Join("\n", strays));
    }

    // ---------------------------------------------------------------- KS5.2: no uncounted model spend

    /// <summary>The configuration types that carry a model process's command line. A file that spawns a
    /// process AND names one of these is spending money on a model.</summary>
    private static readonly string[] ModelConfigTypes =
        ["AgentConfig", "AdvisorConfig", "StatusAgentConfig", "SupervisorConfig"];

    /// <summary>How a process gets started here. <c>WatchHook</c> is named because it is the one spawn
    /// that could not reuse <c>ProcessRunner</c> (it needs stdin) and so hides its <c>Process.Start</c>
    /// behind a helper.</summary>
    /// <remarks><c>ProcessStartInfo</c> is in the list because the delivery agent's own spawn is
    /// <c>proc.Start()</c> on an instance — a rule that only knew the static <c>Process.Start</c> would
    /// miss the biggest spender in the engine.</remarks>
    private static readonly string[] SpawnCalls = ["ProcessRunner.Run", "Process.Start", "ProcessStartInfo"];

    /// <summary>The helpers that spawn a model on someone else's behalf. A CALLER of one of these is as
    /// responsible for the money as the file that holds the <c>Process.Start</c>: this is what makes the
    /// rule catch <c>chat</c> and <c>plan import</c>, which spend through <c>Advisor</c> and could
    /// otherwise say nothing for ever.</summary>
    private static readonly string[] ModelHelpers =
        ["Advisor.AskAsync", "Advisor.AskTextAsync", "Advisor.ConsultAsync", "Advisor.ConsultForVerdictAsync",
         "StatusAgent.Run", "LaneRunner.RunAsync", "MutatingLaneRunner.RunAsync",
         "AuthSmokeTest.RunAsync", "WatchHook.RunAsync"];

    /// <summary>Evidence that a file accounts for what it spent — it produces a receipt, records one, or
    /// hands one to a caller. Deliberately broad: the rule is "this file has an answer to what it cost",
    /// not "this file calls one blessed method".</summary>
    private static readonly string[] AccountingMarkers =
        ["SpendReceipt", "RunSpendLedger", "BilledSpend", "SpendCategory", "RecordCost", "Ledger.Record"];

    /// <summary>KS5.2 — every path that spawns a model either accounts for what it cost or is listed
    /// here with the reason it does not. Silence is the thing this rule exists to make impossible: seven
    /// of the eight model-spawning paths in this engine wrote nothing at all, and the eighth wrote a
    /// number nobody had been charged, so a run's own report could not say where its money went.</summary>
    private static readonly Dictionary<string, string> UncountedSpendExemptions = new(StringComparer.Ordinal)
    {
        ["AgentSession.cs"] =
            "the delivery agent's row is written by RunLoop.EmitSessionFinished from the session record, " +
            "keyed to the session it belongs to — the one spender whose accounting predates KS5.2",
        ["DoctorCommand.cs"] =
            "runs the auth probe as a diagnostic against a plan, not a run — there is no session to key " +
            "a row to, and doctor must stay safe to point at somebody else's plan",
        ["AuditCommand.cs"] =
            "`audit --replay` is an operator's question about a FINISHED stage: no live session, no live " +
            "cap. It prints what it was billed instead of writing a row",
        ["ChatCommand.cs"] =
            "`chat` asks the plan's advisor a question outside any run. It prints what it was billed",
        ["PlanImportService.cs"] =
            "an import runs BEFORE there is a run id — nothing to key a costs row to. It logs the figure",
        ["StatusCommand.cs"] =
            "`status --agent` is operator-invoked between sessions; it prints the reporter's bill rather " +
            "than making the CLI a second writer to a live run's database",
    };

    [Fact]
    public void EveryModelSpawnAccountsForWhatItSpent()
    {
        var violations = new List<string>();
        var reached = new List<string>();

        var sources = SourcesUnder("src");
        var code = sources.ToDictionary(f => f.FullName, CodeOnly, StringComparer.Ordinal);
        // A partial type is ONE type however many files it is spread over — ControlPlaneServer is nine
        // of them — so the accounting may live in a sibling. Keyed on the name before the first dot,
        // which is exactly this repo's partial convention (RunLoop.Plumbing.cs, VerdictEngine.Advisor.cs).
        var byType = sources
            .GroupBy(f => f.Name.Split('.')[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Concat(g.Select(f => code[f.FullName])), StringComparer.Ordinal);

        foreach (var file in sources)
        {
            var text = code[file.FullName];
            var spawnsAModel = SpawnCalls.Any(s => text.Contains(s, StringComparison.Ordinal))
                            && ModelConfigTypes.Any(t => text.Contains(t, StringComparison.Ordinal));
            var callsAModelHelper = ModelHelpers.Any(h => text.Contains(h, StringComparison.Ordinal));
            if (!spawnsAModel && !callsAModelHelper) continue;

            reached.Add(file.Name);
            if (UncountedSpendExemptions.ContainsKey(file.Name)) continue;
            var whole = byType[file.Name.Split('.')[0]];
            if (AccountingMarkers.Any(m => whole.Contains(m, StringComparison.Ordinal))) continue;

            violations.Add(
                $"  {file.Name} spawns a model and records no spend — take a receipt through " +
                "Conductor.Core.Accounting.BilledSpend and record it (RunSpendLedger.Record), or add " +
                "the file to UncountedSpendExemptions with the reason it cannot.");
        }

        Assert.True(violations.Count == 0,
            "KS5.2 — a model was spawned and nobody counted the money:\n" + string.Join("\n", violations));

        // The exemption list must describe reality, the way architecture-baseline.json must: a file that
        // stopped spawning models, or was renamed, leaves a reason behind that reads as a live decision.
        var stale = UncountedSpendExemptions.Keys
            .Where(name => !reached.Contains(name, StringComparer.Ordinal))
            .Select(name => $"  {name} is exempted from the spend rule but no longer reaches a model — drop the entry.")
            .ToList();

        Assert.True(stale.Count == 0,
            "KS5.2 — the exemption list has gone stale:\n" + string.Join("\n", stale));
    }

    /// <summary>The reference direction, read off the csproj files themselves. The link-level rules above
    /// prove what today's build did; this one proves the SHAPE that makes it impossible to do otherwise, so
    /// a future session that adds <c>&lt;ProjectReference Include="..\Conductor\Conductor.csproj"&gt;</c> to
    /// core meets a red test naming the file it edited rather than a circular-reference error it might
    /// "fix" by deleting the arch tests.</summary>
    [Fact]
    public void TheProjectReferenceDirectionPointsOneWayOnly()
    {
        // Matched on the Include attribute, not on raw text: both files EXPLAIN this rule in a comment,
        // and a rule that a comment about the rule can break is a rule nobody will keep.
        static List<string> Includes(string csproj, string element) =>
            Regex.Matches(csproj, $"<{element}\\s+Include=\"(?<v>[^\"]+)\"",
                    RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2))
                .Select(m => m.Groups["v"].Value).ToList();

        var core = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Conductor.Core", "Conductor.Core.csproj"));
        var shell = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Conductor", "Conductor.csproj"));

        Assert.DoesNotContain(Includes(core, "ProjectReference"),
            p => p.EndsWith("Conductor.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(Includes(core, "PackageReference"),
            p => p.StartsWith("Spectre", StringComparison.Ordinal));
        Assert.Contains(Includes(shell, "ProjectReference"),
            p => p.EndsWith("Conductor.Core.csproj", StringComparison.Ordinal));
    }
}
