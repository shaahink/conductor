using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS7.5 — the two context-economics batteries and the delegation guidance, measured where they
/// actually land: in the rendered battery section a session receives, not in the class that builds it.
/// </summary>
/// <remarks>
/// The checkpoint's own accounting is why the duplication test below exists. A prompt-side mechanism
/// that repeats bytes already in the prompt is worse than no mechanism: it costs on every turn and
/// teaches nothing new. So the recap battery is pinned to NOT carry the card context that
/// <see cref="Conductor.Planning.PromptBlockRenderer"/> already renders verbatim.
/// </remarks>
public sealed class KS7_5ContextEconomicsTests : IDisposable
{
    private readonly string _tmpDir;

    public KS7_5ContextEconomicsTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"conductor-ks75-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmpDir)) TestTemp.DeleteTree(_tmpDir); }
        catch (IOException) { /* best-effort cleanup */ }
    }

    private string MakeRepo()
    {
        var root = Path.Combine(_tmpDir, "repo");
        foreach (var (dir, files) in new[]
                 {
                     ("src", 3), ("tests", 2), ("bin", 9), ("obj", 9), ("node_modules", 9), (".git", 9),
                 })
        {
            var d = Path.Combine(root, dir);
            Directory.CreateDirectory(d);
            for (var i = 0; i < files; i++) File.WriteAllText(Path.Combine(d, $"f{i}.cs"), "// x");
        }
        // An empty directory contributes nothing and must not earn a line.
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        return root;
    }

    [Fact]
    public void RepoMapListsSourceDirectoriesAndSkipsBuildOutput()
    {
        var battery = new RepoMapBattery(MakeRepo());

        Assert.False(battery.IsEmpty);
        Assert.Contains("`src/` — 3 source files", battery.Section, StringComparison.Ordinal);
        Assert.Contains("`tests/` — 2 source files", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain("`bin/`", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain("`obj/`", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain("node_modules", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain(".git", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain("`empty/`", battery.Section, StringComparison.Ordinal);
        // The map exists to redirect a sweep, so it says what to do with itself.
        Assert.Contains("delegate a wide sweep to a subagent", battery.Section, StringComparison.Ordinal);
    }

    [Fact]
    public void RepoMapIsDeterministicAndBounded()
    {
        var root = MakeRepo();
        Assert.Equal(new RepoMapBattery(root).Section, new RepoMapBattery(root).Section);

        // maxEntries caps the listing: one row plus the closing sentence.
        var capped = new RepoMapBattery(root, maxEntries: 1).Section;
        Assert.Single(capped.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            l => l.StartsWith("- `", StringComparison.Ordinal));
    }

    [Fact]
    public void RepoMapIsEmptyWhenRootIsMissing()
    {
        Assert.True(new RepoMapBattery(Path.Combine(_tmpDir, "nope")).IsEmpty);
        Assert.True(new RepoMapBattery("").IsEmpty);
    }

    private static TaskItem Card(string id, string stage, string status, string title = "t", string context = "") =>
        new()
        {
            TaskId = id, CheckpointId = id, StageId = stage, Status = status, Title = title,
            Context = context, Kind = WorkItemKinds.Checkpoint,
        };

    [Fact]
    public void DefinitionOfDonePrefersTheCardInFlightAndPreFillsTheClaim()
    {
        var battery = new DefinitionOfDoneBattery(
        [
            Card("KS7.4", "KS7", "done"),
            Card("KS7.5", "KS7", "in_progress", "Context economics"),
            Card("KS7.6", "KS7", "todo", "Something else"),
            Card("KS8.1", "KS8", "in_progress", "Another stage"),
        ], "KS7");

        Assert.False(battery.IsEmpty);
        Assert.Contains("conductor task --done KS7.5 --evidence <path>", battery.Section, StringComparison.Ordinal);
        Assert.Contains("Context economics", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain("KS7.6", battery.Section, StringComparison.Ordinal);
        Assert.DoesNotContain("KS8.1", battery.Section, StringComparison.Ordinal);
    }

    [Fact]
    public void DefinitionOfDoneFallsBackToTheNextTodoAndStaysEmptyWithoutOne()
    {
        Assert.Contains("--done KS7.6 ", new DefinitionOfDoneBattery(
            [Card("KS7.5", "KS7", "done"), Card("KS7.6", "KS7", "todo")], "KS7").Section, StringComparison.Ordinal);

        Assert.True(new DefinitionOfDoneBattery([Card("KS7.5", "KS7", "done")], "KS7").IsEmpty);
        Assert.True(new DefinitionOfDoneBattery([Card("KS7.5", "KS7", "todo")], "").IsEmpty);
        Assert.True(new DefinitionOfDoneBattery([], "KS7").IsEmpty);
    }

    /// <summary>The KS7.5 invariant: the recap must not re-pay for bytes the prompt already carries.
    /// The card's acceptance context is rendered verbatim by the work-items section, so it appears in
    /// a composed prompt EXACTLY once — and the once is not this battery.</summary>
    [Fact]
    public void DefinitionOfDoneDoesNotDuplicateTheAcceptanceTheWorkItemsSectionAlreadyCarries()
    {
        const string acceptance = "AMENDED: the exit as written needs N future sessions, not one.";
        var card = Card("KS7.5", "KS7", "in_progress", "Context economics", acceptance);
        var plan = new PlanConfig { Name = "p", Repo = _tmpDir };

        var recap = new DefinitionOfDoneBattery([card], "KS7").Section;
        var workItems = Conductor.Planning.PromptBlockRenderer.RenderSection(
            [TaskPromptComposition.Compose(plan, card, injectedKnowledge: "")]);

        Assert.Contains(acceptance, workItems, StringComparison.Ordinal);
        Assert.DoesNotContain(acceptance, recap, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(recap + "\n" + workItems, acceptance));
        // And the recap stays small enough that its presence is not itself the problem.
        Assert.True(recap.Length <= 400, $"recap is {recap.Length} bytes");
    }

    [Fact]
    public void LongTitleIsClippedAndTheSectionStaysUnderItsCap()
    {
        var section = new DefinitionOfDoneBattery(
            [Card("KS7.5", "KS7", "in_progress", new string('t', 4000))], "KS7").Section;
        Assert.True(section.Length <= 400, $"section is {section.Length} bytes");
    }

    // ── registration: the batteries only matter if they reach a composed prompt ──

    private PlanConfig PlanWith(BatteriesConfig batteries) => new()
    {
        Name = "p", Repo = MakeRepo(), Batteries = batteries,
    };

    [Fact]
    public void BatterySectionRendersBothWhenTheFlagsAreOnAndTheDataIsThere()
    {
        var plan = PlanWith(new BatteriesConfig { RepoMap = true, DefinitionOfDone = true, Lessons = false, RecentFailure = false });
        var section = new PromptBuilder(plan).BatterySection(new RunState(), store: null,
            checkpoints: [Card("KS7.5", "KS7", "in_progress", "Context economics")], stageId: "KS7");

        Assert.Contains("### repo-map", section, StringComparison.Ordinal);
        Assert.Contains("### definition of done", section, StringComparison.Ordinal);
        Assert.Contains("--done KS7.5 ", section, StringComparison.Ordinal);
    }

    [Fact]
    public void BothAreOptIn_BecauseAPromptIsAlsoAnArgument()
    {
        // Neither ships on by default. The map makes the prompt bigger on a bet; the recap is cheap
        // but not free, and the argv ratchet below says what "not free" costs when the ceiling is
        // 8191 chars. A plan opts in when it has the room.
        var section = new PromptBuilder(PlanWith(new BatteriesConfig { Lessons = false, RecentFailure = false }))
            .BatterySection(new RunState(), store: null, checkpoints: [Card("KS7.5", "KS7", "in_progress")], stageId: "KS7");
        Assert.Equal("", section);
    }

    [Fact]
    public void BatterySectionOmitsTheRecapWhenTheCallerHasNoBoard()
    {
        // The control-plane preview composes without a folded graph: it renders the same prompt minus
        // a section it has no data for, never an empty heading.
        var section = new PromptBuilder(PlanWith(new BatteriesConfig { Lessons = false, RecentFailure = false }))
            .BatterySection(new RunState(), store: null);
        Assert.Equal("", section);
    }

    [Fact]
    public void ToolContractTeachesSearchDelegation()
    {
        var block = ToolContract.Render(new PlanConfig { Name = "p", Repo = _tmpDir });
        Assert.Contains("delegate the wide reads", block, StringComparison.Ordinal);
        Assert.Contains("SUBAGENT", block, StringComparison.Ordinal);
        // It reaches every working-session template through {tools}; the ones that skip it are the
        // advisor (a JSON decider) and chat.
        foreach (var name in new[] { "session.md", "fix.md", "resume.md", "verify.md", "audit.md" })
            Assert.Contains("{tools}", PromptBuilder.BuiltIn(name), StringComparison.Ordinal);
    }

    /// <summary>KS7.5's hardest measured fact, and the reason both batteries are opt-in: the prompt is
    /// not only a token cost, it is an ARGUMENT. A minimal plan on the shipped templates composes an
    /// argv within a few hundred chars of the 8191-char ceiling a cmd/bat-shimmed agent has, and 28 of
    /// this suite's own rigs spawn their fake agent through <c>cmd.exe /c</c>. Crossing it does not
    /// fail loudly — cmd refuses the line, the agent never starts, and the run reports a dead session.
    /// So the shipped prompt gets a ratchet: grow the tools block or a built-in template past this and
    /// a test says so, instead of two dozen live-run rigs failing with "the fake agent never started".</summary>
    [Fact]
    public void ShippedPromptStaysUnderTheCmdExeArgvCeiling()
    {
        var repo = Path.Combine(_tmpDir, "argv-repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "TRACKER.md"), "# t\n\n## Handoff\n\nnothing pending.\n");
        var plan = new PlanConfig
        {
            Name = "argv-ratchet", Repo = repo, Tracker = "TRACKER.md",
            PlanFilePath = Path.Combine(repo, "fixture.plan.json"),
            Agent = new AgentConfig { Command = "cmd.exe", Args = ["/c", "agent.cmd", "{prompt}"] },
        };
        plan.Stages.Add(new StageConfig { Id = "S1", Title = "the only stage", Sessions = 1 });

        // The ceiling is STATED, not resolved: this verdict may not depend on how the agent CLI
        // happens to be installed on the machine running the suite.
        var check = DoctorCommand.CheckArgvLength(plan, (DoctorCommand.CmdExeCommandLineCeiling, "cmd.exe shim"));
        var measured = MeasuredChars(check.Message);

        // The lint measures the PROMPT path only: the battery section, the runner's tail sections and
        // the orchestrator's own flags (claude's --mcp-config) are added later and are not in this
        // number. Measured live: W3AuthTests' rig died at a lint number of 7837 and survived at 7689,
        // so the real spawn carries 350–500 chars the lint never sees. The allowance is that gap,
        // stated rather than assumed, and it is why this ratchet sits below the raw ceiling.
        const int liveSpawnAllowance = 400;
        Assert.NotEqual("fail", check.State);
        Assert.True(measured + liveSpawnAllowance < DoctorCommand.CmdExeCommandLineCeiling,
            $"the shipped prompt composes {measured} chars; with the {liveSpawnAllowance}-char live-spawn allowance that " +
            $"crosses the {DoctorCommand.CmdExeCommandLineCeiling}-char cmd.exe ceiling, and every cmd.exe-hosted rig in " +
            "this suite then dies at spawn with 'the fake agent never started'. Shorten the tools block or a built-in " +
            "template — do not raise this number.");
    }

    /// <summary>"longest composed argv is N chars (...)" — the number, without a regex the analyzer
    /// would want a timeout on.</summary>
    private static int MeasuredChars(string message)
    {
        const string marker = "argv is ";
        var start = message.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the lint did not report a length: {message}");
        start += marker.Length;
        var end = start;
        while (end < message.Length && char.IsAsciiDigit(message[end])) end++;
        return int.Parse(message[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
