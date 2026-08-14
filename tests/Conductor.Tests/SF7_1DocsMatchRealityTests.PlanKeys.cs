using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Tests;

/// <summary>
/// SF7.1 part 6 (KS3.3) — <c>docs/plan-config.md</c> is checked against <see cref="PlanConfig"/>'s own
/// declared shape. The page calls itself "the full schema"; it was missing nine keys the engine reads
/// and carrying one table for a key the engine had never read.
///
/// <para>The expectation is DERIVED, never restated: the key list comes from
/// <see cref="PlanKeySchema"/> walking the type graph, so a field added to a config class fails here
/// until the page that promises to document it does. A hand-typed list would be the exact rot this
/// suite exists to stop — it would have been written on the day the nine keys were already missing.</para>
///
/// <para><b>Scope, chosen deliberately.</b> Root keys, plus one level down through each root block
/// (a list is unwrapped to its element, so <c>stages[]</c>'s own keys count). Deeper than that is out:
/// <see cref="PlanKeySchema.SearchDepth"/> is 4 and an exhaustive walk would demand a row for
/// <c>limits.dnsHealthCheck.backoffMultiplier</c> and <c>pipeline.roles.&lt;role&gt;.agent.args</c> —
/// a test nobody could satisfy is a test that gets deleted. Dictionaries are skipped at the boundary
/// they introduce, because the next segment there is a name the author invents.</para>
/// </summary>
public sealed partial class SF7_1DocsMatchRealityTests
{
    [Fact]
    public void PlanConfigDocDocumentsEveryKeyThePlanSchemaDeclares()
    {
        var missing = UndocumentedPlanKeys(Doc("docs", "plan-config.md"));

        Assert.True(missing.Count == 0,
            $"docs/plan-config.md documents no `key` row or section for {missing.Count} settable " +
            $"path(s): {string.Join(", ", missing)}. Add a row (or a section named for the block), or " +
            "delete the key — a settable key with no documentation is one nobody can be told about.");
    }

    /// <summary>The pin proving the pin. A docs test that cannot fail is decoration, and the failure
    /// mode it must catch is a row quietly disappearing from the page — so delete one here and demand
    /// the derivation names exactly it. (The same demonstration was run against the real file: see
    /// the checkpoint's evidence.)</summary>
    [Fact]
    public void DeletingOneDocumentedRowMakesTheDerivationNameThatExactKey()
    {
        var doc = Doc("docs", "plan-config.md");
        Assert.Empty(UndocumentedPlanKeys(doc));

        foreach (var (row, expected) in new[]
                 {
                     ("| `stallGraceMinutes` |", "limits.stallGraceMinutes"),
                     ("| `standingOrders` |", "supervisor.standingOrders"),
                     ("| `verifyEachDelivery` |", "verifyEachDelivery"),
                 })
        {
            var stale = string.Join('\n', doc.Split('\n').Where(l => !l.TrimStart().StartsWith(row, StringComparison.Ordinal)));
            Assert.NotEqual(doc, stale);
            Assert.Equal([expected], UndocumentedPlanKeys(stale));
        }
    }

    /// <summary>Every settable path in scope that the page does not document, as
    /// <c>root</c> or <c>root.key</c>.</summary>
    internal static IReadOnlyList<string> UndocumentedPlanKeys(string doc)
    {
        var missing = new List<string>();

        foreach (var root in PlanKeySchema.KeysOf(typeof(PlanConfig)))
        {
            if (!HasRow(doc, root) && !HasHeading(doc, root)) missing.Add(root);

            var children = PlanKeySchema.KeysUnder(root);
            if (children.Count == 0) continue;   // a scalar, or a list of scalars, or a dictionary

            var section = SectionFor(doc, root);
            if (section is null)
            {
                missing.Add($"{root} (a block with {children.Count} key(s) and no section of its own)");
                continue;
            }
            missing.AddRange(children.Where(child => !HasRow(section, child)).Select(child => $"{root}.{child}"));
        }

        return missing;
    }

    /// <summary>A table row whose FIRST cell names the key. The first cell matters: `command` appears
    /// in half a dozen descriptions, and a page that merely mentions a word has not documented it.</summary>
    internal static bool HasRow(string text, string key)
        => text.Split('\n').Any(line =>
        {
            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith('|')) return false;
            var cells = trimmed.Split('|');
            return cells.Length > 1 && cells[1].Contains($"`{key}`", StringComparison.Ordinal);
        });

    /// <summary>A heading naming the key — how every block on the page is introduced
    /// (<c>## `limits`</c>, <c>## `stages[]`</c>).</summary>
    internal static bool HasHeading(string text, string key)
        => text.Split('\n').Any(line => line.StartsWith('#')
            && (line.Contains($"`{key}`", StringComparison.Ordinal) || line.Contains($"`{key}[]`", StringComparison.Ordinal)));

    /// <summary>A block's own section: its <c>##</c> heading down to the next one. Scoping matters for
    /// the same reason the first cell does — <c>timeoutMinutes</c> has a row in five different tables
    /// and each block owes its own.</summary>
    internal static string? SectionFor(string doc, string block)
    {
        var lines = doc.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("## ", StringComparison.Ordinal)
            && (l.Contains($"`{block}`", StringComparison.Ordinal) || l.Contains($"`{block}[]`", StringComparison.Ordinal)));
        if (start < 0) return null;

        var next = Array.FindIndex(lines, start + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        return string.Join('\n', lines[start..(next < 0 ? lines.Length : next)]);
    }
}
