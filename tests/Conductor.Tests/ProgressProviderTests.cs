using Conductor.Core;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

// B1.3 — the two escape-hatch progress providers (D-2, F-1). ScriptProvider normalises a non-tabular
// progress doc (Shamshir-shaped PROGRESS.md) into the same CheckpointRow contract the engine consumes;
// PlanCheckpointProvider serves checkpoints declared inline in the plan. Both must be resilient and
// selectable via ProgressProviderFactory, which is fail-fast on misconfiguration.
public sealed class ProgressProviderTests : IDisposable
{
    // A Shamshir-shaped progress doc: a checklist with irregular ids (P-0, P3.4b, F5) — the exact shape
    // TrackerParser's markdown-table regex cannot read, which is why the script provider exists.
    private const string ShamshirProgress = """
        # Shamshir — parity-pipeline PROGRESS

        - [x] P-0   Bootstrap the parity harness
        - [ ] P0.1  Wire the fixture loader
        - [~] P3.4b Flaky-retry edge case
        - [!] F5    Blocked on upstream schema
        """;

    // A tiny, real normaliser the plan would own: PROGRESS.md checklist → checkpoint JSON array.
    // [x]=DONE [ ]=TODO [~]=IN PROGRESS [!]=BLOCKED. Proves ScriptProvider consumes a genuine command.
    private const string NormaliserScript = """
        $map = @{ '[x]' = 'DONE'; '[ ]' = 'TODO'; '[~]' = 'IN PROGRESS'; '[!]' = 'BLOCKED' }
        $rows = @()
        foreach ($line in Get-Content -LiteralPath 'PROGRESS.md') {
          if ($line -match '^\s*-\s*(\[[ x~!]\])\s+(\S+)\s+(.+)$') {
            $rows += [pscustomobject]@{
              id = $Matches[2]; title = $Matches[3].Trim(); status = $map[$Matches[1]]; commit = ''; evidence = ''
            }
          }
        }
        ConvertTo-Json -InputObject @($rows)
        """;

    private readonly string _repo = Path.Combine(Path.GetTempPath(), "cbaton-b13-" + Guid.NewGuid().ToString("N"));

    public ProgressProviderTests()
    {
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "PROGRESS.md"), ShamshirProgress);
        File.WriteAllText(Path.Combine(_repo, "PROGRESS.md.tracker"), ""); // satisfies PlanConfig.TrackerPath if ever loaded
        File.WriteAllText(Path.Combine(_repo, "progress-to-json.ps1"), NormaliserScript);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    private PlanConfig PlanWith(ProgressConfig progress)
        => new() { Repo = _repo, Tracker = "PROGRESS.md.tracker", Progress = progress };

    [Fact]
    public void ScriptProvider_NormalisesShamshirProgress_IntoRowsWithIrregularIds()
    {
        var plan = PlanWith(new ProgressConfig
        {
            Kind = "script",
            Script = new ScriptProviderConfig { Command = "& .\\progress-to-json.ps1" },
        });

        var provider = ProgressProviderFactory.Create(plan);
        Assert.Equal("script", provider.Name);

        var snap = provider.Read(plan);
        Assert.Equal(new[] { "P-0", "P0.1", "P3.4b", "F5" }, snap.Checkpoints.Select(c => c.Id));
        Assert.True(snap.ById("P-0")!.IsDone);
        Assert.False(snap.ById("P0.1")!.IsDone);
        Assert.True(snap.ById("P3.4b")!.IsInProgress);
        Assert.True(snap.ById("F5")!.IsBlocked);
    }

    [Fact]
    public void ScriptProvider_MissingCommand_ThrowsClearError()
    {
        var plan = PlanWith(new ProgressConfig { Kind = "script", Script = new ScriptProviderConfig { Command = "" } });
        var ex = Assert.Throws<InvalidOperationException>(() => ProgressProviderFactory.Create(plan).Read(plan));
        Assert.Contains("progress.script.command is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptProvider_MalformedJson_ThrowsClearError_NotCrash()
    {
        var plan = PlanWith(new ProgressConfig
        {
            Kind = "script",
            Script = new ScriptProviderConfig { Command = "Write-Output 'not json at all'" },
        });
        var ex = Assert.Throws<InvalidOperationException>(() => ProgressProviderFactory.Create(plan).Read(plan));
        Assert.Contains("not a JSON checkpoint array", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptProvider_NonZeroExit_ThrowsClearError()
    {
        var plan = PlanWith(new ProgressConfig
        {
            Kind = "script",
            Script = new ScriptProviderConfig { Command = "Write-Error 'boom'; exit 3" },
        });
        var ex = Assert.Throws<InvalidOperationException>(() => ProgressProviderFactory.Create(plan).Read(plan));
        Assert.Contains("exited 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanCheckpointProvider_ResolvesInlineCheckpoints_AndGroupsByStage()
    {
        var plan = PlanWith(new ProgressConfig
        {
            Kind = "plan-checkpoints",
            Checkpoints =
            [
                new PlanCheckpoint { Id = "P-0", Title = "Bootstrap", Status = "DONE", Commit = "abc1234" },
                new PlanCheckpoint { Id = "P0.1", Title = "Loader", Status = "TODO" },
                new PlanCheckpoint { Id = "P0.2", Title = "Fixtures", Status = "IN PROGRESS" },
            ],
        });

        var provider = ProgressProviderFactory.Create(plan);
        Assert.Equal("plan-checkpoints", provider.Name);

        var snap = provider.Read(plan);
        Assert.Equal(new[] { "P-0", "P0.1", "P0.2" }, snap.Checkpoints.Select(c => c.Id));
        Assert.Equal("abc1234", snap.ById("P-0")!.Commit);
        Assert.True(snap.ById("P-0")!.IsDone);
        Assert.Equal(2, snap.ForStage("P0").Count());
        Assert.False(snap.AllDone);
    }

    [Fact]
    public void Factory_DefaultsToMarkdownTable_WhenProgressUnset()
    {
        var plan = new PlanConfig { Repo = _repo, Tracker = "PROGRESS.md.tracker" };
        Assert.IsType<MarkdownTableProvider>(ProgressProviderFactory.Create(plan));
    }

    [Fact]
    public void Factory_UnknownKind_ThrowsFailFast()
    {
        var plan = PlanWith(new ProgressConfig { Kind = "carrier-pigeon" });
        var ex = Assert.Throws<InvalidOperationException>(() => ProgressProviderFactory.Create(plan));
        Assert.Contains("unknown progress.kind 'carrier-pigeon'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_ScriptKindWithoutConfig_ThrowsFailFast()
    {
        var plan = PlanWith(new ProgressConfig { Kind = "script", Script = null });
        var ex = Assert.Throws<InvalidOperationException>(() => ProgressProviderFactory.Create(plan));
        Assert.Contains("progress.script is missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_PlanCheckpointsEmpty_ThrowsFailFast()
    {
        var plan = PlanWith(new ProgressConfig { Kind = "plan-checkpoints", Checkpoints = [] });
        var ex = Assert.Throws<InvalidOperationException>(() => ProgressProviderFactory.Create(plan));
        Assert.Contains("progress.checkpoints is empty", ex.Message, StringComparison.Ordinal);
    }
}
