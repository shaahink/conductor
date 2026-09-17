using System.Reflection;
using System.Text.RegularExpressions;
using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Courier;
using Conductor.Core.Integrations.Messaging;

namespace Conductor.Tests;

/// <summary>PK4.3 / D7-D8 — the room's words have one road, and the docs must name it. <c>report.ps1</c>
/// posted a card at the claim, before any gate had run (F-OBS-2); it is retired, and a doc that still
/// told a session to run it would put the old road back. The claim's <c>--tell</c> is the new one.</summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    [Fact]
    public void CliDocTaskRowNamesTellWithTheLimitsTheClaimEnforcesAndTheVerdictThatPostsIt()
    {
        // The option is real before the doc is asked about it.
        var tell = typeof(TaskCommand.Settings).GetProperties()
            .SelectMany(p => p.GetCustomAttributesData())
            .Where(a => a.AttributeType.Name == "CommandOptionAttribute")
            .Select(a => (string)a.ConstructorArguments[0].Value!)
            .SingleOrDefault(t => t.Split(' ')[0].Split('|').Contains("--tell"));
        Assert.NotNull(tell);

        var row = Doc("docs", "cli.md").Split('\n').Single(l => l.StartsWith("| `task` |", StringComparison.Ordinal));
        Assert.Contains("`--tell \"<title> | <two to four sentences>\"`", row, StringComparison.Ordinal);
        Assert.Contains($"a title over {CardWords.MaxTitle} characters", row, StringComparison.Ordinal);
        Assert.Contains($"sentences over {CardWords.MaxLine}", row, StringComparison.Ordinal);
        Assert.Contains("when the verdict confirms the claim", row, StringComparison.Ordinal);
        Assert.Contains("gates go red posts nothing", row, StringComparison.Ordinal);

        // The battery a room's session reads teaches the same shape the doc does.
        var voice = new RoomVoiceBattery(new Room { Project = "docs", Chats = { Observer = "-1" } }, character: "fixture").Section;
        Assert.Contains("--tell \"<title> | <two to four sentences>\"", voice, StringComparison.Ordinal);
    }

    [Fact]
    public void NoShippedDocTellsASessionToPostACardWithReportPs1()
    {
        var root = RepoRoot();
        var offenders = new[] { "docs", "templates" }
            .Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.md", SearchOption.AllDirectories))
            .Append(Path.Combine(root, "README.md"))
            .Where(f => !f.Replace('\\', '/').Contains("/docs/dev/", StringComparison.Ordinal))   // the design record may quote the past
            .Where(f => File.Exists(f) && Regex.IsMatch(File.ReadAllText(f), @"\breport\.ps1\b", RegexOptions.None, TimeSpan.FromSeconds(5)))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();
        Assert.True(offenders.Count == 0, "report.ps1 is retired (PK4.3); these still name it: " + string.Join(", ", offenders));
    }
}
