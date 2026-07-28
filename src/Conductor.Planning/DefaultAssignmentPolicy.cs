namespace Conductor.Planning;

/// <summary>The default assignment policy (P1). With no rules it reproduces the classic engine
/// behavior exactly: the stage/plan default agent works the first not-done item, one item per
/// session. Rules add two things, both declarative: a role→agent map (deliver/verify/audit/fix →
/// model/persona/command) and multi-item claims (extra conflict-free items, bounded by MaxItems).
/// Resume never takes a role override — a resumed session must continue with the agent that owns
/// the underlying provider session.</summary>
public sealed class DefaultAssignmentPolicy : IAssignmentPolicy
{
    public SessionAssignment Assign(PipelineRules? rules, SessionKind kind,
        IReadOnlyList<ReadyItem> readyItems, IReadOnlyCollection<string>? claimedPaths)
    {
        var rule = RuleFor(rules?.Roles, RoleFor(kind));
        return new SessionAssignment
        {
            Model = rule?.Model,
            Persona = rule?.Persona,
            Command = rule?.Command,
            Items = ClaimItems(rules?.MultiItem, kind, readyItems, claimedPaths),
        };
    }

    /// <summary>The rules vocabulary for a session kind. Resume maps to no role on purpose.</summary>
    public static string? RoleFor(SessionKind kind) => kind switch
    {
        SessionKind.Deliver => "deliver",
        SessionKind.Verify => "verify",
        SessionKind.Audit => "audit",
        SessionKind.Fix => "fix",
        _ => null,
    };

    private static RoleAgentRule? RuleFor(Dictionary<string, RoleAgentRule>? roles, string? role)
    {
        if (roles is null || role is null) return null;
        // Case-insensitive on purpose: the dictionary comes straight from user JSON whose comparer
        // is the serializer default (ordinal) — "Audit" and "audit" must mean the same rule.
        foreach (var (key, value) in roles)
        {
            if (string.Equals(key, role, StringComparison.OrdinalIgnoreCase))
                return value;
        }
        return null;
    }

    private static IReadOnlyList<ReadyItem> ClaimItems(MultiItemRule? multi, SessionKind kind,
        IReadOnlyList<ReadyItem> readyItems, IReadOnlyCollection<string>? claimedPaths)
    {
        if (readyItems.Count == 0) return [];

        // Classic behavior, always: the first ready item is the session's active item. (The engine
        // has never path-gated the active checkpoint; only EXTRA claims are conflict-checked.)
        var claimed = new List<ReadyItem> { readyItems[0] };

        // Extra items are deliver-only (verify/audit/fix operate on the stage's accumulated state,
        // not on a card set), explicitly opted into, and bounded.
        if (kind != SessionKind.Deliver || multi is not { Enabled: true } || multi.MaxItems <= 1)
            return claimed;

        var taken = new HashSet<string>(StringComparer.Ordinal);
        AddClaims(taken, readyItems[0].PathClaims);
        if (claimedPaths != null)
        {
            foreach (var path in claimedPaths) taken.Add(Normalize(path));
        }

        for (var i = 1; i < readyItems.Count && claimed.Count < multi.MaxItems; i++)
        {
            var item = readyItems[i];
            if (HasConflict(item.PathClaims, taken)) continue;
            claimed.Add(item);
            AddClaims(taken, item.PathClaims);
        }
        return claimed;
    }

    private static void AddClaims(HashSet<string> taken, IReadOnlyList<string>? claims)
    {
        if (claims is null) return;
        foreach (var path in claims) taken.Add(Normalize(path));
    }

    private static bool HasConflict(IReadOnlyList<string>? claims, HashSet<string> taken)
    {
        if (claims is null) return false; // no declared claims = no detectable conflict
        foreach (var path in claims)
        {
            if (taken.Contains(Normalize(path))) return true;
        }
        return false;
    }

    /// <summary>Same normalization the engine's PathClaimTracker applies, so a claim written either
    /// way ("src\\Foo.cs" / "src/foo.cs") collides correctly.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/').ToLowerInvariant();
}
