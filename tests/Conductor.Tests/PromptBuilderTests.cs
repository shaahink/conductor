using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class PromptBuilderTests
{
    private static PlanConfig Plan() => new()
    {
        Name = "Loom",
        Repo = @"C:\repo",
        Tracker = "LOOM-START.md",
        PlanDoc = "docs/proposal.md",
        PromptExtra = "EXTRA-MARKER",
    };

    private static readonly StageConfig Stage = new() { Id = "L2", Title = "BodyFacts", Sessions = 3, Notes = "watch the anchoring" };

    [Fact]
    public void DeliverFillsEveryPlaceholder()
    {
        var p = new PromptBuilder(Plan()).Deliver(Stage, 5, 2, 6);
        Assert.Contains("session #5", p);
        Assert.Contains("stage L2 — BodyFacts", p);
        Assert.Contains("attempt 2/6", p);
        Assert.Contains("LOOM-START.md", p);
        Assert.Contains("EXTRA-MARKER", p);
        Assert.Contains("watch the anchoring", p);
        Assert.DoesNotContain("{", p.Replace("{planName}", "")); // no unfilled {placeholders} left
    }

    [Fact]
    public void FixEmbedsGateFailures()
    {
        var fix = new PendingFix { FromSession = 4, GateFailures = "### Gate `build` FAILED", ProgressSummary = "commits: 0" };
        var p = new PromptBuilder(Plan()).Fix(Stage, 5, 3, 6, fix);
        Assert.Contains("session (#4) did not verify", p);
        Assert.Contains("Gate `build` FAILED", p);
        Assert.Contains("commits: 0", p);
    }

    [Fact]
    public void ResumeStatesTheReason()
    {
        var resume = new PendingResume { ClaudeSessionId = "abc", Reason = "session stalled (no output)", ResumeCount = 1 };
        var p = new PromptBuilder(Plan()).Resume(Stage, 6, 3, 6, resume);
        Assert.Contains("session stalled (no output)", p);
        Assert.Contains("git status", p);
    }

    [Fact]
    public void DeliverRendersReadOrder()
    {
        var plan = Plan();
        plan.ReadOrder = ["docs/ARCH.md", "docs/API.md", "docs/STYLE.md"];
        var p = new PromptBuilder(plan).Deliver(Stage, 1, 1, 1);
        Assert.Contains("Required reading (in order):", p);
        Assert.Contains("1. docs/ARCH.md", p);
        Assert.Contains("2. docs/API.md", p);
        Assert.Contains("3. docs/STYLE.md", p);
    }

    [Fact]
    public void ReadOrderEmptyWhenUnset()
    {
        var p = new PromptBuilder(Plan()).Deliver(Stage, 1, 1, 1);
        Assert.DoesNotContain("Required reading", p);
    }

    [Fact]
    public void PlanDocFallsBackToTrackerWhenUnset()
    {
        var plan = Plan();
        plan.PlanDoc = ""; // a minimal plan with no separate design doc
        var p = new PromptBuilder(plan).Deliver(Stage, 1, 1, 1);
        // The ritual must not render the broken "exactly as `` prescribes".
        Assert.DoesNotContain("as `` prescribes", p);
        Assert.Contains("exactly as `LOOM-START.md` prescribes", p);
    }

    [Fact]
    public void TemplateFileOverridesBuiltIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-tpl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var planPath = Path.Combine(dir, "plan.json");
        try
        {
            File.WriteAllText(Path.Combine(dir, "session.md"), "CUSTOM {stage} TEMPLATE");
            File.WriteAllText(planPath, "{}");
            var plan = Plan();
            typeof(PlanConfig).GetProperty(nameof(PlanConfig.PlanFilePath))!.SetValue(plan, planPath);
            var p = new PromptBuilder(plan).Deliver(Stage, 1, 1, 6);
            Assert.Equal("CUSTOM L2 TEMPLATE", p);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PersonaSystemPromptPrependedToBasePrompt()
    {
        var plan = Plan();
        var stage = new StageConfig { Id = "B2", Title = "Spine", Sessions = 3, Persona = "deliver" };
        var reg = new PersonaRegistry((string?)null);
        var builder = new PromptBuilder(plan, reg);
        var prompt = builder.Deliver(stage, 1, 1, 6);

        // Persona system prompt appears first
        var personaPrompt = reg.ResolveSystemPrompt("deliver")!;
        Assert.StartsWith(personaPrompt, prompt);

        // Conductor contract rules appear AFTER the persona prompt (merge order: contract wins)
        var contractIdx = prompt.IndexOf("Evidence or it did not happen", StringComparison.Ordinal);
        var personaIdx = prompt.IndexOf(personaPrompt, StringComparison.Ordinal);
        Assert.True(contractIdx > personaIdx, "Contract rules must come after persona system prompt");
    }

    [Fact]
    public void NoPersonaMeansNoSystemPromptPrepended()
    {
        var plan = Plan();
        var stage = new StageConfig { Id = "S1", Title = "Test" };
        var builder = new PromptBuilder(plan);
        var prompt = builder.Deliver(stage, 1, 1, 6);

        // No persona → prompt starts with the standard "You are one autonomous engineering session..."
        Assert.StartsWith("You are one autonomous engineering session", prompt);
    }

    [Fact]
    public void PersonaScrapedFromNotesWhenPersonaFieldNull()
    {
        var plan = Plan();
        var stage = new StageConfig { Id = "B2", Title = "Spine", Notes = "Persona: deliver. Do the thing." };
        var reg = new PersonaRegistry((string?)null);
        var builder = new PromptBuilder(plan, reg);
        var prompt = builder.Deliver(stage, 1, 1, 6);

        // Persona resolved from legacy "Persona: deliver" notes hint
        var personaPrompt = reg.ResolveSystemPrompt("deliver")!;
        Assert.StartsWith(personaPrompt, prompt);
    }

    /// <summary>FU-B4.x — Persona divergence: different personas must produce meaningfully different
    /// prompts. If two personas generate identical output the registry is broken or the prompt builder
    /// is ignoring the persona field.</summary>
    [Fact]
    public void PersonaDivergence_DifferentPersonasProduceDifferentPrompts()
    {
        var plan = Plan();
        var reg = new PersonaRegistry((string?)null);
        var builder = new PromptBuilder(plan, reg);

        var deliver = builder.Deliver(
            new StageConfig { Id = "B1", Title = "Test", Persona = "deliver" }, 1, 1, 6);
        var verify = builder.Deliver(
            new StageConfig { Id = "B1", Title = "Test", Persona = "verify" }, 1, 1, 6);
        var advise = builder.Deliver(
            new StageConfig { Id = "B1", Title = "Test", Persona = "advise" }, 1, 1, 6);

        // Each persona must produce distinct prompts — if any two are equal the builder is broken.
        Assert.NotEqual(deliver, verify);
        Assert.NotEqual(deliver, advise);
        Assert.NotEqual(verify, advise);

        // Also verify that the persona-specific system prompts are actually in the output
        Assert.Contains("DELIVERY specialist", deliver, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VERIFICATION specialist", verify, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ADVISORY specialist", advise, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LessonsVariableEmptyWhenNoFile()
    {
        var plan = Plan();
        plan.Batteries = new BatteriesConfig { Lessons = true };
        var builder = new PromptBuilder(plan);
        var section = builder.BatterySection(new RunState());
        // No lessons file → battery section is empty
        Assert.Equal("", section);
    }

    [Fact]
    public void LessonsInjectedViaBatterySection()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"conductor-prompt-test-{Guid.NewGuid():N}");
        try
        {
            var conductorDir = Path.Combine(tmpDir, ".conductor");
            var lessons = new LessonsManager(conductorDir);
            lessons.Append("B7", 45, "Blindly patching concurrency without root cause understanding.");

            var plan = Plan();
            typeof(PlanConfig).GetProperty("PlanFilePath")!.SetValue(plan, Path.Combine(tmpDir, "dummy.json"));
            plan.Repo = tmpDir;
            plan.Batteries = new BatteriesConfig { Lessons = true };
            var stage = new StageConfig { Id = "B8", Title = "Brain" };
            var builder = new PromptBuilder(plan, lessons: lessons);
            var section = builder.BatterySection(new RunState());

            Assert.Contains("### lessons", section);
            Assert.Contains("B7-45", section);
            Assert.Contains("Blindly patching concurrency", section);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }

    [Fact]
    public void BatterySectionRendersWhenConfigured()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"conductor-battery-prompt-{Guid.NewGuid():N}");
        try
        {
            var conductorDir = Path.Combine(tmpDir, ".conductor");
            var lessons = new Conductor.Core.LessonsManager(conductorDir);
            lessons.Append("B7", 45, "Race condition in event log file creation.");

            var plan = Plan();
            typeof(PlanConfig).GetProperty("PlanFilePath")!.SetValue(plan, Path.Combine(tmpDir, "dummy.json"));
            plan.Repo = tmpDir;
            plan.Batteries = new BatteriesConfig { Lessons = true, RecentFailure = true };

            var builder = new PromptBuilder(plan, lessons: lessons);
            var state = new RunState();
            var section = builder.BatterySection(state);

            Assert.Contains("### lessons", section);
            Assert.Contains("Race condition", section);
            Assert.DoesNotContain("### recent-failure", section);
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
            catch (IOException) { /* best-effort */ }
        }
    }

    [Fact]
    public void BatterySectionEmptyWhenNotConfigured()
    {
        var plan = Plan();
        plan.Batteries = null;
        var builder = new PromptBuilder(plan);
        var section = builder.BatterySection(new RunState());
        Assert.Equal("", section);
    }

    // ---------------------------------------------------------------- prompt contract (M0)

    /// <summary>Every session kind must carry the tool contract. The mechanisms it advertises (ledger, bg,
    /// task) were built, wired and never used for the whole of F1-F8 precisely because no prompt mentioned
    /// them — the ledger table sat at zero rows. A capability the agent is not told about does not exist,
    /// so this is a behavioural guarantee, not a docs nicety.</summary>
    [Fact]
    public void EverySessionKindCarriesTheToolContract()
    {
        var b = new PromptBuilder(Plan());
        var prompts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["deliver"] = b.Deliver(Stage, 1, 1, 3),
            ["fix"] = b.Fix(Stage, 2, 1, 3, new PendingFix { FromSession = 1, GateFailures = "build: red", ProgressSummary = "none" }),
            ["resume"] = b.Resume(Stage, 3, 1, 3, new PendingResume { FromSession = 2, Reason = "cancelled" }),
            ["verify"] = b.Verify(Stage, 4, new PendingVerify { FromSession = 3, StageStartHead = "abc123" }),
        };

        foreach (var (kind, prompt) in prompts)
        {
            Assert.Contains("conductor note", prompt, StringComparison.Ordinal);
            Assert.Contains("conductor bg", prompt, StringComparison.Ordinal);
            Assert.Contains("conductor task", prompt, StringComparison.Ordinal);
            Assert.True(prompt.Contains("MCP", StringComparison.Ordinal), $"{kind} prompt must name the MCP surface");
        }
    }

    /// <summary>The verifier's own pass mark must be rendered as a number. It used to say the literal text
    /// "≥{plan.VerifierThreshold}" — an unbound name that shipped to the model for every verification.</summary>
    [Fact]
    public void VerifyPromptRendersTheThresholdAsANumber()
    {
        var plan = Plan();
        plan.Limits.VerifierThreshold = 85;

        var prompt = new PromptBuilder(plan).Verify(Stage, 4, new PendingVerify { FromSession = 3, StageStartHead = "abc123" });

        Assert.Contains("≥85", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("VerifierThreshold", prompt, StringComparison.Ordinal);
    }

    /// <summary>An unresolved placeholder is a bug in the prompt, and prompts are code: fail loudly at
    /// render rather than sending "{someTypo}" to the agent and having nobody notice.</summary>
    [Fact]
    public void UnresolvedPlaceholderIsAHardError()
    {
        var ex = Record.Exception(() =>
            PromptValidator.ThrowIfUnresolved("Deliver stage {stage} within {someTypo} attempts.", "session.md"));

        Assert.IsType<PromptCompositionException>(ex);   // SC3.3: its own type — the run loop parks on exactly this
        Assert.Contains("{stage}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("{someTypo}", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The verifier prompt legitimately contains a JSON object literal. The validator must not
    /// mistake `{"score":0-100,...}` for an unresolved placeholder.</summary>
    [Fact]
    public void JsonLiteralsInAPromptAreNotMistakenForPlaceholders()
    {
        var ex = Record.Exception(() =>
            PromptValidator.ThrowIfUnresolved("""Emit {"score":90,"findings":[],"verdict":"PASS"} and nothing else. {}""", "verify.md"));

        Assert.Null(ex);
    }

    /// <summary>templatesDir was accepted in plan JSON and then ignored: PromptBuilder looked only in the
    /// plan directory, so plans/baton-templates/*.md were dead files and every prompt silently came from a
    /// hardcoded C# string. Editing a template must change what the agent receives.</summary>
    [Fact]
    public void TemplateOnDiskUnderTemplatesDirBeatsTheBuiltIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "conductor-tmpl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "mytemplates"));
        File.WriteAllText(Path.Combine(dir, "conductor.plan.json"), "{}");
        File.WriteAllText(Path.Combine(dir, "mytemplates", "session.md"), "CUSTOM TEMPLATE for {stage} in {planName}.");

        try
        {
            var plan = Plan();
            plan.PlanFilePath = Path.Combine(dir, "conductor.plan.json");
            plan.TemplatesDir = "mytemplates";

            var prompt = new PromptBuilder(plan).Deliver(Stage, 1, 1, 3);

            Assert.StartsWith("CUSTOM TEMPLATE for L2 in Loom.", prompt, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
