using System.Text.Json;

using Conductor.Core.Budget;
using Conductor.Core.History;
using Conductor.Core.Http;
using Conductor.Core.Money;

namespace Conductor.Core.Integrations;

/// <summary>
/// KS8.1 — the three resource families the surface serves: <c>history</c> (every catalogued run),
/// <c>status</c> (one run, the reconciled word plus the Face's own state contract) and <c>money</c>
/// (one run, billed dollars only, through the same analyzer <c>conductor money --json</c> uses).
/// </summary>
public sealed partial class McpObserveServer
{
    internal const string HistoryUri = "conductor://history";
    private const string RunPrefix = "conductor://runs/";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    /// <summary>Concrete resources, not just templates: the index, then a status and a money resource
    /// per readable run. A client that cannot expand a URI template still sees every run.</summary>
    private JsonElement ListResources()
    {
        var resources = new List<object>
        {
            new
            {
                uri = HistoryUri,
                name = "history",
                description = "Every run in this machine's catalogue, newest activity first — plan, repo, "
                    + "reconciled status, stored status, sessions, billed cost and tokens.",
                mimeType = "application/json",
            },
        };

        foreach (var row in Rows())
        {
            if (row.Run is not { } run) continue; // an unreadable row has no run id to address
            var id = run.ShortRunId;
            resources.Add(new
            {
                uri = $"{RunPrefix}{id}/status",
                name = $"{id} status",
                description = $"{run.PlanName} in {row.RepoLabel} — reconciled status, stage rail and checkpoint counts.",
                mimeType = "application/json",
            });
            resources.Add(new
            {
                uri = $"{RunPrefix}{id}/money",
                name = $"{id} money",
                description = $"{run.PlanName} in {row.RepoLabel} — billed spend by month, stage, window and category.",
                mimeType = "application/json",
            });
        }

        return JsonSerializer.SerializeToElement(new { resources });
    }

    private static JsonElement ListTemplates() => JsonSerializer.SerializeToElement(new
    {
        resourceTemplates = new object[]
        {
            new
            {
                uriTemplate = RunPrefix + "{run}/status",
                name = "run status",
                description = "One run by id, id prefix, catalogue slug or repo name.",
                mimeType = "application/json",
            },
            new
            {
                uriTemplate = RunPrefix + "{run}/money",
                name = "run money",
                description = "Billed spend for one run. No price table is applied — every figure was billed.",
                mimeType = "application/json",
            },
        },
    });

    /// <summary>Resolve a URI to its JSON body. <paramref name="refusal"/> is non-empty, and the
    /// return value meaningless, when the URI names nothing this machine holds.</summary>
    private string ReadResource(string uri, out string refusal)
    {
        refusal = "";
        if (string.Equals(uri, HistoryUri, StringComparison.Ordinal))
            return HistoryJson();

        if (!uri.StartsWith(RunPrefix, StringComparison.Ordinal))
        {
            refusal = $"unknown resource '{uri}' — this surface serves {HistoryUri} and {RunPrefix}{{run}}/status|money.";
            return "";
        }

        var rest = uri[RunPrefix.Length..];
        var slash = rest.LastIndexOf('/');
        // The two halves fail differently and the operator needs to be told which half is missing:
        // no slash at all is a run with no view, a leading slash is a view with no run.
        if (slash < 0)
        {
            refusal = $"'{uri}' names a run but no view — append /status or /money.";
            return "";
        }
        if (slash == 0)
        {
            refusal = $"'{uri}' names no run — put a run id, an id prefix, a catalogue slug or a repo name before /{rest[1..]}.";
            return "";
        }

        var selector = rest[..slash];
        var view = rest[(slash + 1)..];
        return view switch
        {
            "status" => StatusJson(selector, out refusal),
            "money" => MoneyOf(selector, out refusal),
            _ => Unknown(view, out refusal),
        };
    }

    private static string Unknown(string view, out string refusal)
    {
        refusal = $"unknown view '{view}' — this surface serves /status and /money.";
        return "";
    }

    private IReadOnlyList<RunHistoryRow> Rows() => RunHistory.List(_root);

