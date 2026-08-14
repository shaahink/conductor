using System.Globalization;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// KS3.3 — schema honesty. Three failures with one shape: a plan key that reads as a working setting
/// and is not one.
///
/// <list type="number">
/// <item>Nine keys the engine really reads had no line anywhere in <c>docs/plan-config.md</c> — the
/// page that calls itself the full schema. An author cannot set what nothing tells them about.</item>
/// <item><c>mutatingLanes[]</c> was the mirror image: documented with a seven-field table, declared on
/// <see cref="PlanConfig"/>, round-tripped by every editor, and read by no code path in <c>src/</c>.
/// The property is deleted; Tier B itself is untouched.</item>
/// <item>Neither <see cref="PlanConfig"/> nor <see cref="LimitsConfig"/> carries a
/// <c>[JsonExtensionData]</c> bucket, so a hand-edited <c>limits.maxRunCostUsdd</c> loads silently and
/// runs uncapped. <c>doctor</c> now warns, naming it.</item>
/// </list>
///
/// <para>Defaults are read off the config objects here, never typed in: a default changed in code
/// without the doc changing with it is exactly the drift this checkpoint exists to end.</para>
/// </summary>
public sealed class KS3_3SchemaHonestyTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string PlanConfigDoc() => File.ReadAllText(Path.Combine(RepoRoot(), "docs", "plan-config.md"));

    // ───────────────────────────────── 1. the nine keys, with the defaults the code has

    /// <summary>The five <c>limits</c> keys, each with its Default cell read off
    /// <see cref="LimitsConfig"/>. Change <c>verifierThreshold</c> to 75 in code and this goes red
    /// until the table says 75.</summary>
    [Fact]
    public void TheFiveUndocumentedLimitsKeysHaveARowStatingTheDefaultTheCodeHas()
    {
        var doc = PlanConfigDoc();
        var d = new LimitsConfig();

        foreach (var (key, expected) in new (string, string)[]
                 {
                     ("maxSessions", Format(d.MaxSessions)),
                     ("stallGraceMinutes", Format(d.StallGraceMinutes)),
                     ("authPreflight", Format(d.AuthPreflight)),
                     ("sameFailureCircuitBreaker", Format(d.SameFailureCircuitBreaker)),
                     ("verifierThreshold", Format(d.VerifierThreshold)),
                 })
        {
            var cell = DefaultCell(doc, "limits", key);
            Assert.True(cell is not null, $"docs/plan-config.md has no `limits.{key}` row — the engine reads the key regardless");
            Assert.Equal(expected, cell);
        }
    }

    /// <summary>The <c>supervisor</c> block: the whole thing was undocumented on this page, so the
    /// assertion is per field, and the three numbers come from <see cref="SupervisorConfig"/>.</summary>
    [Fact]
    public void TheSupervisorBlockIsDocumentedWithTheDefaultsSupervisorConfigHas()
    {
        var doc = PlanConfigDoc();
        var d = new SupervisorConfig();

        Assert.Contains("## `supervisor`", doc, StringComparison.Ordinal);
        Assert.Equal(Format(d.Enabled), DefaultCell(doc, "supervisor", "enabled"));
        Assert.Equal(Format(d.TimeoutMinutes), DefaultCell(doc, "supervisor", "timeoutMinutes"));
        Assert.Equal(Format(d.MaxPerHour), DefaultCell(doc, "supervisor", "maxPerHour"));

        foreach (var field in PlanKeySchema.KeysUnder("supervisor"))
            Assert.Contains($"| `{field}` |", doc, StringComparison.Ordinal);
        foreach (var field in PlanKeySchema.KeysUnder("supervisor.remote"))
            Assert.Contains($"| `{field}` |", doc, StringComparison.Ordinal);

        // The block is real, not decoration: the run's own watch reads it.
        Assert.True(PlanKeySchema.Resolve("supervisor.standingOrders").Known);
        Assert.True(PlanKeySchema.Resolve("supervisor.remote.webhookUrl").Known);
    }

    /// <summary><c>verifyEachDelivery</c>, <c>packs</c> and <c>pipeline</c> — the three root keys that
    /// are not numbers. Each is asserted on the thing that would mislead an author if the page got it
    /// wrong: the default, where a pack file is looked for (and what it costs), and the precedence.</summary>
    [Fact]
    public void TheThreeUndocumentedRootKeysSayTheThingAnAuthorWouldOtherwiseGetWrong()
    {
        var doc = PlanConfigDoc();

        Assert.Contains($"| `verifyEachDelivery` | bool | Default {Format(new PlanConfig().VerifyEachDelivery)}.", doc, StringComparison.Ordinal);
        Assert.Contains("lowest-precedence", doc, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("## `packs`", doc, StringComparison.Ordinal);
        Assert.Contains("<templatesDir>/packs/<name>.md", doc, StringComparison.Ordinal);
        // bugs 15/21: packs are argv, and argv has a ceiling on Windows. The doc must not sell them free.
        Assert.Contains(DoctorCommand.CmdExeCommandLineCeiling.ToString(CultureInfo.InvariantCulture), doc, StringComparison.Ordinal);

        Assert.Contains("## `pipeline`", doc, StringComparison.Ordinal);
        foreach (var field in PlanKeySchema.KeysUnder("pipeline"))
            Assert.Contains($"| `{field}` |", doc, StringComparison.Ordinal);
    }

    // ───────────────────────────────── 2. mutatingLanes: removed, not renamed

    /// <summary>The removal, from all four directions it could come back through: the schema, the
    /// engine sources, the shipped plans, and the page that used to document it.</summary>
    [Fact]
    public void TheInertMutatingLanesBlockIsGoneFromSchemaSourcesPlansAndDocs()
    {
        Assert.False(PlanKeySchema.Resolve("mutatingLanes").Known);

        var doc = PlanConfigDoc();
        Assert.DoesNotContain("| `mutatingLanes` |", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("## `mutatingLanes[]`", doc, StringComparison.Ordinal);

        foreach (var plan in Directory.GetFiles(Path.Combine(RepoRoot(), "plans"), "*.plan.json", SearchOption.AllDirectories))
            Assert.DoesNotContain("\"mutatingLanes\"", File.ReadAllText(plan), StringComparison.Ordinal);

        // …and the Tier B machinery it never drove is still here, which is the point of removing only
        // the plan property.
        Assert.Equal(30, new MutatingLaneConfig().TimeoutMinutes);
    }

    // ───────────────────────────────── 3. no settable root property is read by nothing

    /// <summary>The companion assertion, and the general form of the bug: a property a plan can set
    /// that no code outside its own declaration ever reads. <c>MutatingLanes</c> was the only one; the
    /// day another appears, this names it before it grows a doc table and a plan file entry.
    /// <para>The probe is the one that found it — a member-access sweep of <c>src/</c>. Coarse on
    /// purpose in the safe direction: an unrelated <c>.Name</c> on another type counts as a read, so
    /// this under-reports rather than blocking a legitimate property on a coincidence.</para></summary>
    [Fact]
    public void NoSettablePlanRootPropertyIsReadByNothing()
    {
        var sources = EngineSourcesOutsideThePlanDeclaration();

        var inert = typeof(PlanConfig).GetProperties()
            .Where(p => p.CanRead && p.CanWrite
                && p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), inherit: false).Length == 0)
            .Select(p => p.Name)
            .Where(name => !sources.Any(text => ReadsMember(text, name)))
            .ToList();

        Assert.True(inert.Count == 0,
            $"PlanConfig declares {inert.Count} settable propert(y/ies) nothing in src/ reads: " +
            $"{string.Join(", ", inert)}. A plan can set them, every editor round-trips them, and they " +
            "do nothing — wire them or delete them (KS3.3 deleted MutatingLanes for exactly this).");
    }

    /// <summary>…and the probe itself is proved on a seeded name. <c>MutatingLanes</c> is the seed: a
    /// property name that WAS declared here and is now read by nothing anywhere, so a detector that
    /// cannot see it would pass the fact above no matter what got added to the type.</summary>
    [Fact]
    public void TheInertPropertyProbeReportsASeededUnreadNameAndNotAReadOne()
    {
        var sources = EngineSourcesOutsideThePlanDeclaration();

        Assert.DoesNotContain(sources, text => ReadsMember(text, "MutatingLanes"));
        Assert.Contains(sources, text => ReadsMember(text, "AnalysisLanes"));
        Assert.Contains(sources, text => ReadsMember(text, "Limits"));

        // A prefix is not a read: `.AnalysisLanes` must not answer for a property called `AnalysisLane`.
        Assert.True(ReadsMember("plan.AnalysisLanes.Count", "AnalysisLanes"));
        Assert.False(ReadsMember("plan.AnalysisLanes.Count", "AnalysisLane"));
    }

    private static IReadOnlyList<string> EngineSourcesOutsideThePlanDeclaration()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var declaration = Path.Combine("Models", "PlanConfig.cs");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.EndsWith(declaration, StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();
    }

    /// <summary><c>.Name</c> as a whole member access — the grep that found the bug, with the word
    /// boundary a grep needed to be told about.</summary>
    private static bool ReadsMember(string text, string member)
    {
        var needle = "." + member;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            var after = i + needle.Length;
            if (after >= text.Length) return true;
            var c = text[after];
            if (!char.IsLetterOrDigit(c) && c != '_') return true;
        }
        return false;
    }

    // ───────────────────────────────── 4. doctor warns on an inert key in the file

    /// <summary>The field case, exactly: <c>maxRunCostUsdd</c>. It deserialises to nothing, validates
    /// clean, and leaves the run uncapped while the author can see the cap in the file.</summary>
    [Fact]
    public void DoctorNamesATypodLimitsKeyThatTheLoaderSilentlyDrops()
    {
        var inert = DoctorCommand.InertKeysIn("""
        {
          "name": "t", "repo": ".", "tracker": "t.md",
          "limits": { "maxRunCostUsdd": 100 },
          "stages": [ { "id": "S1", "title": "s", "sessions": 1 } ]
        }
        """);

        var one = Assert.Single(inert);
        Assert.Equal("limits.maxRunCostUsdd", one.Path);
        Assert.Equal("limits.maxRunCostUsd", one.Suggestion);
        Assert.Contains("did you mean", one.Describe(), StringComparison.Ordinal);
    }

    /// <summary>A key removed from the schema is reported as what it is now — inert — rather than
    /// vanishing from every surface the way it did while it was declared.</summary>
    [Fact]
    public void DoctorNamesTheRemovedMutatingLanesBlockLeftInAnOldPlanFile()
    {
        var inert = DoctorCommand.InertKeysIn("""
        {
          "name": "t", "repo": ".", "tracker": "t.md",
          "analysisLanes": [],
          "mutatingLanes": [ { "id": "l", "kind": "fix", "prompt": "p" } ],
          "stages": [ { "id": "S1", "title": "s", "sessions": 1 } ]
        }
        """);

        // Shallowest wins: the block is named once, not once per field inside it.
        var one = Assert.Single(inert);
        Assert.Equal("mutatingLanes", one.Path);
    }

    /// <summary>The three things it must NEVER warn about: <c>//</c> comment text (the scaffold is
    /// full of it), author-named dictionary keys, and a plain unset optional key.</summary>
    [Fact]
    public void DoctorIsSilentOnCommentsAuthorNamedKeysAndAWholeCleanPlan()
    {
        var inert = DoctorCommand.InertKeysIn("""
        {
          // limits.maxRunCostUsdd is a typo, and this line is a comment about it, not a key.
          "name": "t", "repo": ".", "tracker": "t.md",
          "agent": { "command": "claude", "args": ["-p", "{prompt}"], "env": { "MY_TOKEN": "x" } },
          "workflows": { "my-flow": { "name": "my-flow", "steps": [ { "id": "d", "kind": "deliver" } ] } },
          "pipeline": { "roles": { "deliver": { "model": "m" } } },
          "limits": { "maxRunCostUsd": 5 },
          "stages": [ { "id": "S1", "title": "s", "sessions": 1, "qa": { "mode": "off" } } ]
        }
        """);

        Assert.Empty(inert);
    }

    /// <summary>Warn, never fail — and the check survives a plan with no file behind it, which is how
    /// most of this suite builds one.</summary>
    [Fact]
    public async Task TheCheckIsWarnLevelAndNeverFailsTheDoctorRun()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"conductor-ks33-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "t.plan.json");
            await File.WriteAllTextAsync(path, """
            {
              "name": "t", "repo": ".", "tracker": "t.md",
              "agent": { "command": "claude", "args": ["-p", "{prompt}"] },
              "limits": { "maxRunCostUsdd": 100 },
              "stages": [ { "id": "S1", "title": "s", "sessions": 1 } ]
            }
            """);

            var withFile = new PlanConfig { Name = "t", Repo = dir, Tracker = "t.md", PlanFilePath = path };

            var check = await DoctorCommand.CheckInertKeysAsync(withFile);
            Assert.Equal("warn", check.State);
            Assert.Contains("limits.maxRunCostUsdd", check.Message, StringComparison.Ordinal);

            var noFile = await DoctorCommand.CheckInertKeysAsync(new PlanConfig { Name = "t", Repo = dir, Tracker = "t.md" });
            Assert.Equal("ok", noFile.State);
        }
        finally
        {
            try { TestTemp.DeleteTree(dir); } catch (IOException) { /* temp dir */ }
        }
    }

    /// <summary>Every shipped plan is clean under the new check — a lint whose first act is to warn
    /// about the repo's own plans is a lint nobody keeps.</summary>
    [Fact]
    public void EveryShippedPlanFileIsFreeOfInertKeys()
    {
        var dirty = new List<string>();
        foreach (var plan in Directory.GetFiles(Path.Combine(RepoRoot(), "plans"), "*.plan.json", SearchOption.AllDirectories))
        {
            var inert = DoctorCommand.InertKeysIn(File.ReadAllText(plan));
            if (inert.Count > 0) dirty.Add($"{Path.GetFileName(plan)}: {string.Join(", ", inert.Select(k => k.Path))}");
        }

        Assert.True(dirty.Count == 0, "shipped plans carry keys the engine reads by nothing:\n  " + string.Join("\n  ", dirty));
    }

    /// <summary>The extension bucket is not a key. <c>advisor.unknownFields</c> is where keys the type
    /// does NOT declare are parked so that load can refuse them by name — resolving it as settable made
    /// the trap's own evidence locker part of the schema.</summary>
    [Fact]
    public void TheExtensionBucketIsNotASettableKey()
    {
        Assert.False(PlanKeySchema.Resolve("advisor.unknownFields").Known);
        Assert.DoesNotContain("unknownFields", PlanKeySchema.KeysUnder("advisor"), StringComparer.Ordinal);
        Assert.Contains("remediationScript", PlanKeySchema.KeysUnder("advisor"), StringComparer.Ordinal);

        // It still does its real job: an undeclared advisor key lands in the bucket and load refuses it.
        var plan = JsonSerializer.Deserialize<PlanConfig>("""
        {
          "name": "t", "repo": ".", "tracker": "t.md",
          "agent": { "command": "claude", "args": ["-p", "{prompt}"] },
          "advisor": { "provider": "claude" },
          "stages": [ { "id": "S1", "title": "s", "sessions": 1 } ]
        }
        """, PlanConfig.JsonOpts)!;
        Assert.Contains(plan.CollectErrors(), e => e.Contains("plan.advisor.provider", StringComparison.Ordinal));
    }

    // ───────────────────────────────── helpers

    /// <summary>The Default column of the row naming <paramref name="key"/> INSIDE
    /// <paramref name="block"/>'s own section, or null when there is no such row. Tables on this page
    /// are <c>| Field | Type | Default | Description |</c>, and the scoping is not optional:
    /// <c>enabled</c> has a row in six of them.</summary>
    private static string? DefaultCell(string doc, string block, string key)
    {
        var section = SF7_1DocsMatchRealityTests.SectionFor(doc, block);
        if (section is null) return null;
        foreach (var line in section.Split('\n'))
        {
            var cells = line.TrimStart().Split('|');
            if (cells.Length < 5 || !cells[1].Contains($"`{key}`", StringComparison.Ordinal)) continue;
            return cells[3].Trim();
        }
        return null;
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
