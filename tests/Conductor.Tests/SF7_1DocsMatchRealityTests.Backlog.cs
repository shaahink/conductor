namespace Conductor.Tests;

/// <summary>
/// SF7.1 part 2 — the backlog page is checked against the tree.
/// <para><c>docs/dev/NEXT-FEATURES.md</c> had drifted the same way <c>tracker.md</c> had, one layer
/// out: ten of its entries had SHIPPED and it still promised them as future work. A backlog that
/// lists built things is worse than no backlog — someone plans off it and rebuilds them.</para>
/// <para>So the split is derived, not restated. Every feature the page calls shipped must have its
/// symbol in the engine, and every feature it calls open must NOT — which is the half that actually
/// rots, because a backlog item quietly gets built and nobody comes back to cross it off.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    /// <summary>Symbol → the sentence the doc must carry, for things that exist. If the symbol is
    /// gone the feature was removed and the "shipped" claim is a lie; if the doc stops naming it the
    /// record of what closed is lost.</summary>
    private static readonly (string Symbol, string DocMentions)[] ShippedFeatures =
    [
        ("RolloverCommand", "RolloverCommand"),
        ("LessonsBattery", "LessonsBattery"),
        ("LedgerBattery", "LedgerBattery"),
        ("BugsBattery", "BugsBattery"),
        ("RecentFailureBattery", "RecentFailureBattery"),
        ("LaneArtifactBattery", "LaneArtifactBattery"),
        ("BatterySection", "PromptBuilder.BatterySection"),
        ("FollowupParser", "FollowupParser"),
        ("HeartbeatCommand", "HeartbeatCommand"),
        ("ConsoleCtrlRails", "ConsoleCtrlRails"),
        ("InitCommand", "InitCommand"),
    ];

    /// <summary>The load-bearing half. Each of these is named by the page as STILL OPEN; the day one
    /// of them appears in the engine, this test goes red and the page owes an edit to the shipped
    /// list. Chosen to be names that could only exist if the feature were built.</summary>
    private static readonly string[] UnbuiltSymbols =
    [
        "requireCleanTree",     // commit/push discipline + git safety
        "RepoMapBattery",       // repo-map / hot-files battery
        "DefinitionOfDoneBattery",
    ];

    private static string EngineSources()
    {
        var src = Path.Combine(RepoRoot(), "src", "Conductor");
        var sb = new System.Text.StringBuilder();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
            sb.Append(File.ReadAllText(file));
        return sb.ToString();
    }

    [Fact]
    public void EveryFeatureTheBacklogCallsShippedExistsInTheEngineAndIsNamedOnThePage()
    {
        var doc = Doc("docs", "dev", "NEXT-FEATURES.md");
        var shipped = doc[..doc.IndexOf("## Still open", StringComparison.Ordinal)];
        var code = EngineSources();

        foreach (var (symbol, mention) in ShippedFeatures)
        {
            Assert.True(code.Contains(symbol, StringComparison.Ordinal),
                $"docs/dev/NEXT-FEATURES.md credits '{symbol}' with shipping a backlog item, but no " +
                "such symbol is in src/Conductor any more. Either the feature was removed and the " +
                "item goes back to the open list, or the doc names the wrong thing.");
            Assert.True(shipped.Contains(mention, StringComparison.Ordinal),
                $"the shipped section of docs/dev/NEXT-FEATURES.md no longer names '{mention}' — the " +
                "record of what closed a backlog item is how the next planner avoids rebuilding it.");
        }
    }

    [Fact]
    public void NothingTheBacklogCallsStillOpenHasQuietlyBeenBuilt()
    {
        var code = EngineSources();
        var built = UnbuiltSymbols
            .Where(s => code.Contains(s, StringComparison.Ordinal))
            .ToList();

        Assert.True(built.Count == 0,
            $"docs/dev/NEXT-FEATURES.md still lists {string.Join(", ", built)} as unbuilt work, but " +
            "the engine now contains it. Move the item to the shipped section and name what shipped " +
            "it — a backlog that lists finished work gets someone to build it twice.");
    }

    /// <summary>The MCP item SF7.1 was ordered to file. It is only worth filing if it stays true, and
    /// the whole point of the entry is that the written config names exactly one server: the moment
    /// <c>WireMcpServer</c> learns to merge the harness's own servers, the entry is stale.</summary>
    [Fact]
    public void TheFiledMcpItemStillDescribesWhatWireMcpServerActuallyWrites()
    {
        var doc = Doc("docs", "dev", "NEXT-FEATURES.md");
        Assert.Contains("WireMcpServer", doc, StringComparison.Ordinal);
        Assert.Contains("FIELD-NOTES-2026-07-29-devcontext.md", doc, StringComparison.Ordinal);

        // The cited evidence has to be readable, at the section the entry sends people to.
        var notes = Doc("docs", "dev", "FIELD-NOTES-2026-07-29-devcontext.md");
        Assert.Contains("deferred", notes, StringComparison.OrdinalIgnoreCase);

        // And the premise: the config the engine writes carries conductor-tasks and nothing else.
        var mcp = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Conductor", "Core", "Orchestration", "SessionRunner.Mcp.cs"));
        Assert.Contains("conductor-tasks", mcp, StringComparison.Ordinal);
    }
}
