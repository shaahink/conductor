using System.Text.Json;
using System.Text.Json.Nodes;

using Conductor.Core.Planning;
using Conductor.Models;

namespace Conductor.Commands;

/// <summary>
/// KS3.3 — the check that reads the plan FILE and asks the one question no other surface asks: is
/// every key in it a key the engine reads?
///
/// <para>Nothing said so before. <see cref="PlanConfig"/> and <see cref="LimitsConfig"/> carry no
/// <c>[JsonExtensionData]</c> bucket, so a hand-edited <c>limits.maxRunCostUsdd</c> does not fail
/// deserialisation, does not fail validation, and does not appear anywhere: the plan loads, the run
/// starts uncapped, and every surface agrees the cap is set because the author can see it in the file.
/// <c>plan set</c> has refused undeclared keys since SC3.2 — but only for edits made THROUGH it, and
/// the file is a text file people open.</para>
///
/// <para>Warn, never fail. A key the engine does not read cannot break a run, and a plan carrying one
/// stale key must still be able to pass a clean doctor before a launch; the cost of this check being
/// louder than that is an operator learning to ignore a red line.</para>
/// </summary>
public sealed partial class DoctorCommand
{
    /// <summary>How many inert keys are named in the line before it is cut short. A file that has
    /// twenty is not read key-by-key from a terminal, it is read in an editor with the first few
    /// names in hand.</summary>
    private const int InertKeysNamed = 8;

    internal static async Task<Check> CheckInertKeysAsync(PlanConfig plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.PlanFilePath) || !File.Exists(plan.PlanFilePath))
            return new Check("plan-keys", "ok", "no plan file on disk to read — nothing to check");

        string raw;
        try { raw = await File.ReadAllTextAsync(plan.PlanFilePath).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Check("plan-keys", "warn", $"plan file could not be re-read ({ex.Message}) — inert keys unchecked");
        }

        IReadOnlyList<InertKey> inert;
        try { inert = InertKeysIn(raw); }
        catch (JsonException ex)
        {
            // The plan loaded, so this cannot normally happen; if it somehow does, the parse failure
            // is this check's problem and not a verdict on the plan.
            return new Check("plan-keys", "warn", $"plan file could not be re-parsed ({ex.Message}) — inert keys unchecked");
        }

        if (inert.Count == 0)
            return new Check("plan-keys", "ok", "every key in the plan file is one the engine reads");

        var named = inert.Take(InertKeysNamed).Select(k => k.Describe());
        var more = inert.Count > InertKeysNamed ? $", +{inert.Count - InertKeysNamed} more" : "";
        return new Check("plan-keys", "warn",
            $"{inert.Count} key(s) in the plan file are read by nothing: {string.Join("; ", named)}{more} — " +
            "fix the spelling or delete them; a key the plan does not declare looks like it landed and changes nothing");
    }

    /// <summary>An undeclared key and the best thing to say about it — the same "did you mean" data
    /// <c>plan set</c> refuses with, so the two surfaces agree on what a typo looks like.</summary>
    internal sealed record InertKey(string Path, string? Suggestion)
    {
        internal string Describe() => Suggestion is null ? Path : $"{Path} (did you mean {Suggestion}?)";
    }

    /// <summary>Every path in the raw plan document that <see cref="PlanKeySchema"/> does not declare,
    /// SHALLOWEST first: an unknown block is reported once, not once per leaf inside it.
    ///
    /// <para>Reads the raw text so that a key removed by the serialiser (nulls are omitted) cannot hide
    /// and a key the deserialiser silently dropped cannot either — that second one is the whole point.
    /// <c>//</c> comments are skipped by the parser, so comment text is never mistaken for a key, and
    /// author-named dictionary entries (<c>workflows.&lt;name&gt;</c>, <c>agent.env.&lt;VAR&gt;</c>,
    /// <c>pipeline.roles.&lt;role&gt;</c>) resolve through the schema's dictionary step and are never
    /// reported.</para></summary>
    internal static IReadOnlyList<InertKey> InertKeysIn(string planJson)
    {
        var doc = JsonNode.Parse(planJson,
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        var found = new List<InertKey>();
        Walk(doc, "");
        return found;

        void Walk(JsonNode? node, string prefix)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var (key, child) in obj)
                    {
                        var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                        var lookup = PlanKeySchema.Resolve(path);
                        if (!lookup.Known)
                        {
                            found.Add(new InertKey(path, Suggest(key, lookup)));
                            continue;   // shallowest wins: its children are unknown for the same reason
                        }
                        Walk(child, path);
                    }
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++) Walk(arr[i], $"{prefix}.{i}");
                    break;
            }
        }
    }

    private static string? Suggest(string segment, PlanKeySchema.KeyLookup lookup)
        => PlanKeySchema.NearMisses(segment, lookup.ParentKeys) is { Count: > 0 } near
            ? (lookup.ParentPath.Length == 0 ? near[0] : $"{lookup.ParentPath}.{near[0]}")
            : null;
}
