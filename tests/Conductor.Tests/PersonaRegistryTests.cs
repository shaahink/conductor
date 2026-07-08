using Conductor.Core;
using Conductor.Models;

namespace Conductor.Tests;

public class PersonaRegistryTests
{
    [Fact]
    public void BuiltInPersonasAllResolve()
    {
        var reg = new PersonaRegistry((string?)null);
        foreach (var name in PersonaRegistry.KnownPersonas)
        {
            var prompt = reg.ResolveSystemPrompt(name);
            Assert.NotNull(prompt);
            Assert.True(prompt!.Length > 20, $"Persona '{name}' prompt is too short (got {prompt.Length} chars)");
        }
    }

    [Fact]
    public void UnknownPersonaReturnsNullAndDoesNotThrow()
    {
        var reg = new PersonaRegistry((string?)null);
        var prompt = reg.ResolveSystemPrompt("nonexistent-persona-12345");
        Assert.Null(prompt);
    }

    [Fact]
    public void NullOrEmptyPersonaReturnsNull()
    {
        var reg = new PersonaRegistry((string?)null);
        Assert.Null(reg.ResolveSystemPrompt(null));
        Assert.Null(reg.ResolveSystemPrompt(""));
        Assert.Null(reg.ResolveSystemPrompt("  "));
    }

    [Fact]
    public void DiskFileOverridesBuiltIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-personas-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "planner.md"), "DISK PLANNER PROMPT");
            var reg = new PersonaRegistry(dir);
            var prompt = reg.ResolveSystemPrompt("planner");
            Assert.Equal("DISK PLANNER PROMPT", prompt);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void MissingDiskFileFallsBackToBuiltIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-personas-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // dir exists but has no planner.md → built-in fallback
            var reg = new PersonaRegistry(dir);
            var prompt = reg.ResolveSystemPrompt("planner");
            Assert.NotNull(prompt);
            Assert.True(prompt!.Length > 20);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PlanDirBasedRegistryFindsDiskFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-personas-{Guid.NewGuid():N}");
        var personasDir = Path.Combine(dir, "personas");
        Directory.CreateDirectory(personasDir);
        try
        {
            File.WriteAllText(Path.Combine(personasDir, "planner.md"), "FROM DISK VIA PLAN");
            var plan = new PlanConfig { Repo = ".", Tracker = "t.md" };
            typeof(PlanConfig).GetProperty(nameof(PlanConfig.PlanFilePath))!.SetValue(plan, Path.Combine(dir, "plan.json"));
            var reg = new PersonaRegistry(plan);
            var prompt = reg.ResolveSystemPrompt("planner");
            Assert.Equal("FROM DISK VIA PLAN", prompt);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
