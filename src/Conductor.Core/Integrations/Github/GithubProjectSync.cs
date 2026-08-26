using System.Globalization;
using System.Text.Json;

namespace Conductor.Core.Integrations.Github;

/// <summary>One card's place on the project board: which issue, and which status it should carry.</summary>
/// <param name="Key">The board key — a task id, or <c>bug:N</c> / <c>followup:N</c> for the ledger
/// half. Identity for reporting only; the board itself is keyed by the issue's node id.</param>
/// <param name="NodeId">The issue's GraphQL global id. Projects v2 adds an item by CONTENT id, not
/// by issue number, and the number is carried alongside only so a pass can recognise its own items
/// in the board's listing.</param>
public sealed record GithubProjectPlacement(string Key, int IssueNumber, string NodeId, string Status);

/// <summary>DV6.2 — what one project pass DID, in the numbers the idempotence bar is stated in.</summary>
public sealed class GithubProjectPass
{
    /// <summary>Cards this pass put on the board for the first time.</summary>
    public List<string> Added { get; } = [];

    /// <summary>Cards whose column this pass set or changed — the actual movement a Kanban is for.</summary>
    public List<string> Moved { get; } = [];

    /// <summary>Cards already on the board in the right column. A second identical pass is ALL of
    /// these, and <see cref="GithubClient.MutationCount"/> stays where it was.</summary>
    public List<string> Unchanged { get; } = [];

    /// <summary>Cards on the board with no status set, because this board offers no option that
    /// matches their status. Named, never silently dropped.</summary>
    public List<string> Unplaced { get; } = [];

    /// <summary>Every column fallback and every unplaced status, said once — the sentences that stop
    /// a board showing a blocked card as in-progress from being a quiet lie.</summary>
    public List<string> Notes { get; } = [];

    public List<string> Errors { get; } = [];

    /// <summary>The board's own title, once resolved. What a proof transcript quotes to show WHICH
    /// board was written.</summary>
    public string? ProjectTitle { get; set; }

    public string? ProjectUrl { get; set; }

    public bool Ok => Errors.Count == 0;

    public string Summary() => string.Create(CultureInfo.InvariantCulture,
        $"project: {Added.Count} added · {Moved.Count} moved · {Unchanged.Count} in place · " +
        $"{Unplaced.Count} unplaced · {Errors.Count} errors");
}

/// <summary>
/// DV6.2 — the columns. The Projects v2 mutation path KS9.3 refused to half-build, built.
///
/// <para><b>What KS9.3 decided, and what changed.</b> KS9.3 left the project half unbuilt because the
/// machine's token lacks the classic <c>project</c> scope and a mutation path that had never run once
/// would be a claim, not a feature. That reasoning stands for the LIVE proof and only for it: the
/// path below is exercised end to end against a stub GraphQL server, and each of its four documents
/// was validated against the real api.github.com schema on 2026-08-26 — the three reads came back
/// <c>INSUFFICIENT_SCOPES</c>, which is GitHub validating the document and THEN refusing the token,
/// and the two mutation input shapes were confirmed by introspection, which needs no scope. So the
/// refusal MOVED: it is the scope gate alone now (<see cref="GithubProjects.PreflightAsync"/>), and
/// granting <c>project</c> makes this run.</para>
///
/// <para><b>Idempotence is GitHub's, not ours.</b> <c>addProjectV2ItemById</c> returns the EXISTING
/// item when the content is already on the board. That matters more than it sounds: bug #79 is a
/// mirror duplicating a whole board because it decided "have I already created this" from a read
/// replica that had not caught up. Here a stale listing costs at most one redundant add and one
/// redundant field write per card — both idempotent — and can never mint a second item. The listing
/// is an optimisation; it is never the authority.</para>
///
/// <para><b>One direction, still.</b> The board's items answer exactly one question — is this card
/// already here, and in which column — and never influence what the run believes. D-7 / A16 / ADR
/// 0005 are unchanged by this file.</para>
/// </summary>
public sealed class GithubProjectSync(GithubClient client, string owner, int projectNumber)
{
    /// <summary>The board and its Status options in ONE request. Validated live against
    /// api.github.com on 2026-08-26: every field resolved and was refused for scope, which is the API
    /// confirming the document.</summary>
    public const string ResolveQuery =
        "query($owner:String!,$number:Int!){ repositoryOwner(login:$owner){ " +
        "... on ProjectV2Owner { projectV2(number:$number){ id title url " +
        "field(name:\"Status\"){ ... on ProjectV2SingleSelectField { id name options{ id name } } } } } } }";

    /// <summary>What is already on the board, with each item's current status option. A hundred at a
    /// time, walked by cursor.</summary>
    public const string ItemsQuery =
        "query($project:ID!,$cursor:String){ node(id:$project){ ... on ProjectV2 { " +
        "items(first:100,after:$cursor){ pageInfo{ hasNextPage endCursor } nodes{ id " +
        "content{ ... on Issue { number } } " +
        "fieldValueByName(name:\"Status\"){ ... on ProjectV2ItemFieldSingleSelectValue { optionId } } } } } } }";

