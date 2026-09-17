using Conductor.Core.Courier;
using Conductor.Core.Release;

namespace Conductor.Tests;

/// <summary>PK1.2 / D1 - the install shape. Measured first (.conductor/evidence/PK1/pk1.2-lock-measurement.log):
/// a courier running beside the engine made the next engine publish fail on shared dll locks, and one
/// running from its own directory did not. These pin the vocabulary the installer, the install verb and
/// the release preflight share.</summary>
public sealed class PK1_2CourierInstallShapeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pk12-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    public static TheoryData<string?, CourierShape> Shapes => new()
    {
        { null, CourierShape.NotRunning },
        { "", CourierShape.NotRunning },
        { @"C:\Users\x\AppData\Local\Programs\conductor\courier\conductor-courier.exe", CourierShape.OwnDirectory },
        { @"C:\Users\x\AppData\Local\Programs\conductor\COURIER\Conductor-Courier.EXE", CourierShape.OwnDirectory },
        { "/opt/conductor/courier/conductor-courier", CourierShape.OwnDirectory },
        { @"C:\Code\conductor\src\Conductor\bin\Debug\net10.0\conductor-courier.exe", CourierShape.BesideEngine },
        { @"C:\Users\x\AppData\Local\Programs\conductor\conductor-courier.exe", CourierShape.BesideEngine },
        { @"C:\Users\x\AppData\Local\Programs\conductor\conductor.exe", CourierShape.InsideEngine },
        { @"C:\Users\x\AppData\Local\Programs\conductor\courier\conductor.exe", CourierShape.InsideEngine },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void AShapeIsReadOffTheBinaryAndItsDirectory(string? exe, CourierShape expected) =>
        Assert.Equal(expected, CourierBinary.ShapeOf(exe));

    /// <summary>Property, not example: every shape has a reinstall line, and only the own-directory one
    /// promises the engine installs without stopping the courier. A shape added later that forgets its
    /// line fails here rather than printing the fallback.</summary>
    [Fact]
    public void EveryShapeSaysWhatTheReinstallDoesToIt()
    {
        var exeFor = new Dictionary<CourierShape, string?>
        {
            [CourierShape.NotRunning] = null,
            [CourierShape.OwnDirectory] = @"C:\i\courier\conductor-courier.exe",
            [CourierShape.BesideEngine] = @"C:\i\conductor-courier.exe",
            [CourierShape.InsideEngine] = @"C:\i\conductor.exe",
        };
        Assert.Equal(Enum.GetValues<CourierShape>().OrderBy(s => s), exeFor.Keys.OrderBy(s => s));

        var lines = exeFor.ToDictionary(kv => kv.Key, kv => ReleasePreflight.ReinstallLine(kv.Value));
        Assert.Equal(lines.Count, lines.Values.Distinct(StringComparer.Ordinal).Count());
        foreach (var (shape, line) in lines)
        {
            Assert.DoesNotContain("stops the courier at step 0", line, StringComparison.Ordinal);
            Assert.Equal(shape == CourierShape.OwnDirectory,
                line.Contains("publishes the engine without stopping it", StringComparison.Ordinal));
        }
        Assert.Contains("once", lines[CourierShape.InsideEngine], StringComparison.Ordinal);
        Assert.Contains("once", lines[CourierShape.BesideEngine], StringComparison.Ordinal);
    }

    [Fact]
    public void ThePreflightCourierLineCarriesTheShape()
    {
        var facts = new CourierFacts(true, "User", true, "Running", true, 4242, 1, 1, true,
            CourierExe: @"C:\i\courier\conductor-courier.exe");
        var check = ReleasePreflight.Courier(facts);
        Assert.Equal(ReleaseCheck.Ok, check.State);
        Assert.Contains(check.Detail, d => d.Contains("publishes the engine without stopping it", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"C:\i\courier\conductor-courier.exe", "Conductor Courier", "--task-name \"Conductor Courier\"")]
    [InlineData(@"C:\i\courier\conductor-courier.exe", "pk12 scratch", "--task-name \"pk12 scratch\"")]
    [InlineData(@"C:\i\conductor.exe", "Conductor Courier", "courier run")]
    public void TheTaskPassesEachBinaryTheArgumentsItTakes(string exe, string task, string expected) =>
        Assert.Equal(expected, CourierTask.ArgumentsFor(exe, task));

    /// <summary>The courier's own binary must accept what the task hands it - the task XML and the
    /// courier's parser are two files, and this is the line between them.</summary>
    [Fact]
    public void TheCourierAcceptsTheArgumentsItsTaskIsRegisteredWith()
    {
        var args = CourierTask.ArgumentsFor(@"C:\i\courier\conductor-courier.exe", "pk12 scratch");
        var argv = System.Text.RegularExpressions.Regex.Matches(args, "\"[^\"]*\"|\\S+",
                System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1))
            .Select(m => m.Value.Trim('"')).ToList();
        Assert.Equal(new Conductor.Courier.CourierProgram.Options(false, "pk12 scratch"),
            Conductor.Courier.CourierProgram.Parse(argv, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void AnInstallsOwnCourierDirectoryWinsOverTheCopyBesideTheEngine()
    {
        Directory.CreateDirectory(Path.Combine(_root, CourierBinary.DirName));
        Assert.Equal(CourierBinary.Beside(_root), CourierBinary.Resolve(_root));

        File.WriteAllText(CourierBinary.InOwnDirectory(_root), "");
        Assert.Equal(CourierBinary.InOwnDirectory(_root), CourierBinary.Resolve(_root));
    }
}