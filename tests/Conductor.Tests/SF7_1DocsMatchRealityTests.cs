using System.Text.RegularExpressions;
using Conductor.Core;
using Conductor.Core.Watch;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SF7.1 — the shipped docs are checked against the CODE, not against themselves. This era exists
/// because prose outlived the thing it described: a plan-set comment, an advisor default, a hosted
/// service registration. <c>docs/tracker.md</c> was the same story one layer out — it documented a
/// <c>.conductor/</c> tree in which five entries (<c>events.jsonl</c>, <c>state.json</c>,
/// <c>queue/</c>, <c>lanes/</c>, <c>audits/</c>) were absent after 36 real sessions, and omitted
/// fourteen artifacts the engine writes every run.
/// <para>These tests DERIVE the expectation from the source instead of restating it, so the next
/// drift is a red test rather than a re-read: a new artifact under <c>plan.StateDir</c>, a changed
/// <see cref="AdvisorConfig"/> default, or a new <see cref="WatchReason"/> all fail here until the
/// doc that promises them is updated.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Doc(params string[] parts)
        => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    /// <summary>Every <c>Path.Combine(plan.StateDir, "name")</c> in the engine — the one syntax that
    /// creates something inside a run's state dir.</summary>
    [GeneratedRegex("""StateDir,\s*"(?<name>[^"]+)"\s*[,)]""", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex StateDirChild();

    private static IReadOnlyList<string> RuntimeArtifactsTheEngineNames()
    {
        var src = Path.Combine(RepoRoot(), "src", "Conductor");
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
            foreach (Match m in StateDirChild().Matches(File.ReadAllText(file)))
            {
                var name = m.Groups["name"].Value;
                if (name is "..") continue;   // the repo root, not an artifact
                names.Add(name);
            }

        // Two more the engine keeps behind a public const rather than an inline literal.
        names.Add(EngineLock.FileName);
        names.Add(SupervisorPolicy.FiresFile);
        return [.. names];
    }

    /// <summary>The runtime-files section of <c>docs/tracker.md</c> must NAME everything the engine
    /// can drop into <c>.conductor/</c>. An operator who finds a file there and cannot find it in the
    /// docs has no way to tell an artifact from debris.</summary>
    [Fact]
    public void TrackerDocNamesEveryRuntimeArtifactTheEngineCanWrite()
    {
        // Only the runtime-files block counts. Searching the whole page would let a name that happens
        // to appear in an unrelated example ("docs/evidence/L0.1-test.md") pass for documentation.
        var doc = Doc("docs", "tracker.md");
        var section = doc[doc.IndexOf("## Runtime files", StringComparison.Ordinal)..];
        var open = section.IndexOf("```", StringComparison.Ordinal);
        var close = section.IndexOf("```", open + 3, StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, "docs/tracker.md has no fenced tree under '## Runtime files'");
        var block = section[(open + 3)..close];

        var missing = RuntimeArtifactsTheEngineNames()
            .Where(n => !block.Contains(n, StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            $"docs/tracker.md does not mention {missing.Count} artifact(s) the engine writes under " +
            $"plan.StateDir: {string.Join(", ", missing)}. Add them to the runtime-files block, or " +
            "stop writing them.");
    }

    /// <summary>The converse, for the two carriers that outlived their writers. Nothing constructs an
    /// <c>EventLog</c> any more — the event spine is the <c>events</c> table in <c>run.db</c>
    /// (<c>SqliteRunStore</c> is the registered <c>IEventSink</c>) — and the live run loop never
    /// writes <c>state.json</c>. Both were still documented as live products, so a doc reader looked
    /// for a file 36 sessions had not produced. If a writer is ever restored, this test fails and the
    /// doc gets its paragraph back.</summary>
    [Fact]
    public void TheEventLogAndStateJsonCarriersAreDocumentedAsLegacyBecauseNothingWritesThem()
    {
        var src = Path.Combine(RepoRoot(), "src", "Conductor");
        var constructions = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(Path.Combine("Events", "EventLog.cs"), StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("new EventLog(", StringComparison.Ordinal))
            .ToList();
        Assert.True(constructions.Count == 0,
            "something constructs an EventLog again — .conductor/events.jsonl has a writer once more, " +
            $"so docs/tracker.md must stop calling it legacy: {string.Join(", ", constructions)}");

        var doc = Doc("docs", "tracker.md");
        foreach (var carrier in new[] { "events.jsonl", "state.json" })
        {
            var line = doc.Split('\n').FirstOrDefault(l => l.Contains(carrier, StringComparison.Ordinal));
            Assert.NotNull(line);
            Assert.Contains("LEGACY", line!, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>`plan-config.md` states the advisor defaults as numbers and strings. They are read off
    /// <see cref="AdvisorConfig"/> here, so changing a default in code without touching the doc is a
    /// red test — the exact failure mode SC3.4 fixed once already (the shipped default used to be an
    /// argless invocation the doc never admitted to).</summary>
    [Fact]
    public void PlanConfigDocStatesTheAdvisorDefaultsAdvisorConfigActuallyHas()
    {
        var doc = Doc("docs", "plan-config.md");
        var d = new AdvisorConfig();

        Assert.Contains($"Default `\"{d.Command}\"`", doc, StringComparison.Ordinal);
        Assert.Contains($"Default `[{string.Join(", ", AdvisorConfig.DefaultArgs.Select(a => $"\"{a}\""))}]`",
            doc, StringComparison.Ordinal);
        Assert.Contains($"Default {d.TimeoutMinutes}.", doc, StringComparison.Ordinal);
        Assert.Contains($"`\"{d.Output}\"` (raw stdout, the default)", doc, StringComparison.Ordinal);

        // Every key the engine reads has a row; every kind it unwraps is named.
        foreach (var field in AdvisorConfig.KnownFields)
            Assert.Contains($"| `{field}` |", doc, StringComparison.Ordinal);
        foreach (var kind in AdvisorConfig.OutputKinds)
            Assert.Contains($"`\"{kind}\"`", doc, StringComparison.Ordinal);

        // The key five shipped plans set and nothing reads. The doc says so; keep it true.
        Assert.DoesNotContain("provider", AdvisorConfig.KnownFields, StringComparer.Ordinal);
    }

    /// <summary>`operating.md`'s wake table is the operator's whole contract with `conductor watch`.
    /// A <see cref="WatchReason"/> the engine can return and the table does not list is a night call
    /// nobody was told to expect.</summary>
    [Fact]
    public void OperatingDocWakeTableNamesEveryWatchReasonTheEngineCanReturn()
    {
        var doc = Doc("docs", "operating.md");
        // The doc speaks the wire's kebab-case vocabulary, not the enum's.
        var spelling = new Dictionary<WatchReason, string>
        {
            [WatchReason.NeedsHuman] = "needs-human",
            [WatchReason.OwnerPark] = "owner-gate",
            [WatchReason.CircuitBreaker] = "circuit-breaker",
            [WatchReason.PhaseRedTwice] = "phase-red-twice",
            [WatchReason.EngineGone] = "engine-gone",
            [WatchReason.RunEnded] = "run-ended",
            [WatchReason.Timeout] = "reason=timeout",
        };

        foreach (var reason in Enum.GetValues<WatchReason>())
        {
            Assert.True(spelling.ContainsKey(reason),
                $"WatchReason.{reason} is new — give it a wake row in docs/operating.md and a spelling here");
            Assert.Contains(spelling[reason], doc, StringComparison.Ordinal);
        }
    }
}