    /// <summary>Put an issue on the board. Idempotent by GitHub's contract — an issue already on the
    /// board comes back with the item id it already had.</summary>
    public const string AddItemMutation =
        "mutation($project:ID!,$content:ID!){ " +
        "addProjectV2ItemById(input:{projectId:$project,contentId:$content}){ item{ id } } }";

    /// <summary>Move it to a column. Input shape confirmed by live introspection on 2026-08-26:
    /// projectId, itemId, fieldId, and a ProjectV2FieldValue carrying singleSelectOptionId.</summary>
    public const string SetStatusMutation =
        "mutation($project:ID!,$item:ID!,$field:ID!,$option:String!){ " +
        "updateProjectV2ItemFieldValue(input:{projectId:$project,itemId:$item,fieldId:$field," +
        "value:{singleSelectOptionId:$option}}){ projectV2Item{ id } } }";

    private Board? _board;

    /// <summary>
    /// Reconcile the board's columns against the fold. Never throws: the verdict is the return value,
    /// on the same posture as the rest of this integration — a run must not be harmed by a board it
    /// could not write.
    /// </summary>
    public async Task<GithubProjectPass> PlaceAsync(
        IReadOnlyList<GithubProjectPlacement> desired, bool dryRun, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(desired);
        var pass = new GithubProjectPass();
        if (desired.Count == 0) return pass;

        // DV6.2, measured on the live rig: a caller CAN hand this the same issue twice. The ledger
        // plan builds one card per row of followups.md, and that file carries 91 rows for 55 distinct
        // ids — so 36 of them are second rows for a followup that already has a card, and some of
        // those rows disagree about whether it is closed. Left alone that shows up as a card being
        // "moved" on every pass, oscillating between two columns forever. One placement per ISSUE,
        // first wins, and the count that was dropped is SAID rather than quietly folded away.
        var byIssue = new Dictionary<int, GithubProjectPlacement>();
        var duplicates = 0;
        foreach (var card in desired)
            if (!byIssue.TryAdd(card.IssueNumber, card)) duplicates++;
        if (duplicates > 0)
            pass.Notes.Add($"{Num(duplicates)} of {Num(desired.Count)} cards named an issue another card " +
                "had already claimed — the ledger has more rows than ids. One placement per issue was made.");
        desired = [.. byIssue.Values];

        var board = await BoardAsync(pass, ct).ConfigureAwait(false);
        if (board is null) return pass;
        pass.ProjectTitle = board.Title;
        pass.ProjectUrl = board.Url;

        var observed = await ItemsAsync(board.ProjectId, pass, ct).ConfigureAwait(false);
        if (observed is null) return pass;

        var said = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in desired)
        {
            var (columnName, fallback) = GithubProjectColumns.Resolve(card.Status, board.Options.Keys);
            if (columnName is null)
            {
                pass.Unplaced.Add(card.Key);
                if (said.Add("unplaced:" + card.Status))
                    pass.Notes.Add(GithubProjectColumns.UnplacedNote(card.Status, board.Options.Keys));
            }
            else if (fallback && said.Add("fallback:" + card.Status))
            {
                pass.Notes.Add(GithubProjectColumns.FallbackNote(card.Status, columnName));
            }

            var wantOption = columnName is null ? null : board.Options[columnName];
            observed.TryGetValue(card.IssueNumber, out var here);

            if (here is not null && string.Equals(here.OptionId, wantOption, StringComparison.Ordinal))
            {
                pass.Unchanged.Add(card.Key);
                continue;
            }

            if (here is null) pass.Added.Add(card.Key);
            if (wantOption is not null) pass.Moved.Add(card.Key);
            if (dryRun) continue;

            var itemId = here?.ItemId;
            if (itemId is null)
            {
                itemId = await AddAsync(board.ProjectId, card, pass, ct).ConfigureAwait(false);
                if (itemId is null)
                {
                    pass.Added.Remove(card.Key);
                    pass.Moved.Remove(card.Key);
                    continue;
                }
            }

            if (wantOption is not null)
                await SetStatusAsync(board.ProjectId, itemId, board.FieldId, wantOption, card, pass, ct)
                    .ConfigureAwait(false);
        }

