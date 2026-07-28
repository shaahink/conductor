namespace Conductor.Tests;

/// <summary>P1 — the pure assignment policy. These tests exercise ONLY Conductor.Planning types:
/// rules + facts in, decision out. The engine-side proof (the resolved model actually reaching the
/// spawned agent process, the prompt naming every claimed item) lives in HarnessTests.</summary>
public sealed class DefaultAssignmentPolicyTests
{
    private readonly DefaultAssignmentPolicy _policy = new();

    private static List<ReadyItem> Items(params string[] ids) =>
        [.. ids.Select(id => new ReadyItem { Id = id, Title = $"title of {id}" })];

    [Fact]
    public void NoRules_ReproducesClassicBehavior()
    {
        var a = _policy.Assign(null, SessionKind.Deliver, Items("A", "B", "C"), claimedPaths: null);
        Assert.Null(a.Model);
        Assert.Null(a.Persona);
        Assert.Null(a.Command);
        var item = Assert.Single(a.Items); // one item —
        Assert.Equal("A", item.Id);        // — the FIRST not-done one
    }

    [Fact]
    public void RoleMap_AppliesToMatchingKindOnly()
    {
        var rules = new PipelineRules
        {
            Roles = new Dictionary<string, RoleAgentRule>(StringComparer.Ordinal)
            {
                ["audit"] = new() { Model = "strong-audit-model", Persona = "qa" },
            },
        };

        var audit = _policy.Assign(rules, SessionKind.Audit, Items("A"), claimedPaths: null);
        Assert.Equal("strong-audit-model", audit.Model);
        Assert.Equal("qa", audit.Persona);

        var deliver = _policy.Assign(rules, SessionKind.Deliver, Items("A"), claimedPaths: null);
        Assert.Null(deliver.Model); // deliver has no rule → stage/plan default
        Assert.Null(deliver.Persona);
    }

    [Fact]
    public void RoleKeys_AreCaseInsensitive()
    {
        var rules = new PipelineRules
        {
            Roles = new Dictionary<string, RoleAgentRule>(StringComparer.Ordinal)
            {
                ["Deliver"] = new() { Model = "m1" },
            },
        };
        Assert.Equal("m1", _policy.Assign(rules, SessionKind.Deliver, Items("A"), null).Model);
    }

    [Fact]
    public void Resume_NeverTakesARoleOverride()
    {
        // A resumed session must continue with the agent that owns the provider session —
        // swapping the model/command mid-resume would break the resume itself.
        var rules = new PipelineRules
        {
            Roles = new Dictionary<string, RoleAgentRule>(StringComparer.Ordinal)
            {
                ["deliver"] = new() { Model = "m1" },
                ["fix"] = new() { Model = "m2" },
            },
        };
        var a = _policy.Assign(rules, SessionKind.Resume, Items("A"), null);
        Assert.Null(a.Model);
        Assert.Null(a.Command);
    }

    [Fact]
    public void MultiItem_Disabled_ClaimsExactlyOne()
    {
        var rules = new PipelineRules { MultiItem = new MultiItemRule { Enabled = false, MaxItems = 5 } };
        Assert.Single(_policy.Assign(rules, SessionKind.Deliver, Items("A", "B", "C"), null).Items);
    }

    [Fact]
    public void MultiItem_Enabled_ClaimsUpToMaxItems()
    {
        var rules = new PipelineRules { MultiItem = new MultiItemRule { Enabled = true, MaxItems = 2 } };
        var a = _policy.Assign(rules, SessionKind.Deliver, Items("A", "B", "C"), null);
        Assert.Equal(["A", "B"], a.Items.Select(i => i.Id));
    }

    [Fact]
    public void MultiItem_IsDeliverOnly()
    {
        var rules = new PipelineRules { MultiItem = new MultiItemRule { Enabled = true, MaxItems = 3 } };
        Assert.Single(_policy.Assign(rules, SessionKind.Verify, Items("A", "B"), null).Items);
        Assert.Single(_policy.Assign(rules, SessionKind.Fix, Items("A", "B"), null).Items);
        Assert.Single(_policy.Assign(rules, SessionKind.Audit, Items("A", "B"), null).Items);
    }

    [Fact]
    public void MultiItem_PathConflictingClaim_IsRefused()
    {
        var rules = new PipelineRules { MultiItem = new MultiItemRule { Enabled = true, MaxItems = 3 } };
        var items = new List<ReadyItem>
        {
            new() { Id = "A", Title = "a", PathClaims = ["src/Engine/Loop.cs"] },
            // B overlaps A (same file, different separators/case — normalization must catch it)
            new() { Id = "B", Title = "b", PathClaims = [@"SRC\Engine\LOOP.CS"] },
            new() { Id = "C", Title = "c", PathClaims = ["docs/README.md"] },
        };
        var a = _policy.Assign(rules, SessionKind.Deliver, items, claimedPaths: null);
        Assert.Equal(["A", "C"], a.Items.Select(i => i.Id)); // B refused, C claimed
    }

    [Fact]
    public void MultiItem_ExternallyClaimedPaths_BlockExtraItems()
    {
        var rules = new PipelineRules { MultiItem = new MultiItemRule { Enabled = true, MaxItems = 3 } };
        var items = new List<ReadyItem>
        {
            new() { Id = "A", Title = "a" }, // active item: claimed unconditionally (classic behavior)
            new() { Id = "B", Title = "b", PathClaims = ["src/lane-owned.cs"] },
        };
        var a = _policy.Assign(rules, SessionKind.Deliver, items, claimedPaths: ["src/lane-owned.cs"]);
        var item = Assert.Single(a.Items);
        Assert.Equal("A", item.Id); // B refused — a running lane owns its path
    }

    [Fact]
    public void NoReadyItems_YieldsEmptyClaim()
    {
        Assert.Empty(_policy.Assign(null, SessionKind.Deliver, [], null).Items);
    }
}
