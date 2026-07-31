using System.Text.Json;
using System.Text.Json.Nodes;
using Conductor.Commands;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SC3.3 — brace safety end to end. A literal <c>{word}</c> in one stage's <c>notes</c> passed
/// <c>doctor</c>, then killed a 13-hour run at a stage boundary with the refusal on stderr only:
/// nothing in <c>conductor.log</c>, and <c>status</c> went on calling the dead run idle.
/// Three rules are measured here, at the three places they have to hold:
/// authored prose is refused at plan load; a value carrying a brace is prose and can never be fatal;
/// a template that really is broken parks the run instead of taking the process down.
/// </summary>
public sealed class SC3_3BraceSafetyTests
{
    private static PlanConfig Plan() => new()
    {
        Name = "Loom",
        Repo = @"C:\repo",
        Tracker = "LOOM-START.md",
        PlanDoc = "docs/proposal.md",
        PromptExtra = "EXTRA-MARKER",
    };

    // ------------------------------------------------------------------ the escape

    /// <summary>The whole point of the escape: prose that needs a brace can have one. Before this,
    /// the ONLY way to write `{model}` in a stage note was to not write it.</summary>
    [Fact]
    public void DoubledBracesInStageNotesRenderAsOneLiteralBrace()
    {
        var stage = new StageConfig { Id = "L2", Title = "BodyFacts", Notes = "Add \"--model\", {{model}} to args. See GET /tasks/{{id}}/prompt." };

        var prompt = new PromptBuilder(Plan()).Deliver(stage, 1, 1, 3);

        Assert.Contains("\"--model\", {model} to args", prompt, StringComparison.Ordinal);
        Assert.Contains("GET /tasks/{id}/prompt", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", prompt, StringComparison.Ordinal);
    }

    /// <summary>A template's own escape must survive substitution: `{{extra}}` is the literal word,
    /// not the value of <c>extra</c>. That only works because escapes are held BEFORE the
    /// substitution pass, not after it.</summary>
    [Fact]
    public void AnEscapeInATemplateIsNotSubstituted()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "conductor.plan.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "session.md"), "Stage {stage}. The variable is spelled {{extra}} and its value is {extra}.");
            var plan = Plan();
            plan.PlanFilePath = Path.Combine(dir, "conductor.plan.json");

            var prompt = new PromptBuilder(plan).Deliver(new StageConfig { Id = "L2", Title = "t" }, 1, 1, 1);

            Assert.Equal("Stage L2. The variable is spelled {extra} and its value is EXTRA-MARKER.", prompt);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ------------------------------------------------------------------ values are data

    /// <summary>The class of failure that actually killed the run: text the ENGINE substitutes in.
    /// Gate output, a tracker handoff, an agent's own transcript tail and a stage title are data —
    /// a brace in any of them is prose, and no prose may be able to stop the engine.</summary>
    [Fact]
    public void BracesInSubstitutedValuesAreProseAndNeverThrow()
    {
        var plan = Plan();
        var stage = new StageConfig { Id = "L2", Title = "render {model} safely" };
        var builder = new PromptBuilder(plan);

        var fix = builder.Fix(stage, 2, 1, 3, new PendingFix
        {
            FromSession = 1,
            GateFailures = "Assert.Equal() Failure: expected {template} got {rendered}",
            ProgressSummary = "handoff said: keep {braces} out of prose",
        });
        var advisor = builder.Advisor(stage, "GatesRed", "build: red", "0",
            handoff: "last: fixed the {model} token", tail: """{"type":"text","part":{"text":"hi"}}""", 1, 3);

        Assert.Contains("expected {template} got {rendered}", fix, StringComparison.Ordinal);
        Assert.Contains("keep {braces} out of prose", fix, StringComparison.Ordinal);
        Assert.Contains("render {model} safely", fix, StringComparison.Ordinal);
        Assert.Contains("last: fixed the {model} token", advisor, StringComparison.Ordinal);
    }

    /// <summary>Substitution is order-independent now. A value is never re-scanned, so a variable
    /// name inside another variable's value stays text — it used to expand or not depending on where
    /// the two names happened to sit in a Dictionary.</summary>
    [Fact]
    public void AValueIsNeverReScannedForOtherVariables()
    {
        var stage = new StageConfig { Id = "L2", Title = "t", Notes = "{{sessionNumber}} is not this session's number" };

        var prompt = new PromptBuilder(Plan()).Deliver(stage, 42, 1, 3);

        Assert.Contains("{sessionNumber} is not this session's number", prompt, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the template is still code

    [Fact]
    public void ATemplateTypoIsStillARefusal_AndNamesTheEscape()
    {
        var ex = Assert.Throws<PromptCompositionException>(() =>
            PromptValidator.ThrowIfUnresolved("Deliver stage {stage} within {someTypo} attempts.", "session.md"));

        Assert.Contains("{stage}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("{someTypo}", ex.Message, StringComparison.Ordinal);
        Assert.Contains("{{stage}}", ex.Message, StringComparison.Ordinal);   // the way out, in the refusal
    }

    [Fact]
    public void JsonLiteralsAreStillNotPlaceholders()
    {
        var ex = Record.Exception(() =>
            PromptValidator.ThrowIfUnresolved("""Emit {"score":90,"findings":[],"verdict":"PASS"} and nothing else. {}""", "verify.md"));

        Assert.Null(ex);
    }

    // ------------------------------------------------------------------ plan load / doctor

    [Fact]
    public void AuthoredProseWithAPlaceholderIsRefusedAtPlanLoad()
    {
        var plan = Plan();
        plan.Stages.Add(new StageConfig { Id = "L2", Title = "t", Notes = "pin the model with {model} in args" });

        var errors = plan.CollectErrors();

        var hit = Assert.Single(errors, e => e.Contains("{model}", StringComparison.Ordinal));
        Assert.Contains("stage 'L2' notes", hit, StringComparison.Ordinal);
        Assert.Contains("{{model}}", hit, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptExtraIsJudgedTheSameWayAsStageNotes()
    {
        var plan = Plan();
        plan.PromptExtra = "always claim with conductor task --done {id}";

        Assert.Contains(plan.CollectErrors(), e => e.Contains("plan.promptExtra", StringComparison.Ordinal) && e.Contains("{id}", StringComparison.Ordinal));
    }

    [Fact]
    public void EscapedProseLoadsClean()
    {
        var plan = Plan();
        plan.Stages.Add(new StageConfig { Id = "L2", Title = "t", Notes = "pin the model with {{model}} in args" });
        plan.PromptExtra = "serve at GET /tasks/{{id}}/prompt";

        Assert.DoesNotContain(plan.CollectErrors(), e => e.Contains("prose is substituted", StringComparison.Ordinal));
    }

    /// <summary>The rule is only worth having if the repo's own plans obey it. This is the sweep that
    /// found <c>plans/conductor-planner.plan.json</c>'s <c>GET /tasks/{id}/prompt</c> — real prose, in
    /// a shipped plan, that would have reached its agent as a broken instruction.</summary>
    [Fact]
    public void NoShippedPlanCarriesAnUnresolvablePlaceholderInItsProse()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var plans = Directory.GetFiles(Path.Combine(dir!.FullName, "plans"), "*.plan.json");
        Assert.NotEmpty(plans);

        var found = new List<string>();
        foreach (var file in plans)
        {
            var root = JsonNode.Parse(File.ReadAllText(file),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var name = Path.GetFileName(file);
            foreach (var stage in root?["stages"]?.AsArray() ?? [])
            {
                var tokens = PromptPlaceholders.UnresolvableIn(stage?["notes"]?.GetValue<string>());
                if (tokens.Count > 0) found.Add($"{name}: stage {stage?["id"]} — {string.Join(", ", tokens)}");
            }
            var extra = PromptPlaceholders.UnresolvableIn(root?["promptExtra"]?.GetValue<string>());
            if (extra.Count > 0) found.Add($"{name}: promptExtra — {string.Join(", ", extra)}");
        }

        Assert.True(found.Count == 0, "Shipped plans carry prose the renderer cannot resolve:\n  " + string.Join("\n  ", found));
    }

    /// <summary>A template FILE is not part of the plan document, so plan load cannot see it. Doctor
    /// composes every session kind for every stage instead — the typo that used to surface as a
    /// mid-run death now surfaces before launch, naming the stage, the template and the token.</summary>
    [Fact]
    public void DoctorComposesTheRealPromptAndFailsOnABrokenTemplate()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "tpl"));
            File.WriteAllText(Path.Combine(dir, "conductor.plan.json"), "{}");
            File.WriteAllText(Path.Combine(dir, "tpl", "session.md"), "Stage {stage} of {planName}. Budget: {stageBudget}\n");
            var plan = Plan();
            plan.PlanFilePath = Path.Combine(dir, "conductor.plan.json");
            plan.TemplatesDir = "tpl";
            plan.Stages.Add(new StageConfig { Id = "L2", Title = "t" });

            var broken = DoctorCommand.CheckPrompt(plan);
            Assert.Equal("fail", broken.State);
            Assert.Contains("{stageBudget}", broken.Message, StringComparison.Ordinal);
            Assert.Contains("session.md", broken.Message, StringComparison.Ordinal);
            Assert.Contains("L2", broken.Message, StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(dir, "tpl", "session.md"), "Stage {stage} of {planName}. Budget: {{stageBudget}}\n");
            Assert.Equal("ok", DoctorCommand.CheckPrompt(plan).State);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "conductor-sc33-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
