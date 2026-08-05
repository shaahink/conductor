using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Conductor.Models;

namespace Conductor.Core.Planning;

/// <summary>
/// SC3.2 — what keys a plan file is actually allowed to have, read off <see cref="PlanConfig"/>'s own
/// shape rather than a hand-kept list that would drift the first time a field is added.
///
/// <para><c>plan set</c> navigated the serialised JSON and assigned the leaf, and
/// <c>JsonObject</c>'s indexer creates what it cannot find — so <c>plan set limits.maxRunCostUsdd 100</c>
/// exited 0, printed a cheerful confirmation, and wrote a cost cap nothing in the engine reads. The
/// run stayed uncapped and every surface agreed the edit had landed.</para>
///
/// <para>The check cannot be "is the key present in the JSON": <see cref="PlanConfig.JsonOpts"/> omits
/// nulls, so an unset optional key like <c>limits.maxRunCostUsd</c> is absent from the round-tripped
/// document even though it is the single most documented edit anyone makes. Presence answers the wrong
/// question; the declared type graph answers the right one.</para>
/// </summary>
public static class PlanKeySchema
{
    /// <summary>How deep <see cref="FindPaths"/> will look for a bare key name. Everything an author
    /// sets by hand lives within four segments (<c>pipeline.roles.x.model</c> is the deep end).</summary>
    public const int SearchDepth = 4;

    /// <summary>The result of matching a dotted key against the plan's declared shape.
    /// <paramref name="Canonical"/> is the same path in the file's own casing — set through it and a
    /// key typed <c>Limits.MaxRunCostUsd</c> lands on the existing <c>limits.maxRunCostUsd</c> instead
    /// of beside it.</summary>
    public sealed record KeyLookup(
        bool Known,
        string UnknownSegment,
        string ParentPath,
        IReadOnlyList<string> ParentKeys,
        IReadOnlyList<string> Canonical);

    /// <summary>Does <see cref="PlanConfig"/> declare something at this dotted path? Array segments must
    /// be an index; dictionary segments (<c>workflows.&lt;name&gt;</c>) accept any key, because the author
    /// names those.</summary>
    public static KeyLookup Resolve(string dottedKey)
    {
        ArgumentNullException.ThrowIfNull(dottedKey);
        var parts = dottedKey.Split('.');
        var type = typeof(PlanConfig);
        var canonical = new List<string>(parts.Length);

        for (var i = 0; i < parts.Length; i++)
        {
            var next = Step(type, parts[i], out var canonicalSegment);
            if (next is null)
                return new KeyLookup(false, parts[i], string.Join('.', canonical), KeysOf(type), []);
            canonical.Add(canonicalSegment);
            type = next;
        }

        return new KeyLookup(true, "", "", [], canonical);
    }