    /// <summary>The index. Both status words ride every row: the stored one because a surface that
    /// dropped it would be hiding the evidence for its own claim, and the reconciled one because a
    /// run whose engine died still says <c>running</c> in the database.</summary>
    private string HistoryJson()
    {
        var runs = Rows().Select(row => new
        {
            runId = row.Run?.RunId ?? "",
            shortRunId = row.Run?.ShortRunId ?? "",
            slug = row.Slug,
            // The RUN's plan name, not the catalogue entry's. One store holds every run of a
            // (repo, plan) pair as the catalogue first saw it, and a repo that renamed its plan —
            // conductor's own store, `karvansara core` then `karvansara edge` — keeps answering with
            // the old name from that column. `conductor history` picks the run's own name here
            // (HistoryCommand.cs:141) and this surface must not be a second account of it.
            plan = string.IsNullOrEmpty(row.Run?.PlanName) ? row.Plan : row.Run!.PlanName,
            cataloguedAs = row.Plan,
            repo = row.RepoLabel,
            status = row.Status,
            storedStatus = row.StoredStatus,
            readable = row.Readable,
            sessions = row.Run?.Sessions ?? 0,
            costUsd = row.Run?.CostUsd ?? 0m,
            tokens = row.Run?.Tokens ?? 0L,
            startedUtc = row.Run?.StartedUtc,
            endedUtc = row.Run?.EndedUtc,
            lastActivityUtc = row.Run?.LastActivityUtc,
            checkpointsDone = RunHistory.CheckpointCounts(row).Done,
            checkpointsTotal = RunHistory.CheckpointCounts(row).Total,
            statusUri = row.Run is null ? null : $"{RunPrefix}{row.Run.ShortRunId}/status",
            moneyUri = row.Run is null ? null : $"{RunPrefix}{row.Run.ShortRunId}/money",
        }).ToList();

        return JsonSerializer.Serialize(new { root = _root, count = runs.Count, runs }, Pretty);
    }

    /// <summary>One run's status: the reconciled word, the stored word, and the whole
    /// <see cref="StateDto"/> the Face reads — the same projection, from the archive.</summary>
    private string StatusJson(string selector, out string refusal)
    {
        var view = ArchiveView.Open(_root, selector, out refusal);
        if (view is null) return "";

        var state = JsonSerializer.SerializeToElement(view.State(), ControlPlaneJsonContext.Default.StateDto);
        return JsonSerializer.Serialize(new
        {
            runId = view.Run.RunId,
            plan = view.Run.PlanName,
            repo = view.Repo,
            status = view.Status,
            storedStatus = view.Run.Status,
            storeLooksLive = view.StoreLooksLive,
            startedUtc = view.Run.StartedUtc,
            endedUtc = view.Run.EndedUtc,
            lastActivityUtc = view.Run.LastActivityUtc,
            engine = view.Run.EngineStampText,
            state,
        }, Pretty);
    }

    /// <summary>One run's billed spend, through <see cref="MoneyAnalyzer"/> and
    /// <see cref="MoneyJson"/> — the same numbers and the same shape as <c>conductor money --json</c>,
    /// so nothing here can drift into a second account of the same dollars.</summary>
    private string MoneyOf(string selector, out string refusal)
    {
        var view = ArchiveView.Open(_root, selector, out refusal);
        if (view is null) return "";

        var archive = RunArchive.TryOpen(view.RunDbPath, out var problem);
        if (archive is null)
        {
            refusal = ArchiveView.Describe(view.RunDbPath, problem);
            return "";
        }

        var run = view.Run;
        var sessions = archive.Sessions(run.RunId);
        var costs = archive.Costs(run.RunId);
        var windows = BudgetAnalyzer.Analyze(run.RunId, run.PlanName, sessions, archive.SoftBreaks(run.RunId)).Windows;
        var priced = MoneyAnalyzer.AnalyzeRun(run.RunId, run.PlanName, RunHistory.RepoLabel(view.Repo),
            run.StartedUtc, run.LastActivityUtc, sessions, costs, windows);
        return MoneyJson.Serialize(MoneyAnalyzer.Combine(run.ShortRunId, [priced]));
    }
}
