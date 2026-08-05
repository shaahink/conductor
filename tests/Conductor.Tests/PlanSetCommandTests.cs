using System.Text.Json;
using System.Text.Json.Nodes;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SC3.2 — `conductor plan set` stops failing silently. Three failures used to stack on one two-word
/// command: a key the plan does not declare was created and cheerfully confirmed, every `//` comment
/// in the file was dropped, and the edit reached no running engine.
///
/// <para>The schema half is measured against <see cref="PlanConfig"/> itself and against every plan
/// this repo ships, not against a hand-kept list — a list would be wrong the first time a field is
/// added, which is the class of defect this checkpoint exists to kill.</para>
/// </summary>
public sealed class PlanSetCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-planset-{Guid.NewGuid():N}");
    private readonly string _repo;

    public PlanSetCommandTests()
    {
        _repo = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "TRACKER.md"), "# tracker\n");
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_dir); } catch (Exception) { /* best effort */ }
    }

    /// <summary>An annotated plan, the shape `conductor init` invites: comments, and a `limits` block
    /// that deliberately has no cost cap — adding one is the most documented edit there is.</summary>
    private string WritePlan(string name = "plan.json", string? extra = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, $$"""
        {
          // why this plan exists
          "version": "1.0",
          "name": "planset-test",
          "repo": "{{_repo.Replace('\\', '/')}}",
          "tracker": "TRACKER.md",
          "agent": {
            // the agent CLI this project drives
            "command": "cmd",
            "args": ["/c", "echo", "{prompt}"]
          },
          "limits": { "stallMinutes": 20 },
          "stages": [ { "id": "S1", "title": "one", "sessions": 1 } ],
          "gates": []{{extra}}
        }
        """);
        return path;
    }

    // ---------------------------------------------------------------- schema: what a plan may say

    [Fact]
    public void Resolve_KnowsARealNestedLeaf_EvenThoughItIsAbsentFromTheFile()
    {
        // The trap this whole checkpoint turns on: JsonOpts omits nulls, so an unset maxRunCostUsd is
        // NOT in the serialised document. Presence would refuse the edit everyone makes first.
        var plan = new PlanConfig { Name = "x", Repo = _repo, Tracker = "TRACKER.md" };
        var json = JsonSerializer.Serialize(plan, PlanConfig.JsonOpts);
        Assert.DoesNotContain("maxRunCostUsd", json, StringComparison.Ordinal);

        var lookup = PlanKeySchema.Resolve("limits.maxRunCostUsd");
        Assert.True(lookup.Known);
        Assert.Equal(["limits", "maxRunCostUsd"], lookup.Canonical);
    }

    [Fact]
    public void Resolve_CanonicalisesCasing_SoAnEditLandsOnTheExistingKey()
    {
        var lookup = PlanKeySchema.Resolve("Limits.MaxRunCostUsd");
        Assert.True(lookup.Known);
        Assert.Equal(["limits", "maxRunCostUsd"], lookup.Canonical);
    }

    [Fact]
    public void Resolve_RefusesAKeyThePlanDoesNotDeclare_AndNamesTheBlockAndItsKeys()
    {
        var lookup = PlanKeySchema.Resolve("limits.maxRunCostUsdd");
        Assert.False(lookup.Known);
        Assert.Equal("maxRunCostUsdd", lookup.UnknownSegment);
        Assert.Equal("limits", lookup.ParentPath);
        Assert.Contains("maxRunCostUsd", lookup.ParentKeys, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("gates.0.timeoutMinutes")]       // array index
    [InlineData("stages.0.notes")]               // array index into a different collection
    [InlineData("workflows.deliver-verify.steps.0.runIf")] // author-named dictionary key
    [InlineData("agent.model")]
    [InlineData("telegram.pollIntervalSeconds")] // an object that is null in most plans
    [InlineData("telegram.allowedChatIds.0")]    // plan-config.md documents setting this from the CLI
    [InlineData("packs.0")]                      // list of scalars
    public void Resolve_WalksArraysDictionariesAndScalars(string key) =>
        Assert.True(PlanKeySchema.Resolve(key).Known, key);

    [Theory]
    [InlineData("gates.first.timeoutMinutes")]   // an array wants an index, not a name
    [InlineData("limits.stallMinutes.deeper")]   // nothing lives past a scalar
    [InlineData("limitz.stallMinutes")]          // typo'd block
    [InlineData("planDir")]                      // [JsonIgnore] — computed, never a file key
    public void Resolve_RefusesWhatCannotBeSet(string key) =>
        Assert.False(PlanKeySchema.Resolve(key).Known, key);

    [Fact]
    public void FindPaths_TurnsABareNameIntoTheDottedPath()
    {
        var doc = JsonNode.Parse("{}");
        Assert.Equal(["limits.maxRunCostUsd"], PlanKeySchema.FindPaths("maxRunCostUsd", doc));
    }

    [Fact]
    public void FindPaths_OffersOnlyPathsThatExistInThisFile()
    {
        // Suggesting gates.0.timeoutMinutes for a plan with no gates just moves the failure along.
        var empty = JsonNode.Parse("""{"gates":[]}""");
        Assert.DoesNotContain(PlanKeySchema.FindPaths("timeoutMinutes", empty), p => p.StartsWith("gates.", StringComparison.Ordinal));

        var one = JsonNode.Parse("""{"gates":[{"name":"smoke"}]}""");
        Assert.Contains("gates.0.timeoutMinutes", PlanKeySchema.FindPaths("timeoutMinutes", one), StringComparer.Ordinal);
    }

    [Fact]
    public void NearMisses_NamesTheOneCharacterTypoAndNothingElse()
    {
        var keys = PlanKeySchema.KeysOf(typeof(LimitsConfig));
        Assert.Equal("maxRunCostUsd", PlanKeySchema.NearMisses("maxRunCostUsdd", keys)[0]);
        Assert.Empty(PlanKeySchema.NearMisses("somethingEntirelyElse", keys));
    }

    [Fact]
    public void IsObjectAt_SeparatesADeclaredBlockFromAScalarOrATypo()
    {
        Assert.True(PlanKeySchema.IsObjectAt("telegram"));
        Assert.False(PlanKeySchema.IsObjectAt("limits.stallMinutes"));
        Assert.False(PlanKeySchema.IsObjectAt("telegran"));
    }

    /// <summary>The mirror is load-bearing: if the schema walk disagrees with what a plan file may
    /// really contain, this rule would refuse working edits. Every leaf of every plan this repo ships
    /// must resolve — the same shape of check SC3.1's model rule got.
    /// <para>It used to resolve all but one: five shipped plans set <c>advisor.provider</c>, which
    /// <c>AdvisorConfig</c> does not declare and the deserialiser dropped on the floor (bug #7, found
    /// by pointing this rule at real files). SC3.4 removed the key from those plans and made an
    /// unknown advisor key a plan-load failure, so the exception is gone and the rule is now
    /// unconditional — which is what stops a second dead key slipping in behind the first.</para></summary>
    [Fact]
    public void EveryKeyInEveryShippedPlanResolves()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var plans = Directory.GetFiles(Path.Combine(dir!.FullName, "plans"), "*.plan.json");
        Assert.NotEmpty(plans);

        var unknown = new List<string>();
        foreach (var file in plans)
        {
            var root = JsonNode.Parse(File.ReadAllText(file),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var path in LeafPaths(root, ""))
            {
                if (!PlanKeySchema.Resolve(path).Known) unknown.Add($"{Path.GetFileName(file)}: {path}");
            }
        }

        Assert.True(unknown.Count == 0, "Shipped plans carry keys the schema walk refuses:\n  " + string.Join("\n  ", unknown));
    }

    private static IEnumerable<string> LeafPaths(JsonNode? node, string prefix)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    var path = prefix.Length == 0 ? key : prefix + "." + key;
                    // A dictionary's author-named keys are values, not schema: stop at the first leaf
                    // under them rather than asserting on names the author invented.
                    if (child is JsonObject or JsonArray)
                    {
                        foreach (var deeper in LeafPaths(child, path)) yield return deeper;
                    }
                    else yield return path;
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    foreach (var deeper in LeafPaths(arr[i], $"{prefix}.{i}")) yield return deeper;
                }
                break;
        }
    }

    // ---------------------------------------------------------------- the command

    [Fact]
    public void Set_RefusesAnUndeclaredKey_AndLeavesTheFileByteIdentical()
    {
        var path = WritePlan();
        var before = File.ReadAllBytes(path);

        Assert.Equal(1, PlanSetCommand.ExecuteSet(path, "limits.maxRunCostUsdd", "100"));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Set_RefusesABareNameThatBelongsNested_AndSaysWhereItLives()
    {
        var path = WritePlan();
        Assert.Equal(1, PlanSetCommand.ExecuteSet(path, "maxRunCostUsd", "100"));

        var lookup = PlanKeySchema.Resolve("maxRunCostUsd");
        var lines = PlanSetCommand.RefusalLines("maxRunCostUsd", lookup, JsonNode.Parse("{}"), create: false);
        Assert.Contains(lines, l => l.Contains("limits.maxRunCostUsd", StringComparison.Ordinal));
    }

    [Fact]
    public void Set_WritesAnUndeclaredKeyOnlyWithCreate()
    {
        var path = WritePlan();
        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "limits.maxRunCostUsdd", "100", create: true));
        Assert.Contains("maxRunCostUsdd", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_StillSetsARealKeyThatTheSerialiserOmittedForBeingNull()
    {
        var path = WritePlan();
        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "limits.maxRunCostUsd", "100"));
        Assert.Equal(100m, PlanConfig.Load(path).Limits.MaxRunCostUsd);
    }

    [Fact]
    public void Set_CreatesADeclaredBlockThatIsAbsentFromTheFile()
    {
        // `plan set telegram.<x>` is documented in plan-config.md and used to die on "Key segment
        // 'telegram' not found" for every plan that had not already written the block by hand.
        var path = WritePlan();
        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "telegram.enableTwoWay", "true"));
        Assert.True(PlanConfig.Load(path).Telegram?.EnableTwoWay);
    }

    [Fact]
    public void Set_LandsOnTheExistingKeyWhenTheCaseIsWrong()
    {
        var path = WritePlan();
        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "Limits.StallMinutes", "45"));
        Assert.Equal(45, PlanConfig.Load(path).Limits.StallMinutes);
        Assert.DoesNotContain("StallMinutes", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_KeepsTheAnnotatedFileBesideTheRewrittenOne()
    {
        var path = WritePlan();
        var original = File.ReadAllText(path);
        Assert.Equal(2, PlanSetCommand.CountCommentLines(original));

        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "limits.stallMinutes", "30"));

        Assert.Equal(0, PlanSetCommand.CountCommentLines(File.ReadAllText(path)));   // the rewrite drops them
        Assert.Equal(original, File.ReadAllText(path + ".bak"));                     // and they survive next door
    }

    [Theory]
    [InlineData("{ \"a\": 1 }", 0)]
    [InlineData("// one\n// two\n", 2)]
    [InlineData("{ \"repo\": \"https://x/y\" }", 0)]           // a URL in a string is not a comment
    [InlineData("{ \"a\": 1 } // trailing", 1)]                 // dropped by the rewrite too
    [InlineData("/* two\n   lines */\n{ }", 2)]
    public void CountCommentLines_CountsWhatTheRewriteWillDrop(string text, int expected) =>
        Assert.Equal(expected, PlanSetCommand.CountCommentLines(text));

    // ---------------------------------------------------------------- reach

    [Fact]
    public void DecideReach_SaysNoEngine_WhenNothingHoldsTheLock()
    {
        var stateDir = Path.Combine(_dir, "state-none");
        Directory.CreateDirectory(stateDir);
        Assert.Equal(PlanSetCommand.Reach.NoEngine, PlanSetCommand.DecideReach(stateDir));
    }

    [Fact]
    public void DecideReach_QueuesForALiveEngine_AndRefusesToEatAPendingCommand()
    {
        // This process is a live process holding the lock — exactly what the run loop writes.
        var stateDir = Path.Combine(_dir, "state-live");
        Directory.CreateDirectory(stateDir);
        EngineLock.Write(stateDir);
        Assert.Equal(PlanSetCommand.Reach.Queued, PlanSetCommand.DecideReach(stateDir));

        File.WriteAllText(Path.Combine(stateDir, "control.json"), """{"command":"pause"}""");
        Assert.Equal(PlanSetCommand.Reach.ControlBusy, PlanSetCommand.DecideReach(stateDir));
    }

    [Fact]
    public void DecideReach_IgnoresALockLeftBehindByADeadEngine()
    {
        var stateDir = Path.Combine(_dir, "state-stale");
        Directory.CreateDirectory(stateDir);
        // pid 0 is never a live process; the stamp keeps a recycled id from reading as alive.
        File.WriteAllText(EngineLock.PathFor(stateDir), "0\n" + DateTime.UtcNow.ToString("O"));
        Assert.Equal(PlanSetCommand.Reach.NoEngine, PlanSetCommand.DecideReach(stateDir));
    }

    [Fact]
    public void ReachLine_AlwaysEndsInSomethingTheOperatorCanDo()
    {
        Assert.Contains("pid 4242", PlanSetCommand.ReachLine(PlanSetCommand.Reach.Queued, 4242, "p.json"), StringComparison.Ordinal);
        Assert.Contains("conductor plan reload --plan p.json", PlanSetCommand.ReachLine(PlanSetCommand.Reach.NoEngine, null, "p.json"), StringComparison.Ordinal);
        Assert.Contains("conductor plan reload --plan p.json", PlanSetCommand.ReachLine(PlanSetCommand.Reach.ControlBusy, 1, "p.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void Set_QueuesAReloadForALiveEngine()
    {
        var path = WritePlan();
        var stateDir = Path.Combine(_repo, ".conductor");
        Directory.CreateDirectory(stateDir);
        EngineLock.Write(stateDir);

        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "limits.stallMinutes", "25"));

        var control = File.ReadAllText(Path.Combine(stateDir, "control.json"));
        Assert.Equal(ControlAction.ReloadPlan, ControlFile.Parse(control).Action);
    }

    [Fact]
    public void Set_WritesNoStateDirectoryWhenThereIsNoRun()
    {
        // An edit must never conjure a .conductor: `plan set` is authoring, not run control.
        var path = WritePlan();
        Assert.Equal(0, PlanSetCommand.ExecuteSet(path, "limits.stallMinutes", "25"));
        Assert.False(Directory.Exists(Path.Combine(_repo, ".conductor")));
    }
}