    /// <summary>Every settable path whose LAST segment is <paramref name="leafName"/> — the data behind
    /// "did you mean limits.maxRunCostUsd?". Collection segments are enumerated from
    /// <paramref name="doc"/> (the plan's own JSON) rather than the schema, so every path offered is one
    /// that really exists in this file and can be pasted straight back into the command.</summary>
    public static IReadOnlyList<string> FindPaths(string leafName, JsonNode? doc, int maxDepth = SearchDepth)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(leafName)) return found;
        Walk(typeof(PlanConfig), doc, prefix: "", depth: 0);
        return found;

        void Walk(Type type, JsonNode? node, string prefix, int depth)
        {
            if (depth >= maxDepth) return;

            if (ElementTypeOf(type) is { } element)
            {
                // Only real indices/keys: suggesting gates.0.timeoutMinutes for an empty gates array
                // would just move the failure one command along.
                if (node is JsonArray arr)
                {
                    for (var i = 0; i < arr.Count; i++) Walk(element, arr[i], $"{prefix}{i}.", depth + 1);
                }
                else if (node is JsonObject dict && IsDictionary(type))
                {
                    foreach (var (key, child) in dict) Walk(element, child, $"{prefix}{key}.", depth + 1);
                }
                return;
            }

            foreach (var prop in JsonProperties(type))
            {
                var name = JsonNameOf(prop);
                if (name.Equals(leafName, StringComparison.OrdinalIgnoreCase)) found.Add(prefix + name);
                Walk(Unwrap(prop.PropertyType), node?[name], prefix + name + ".", depth + 1);
            }
        }
    }

    /// <summary>Declared keys of the object that owns <paramref name="wanted"/>'s slot, ordered by how
    /// close they are to it — a one-character typo should be named, not left to a re-read of the docs.
    /// Only genuinely near misses come back (edit distance within a third of the name's length).</summary>
    public static IReadOnlyList<string> NearMisses(string wanted, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(candidates);
        var budget = Math.Max(1, wanted.Length / 3);
        return candidates
            .Select(c => (Name: c, Distance: Distance(wanted, c)))
            .Where(x => x.Distance <= budget)
            .OrderBy(x => x.Distance).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Name)
            .ToList();
    }

    /// <summary>True when the plan declares an OBJECT at this path — the only shape <c>plan set</c> may
    /// create on its way to a leaf. <c>telegram.allowedChatIds</c> on a plan with no <c>telegram</c>
    /// block is a documented edit, and the block being absent from the file is not the author's typo;
    /// a missing array element is a different thing and stays refused.</summary>
    public static bool IsObjectAt(string dottedPath)
    {
        ArgumentNullException.ThrowIfNull(dottedPath);
        var type = typeof(PlanConfig);
        foreach (var segment in dottedPath.Split('.'))
        {
            var next = Step(type, segment, out _);
            if (next is null) return false;
            type = next;
        }
        return KeysOf(type).Count > 0;
    }

    /// <summary>The declared keys of a type, as they are spelled in the file. Empty for a scalar (which
    /// is why a path that continues past one is refused) and for a collection.</summary>
    public static IReadOnlyList<string> KeysOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return ElementTypeOf(type) is not null ? [] : JsonProperties(type).Select(JsonNameOf).ToList();
    }

    /// <summary>One segment of the walk: the type reached by following <paramref name="segment"/>, or
    /// null when this type declares no such thing.</summary>
    private static Type? Step(Type type, string segment, out string canonical)
    {
        canonical = segment;
        if (segment.Length == 0) return null;

        if (ElementTypeOf(type) is { } element)
        {
            if (IsDictionary(type)) return element;                                   // author-named key
            return int.TryParse(segment, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var i) && i >= 0 ? element : null;
        }

        foreach (var prop in JsonProperties(type))
        {
            var name = JsonNameOf(prop);
            if (!name.Equals(segment, StringComparison.OrdinalIgnoreCase)) continue;
            canonical = name;
            return Unwrap(prop.PropertyType);
        }
        return null;
    }

    private static IEnumerable<PropertyInfo> JsonProperties(Type type)
    {
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type.IsEnum)
            return [];
        return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetCustomAttribute<JsonIgnoreAttribute>() is null);
    }

    private static string JsonNameOf(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
        ?? System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(prop.Name);

    private static Type Unwrap(Type type) => Nullable.GetUnderlyingType(type) ?? type;

    private static bool IsDictionary(Type type) =>
        type.IsGenericType && typeof(IDictionary).IsAssignableFrom(type);

    /// <summary>The element type of a list or dictionary, or null for anything else. Strings are
    /// deliberately not collections here — they are leaves.</summary>
    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string)) return null;
        if (type.IsArray) return type.GetElementType();
        if (!type.IsGenericType) return null;
        var args = type.GetGenericArguments();
        if (IsDictionary(type)) return args.Length == 2 ? Unwrap(args[1]) : null;
        return typeof(IEnumerable).IsAssignableFrom(type) && args.Length == 1 ? Unwrap(args[0]) : null;
    }

    /// <summary>Levenshtein distance, iterative two-row. Names here are short; this is not a hot path.</summary>
    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
