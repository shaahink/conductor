using Conductor.Commands;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// M8.2 (design doc) — <c>conductor init</c> scaffolds a runnable plan with gates chosen from the
/// detected repo type. Drives the detection + scaffold directly against temp repos.
/// </summary>
public sealed class InitCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"init-{Guid.NewGuid():N}");

    public InitCommandTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } }

    private string Touch(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, "");
        return p;
    }

    [Fact]
    public void DetectsDotnetFromCsproj()
    {
        Touch("App.csproj");
        Assert.Equal(InitCommand.RepoKind.Dotnet, InitCommand.DetectRepoKind(_dir));
    }

    [Fact]
    public void DetectsGoNodeRustPython()
    {
        Touch("go.mod");
        Assert.Equal(InitCommand.RepoKind.Go, InitCommand.DetectRepoKind(_dir));
        File.Delete(Path.Combine(_dir, "go.mod"));

        Touch("package.json");
        Assert.Equal(InitCommand.RepoKind.Node, InitCommand.DetectRepoKind(_dir));
        File.Delete(Path.Combine(_dir, "package.json"));

        Touch("Cargo.toml");
        Assert.Equal(InitCommand.RepoKind.Rust, InitCommand.DetectRepoKind(_dir));
        File.Delete(Path.Combine(_dir, "Cargo.toml"));

        Touch("pyproject.toml");
        Assert.Equal(InitCommand.RepoKind.Python, InitCommand.DetectRepoKind(_dir));
    }

    [Fact]
    public void DetectsGenericWhenNoMarkers() =>
        Assert.Equal(InitCommand.RepoKind.Generic, InitCommand.DetectRepoKind(_dir));

    [Fact]
    public void DotnetMarkerWinsOverNodeWhenBothPresent()
    {
        // A .NET repo that also ships a package.json (e.g. a web front-end) is still dotnet-first.
        Touch("App.csproj");
        Touch("package.json");
        Assert.Equal(InitCommand.RepoKind.Dotnet, InitCommand.DetectRepoKind(_dir));
    }

    [Fact]
    public void GatesMatchRepoKind()
    {
        Assert.Equal(("dotnet build", "dotnet test"), InitCommand.GatesFor(InitCommand.RepoKind.Dotnet));
        Assert.Equal(("go build ./...", "go test ./..."), InitCommand.GatesFor(InitCommand.RepoKind.Go));
        Assert.Equal(("", ""), InitCommand.GatesFor(InitCommand.RepoKind.Generic));
    }

    [Fact]
    public void ScaffoldedPlanLoadsAndCarriesDetectedGates()
    {
        var json = InitCommand.BuildPlanJson("Demo", _dir, InitCommand.RepoKind.Dotnet);
        var planPath = Path.Combine(_dir, "conductor.plan.json");
        File.WriteAllText(planPath, json);
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), InitCommand.BuildTrackerMd("Demo"));

        var plan = PlanConfig.Load(planPath); // must not throw — the init self-check depends on this
        Assert.Equal("Demo", plan.Name);
        Assert.Single(plan.Stages);
        Assert.Contains(plan.Gates, g => g.Name == "build" && g.Command == "dotnet build");
        Assert.Contains(plan.Gates, g => g.Name == "tests" && g.Command == "dotnet test");
    }

    [Fact]
    public void GenericScaffoldHasNoGates()
    {
        var json = InitCommand.BuildPlanJson("Demo", _dir, InitCommand.RepoKind.Generic);
        var planPath = Path.Combine(_dir, "conductor.plan.json");
        File.WriteAllText(planPath, json);
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), InitCommand.BuildTrackerMd("Demo"));

        var plan = PlanConfig.Load(planPath);
        Assert.Empty(plan.Gates);
    }
}
