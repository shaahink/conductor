using System.Text;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS3.1 truth gates — <c>conductor plan new</c>: from an empty repo, one command, a plan doctor has
/// nothing to fail on, and the JSON never opened in an editor.
///
/// <para>Three separate defects met in the old first mile, and each has a fact here. (1) A mistyped
/// sub-command fell through <c>PlanCommand</c>'s switch into the plan SUMMARY, which printed happily
/// and exited 0 — <c>conductor plan improt PRD.md</c> imported nothing and said so nowhere. (2) The
/// scaffold's templates spelled the escalation token, so <c>doctor</c>'s KS1.4 sweep answered
/// "1 fail" on a file the operator had just been handed and could not have caused. (3) The agent block
/// named one CLI whether or not this machine has it.</para>
///
/// <para>Nothing here spends: the structured kinds take the deterministic parser, and the one prose
/// fact wires a fake advisor — a script that prints the import contract's JSON — exactly as a real CLI
/// would be wired. The fake writes a spoor file when it runs, which is how the "structured input
/// spawns no model" fact is a measurement rather than an assumption.</para>
/// </summary>
public sealed class KS3_1PlanNewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ks31-{Guid.NewGuid():N}");

    public KS3_1PlanNewTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---- the sub-verb, and the one that is not ---------------------------------------------------

    /// <summary>The routing itself: the same loadable plan, one bare invocation and one mistyped
    /// sub-command. Before KS3.1 both printed the summary and exited 0, so the exit code is the whole
    /// proof — a refusal that cannot be told from a success is not a refusal.</summary>
    [Fact]
    public void PlanVerb_RefusesAnUnknownSubCommand_AndStillAnswersTheBareOne()
    {
        var dir = NewRepo("routing");
        Assert.True(PlanNewCommand.TryScaffold(dir, "routing", dir, RepoKind.Generic, AgentStub()));
        var planPath = Path.Combine(dir, "conductor.plan.json");

        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings { Plan = planPath, Verb = "" }));
        Assert.Equal(1, PlanCommand.Dispatch(new PlanCommand.Settings { Plan = planPath, Verb = "improt" }));
    }

    /// <summary>...and the refusal names the way out. Asserted on the message as a string: capturing a
    /// process-global console from a test is how bug #26's order-dependent flake happened.</summary>
    [Fact]
    public void TheRefusalNamesEverySubCommandThereIs()
    {
        var message = PlanCommand.UnknownVerbMessage("improt");

        Assert.Contains("improt", message, StringComparison.Ordinal);
        foreach (var verb in new[] { "new", "set", "reload", "add-stage", "import" })
            Assert.Contains(verb, message, StringComparison.Ordinal);
    }

    /// <summary>`new` is routed, not swallowed: dispatched with an output directory it scaffolds there,
    /// and it never resolves a plan path on the way — there is no plan yet to resolve.</summary>
    [Fact]
    public void PlanNew_IsRoutedByTheVerbSwitch()
    {
        var dir = NewRepo("routed");

        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings
        {
            Verb = "new", Output = dir, Agent = AgentStub(),
        }));
        Assert.True(File.Exists(Path.Combine(dir, "conductor.plan.json")));
    }

    // ---- one invocation, the whole file set ------------------------------------------------------

    [Fact]
    public void OneInvocation_WritesThePlanTheTrackerAndEveryBuiltInTemplate()
    {
        var dir = NewRepo("fileset");

        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings { Verb = "new", Output = dir, Agent = AgentStub() }));

        Assert.True(File.Exists(Path.Combine(dir, "conductor.plan.json")));
        Assert.True(File.Exists(Path.Combine(dir, "TRACKER.md")));
        foreach (var name in PromptBuilder.BuiltInNames)
            Assert.True(File.Exists(Path.Combine(dir, "templates", name)), $"plan new did not scaffold templates/{name}");

        var written = Directory.GetFiles(Path.Combine(dir, "templates")).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(PromptBuilder.BuiltInNames.Order(StringComparer.Ordinal), written);
        Assert.NotEmpty(PlanConfig.Load(Path.Combine(dir, "conductor.plan.json")).Stages);
    }

    /// <summary>"The JSON is never opened in an editor" is a property of the command, not of the drill
    /// that happened to go well: nothing in <c>PlanNewCommand</c> may launch a process or consult
    /// EDITOR/VISUAL. Read off the source, because that is the thing a later session will edit.</summary>
    [Fact]
    public void PlanNew_OpensNoEditor()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Conductor", "Commands", "PlanNewCommand.cs"));

        foreach (var forbidden in new[] { "Process.Start", "ProcessRunner", "\"EDITOR\"", "\"VISUAL\"", "notepad", "code -" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    // ---- doctor-clean by construction ------------------------------------------------------------

    /// <summary>The bar the checkpoint is held to. Every doctor check that reads the produced artefacts
    /// is run over them and none may say "fail".
    /// <para>The agent is a stated stub file rather than whatever CLI this machine has, and the argv
    /// ceiling is stated rather than resolved, for the reason KS1.4 gives: a gate whose verdict flips
    /// because a developer installed the agent CLI a different way reports the weather. The machine's
    /// own answer — the full check list including the network legs — is the transcript in
    /// <c>.conductor/evidence/KS3/ks3-1.md</c>.</para></summary>
    [Fact]
    public async Task TheScaffoldIsDoctorCleanByConstruction()
    {
        var dir = NewRepo("clean");
        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings { Verb = "new", Output = dir, Agent = AgentStub() }));

        var failed = (await PlanFacingChecks(Path.Combine(dir, "conductor.plan.json")))
            .Where(c => c.State == "fail")
            .Select(c => $"{c.Name}: {c.Message}")
            .ToList();

        Assert.True(failed.Count == 0, string.Join("\n", failed));
    }

    /// <summary>The check that was actually red, named on its own so a regression says which one. The
    /// escalation token is matched as a case-insensitive substring of the handoff
    /// (<c>ProgressConventions.MentionsHuman</c>), so a template that spells it hands every session a
    /// prompt whose echo parks the run.</summary>
    [Fact]
    public async Task NoScaffoldedTemplateSpellsTheEscalationToken()
    {
        var dir = NewRepo("escalation");
        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings { Verb = "new", Output = dir, Agent = AgentStub() }));
        var plan = PlanConfig.Load(Path.Combine(dir, "conductor.plan.json"));

        Assert.Equal("ok", (await DoctorCommand.CheckEscalationTokenAsync(plan)).State);

        // ...and the built-ins themselves, which is where the copies come from.
        var token = new ProgressConventions().HumanToken;
        foreach (var name in PromptBuilder.BuiltInNames)
            Assert.DoesNotContain(token, PromptBuilder.BuiltIn(name), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The agent leg of "clean by construction": the scaffold names a CLI this machine has,
    /// in preference order, and falls back to the documented default rather than to nothing — doctor
    /// then reports the true thing ("not found on PATH") instead of an accident of the template.</summary>
    [Fact]
    public void TheAgentBlockNamesACliThisMachineActuallyHas()
    {
        Assert.Equal("claude", PlanNewCommand.ResolveAgentCommand(c => c is "claude" or "opencode"));
        Assert.Equal("opencode", PlanNewCommand.ResolveAgentCommand(c => c == "opencode"));
        Assert.Equal(InitCommand.DefaultAgentCommand, PlanNewCommand.ResolveAgentCommand(_ => false));

        // A named CLI is a promise that the flags it needs are written with it.
        Assert.Contains("\"provider\": \"claude\"", InitCommand.BuildPlanJson("p", ".", RepoKind.Generic, "claude"), StringComparison.Ordinal);
        Assert.Contains("--output-format", InitCommand.BuildPlanJson("p", ".", RepoKind.Generic, "claude"), StringComparison.Ordinal);
    }

    /// <summary>...and what the operator NAMED is what lands in the file. The name selects the argument
    /// shape; it is not a substitute for the command. An early cut of this wrote the table's key back
    /// out, so <c>--agent C:\tools\claude.exe</c> silently became <c>claude</c> — and a scaffold that
    /// drops the path it was given is exactly the "doctor is green about the wrong thing" failure this
    /// checkpoint exists to close.</summary>
    [Fact]
    public void TheAgentTheOperatorNamedIsTheAgentThatIsWritten()
    {
        var dir = NewRepo("named-agent");
        var stub = AgentStub();

        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings { Verb = "new", Output = dir, Agent = stub }));

        var plan = PlanConfig.Load(Path.Combine(dir, "conductor.plan.json"));
        Assert.Equal(stub, plan.Agent.Command);
        Assert.Equal("ok", DoctorCommand.CheckAgentCli(plan).State);
    }

    // ---- three input kinds -----------------------------------------------------------------------

    /// <summary>An existing tracker markdown. Deterministic — and the fact that matters is the second
    /// assertion: the fake advisor left no spoor, so no model was spawned and nothing was spent.</summary>
    [Fact]
    public void StructuredInput_ParsesDeterministically_AndSpawnsNoModel()
    {
        var dir = NewRepo("tracker-kind");
        var (advisor, spoor) = FakeAdvisor(dir);
        var doc = Path.Combine(dir, "OLD-TRACKER.md");
        File.WriteAllText(doc, TrackerDocument, Utf8);

        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings
        {
            Verb = "new", Output = dir, Agent = AgentStub(), Advisor = advisor, FromIdea = doc,
        }));

        var plan = PlanConfig.Load(Path.Combine(dir, "conductor.plan.json"));
        Assert.Equal(["B1", "B2"], plan.Stages.Select(s => s.Id));
        Assert.Equal(["B1.1", "B1.2", "B2.1"], plan.Progress!.Checkpoints!.Select(c => c.Id));
        Assert.False(File.Exists(spoor), "a structured document consulted the advisor — that path must cost nothing");
    }

    /// <summary>A PRD file path: requirements sections and their numbered items. Same deterministic
    /// route, and the same zero spend — a document that is shaped enough to schedule never needs a
    /// model to say so.</summary>
    [Fact]
    public void PrdFilePath_BecomesStagesAndDeclaredWork()
    {
        var dir = NewRepo("prd-kind");
        var (advisor, spoor) = FakeAdvisor(dir);
        var doc = Path.Combine(dir, "PRD.md");
        File.WriteAllText(doc, PrdDocument, Utf8);

        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings
        {
            Verb = "new", Output = dir, Agent = AgentStub(), Advisor = advisor, FromIdea = doc,
        }));

        var plan = PlanConfig.Load(Path.Combine(dir, "conductor.plan.json"));
        Assert.Equal(["R1", "R2"], plan.Stages.Select(s => s.Id));
        Assert.Equal(["R1.1", "R1.2", "R2.1"], plan.Progress!.Checkpoints!.Select(c => c.Id));
        Assert.False(File.Exists(spoor));
    }

    /// <summary>Free idea text — the one kind that genuinely needs a model, routed through the advisor
    /// the operator named on the command line. The advisor here is a script printing the import
    /// contract's JSON, so the path is proven with no credential and no spend.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public void FreeIdeaText_RoutesThroughTheAdvisorTheOperatorNamed()
    {
        var dir = NewRepo("prose-kind");
        var (advisor, spoor) = FakeAdvisor(dir);

        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings
        {
            Verb = "new", Output = dir, Agent = AgentStub(), Advisor = advisor,
            FromIdea = "a service that ingests a feed and reports on it",
        }));

        var plan = PlanConfig.Load(Path.Combine(dir, "conductor.plan.json"));
        Assert.Equal(["A1", "A2"], plan.Stages.Select(s => s.Id));
        Assert.Equal(["A1.1", "A1.2", "A2.1"], plan.Progress!.Checkpoints!.Select(c => c.Id));
        Assert.True(File.Exists(spoor), "prose must reach the advisor — that is the only thing a model is for here");
    }

    /// <summary>Every kind drops the placeholder. Stated as its own fact because the placeholder is the
    /// difference between "a plan about your idea" and "a plan about your idea, plus a stage called
    /// rename me that the board will show forever".</summary>
    [Fact]
    public void RealStagesRetireThePlaceholder()
    {
        var dir = NewRepo("placeholder");
        var doc = Path.Combine(dir, "OLD-TRACKER.md");
        File.WriteAllText(doc, TrackerDocument, Utf8);

        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings
        {
            Verb = "new", Output = dir, Agent = AgentStub(), FromIdea = doc,
        }));

        var plan = PlanConfig.Load(Path.Combine(dir, "conductor.plan.json"));
        Assert.DoesNotContain(InitCommand.PlaceholderStageId, plan.Stages.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(plan.Progress!.Checkpoints!,
            c => c.Id.StartsWith(InitCommand.PlaceholderStageId + ".", StringComparison.OrdinalIgnoreCase));

        // The tracker is the file a human reads. It must not still say "rename me" either.
        var tracker = File.ReadAllText(Path.Combine(dir, "TRACKER.md"));
        Assert.DoesNotContain("rename me", tracker, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("| B1.1 |", tracker, StringComparison.Ordinal);
        Assert.Contains("## Handoff", tracker, StringComparison.Ordinal); // and the rest of the file survives
    }

    // ---- the refusal that must not leave rubble --------------------------------------------------

    /// <summary>Prose with no advisor: the command says exactly what is missing and the scaffold it
    /// already wrote stays — loadable, complete, and one <c>plan import</c> away from being the plan
    /// that was asked for. A half-written plan would be the worst of both.</summary>
    [Fact]
    public void ProseWithNoAdvisor_RefusesAndLeavesALoadableScaffold()
    {
        var dir = NewRepo("no-advisor");

        Assert.NotEqual(0, PlanCommand.Dispatch(new PlanCommand.Settings
        {
            Verb = "new", Output = dir, Agent = AgentStub(),
            FromIdea = "build me a thing that ingests a feed",
        }));

        var plan = PlanConfig.Load(Path.Combine(dir, "conductor.plan.json"));
        Assert.Equal([InitCommand.PlaceholderStageId], plan.Stages.Select(s => s.Id));
        Assert.Empty(plan.CollectErrors());
        Assert.Null(plan.Advisor);
        foreach (var name in PromptBuilder.BuiltInNames)
            Assert.True(File.Exists(Path.Combine(dir, "templates", name)));
    }

    [Fact]
    public void ScaffoldingOverAnExistingPlanIsRefused()
    {
        var dir = NewRepo("occupied");
        Assert.Equal(0, PlanCommand.Dispatch(new PlanCommand.Settings { Verb = "new", Output = dir, Agent = AgentStub() }));
        var before = File.ReadAllBytes(Path.Combine(dir, "conductor.plan.json"));

        Assert.Equal(1, PlanCommand.Dispatch(new PlanCommand.Settings { Verb = "new", Output = dir, Agent = AgentStub() }));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(dir, "conductor.plan.json")));
    }

    // ---- the entrypoints this one was built beside are unchanged ---------------------------------

    /// <summary>KS3.1 parameterised the scaffold's agent and advisor blocks. <c>init</c> passes neither,
    /// so what it writes must still be the text it has always written — the same CLI, the same argument
    /// shape, the advisor still a commented hint nobody is paying for.</summary>
    [Fact]
    public void InitStillWritesExactlyWhatItWroteBefore()
    {
        var json = InitCommand.BuildPlanJson("Demo", "C:/repo", RepoKind.Dotnet);

        Assert.Equal(json, InitCommand.BuildPlanJson("Demo", "C:/repo", RepoKind.Dotnet, InitCommand.DefaultAgentCommand));
        Assert.Contains("\"command\": \"opencode\",", json, StringComparison.Ordinal);
        Assert.Contains("\"args\": [\"run\", \"{prompt}\"],", json, StringComparison.Ordinal);
        Assert.Contains("\"provider\": \"opencode\"", json, StringComparison.Ordinal);
        Assert.Contains("// \"advisor\": {", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"advisor\": {\n", json, StringComparison.Ordinal);
    }

    /// <summary>...and the live advisor block only appears when someone asks for it by name, because
    /// the advisor is consulted mid-run too — switching it on is a spend decision.</summary>
    [Fact]
    public void TheAdvisorIsLiveOnlyWhenNamed()
    {
        var dir = Path.Combine(_root, "advisor-block");
        Directory.CreateDirectory(dir);
        var named = InitCommand.BuildPlanJson("Demo", dir.Replace("\\", "/"), RepoKind.Generic, "claude", "claude");
        var planPath = Path.Combine(dir, "conductor.plan.json");
        File.WriteAllText(planPath, named, Utf8);
        File.WriteAllText(Path.Combine(dir, "TRACKER.md"), InitCommand.BuildTrackerMd("Demo"), Utf8);

        var plan = PlanConfig.Load(planPath);
        Assert.NotNull(plan.Advisor);
        Assert.True(plan.Advisor!.Enabled);
        Assert.Equal("claude", plan.Advisor.Command);
        Assert.Contains("{prompt}", plan.Advisor.Args, StringComparer.Ordinal);
        Assert.Empty(plan.CollectErrors());
    }

    // ---- fixtures --------------------------------------------------------------------------------

    private static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>A structured tracker: two stage sections and their rows, the shape every plan document
    /// in this repo has.</summary>
    private const string TrackerDocument = """
        # Something — TRACKER

        ## Checkpoints

        ### B1 — Ingest — read the feed

        | # | Checkpoint | Status | Commit | Evidence |
        |---|-----------|--------|--------|----------|
        | B1.1 | Parse the feed | TODO |  |  |
        | B1.2 | Store the rows | TODO |  |  |

        ### B2 — Report

        | # | Checkpoint | Status | Commit | Evidence |
        |---|-----------|--------|--------|----------|
        | B2.1 | Render the summary | TODO |  |  |
        """;

    /// <summary>A PRD: requirement sections with numbered items under them.</summary>
    private const string PrdDocument = """
        # Feed service — product requirements

        The team wants yesterday's feed on today's dashboard.

        ## R1 — Ingest — pull the feed and keep it

        - **R1.1** Poll the upstream feed on a schedule and record what came back.
        - **R1.2** Persist each row once, keyed on the upstream id.

        ## R2 — Report — answer the question the dashboard asks

        - **R2.1** Render a daily summary from the stored rows.
        """;

    /// <summary>A git repo with one commit — doctor's git check is a fail without one, and the point of
    /// this class is a scaffold nothing fails on.</summary>
    private string NewRepo(string leaf)
    {
        var dir = Path.Combine(_root, leaf);
        Directory.CreateDirectory(dir);
        void Git(string args) => ProcessRunner.Run("git", args.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            dir, TimeSpan.FromSeconds(30), CancellationToken.None);
        Git("init -b main");
        Git("config user.email ks31@test");
        Git("config user.name KS31");
        File.WriteAllText(Path.Combine(dir, "README.md"), "# scratch\n", Utf8);
        Git("add README.md");
        Git("commit -m init --no-gpg-sign");
        return dir;
    }

    /// <summary>An "agent CLI" that exists and nothing more. Extensionless on purpose: the scaffold's
    /// argv is measured against CreateProcess' ceiling rather than a command-interpreter shim's, so this
    /// class's verdict does not depend on how the machine running it installed its agent.</summary>
    private string AgentStub()
    {
        var path = Path.Combine(_root, "agent-stub");
        if (!File.Exists(path)) File.WriteAllText(path, "", Utf8);
        return path;
    }

    /// <summary>A "model" that costs nothing: a script that prints the import contract's JSON and
    /// touches a spoor file, so "no model was spawned" is something a test can measure.</summary>
    private static (string Command, string Spoor) FakeAdvisor(string dir)
    {
        var answer = Path.Combine(dir, "advisor-answer.json");
        var spoor = Path.Combine(dir, "advisor-ran.txt");
        File.WriteAllText(answer, """
            {"stages":[
              {"id":"A1","title":"Ingest","sessions":2,"kind":"deliver","checkpoints":[
                {"id":"A1.1","title":"Parse the feed"},{"id":"A1.2","title":"Store the rows"}]},
              {"id":"A2","title":"Report","sessions":2,"kind":"deliver","dependsOn":["A1"],"checkpoints":[
                {"id":"A2.1","title":"Render the summary"}]}],"gates":[]}
            """, Utf8);

        var script = Path.Combine(dir, "fake-advisor.cmd");
        File.WriteAllText(script, string.Join("\r\n",
            "@echo off",
            $"echo ran > \"{spoor}\"",
            $"type \"{answer}\"",
            "exit /b 0",
            ""), Utf8);
        return (script, spoor);
    }

    /// <summary>Every doctor check that reads the produced artefacts. Assembled rather than taken from
    /// <c>RunChecksAsync</c> because that list also carries the network and disk legs, which say
    /// something about the machine and nothing about the scaffold.</summary>
    private static async Task<List<DoctorCommand.Check>> PlanFacingChecks(string planPath)
    {
        var plan = PlanConfig.Load(planPath);
        return
        [
            DoctorCommand.CheckAgentCli(plan),
            DoctorCommand.CheckModelToken(plan),
            DoctorCommand.CheckGit(plan),
            DoctorCommand.CheckSatelliteRepos(plan),
            DoctorCommand.CheckGates(plan),
            DoctorCommand.CheckWorkCoverage(plan),
            DoctorCommand.CheckPrompt(plan),
            DoctorCommand.CheckAdvisor(plan),
            DoctorCommand.CheckGatePaths(plan),
            DoctorCommand.CheckHooks(plan),
            DoctorCommand.CheckCheckpointIds(plan),
            DoctorCommand.CheckPlanDrift(plan),
            DoctorCommand.CheckArgvLength(plan, (DoctorCommand.CreateProcessCommandLineCeiling, "CreateProcess")),
            DoctorCommand.CheckBudget(plan, 0m, hasRun: false),
            DoctorCommand.CheckTokenBudget(plan),
            await DoctorCommand.CheckTemplateBracesAsync(plan),
            await DoctorCommand.CheckEscalationTokenAsync(plan),
        ];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate repo root (Conductor.slnx)");
    }
}
