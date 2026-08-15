using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS3.5 — the three import bridges, each against a committed sample of the real thing.
/// <para>The claim under test is narrow and checkable: a board you already wrote — a spec-kit
/// <c>tasks.md</c>, a Task-Master <c>tasks.json</c>, a plain markdown checklist — becomes a
/// conductor plan with NO model call, and the plan it becomes is one this engine can actually
/// drive. The second half is the part that fails silently: spec-kit's own <c>T001</c> is not a
/// checkpoint id this engine can claim (no stage prefix), so a converter that passed ids through
/// verbatim would produce a plan that loads, schedules, and then never confirms a single row.
/// So the id shapes are asserted THROUGH the readers that impose them —
/// <c>FakeAgentCommand.StageFromPrompt</c> and <c>FakeAgentCommand.FirstOpenRow</c> — rather than
/// against a regex copied into this file, which could drift from theirs without either failing.</para>
/// </summary>
public sealed class KS3_5ImportBridgeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ks35-{Guid.NewGuid():N}");

    public KS3_5ImportBridgeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Fixture(string kind, string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", kind, name);

    private static string Read(string kind, string name) => File.ReadAllText(Fixture(kind, name));

    // ── the three converters, one fact each: stage count, checkpoint ids, dependency order ──────

    /// <summary>The spec-kit sample: three phases that own tasks, and two headings
    /// ("Dependencies", "Notes") that own none and therefore are not stages — a stage with no
    /// checkpoint is a stage nothing can ever claim.</summary>
    [Fact]
    public void SpecKitSampleConvertsToItsPhasesInDocumentOrder()
    {
        var (result, format) = ImportBridge.Read(Read("speckit", "tasks.md"));

        Assert.Equal(ImportFormat.SpecKit, format);
        Assert.NotNull(result);
        Assert.Equal(["P31", "P32", "P33"], result.Stages.Select(s => s.Id));
        Assert.Equal(["Setup", "Tests First (TDD)", "Core Implementation"], result.Stages.Select(s => s.Title));
        Assert.Equal(
            ["P31.T001", "P31.T002", "P32.T003", "P33.T004", "P33.T005"],
            result.Checkpoints.Select(c => c.Id));

        // The document states no ordering of its own, so the linear chain the document IMPLIES is
        // what it gets — not an invented one, and not none at all.
        Assert.Null(result.Stages[0].DependsOn);
        Assert.Equal(["P31"], result.Stages[1].DependsOn!);
        Assert.Equal(["P32"], result.Stages[2].DependsOn!);

        // "[P]" is spec-kit's parallel marker, not part of the task's name.
        Assert.Equal("Configure linting and formatting", result.Checkpoints[1].Title);
    }

    /// <summary>Task-Master: a top-level task becomes a stage, its subtasks its checkpoints, and a
    /// task with NO subtasks still gets one row — otherwise the third task would convert to a stage
    /// with nothing in it. This is the one source of the three that declares its own ordering, so
    /// it is the one that must not have a chain invented for it.</summary>
    [Fact]
    public void TaskMasterSampleUsesTheFilesOwnDependencies()
    {
        var (result, format) = ImportBridge.Read(Read("taskmaster", "tasks.json"));

        Assert.Equal(ImportFormat.TaskMaster, format);
        Assert.NotNull(result);
        Assert.Equal(["T1", "T2", "T3"], result.Stages.Select(s => s.Id));
        Assert.Equal(["T1.1", "T1.2", "T2.1", "T3.1"], result.Checkpoints.Select(c => c.Id));

        Assert.Null(result.Stages[0].DependsOn);           // "dependencies": []
        Assert.Equal(["T1"], result.Stages[1].DependsOn!);  // "dependencies": [1]
        Assert.Equal(["T2"], result.Stages[2].DependsOn!);  // "dependencies": [2]

        // A finished subtask imports as DONE: re-importing a half-run board must not re-open work.
        Assert.Equal("DONE", result.Checkpoints.Single(c => c.Id == "T1.2").Status);
        Assert.Null(result.Checkpoints.Single(c => c.Id == "T1.1").Status);
        // The subtask-less task carries its own title down into its single row.
        Assert.Equal("Document the API", result.Checkpoints.Single(c => c.Id == "T3.1").Title);
    }

    /// <summary>The checklist: headings become stages, checkbox items their checkpoints. The
    /// document's own H1 owns no items (they all sit under the two H2s), so it is not a stage.</summary>
    [Fact]
    public void ChecklistSampleConvertsHeadingsToStages()
    {
        var (result, format) = ImportBridge.Read(Read("checklist", "checklist.md"));

        Assert.Equal(ImportFormat.Checklist, format);
        Assert.NotNull(result);
        Assert.Equal(["C1", "C2"], result.Stages.Select(s => s.Id));
        Assert.Equal(["Before the release", "After the release"], result.Stages.Select(s => s.Title));
        Assert.Equal(["C1.1", "C1.2", "C1.3", "C2.1", "C2.2"], result.Checkpoints.Select(c => c.Id));

        Assert.Null(result.Stages[0].DependsOn);
        Assert.Equal(["C1"], result.Stages[1].DependsOn!);

        Assert.Equal("DONE", result.Checkpoints[0].Status);   // "- [x] Freeze the release branch"
        Assert.Null(result.Checkpoints[1].Status);            // "- [ ] Update the changelog"
    }

    // ── detection is by content, not by name ───────────────────────────────────────────────────

    /// <summary>The selector never sees a filename — <see cref="ImportBridge.Read"/> takes text.
    /// That matters because all three formats travel under names that lie: spec-kit's file is
    /// <c>tasks.md</c> and Task-Master's is <c>tasks.json</c>, and a checklist is called anything at
    /// all. The dangerous case is the overlap: spec-kit task lines ARE checkbox items, so the loose
    /// checklist reader would happily claim a spec-kit document and mint worse ids for it. Order,
    /// not filename, is what stops that.</summary>
    [Fact]
    public void ContentPicksTheReaderEvenWhenTheReadersOverlap()
    {
        var speckit = Read("speckit", "tasks.md");

        // Both readers recognise the text; only one may have it.
        Assert.True(ChecklistImporter.Looks(speckit), "the checklist reader does not overlap spec-kit - this test is moot");
        Assert.True(SpecKitImporter.Looks(speckit));
        Assert.Equal(ImportFormat.SpecKit, ImportBridge.Read(speckit).Format);

        // The .md/.json swap: the Task-Master document read under no name at all is still JSON.
        Assert.Equal(ImportFormat.TaskMaster, ImportBridge.Read(Read("taskmaster", "tasks.json")).Format);
        Assert.False(SpecKitImporter.Looks(Read("checklist", "checklist.md")));

        // And nothing deterministic is the ONLY case allowed to reach the paid advisor path.
        Assert.Equal(ImportFormat.None, ImportBridge.Read("Build me a greeting service, please.").Format);
        Assert.Equal(ImportFormat.None, ImportBridge.Read("   ").Format);
    }

    /// <summary>The bridges are reachable from the CLI's import path, not just from a unit test:
    /// <c>conductor plan import</c> calls <see cref="PlanImportService.ParseKnown"/>, and only a
    /// null result there falls through to the model.</summary>
    [Fact]
    public void PlanImportReachesTheBridgesWithNoModelCall()
    {
        var (result, format) = PlanImportService.ParseKnown(Read("speckit", "tasks.md"));

        Assert.NotNull(result);
        Assert.Equal(ImportFormat.SpecKit, format);
        Assert.Equal(5, result.Checkpoints.Count);
    }

    // ── the ids the engine's own readers require ───────────────────────────────────────────────

    /// <summary>The failure this checkpoint exists to prevent, asserted through the code that
    /// causes it. A spec-kit board imported with its own ids (<c>T001</c>) would produce a plan
    /// that loads and schedules and never confirms anything: the fake agent reads its stage out of
    /// the prompt with one regex and picks its row out of the tracker with another, and a bare
    /// <c>T001</c> row matches neither. Both readers are called here directly.</summary>
    [Fact]
    public void ConvertedSpecKitIdsSatisfyTheFakeAgentsOwnReaders()
    {
        var result = ImportBridge.Read(Read("speckit", "tasks.md")).Result!;
        var tracker = DemoCommand.TrackerFor(result.Checkpoints);

        foreach (var stage in result.Stages)
        {
            Assert.True(ImportBridge.IsDrivableStageId(stage.Id), $"stage id '{stage.Id}' is not drivable");

            // The prompt the engine renders for a delivery session, as FakeAgentCommand reads it.
            Assert.Equal(stage.Id, FakeAgentCommand.StageFromPrompt(
                $"DELIVER the next incomplete checkpoint(s) of stage {stage.Id} only."));

            // And the first row it would claim for that stage is that stage's first row.
            var expected = result.Checkpoints.First(c => c.Id.StartsWith(stage.Id + ".", StringComparison.Ordinal));
            Assert.Equal(expected.Id, FakeAgentCommand.FirstOpenRow(tracker, stage.Id));
        }

        Assert.All(result.Checkpoints, c => Assert.True(ImportBridge.IsDrivableCheckpointId(c.Id),
            $"checkpoint id '{c.Id}' is not drivable - the demo agent could never claim it"));

        // The raw spec-kit id is the thing that would NOT work — the reason the conversion exists.
        Assert.False(ImportBridge.IsDrivableCheckpointId("T001"));
        Assert.Null(FakeAgentCommand.FirstOpenRow("| T001 | Create the project skeleton | TODO |  |  |", "P31"));
    }

    /// <summary>Every bridge's output, not just spec-kit's: an id shape that only holds for the
    /// format someone happened to test is not a rule.</summary>
    [Theory]
    [InlineData("speckit", "tasks.md")]
    [InlineData("taskmaster", "tasks.json")]
    [InlineData("checklist", "checklist.md")]
    public void EveryBridgeMintsDrivableIds(string kind, string name)
    {
        var result = ImportBridge.Read(Read(kind, name)).Result!;

        Assert.All(result.Stages, s => Assert.True(ImportBridge.IsDrivableStageId(s.Id), $"stage '{s.Id}'"));
        Assert.All(result.Checkpoints, c =>
        {
            Assert.True(ImportBridge.IsDrivableCheckpointId(c.Id), $"checkpoint '{c.Id}'");
            Assert.Contains(result.Stages, s => c.Id.StartsWith(s.Id + ".", StringComparison.Ordinal));
        });
    }

    // ── the plan the demo writes from a converted document ─────────────────────────────────────

    /// <summary>The converted plan must LOAD — the same self-check <c>conductor init</c> applies —
    /// and it must keep the two properties the demo exists to demonstrate: gates that pin no shell
    /// (so the demo is not Windows-only) and state that stays inside the throwaway directory (so a
    /// stranger's machine catalogue does not grow a row pointing at a deleted temp dir).</summary>
    [Fact]
    public void ImportedDemoPlanLoadsWithPortableGatesAndPinnedState()
    {
        var imported = DemoCommand.LoadImport(Fixture("taskmaster", "tasks.json"));
        Assert.NotNull(imported);

        var planPath = Path.Combine(_dir, "conductor.plan.json");
        File.WriteAllText(planPath, DemoCommand.PlanJson(_dir, "/usr/local/bin/conductor", imported.StagesJson));
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), imported.Tracker);

        var plan = PlanConfig.Load(planPath);
        Assert.Equal(DemoCommand.DemoPlanName, plan.Name);
        Assert.Equal(3, plan.Stages.Count);
        Assert.Equal(["T1", "T2", "T3"], plan.Stages.Select(s => s.Id));
        // The source's own ordering survives into the file a stranger reads.
        Assert.Equal(["T1"], plan.Stages[1].DependsOn!);

        Assert.All(plan.Gates, g => Assert.True(string.IsNullOrEmpty(g.Shell),
            $"gate '{g.Name}' pins shell '{g.Shell}' - an imported demo must run on the host's own shell"));
        Assert.All(plan.Gates, g => Assert.StartsWith("git ", g.Command, StringComparison.Ordinal));

        var home = Path.Combine(_dir, "machine-state-home");
        DemoCommand.PinStateToTheThrowawayRepo(_dir);
        var resolved = StateHome.Resolve(_dir, DemoCommand.DemoPlanName, root: home);

        Assert.Equal(StateSource.Pointer, resolved.Source);
        Assert.StartsWith(Path.GetFullPath(_dir), resolved.RunDbPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(StateHome.CataloguePathFor(home)),
            "an imported demo wrote to the machine catalogue - it is back in `conductor history`");
    }

    /// <summary>A document nothing recognises must cost the caller a message, not a directory —
    /// and the default <c>conductor demo</c> must be unreachable from this path by accident.</summary>
    [Fact]
    public void AnUnrecognisedDocumentIsRefusedBeforeAnythingIsBuilt()
    {
        var path = Path.Combine(_dir, "prose.md");
        File.WriteAllText(path, "# Ideas\n\nWe should probably build a greeting service at some point.\n");

        Assert.Null(DemoCommand.LoadImport(path));
        Assert.Null(DemoCommand.LoadImport(Path.Combine(_dir, "no-such-file.md")));
    }

    /// <summary>The default scaffold is not a variant of the imported one: with no document, the
    /// plan file is byte-for-byte what it was before this seam existed.</summary>
    [Fact]
    public void TheDefaultDemoPlanIsUnchangedByTheSeam()
    {
        Assert.Equal(
            DemoCommand.PlanJson(_dir, "/usr/local/bin/conductor"),
            DemoCommand.PlanJson(_dir, "/usr/local/bin/conductor", stagesJson: null));

        var plan = PlanConfig.Load(WriteDefault());
        Assert.Equal(2, plan.Stages.Count);
        Assert.Equal(["D1", "D2"], plan.Stages.Select(s => s.Id));

        string WriteDefault()
        {
            var p = Path.Combine(_dir, "default.plan.json");
            // The plan is only valid alongside its tracker - PlanConfig.Validate checks the file is there.
            File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), DemoCommand.Tracker);
            File.WriteAllText(p, DemoCommand.PlanJson(_dir, "/usr/local/bin/conductor"));
            return p;
        }
    }
}
