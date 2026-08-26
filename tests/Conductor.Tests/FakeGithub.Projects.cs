using System.Globalization;
using System.Text.Json;

namespace Conductor.Tests;

/// <summary>
/// DV6.2 — the Projects v2 half of the fake: a stateful board behind the ONE GraphQL endpoint.
///
/// <para>Stateful for the same reason the issue half is. The claims that matter are about the SECOND
/// pass — "an unchanged card costs zero mutations", "a stale listing cannot mint a second item" — and
/// neither is meaningful against a handler that answers <c>200 {}</c>. So this keeps items and their
/// status options, answers the items query from them, and honours both mutations.</para>
///
/// <para>It reproduces the two GitHub behaviours the mutation path is built around:
/// <c>addProjectV2ItemById</c> is IDEMPOTENT (an issue already on the board comes back with the item
/// id it already had), and the items listing can be a stale read replica while the mutations are not.
/// Those are exactly the pair that bug #79 got wrong on the issue half.</para>
/// </summary>
internal sealed partial class FakeGithub
{
    public const string ProjectNodeId = "PVT_kwtest";
    public const string StatusFieldId = "PVTSSF_status";

    /// <summary>GitHub's own default board template: three columns and no word for blocked or
    /// skipped. The default matters — it is what the fallback rules are aimed at.</summary>
    public List<(string Id, string Name)> ProjectOptions { get; set; } =
        [("opt_todo", "Todo"), ("opt_doing", "In Progress"), ("opt_done", "Done")];

    /// <summary>The board has no single-select field called Status. A board that renamed it must be
    /// named as such, not searched for by shape.</summary>
    public bool NoStatusField { get; set; }

    /// <summary>There is no project with that number under this owner.</summary>
    public bool NoSuchProject { get; set; }

    /// <summary>The GraphQL reply GitHub sends when the token lacks the scope: HTTP 200, an
    /// <c>errors</c> array, no data. Set to the verbatim message and it is served instead of any
    /// board.</summary>
    public string? ProjectError { get; set; }

    /// <summary>DV6.2 — the replica lag, on the project half. With this set the ITEMS query answers
    /// empty while the mutations still work, which is the shape that duplicated a whole issue board
    /// in bug #79. Here it must cost redundant writes and nothing else.</summary>
    public bool ProjectItemsAreStale { get; set; }

    private readonly Dictionary<string, ProjectItem> _items = new(StringComparer.Ordinal);
    private int _nextItem = 1;

    private sealed class ProjectItem
    {
        public string ItemId { get; init; } = "";
        public int IssueNumber { get; init; }
        public string? OptionId { get; set; }
    }

    /// <summary>Every GraphQL document this fake was sent, in order — what "the second pass issued no
    /// mutation" is asserted against when a count is not specific enough.</summary>
    public List<string> GraphQlDocuments { get; } = [];

    /// <summary>The board's own state: which COLUMN that issue is in, by name, or null when it is on
    /// the board with no status, and absent when it is not on the board at all.</summary>
    public string? ColumnOf(int issueNumber)
    {
        var item = _items.Values.Single(i => i.IssueNumber == issueNumber);
        return item.OptionId is null ? null : ProjectOptions.Find(o => o.Id == item.OptionId).Name;
    }

    public bool IsOnBoard(int issueNumber) => _items.Values.Any(i => i.IssueNumber == issueNumber);

    /// <summary>How many items the board holds. The duplicate question, asked directly.</summary>
    public int BoardItemCount => _items.Count;

    /// <summary>The GraphQL global id of an issue, in the shape the real API uses. Parsed back out on
    /// the way in, which is how this fake knows which issue an <c>addProjectV2ItemById</c> is about.
    /// </summary>
    public static string NodeId(int issueNumber) =>
        "I_kw" + issueNumber.ToString(CultureInfo.InvariantCulture);

    private static int NumberOfNode(string nodeId) =>
        int.Parse(nodeId["I_kw".Length..], CultureInfo.InvariantCulture);

    private string GraphQl(string body)
    {
        var envelope = JsonDocument.Parse(body).RootElement;
        var document = envelope.GetProperty("query").GetString() ?? "";
        var variables = envelope.GetProperty("variables");
        GraphQlDocuments.Add(document);

        if (ProjectError is { } why)
            return "{\"errors\":[{\"type\":\"INSUFFICIENT_SCOPES\",\"message\":" + Str(why) + "}]}";

        if (document.Contains("repositoryOwner", StringComparison.Ordinal)) return ResolveProject();
        if (document.Contains("addProjectV2ItemById", StringComparison.Ordinal)) return AddItem(variables);
        if (document.Contains("updateProjectV2ItemFieldValue", StringComparison.Ordinal)) return SetStatus(variables);
        if (document.Contains("items(first:100", StringComparison.Ordinal)) return ListItems();
        return "{\"data\":null,\"errors\":[{\"message\":\"the fake was sent a document it does not know\"}]}";
    }

    private string ResolveProject()
    {
        if (NoSuchProject) return "{\"data\":{\"repositoryOwner\":{\"projectV2\":null}}}";
        var field = NoStatusField
            ? "null"
            : "{\"id\":" + Str(StatusFieldId) + ",\"name\":\"Status\",\"options\":[" +
              string.Join(",", ProjectOptions.Select(o => "{\"id\":" + Str(o.Id) + ",\"name\":" + Str(o.Name) + "}")) +
              "]}";
        return "{\"data\":{\"repositoryOwner\":{\"projectV2\":{\"id\":" + Str(ProjectNodeId) +
               ",\"title\":\"Divan\",\"url\":\"https://github.test/users/owner/projects/7\",\"field\":" + field + "}}}}";
    }

    private string ListItems()
    {
        var nodes = ProjectItemsAreStale
            ? ""
            : string.Join(",", _items.Values.Select(i =>
                "{\"id\":" + Str(i.ItemId) + ",\"content\":{\"number\":" + Num(i.IssueNumber) + "},\"fieldValueByName\":" +
                (i.OptionId is null ? "null" : "{\"optionId\":" + Str(i.OptionId) + "}") + "}"));
        return "{\"data\":{\"node\":{\"items\":{\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":null},\"nodes\":[" +
               nodes + "]}}}}";
    }

    /// <summary>Idempotent, exactly as documented: an issue already on the board comes back with the
    /// item id it already had. This is the property the mutation path leans on instead of trusting a
    /// listing, and a fake that minted a second item here would hide that it does.</summary>
    private string AddItem(JsonElement variables)
    {
        var number = NumberOfNode(variables.GetProperty("content").GetString() ?? "");
        var existing = _items.Values.FirstOrDefault(i => i.IssueNumber == number);
        if (existing is null)
        {
            existing = new ProjectItem
            {
                ItemId = "PVTI_" + (_nextItem++).ToString(CultureInfo.InvariantCulture),
                IssueNumber = number,
            };
            _items[existing.ItemId] = existing;
        }
        return "{\"data\":{\"addProjectV2ItemById\":{\"item\":{\"id\":" + Str(existing.ItemId) + "}}}}";
    }

    private string SetStatus(JsonElement variables)
    {
        var itemId = variables.GetProperty("item").GetString() ?? "";
        if (_items.TryGetValue(itemId, out var item))
            item.OptionId = variables.GetProperty("option").GetString();
        return "{\"data\":{\"updateProjectV2ItemFieldValue\":{\"projectV2Item\":{\"id\":" + Str(itemId) + "}}}}";
    }
}