        return pass;
    }

    // ── the board ────────────────────────────────────────────────────────────────────────────────

    private sealed record Board(
        string ProjectId, string Title, string? Url, string FieldId, Dictionary<string, string> Options);

    private sealed record Item(string ItemId, string? OptionId);

    /// <summary>Resolved once and cached for the life of this sync: a run's boundaries each drive a
    /// pass, and re-resolving the same board every boundary would be one request per boundary for an
    /// answer that does not change.</summary>
    private async Task<Board?> BoardAsync(GithubProjectPass pass, CancellationToken ct)
    {
        if (_board is not null) return _board;

        var (data, error) = await client.GraphQlAsync(
            ResolveQuery,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["owner"] = owner,
                ["number"] = projectNumber,
            }, ct).ConfigureAwait(false);
        if (error is not null) { pass.Errors.Add($"project #{Num(projectNumber)}: {error}"); return null; }

        var project = Path(data, "repositoryOwner", "projectV2");
        if (project is null)
        {
            pass.Errors.Add($"there is no project #{Num(projectNumber)} on '{owner}' — the number is " +
                "the one in the project url, and a project owned by an organisation is numbered under " +
                "that organisation, not under the repository's owner.");
            return null;
        }

        var field = Path(project, "field");
        var fieldId = Text(field, "id");
        if (fieldId is null)
        {
            pass.Errors.Add($"project #{Num(projectNumber)} has no single-select field named " +
                $"'{GithubProjectColumns.StatusField}', so there are no columns to write to.");
            return null;
        }

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        if (field!.Value.TryGetProperty("options", out var list) && list.ValueKind is JsonValueKind.Array)
            foreach (var option in list.EnumerateArray())
                if (Text(option, "name") is { } name && Text(option, "id") is { } id)
                    options[name] = id;

        if (options.Count == 0)
        {
            pass.Errors.Add($"project #{Num(projectNumber)}'s '{GithubProjectColumns.StatusField}' field " +
                "offers no options at all, so no card could be placed anywhere.");
            return null;
        }

        _board = new Board(
            Text(project, "id") ?? "",
            Text(project, "title") ?? "#" + Num(projectNumber),
            Text(project, "url"),
            fieldId,
            options);
        return _board;
    }

    /// <summary>Everything already on the board, by issue number. An OPTIMISATION and never the
    /// authority — see this type's remarks on bug #79.</summary>
    private async Task<Dictionary<int, Item>?> ItemsAsync(
        string projectId, GithubProjectPass pass, CancellationToken ct)
    {
        var byIssue = new Dictionary<int, Item>();
        string? cursor = null;
        // Bounded on the PagedAsync precedent: a board that answered hasNextPage forever would be
        // hammered, and no conductor board is two thousand cards.
        for (var page = 0; page < 20; page++)
        {
            var (data, error) = await client.GraphQlAsync(
                ItemsQuery,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["project"] = projectId,
                    ["cursor"] = cursor,
                }, ct).ConfigureAwait(false);
            if (error is not null) { pass.Errors.Add($"project items: {error}"); return null; }

            var items = Path(data, "node", "items");
            if (items is null) break;
            if (items.Value.TryGetProperty("nodes", out var nodes) && nodes.ValueKind is JsonValueKind.Array)
                foreach (var node in nodes.EnumerateArray())
                {
                    var itemId = Text(node, "id");
                    var number = Path(node, "content") is { } content
                        && content.TryGetProperty("number", out var n) && n.ValueKind is JsonValueKind.Number
                            ? n.GetInt32()
                            : (int?)null;
                    if (itemId is null || number is null) continue;
                    byIssue[number.Value] = new Item(itemId, Text(Path(node, "fieldValueByName"), "optionId"));
                }

            var info = Path(items, "pageInfo");
            if (info is null
                || !info.Value.TryGetProperty("hasNextPage", out var more)
                || more.ValueKind is not JsonValueKind.True)
                break;
            cursor = Text(info, "endCursor");
            if (cursor is null) break;
        }
        return byIssue;
    }

    private async Task<string?> AddAsync(
        string projectId, GithubProjectPlacement card, GithubProjectPass pass, CancellationToken ct)
    {
        var (data, error) = await client.GraphQlAsync(
            AddItemMutation,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["project"] = projectId,
                ["content"] = card.NodeId,
            }, ct).ConfigureAwait(false);
        if (error is not null) { pass.Errors.Add($"{card.Key}: {error}"); return null; }

        var itemId = Text(Path(data, "addProjectV2ItemById", "item"), "id");
        if (itemId is null) pass.Errors.Add($"{card.Key}: the board accepted the item and returned no id");
        return itemId;
    }

    private async Task SetStatusAsync(
        string projectId, string itemId, string fieldId, string optionId,
        GithubProjectPlacement card, GithubProjectPass pass, CancellationToken ct)
    {
        var (_, error) = await client.GraphQlAsync(
            SetStatusMutation,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["project"] = projectId,
                ["item"] = itemId,
                ["field"] = fieldId,
                ["option"] = optionId,
            }, ct).ConfigureAwait(false);
        if (error is not null)
        {
            pass.Errors.Add($"{card.Key}: {error}");
            pass.Moved.Remove(card.Key);
        }
    }

    // ── reading a tree nobody typed a class for ──────────────────────────────────────────────────

    private static JsonElement? Path(JsonElement? from, params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var here = from;
        foreach (var name in names)
        {
            if (here is null || here.Value.ValueKind is not JsonValueKind.Object) return null;
            if (!here.Value.TryGetProperty(name, out var next) || next.ValueKind is JsonValueKind.Null) return null;
            here = next;
        }
        return here;
    }

    private static string? Text(JsonElement? from, string name) =>
        from is not null && from.Value.ValueKind is JsonValueKind.Object
        && from.Value.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);
}
