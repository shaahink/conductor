using Conductor.Core;

namespace Conductor.Tests;

/// <summary>
/// KS0.3, bug #16 — the gate battery must never try to rebuild the engine running it.
///
/// <para>The two tests that carry the weight are the negative ones. Redirecting a build is cheap to
/// get right; redirecting one that should have been left alone is a gate that silently stopped testing
/// what it was pointed at. So: an engine installed outside the tree (the normal case, and the case
/// this repo's own gates run under today) must be left completely alone, and a command that already
/// chose its output must not have a second one appended.</para>
/// </summary>
public sealed class KS0_3ShadowBuildTests
{
    private const string Tree = @"C:\code\conductor";
    private const string ImageInTree = @"C:\code\conductor\src\Conductor\bin\Debug\net10.0\conductor.exe";
    private const string ImageInstalled = @"C:\Users\shahi\AppData\Local\Programs\conductor\conductor.exe";
    private const string Shadow = @"C:\temp\conductor-gate-build\conductor-1234abcd";

    [Fact]
    public void AnEngineRunningFromTheTreeSendsTheBuildToTheShadowPath()
    {
        var r = ShadowBuild.For("dotnet build Conductor.slnx -clp:ErrorsOnly", Tree, ImageInTree, Shadow);

        Assert.NotNull(r);
        Assert.True(r.Rewritten);
        Assert.Equal($"dotnet build Conductor.slnx -clp:ErrorsOnly --artifacts-path \"{Shadow}\"", r.Command);
    }

    [Fact]
    public void AnInstalledEngineIsLeftCompletelyAlone_ThisRunsOwnGatesDependOnIt()
    {
        // Measured on this machine, 2026-08-13: the engine driving these sessions is the installed
        // copy, so `dotnet build Conductor.slnx` must reach the gate byte for byte as written.
        Assert.Null(ShadowBuild.For("dotnet build Conductor.slnx -clp:ErrorsOnly", Tree, ImageInstalled, Shadow));
    }

    [Fact]
    public void NoRunningImageAtAllIsNotOurProblem()
        => Assert.Null(ShadowBuild.For("dotnet build Conductor.slnx", Tree, null, Shadow));

    [Theory]
    [InlineData("dotnet test Conductor.slnx")]
    [InlineData("dotnet publish src/Conductor")]
    [InlineData("dotnet pack src/Conductor")]
    [InlineData("msbuild Conductor.slnx")]
    public void EveryVerbThatWritesAnAssemblyIsRedirected(string command)
    {
        var r = ShadowBuild.For(command, Tree, ImageInTree, Shadow);

        Assert.NotNull(r);
        Assert.True(r.Rewritten);
        Assert.EndsWith($"--artifacts-path \"{Shadow}\"", r.Command, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dotnet build Conductor.slnx --artifacts-path C:\\elsewhere")]
    [InlineData("dotnet publish src/Conductor -o C:\\elsewhere")]
    [InlineData("dotnet build Conductor.slnx -p:BaseOutputPath=C:\\elsewhere\\")]
    public void ACommandThatAlreadyChoseItsOutputIsNotOverridden(string command)
    {
        var r = ShadowBuild.For(command, Tree, ImageInTree, Shadow);

        Assert.NotNull(r);
        Assert.False(r.Rewritten);
        Assert.Equal(command, r.Command);
    }

    [Theory]
    [InlineData("cmd /c \"cd /d C:\\code\\conductor\\face-go && go build ./... && go vet ./...\"")]
    [InlineData("dotnet run --project src/Conductor -- doctor")]
    [InlineData("pwsh -File tools/gates/ratchet.ps1")]
    public void WhatCannotBeRedirectedIsRunUnchanged_ButTheOperatorIsWarned(string command)
    {
        var r = ShadowBuild.For(command, Tree, ImageInTree, Shadow);

        Assert.NotNull(r);
        Assert.False(r.Rewritten);
        Assert.Equal(command, r.Command);
        Assert.Contains("not a stale", r.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWarningNamesTheImage_SoNobodyGoesHuntingForAnOrphan()
    {
        var r = ShadowBuild.For("cmd /c build.bat", Tree, ImageInTree, Shadow);

        Assert.NotNull(r);
        Assert.Contains(ImageInTree, r.Why, StringComparison.Ordinal);
        Assert.Contains("THIS run", r.Why, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShadowRootIsOutsideTheTreeItBuilds_AndStablePerTree()
    {
        var a = ShadowBuild.RootFor(Tree);
        var b = ShadowBuild.RootFor(Tree + @"\");

        Assert.Equal(a, b);
        Assert.False(a.StartsWith(Tree, StringComparison.OrdinalIgnoreCase),
            "the shadow output must not live inside the tree being built");
        Assert.NotEqual(a, ShadowBuild.RootFor(@"C:\code\conductor-site"));
    }
}
