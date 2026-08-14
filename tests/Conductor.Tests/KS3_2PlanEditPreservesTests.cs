using System.Text;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS3.2 — the plan editor stops destroying the file it edits.
///
/// <para>The replayed trap (field memory, 2026-08): adding one stage through the editor changed
/// three unrelated things — <c>progress.kind</c>, gate <c>timeoutMinutes</c> and <c>gatePolicy</c>
/// were materialised into a file that never carried them, and every <c>//</c> comment vanished.
/// Both halves came from re-serialising the whole model. The fix (<see cref="PlanDocumentEditor"/>)
/// splices only the semantic diff into the raw bytes, so these tests assert on the exact text:
/// comments verbatim, key order intact, and no default the author never wrote.</para>
/// </summary>
public sealed class KS3_2PlanEditPreservesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-ks32-{Guid.NewGuid():N}");
    private readonly string _repo;

    public KS3_2PlanEditPreservesTests()
    {
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), InitCommand.BuildTrackerMd("ks32"));
    }

    public void Dispose() => TestTemp.DeleteTree(_dir);

    /// <summary>The exact fixture the trap needs: an init scaffold — comments plus only the keys
    /// init writes; no <c>progress</c>, no <c>gatePolicy</c>, no <c>limits.stallMinutes</c>.</summary>
    private string ScaffoldPlan()
    {
        var path = Path.Combine(_dir, "conductor.plan.json");
        File.WriteAllText(path, InitCommand.BuildPlanJson("ks32", _repo.Replace('\\', '/'), RepoKind.Generic));
        return path;
    }

    private static List<string> CommentLines(string text) =>
        [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Select(l => l.Trim())];

    // ------------------------------------------------------------------ the replay

    /// <summary>Add one stage; demand a diff that is ONLY the stage. Lines removed from the original
    /// may differ by nothing but the comma that now precedes the insertion; every other new line is
    /// the stage object or the <c>planVersion</c> the edit bumps. The three fields the old editor
    /// used to invent stay absent.</summary>
    [Fact]
    public void AddStage_TheDiffIsTheStageAndThePlanVersionAndNothingElse()
    {
        var path = ScaffoldPlan();
        var before = File.ReadAllText(path);

        var settings = new PlanCommand.Settings
        {
            Verb = "add-stage",
            Key = """{"id":"KS9","title":"the far door","sessions":2}""",
        };
        Assert.Equal(0, PlanAddStageCommand.ExecuteAddStage(path, settings));

        var after = File.ReadAllText(path);

        // The trap's three materialised fields are still absent from the file.
        Assert.DoesNotContain("\"progress\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("gatePolicy", after, StringComparison.Ordinal);
        Assert.DoesNotContain("stallMinutes", after, StringComparison.Ordinal);

        // Line diff: every removed line reappears with only a trailing comma; every added line is
        // the stage block, that comma-bearing twin, or the planVersion line.
        var beforeLines = before.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var afterLines = after.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var removed = beforeLines.Except(afterLines, StringComparer.Ordinal).ToList();
        var added = afterLines.Except(beforeLines, StringComparer.Ordinal).ToList();

        foreach (var line in removed)
            Assert.Contains(line + ",", added, StringComparer.Ordinal);
        var genuinelyNew = added.Where(l => !removed.Contains(l.TrimEnd(','), StringComparer.Ordinal)).ToList();
        string[] stageContent =
        [
            "\"id\": \"KS9\"", "\"title\": \"the far door\"", "\"sessions\": 2",
            "\"ownerGate\": false", "\"kind\": \"deliver\"", "\"planVersion\":",
        ];
        Assert.All(genuinelyNew, line => Assert.True(
            line.Trim().TrimEnd(',') is "{" or "}" ||
            stageContent.Any(s => line.Contains(s, StringComparison.Ordinal)),
            $"unexpected new line: '{line}'"));
        Assert.Contains(genuinelyNew, l => l.Contains("planVersion", StringComparison.Ordinal));

        // And the edit is real: the plan now has the stage.
        var reloaded = PlanConfig.Load(path);
        Assert.Contains(reloaded.Stages, s => s.Id == "KS9");
    }

    // ------------------------------------------------------------------ comments, verbatim

    [Fact]
    public void PlanSet_AddStage_And_ImportApply_KeepEveryCommentLineVerbatim()
    {
        // plan set
        var setPath = ScaffoldPlan();
        var comments = CommentLines(File.ReadAllText(setPath));
        Assert.NotEmpty(comments);
        Assert.Equal(0, PlanSetCommand.ExecuteSet(setPath, "limits.maxRunCostUsd", "5"));
        Assert.Equal(comments, CommentLines(File.ReadAllText(setPath)));
        Assert.False(File.Exists(setPath + ".bak"));

        // add-stage (this run continues on the same file: comments must survive stacking edits)
        var settings = new PlanCommand.Settings { Verb = "add-stage", Key = """{"id":"KS8","title":"spend","sessions":1}""" };
        Assert.Equal(0, PlanAddStageCommand.ExecuteAddStage(setPath, settings));
        Assert.Equal(comments, CommentLines(File.ReadAllText(setPath)));

        // import apply (PlanDiff.Apply then save — the same path `plan import -y` takes)
        var plan = PlanConfig.Load(setPath);
        var incoming = PlanImportService.ParseStructured("""
            ### KS5 — governed spend
            - **KS5.1** the machine ledger
            - **KS5.2** lanes counted
            ### KS6 — the far door
            - **KS6.1** one-way sync
            """);
        Assert.NotNull(incoming);
        var diff = PlanDiff.Compute(plan, incoming!);
        Assert.False(diff.IsEmpty);
        diff.Apply(plan);
        var afterImport = File.ReadAllText(setPath);
        Assert.Equal(comments, CommentLines(afterImport));

        // Sanity: all three edits actually landed.
        var final = PlanConfig.Load(setPath);
        Assert.Equal(5m, final.Limits.MaxRunCostUsd);
        Assert.Contains(final.Stages, s => s.Id == "KS8");
        Assert.Contains(final.Stages, s => s.Id == "KS5");
    }

    // ------------------------------------------------------------------ idempotence

    [Fact]
    public void PlanSet_TwiceWithTheSameValue_ChangesNothingButPlanVersion()
    {
        var path = ScaffoldPlan();
        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "limits.maxRunCostUsd", "5"));
        var first = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "limits.maxRunCostUsd", "5"));
        var second = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        Assert.Equal(first.Length, second.Length);
        var differing = Enumerable.Range(0, first.Length).Where(i => first[i] != second[i]).ToList();
        var only = Assert.Single(differing);
        Assert.Contains("planVersion", second[only], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the Face's seam

    /// <summary>Both control-plane save sites go through <see cref="PlanConfig.Save"/> — mutate the
    /// model the way <c>/plan/edit</c> does and prove the writer is the preserving one, so a Face
    /// edit can no longer undo a CLI-preserved file.</summary>
    [Fact]
    public void ModelMutationPlusSave_PreservesCommentsAndInventsNoDefaults()
    {
        var path = ScaffoldPlan();
        var comments = CommentLines(File.ReadAllText(path));

        var plan = PlanConfig.Load(path);
        plan.Stages[0].Title = "renamed by the Face";
        plan.Gates.Add(new GateConfig { Name = "lint", Command = "git diff --check" });
        plan.Save();

        var after = File.ReadAllText(path);
        Assert.Equal(comments, CommentLines(after));
        Assert.DoesNotContain("\"progress\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain("gatePolicy", after, StringComparison.Ordinal);

        var reloaded = PlanConfig.Load(path);
        Assert.Equal("renamed by the Face", reloaded.Stages[0].Title);
        Assert.Contains(reloaded.Gates, g => g.Name == "lint");
    }

    /// <summary>Deleting a member is the other half of the Face's edit surface (and the placeholder
    /// drop in `init --from-idea`): the removal takes only its own lines.</summary>
    [Fact]
    public void StageDelete_TakesOnlyTheStage_CommentsStay()
    {
        var path = ScaffoldPlan();
        var settings = new PlanCommand.Settings { Verb = "add-stage", Key = """{"id":"KS9","title":"the far door","sessions":2}""" };
        Assert.Equal(0, PlanAddStageCommand.ExecuteAddStage(path, settings));
        var comments = CommentLines(File.ReadAllText(path));

        var plan = PlanConfig.Load(path);
        plan.Stages.RemoveAll(s => s.Id == "S1");
        plan.Save();

        var after = File.ReadAllText(path);
        Assert.Equal(comments, CommentLines(after));
        Assert.DoesNotContain("rename me", after, StringComparison.Ordinal);
        var reloaded = PlanConfig.Load(path);
        var only = Assert.Single(reloaded.Stages);
        Assert.Equal("KS9", only.Id);
    }

    // ------------------------------------------------------------------ encoding

    /// <summary>The old writers disagreed about the BOM (`plan set` wrote without, `Save()` with) —
    /// the preserving writer keeps whichever the file itself has, both ways.</summary>
    [Fact]
    public void TheFilesOwnBomStateSurvivesEitherWay()
    {
        var bare = ScaffoldPlan();
        Assert.Equal(0, PlanSetCommand.ExecuteSet(bare, "limits.maxRunCostUsd", "9"));
        Assert.False(HasBom(bare), "a BOM-less file grew a BOM");

        var bommed = Path.Combine(_dir, "bommed.plan.json");
        File.WriteAllText(bommed, InitCommand.BuildPlanJson("ks32b", _repo.Replace('\\', '/'), RepoKind.Generic),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Assert.True(HasBom(bommed));
        Assert.Equal(0, PlanSetCommand.ExecuteSet(bommed, "limits.maxRunCostUsd", "9"));
        Assert.True(HasBom(bommed), "the file's BOM was dropped");

        var plan = PlanConfig.Load(bommed);
        plan.Stages[0].Sessions = 3;
        plan.Save();
        Assert.True(HasBom(bommed), "Save() dropped the BOM the file carried");
    }

    private static bool HasBom(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> head = stackalloc byte[3];
        return fs.Read(head) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
    }

    // ------------------------------------------------------------------ the editor itself

    /// <summary>Unknown keys are content the model cannot see — the splicer must route around them,
    /// not delete them.</summary>
    [Fact]
    public void AKeyTheModelDoesNotDeclare_SurvivesAnEdit()
    {
        var path = ScaffoldPlan();
        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "limits.maxRunCostUsdd", "100", create: true));
        Assert.Contains("maxRunCostUsdd", File.ReadAllText(path), StringComparison.Ordinal);

        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "limits.maxRunCostUsd", "5"));
        var after = File.ReadAllText(path);
        Assert.Contains("maxRunCostUsdd", after, StringComparison.Ordinal); // still there
        Assert.Equal(5m, PlanConfig.Load(path).Limits.MaxRunCostUsd);
    }

    [Fact]
    public void ApplyDiff_ReturnsTheInputBytesWhenNothingChanged()
    {
        var text = File.ReadAllText(ScaffoldPlan());
        var plan = JsonSerializer.Deserialize<PlanConfig>(text, PlanConfig.JsonOpts)!;
        var node = JsonSerializer.SerializeToNode(plan, PlanConfig.JsonOpts)!;
        var same = JsonSerializer.SerializeToNode(plan, PlanConfig.JsonOpts)!;
        Assert.Equal(text, PlanDocumentEditor.ApplyDiff(text, node, same));
    }
}
