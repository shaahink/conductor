using Conductor.Core.Planning;

namespace Conductor.Tests;

/// <summary>
/// U0.1 — pins the resolution order <see cref="PlanDiscovery.Discover"/> implements for
/// <c>PlanSettings.ResolvePlanPath</c>: exactly one <c>*.plan.json</c> in cwd wins outright (even
/// over several candidates under <c>./plans/</c>); only an EMPTY cwd falls back to scanning
/// <c>./plans/</c>; zero matches anywhere is an empty list (the caller turns that into the
/// friendly "run `conductor init`" error — untestable console/throw shell, doc comment on
/// <c>ResolvePlanPath</c> calls it out as manual-only).
/// </summary>
public sealed class PlanDiscoveryTests : IDisposable
{
    private readonly string _root;

    public PlanDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"conductor-plandiscovery-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { TestTemp.DeleteTree(_root); } catch { }
    }

    private string WriteFile(string relativePath, string content = "{}")
    {
        var full = Path.Combine([_root, .. relativePath.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    [Fact]
    public void NoPlanFilesAnywhere_ReturnsEmpty()
    {
        var candidates = PlanDiscovery.Discover(_root);
        Assert.Empty(candidates);
    }

    [Fact]
    public void SinglePlanFileInCwd_IsDiscovered()
    {
        var path = WriteFile("foo.plan.json", """{"name":"Foo Plan"}""");

        var candidates = PlanDiscovery.Discover(_root);

        var only = Assert.Single(candidates);
        Assert.Equal(path, only.Path);
        Assert.Equal("Foo Plan", only.Name);
    }

    [Fact]
    public void MultiplePlanFilesInCwd_AllReturned_OrderedByPathOrdinalIgnoreCase()
    {
        WriteFile("b.plan.json");
        WriteFile("a.plan.json");

        var candidates = PlanDiscovery.Discover(_root);

        Assert.Equal(2, candidates.Count);
        Assert.True(string.Compare(candidates[0].Path, candidates[1].Path, StringComparison.OrdinalIgnoreCase) < 0,
            "candidates must be ordered ordinal-ignore-case by path");
    }

    [Fact]
    public void EmptyCwd_FallsBackToPlansSubdirectory()
    {
        var path = WriteFile("plans/sub.plan.json", """{"name":"Sub Plan"}""");

        var candidates = PlanDiscovery.Discover(_root);

        var only = Assert.Single(candidates);
        Assert.Equal(path, only.Path);
        Assert.Equal("Sub Plan", only.Name);
    }

    [Fact]
    public void CwdMatchWinsOutright_EvenWhenPlansSubdirAlsoHasCandidates()
    {
        WriteFile("cwd.plan.json");
        WriteFile("plans/nested.plan.json");

        var candidates = PlanDiscovery.Discover(_root);

        var only = Assert.Single(candidates);
        Assert.EndsWith("cwd.plan.json", only.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CwdWithMultipleMatches_StillWinsOutright_PlansSubdirIgnored()
    {
        WriteFile("one.plan.json");
        WriteFile("two.plan.json");
        WriteFile("plans/three.plan.json");

        var candidates = PlanDiscovery.Discover(_root);

        Assert.Equal(2, candidates.Count);
        Assert.DoesNotContain(candidates, c => c.Path.Contains("three.plan.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MissingNameField_FallsBackToFileNameWithoutExtension()
    {
        var path = WriteFile("noname.plan.json", "{}");

        var candidates = PlanDiscovery.Discover(_root);

        var only = Assert.Single(candidates);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), only.Name);
    }

    [Fact]
    public void UnreadableOrMalformedJson_FallsBackToFileNameWithoutExtension_NeverThrows()
    {
        var path = WriteFile("broken.plan.json", "{ not valid json !!!");

        var candidates = PlanDiscovery.Discover(_root);

        var only = Assert.Single(candidates);
        Assert.Equal(Path.GetFileNameWithoutExtension(path), only.Name);
    }

    [Fact]
    public void MissingPlansSubdirectory_DoesNotThrow()
    {
        // _root/plans never created — Directory.Exists guard must hold.
        var candidates = PlanDiscovery.Discover(_root);
        Assert.Empty(candidates);
    }
}
