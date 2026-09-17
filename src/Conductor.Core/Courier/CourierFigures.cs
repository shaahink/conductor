using System.Globalization;
using System.Text;
using System.Text.Json;

using Conductor.Core.Events;
using Conductor.Core.Evidence;
using Conductor.Core.Inbox;
using Conductor.Core.Integrations.Messaging;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Courier;

/// <summary>PK5.2 / D10 / findings F-COUR-7 - the figure verbs, answered by the courier from the
/// store.
///
/// <para>KS11.4/KS11.5 built <c>/progress</c>, <c>/money</c>, <c>/tokens</c> and <c>/evidence</c> on
/// the in-run poll loop, and on a machine with a courier that loop does not poll
/// (<c>TelegramService.AllowsControl</c> is false under a courier): the observer group asked what the
/// run cost and nothing answered. So the courier answers, for the chat's project, from that project's
/// newest run - whether or not a run is live. The in-run handlers stay for a courier-less machine.</para>
///
/// <para><b>Nothing here writes.</b> ADR-0005 says the phone cannot change what the run decides and
/// ADR-0008's second condition says the courier is ingress for notes, never for run state; this is
/// the read side of that, held to the letter. The database is found WITHOUT
/// <see cref="StateHome.Resolve"/> (which upserts the catalogue and may import a legacy file), the plan
/// is pinned to that finding before anything downstream can resolve it again, and the store is opened
/// with <see cref="SqliteRunStore.OpenReadOnly"/> - which creates no sidecar either. The figures
/// themselves are not computed here: money and tokens are <see cref="MessageComposer"/>'s, which
/// reads <see cref="MoneySection.Read"/> exactly as <c>conductor money</c> does, and status is
/// <see cref="StatusReportBuilder.Build"/>, exactly as <c>conductor status</c> does.</para></summary>
public static class CourierFigures
{
    /// <summary>The verbs the courier answers from the store, without the slash. Each is a Browse verb
    /// of <see cref="SurfaceCommands"/>, so every listed chat may ask - a test holds that as the list
    /// grows.</summary>
    public static readonly IReadOnlyList<string> Verbs = ["status", "progress", "money", "tokens", "evidence"];

    /// <summary>How many registered artifacts a bare <c>/evidence</c> lists, newest first.</summary>
    public const int EvidenceListMax = 10;

    public static bool Answers(string verb) => Verbs.Contains(verb, StringComparer.Ordinal);

    /// <summary>The answer to one figure verb for one project, as HTML. Never null; a project with no
    /// run recorded says so rather than printing zeros.</summary>
    /// <param name="verb">One of <see cref="Verbs"/>.</param>
    /// <param name="arg">What followed the verb - a checkpoint id for <c>/evidence</c>, ignored otherwise.</param>
    /// <param name="stateHomeRoot">The courier's state home, whose catalogue names the database.</param>
    public static string Answer(string verb, string arg, ProjectRef project, string stateHomeRoot, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Answers(verb))
            throw new ArgumentOutOfRangeException(nameof(verb), verb, "not a figure verb the courier answers");

        var plan = PlanFor(project, warn);
        var found = DatabaseFor(project.Repo, plan.Name, stateHomeRoot);
        var nothing = $"No run of <b>{Escape(plan.Name)}</b> is recorded on this machine yet - there is nothing to count.";
        if (!File.Exists(found.RunDbPath)) return nothing;
        plan.PinState(found);

        using var store = SqliteRunStore.OpenReadOnly(found.RunDbPath);
        if (store.GetLatestRunId(plan.Name) is not { Length: > 0 } runId) return nothing;

        var body = verb switch
        {
            "status" => StatusText(StatusReportBuilder.Build(plan, store)),
            "evidence" => EvidenceText(plan.Name, arg, EvidenceRegistry.From(store.ReadAllEvents(runId)),
                [.. plan.Stages.Select(s => s.Id)]),
            _ => Composed(verb, plan, StateOf(store, plan, runId), store, warn),
        };
        return body.TrimEnd() + "\n\n<i>" + Escape($"the courier, from {project.RepoLeaf}'s run.db (run {Short(runId)}), read-only") + "</i>";
    }

    /// <summary>The project's plan file: the one under its checkout whose <c>name</c> is the project's
    /// plan, looked for where plans live (the repo, <c>plans/</c>, and one folder under it). When none
    /// loads, a bare plan carrying the name and the repo - the money still answers from the store, and
    /// the progress simply has no tracker to count.</summary>
    public static PlanConfig PlanFor(ProjectRef project, Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var path in PlanFiles(project.Repo))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (!doc.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || !string.Equals(name.GetString()?.Trim(), project.Plan.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                return PlanConfig.Load(path);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                or InvalidOperationException or ArgumentException)
            {
                // A plan that does not load is not this answer's to fix - said, then the next candidate or the bare plan.
                warn?.Invoke($"courier: {path} names {project.Plan} but does not load ({ex.GetType().Name}: {ex.Message}); figures read without it");
            }
        }

        return new PlanConfig { Name = project.Plan, Repo = project.Repo };
    }

    /// <summary>Which database a (repo, plan) reads, with ZERO side effects: the repo's pointer when
    /// it has one (the engine's own precedence), else the state catalogue's entry, else the derived or
    /// legacy path <see cref="StateHome.Peek"/> names. The environment override is not honoured - one
    /// courier answers for every project, and one variable cannot stand in for all of them.</summary>
    public static StateResolution DatabaseFor(string repo, string plan, string stateHomeRoot)
    {
        var peek = StateHome.Peek(repo, plan, stateHomeRoot, honourEnvOverride: false);
        if (peek.Source == StateSource.Pointer) return peek;
        return StateCatalogue.Find(stateHomeRoot, repo, plan) is { RunDb.Length: > 0 } entry && File.Exists(entry.RunDb)
            ? new StateResolution(entry.RunDb, StateSource.Derived, null)
            : peek;
    }

    /// <summary>The run as the engine last saved it - the same object the in-run composer holds, so
    /// the "run's own counter" line means the same thing on both surfaces. A store with no saved state
    /// folds its events instead.</summary>
    public static RunState StateOf(IRunStore store, PlanConfig plan, string runId)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            if (store.LoadRunStateJson(runId) is { Length: > 0 } json
                && JsonSerializer.Deserialize<RunState>(json, PlanConfig.JsonOpts) is { } saved)
            {
                if (string.IsNullOrEmpty(saved.RunId)) saved.RunId = runId;
                return saved;
            }
        }
        catch (JsonException) { }

        var folded = RunStateProjection.Fold(store.ReadAllEvents(runId));
        folded.RunId = runId;
        if (string.IsNullOrEmpty(folded.PlanName)) folded.PlanName = plan.Name;
        return folded;
    }

    private static string Composed(string verb, PlanConfig plan, RunState state, IRunStore store, Action<string>? warn)
    {
        var composer = new MessageComposer(plan, state, Progress(plan), store, warn ?? (_ => { }));
        return verb switch
        {
            "progress" => composer.ProgressText(),
            "money" => composer.MoneyText(),
            "tokens" => composer.TokensText(),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, "not a composed figure verb"),
        };
    }

    /// <summary>The plan's own progress reader - the one <c>/status</c> in-run and <c>conductor status</c>
    /// both use. A bare plan (no file loaded) has no tracker, and its empty tracker path is the CHECKOUT:
    /// reading that would throw, so it gets no rows instead.</summary>
    private static IProgressProvider Progress(PlanConfig plan)
    {
        if (string.IsNullOrEmpty(plan.PlanFilePath) || string.IsNullOrWhiteSpace(plan.Tracker)) return NoTracker.Instance;
        try { return ProgressProviderFactory.Create(plan); }
        catch (InvalidOperationException) { return NoTracker.Instance; }
    }

    private sealed class NoTracker : IProgressProvider
    {
        public static readonly NoTracker Instance = new();
        public string Name => "none";
        public TrackerSnapshot Read(PlanConfig plan, CancellationToken ct = default) => new();
    }

    /// <summary><c>/status</c>, from the report <c>conductor status</c> renders - so a dead engine's
    /// open session reads as interrupted here too, not as running (bug #39).</summary>
    public static string StatusText(StatusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var sb = new StringBuilder();
        sb.AppendLine($"<b>Conductor — {Escape(report.PlanName)}</b>");
        sb.AppendLine();
        sb.AppendLine(Escape(report.Verdict));
        sb.AppendLine(Escape(
            $"Checkpoints {report.DoneCount}/{report.TotalCount} · sessions {report.SessionCount} · billed {MoneyLine.Usd(report.TotalCostUsd)}"
            + (report.OverheadCostUsd > 0m ? $" (+{MoneyLine.Usd(report.OverheadCostUsd)} overhead)" : "")));
        if (report.WhatHurt is { Length: > 0 } hurt)
            sb.AppendLine("What hurt: " + Escape(hurt));

        if (report.Stages.Count > 0)
        {
            sb.AppendLine();
            foreach (var stage in report.Stages)
            {
                var here = string.Equals(stage.Id, report.CurrentStageId, StringComparison.OrdinalIgnoreCase) ? " ◀" : "";
                sb.AppendLine($"<code>{Escape($"{stage.Id,-6} {stage.Done}/{stage.Total} {stage.State}")}</code>{here}");
            }
        }

        return sb.ToString();
    }

    /// <summary><c>/evidence</c>, from the evidence registry - the artifacts the run REGISTERED, with
    /// their bytes and hashes, rather than the tracker's evidence cell. Bare, the newest few; with a
    /// checkpoint id, everything registered against it. The courier names a file; it does not send
    /// one.
    /// <para>Bare lists THIS plan's checkpoints first, then other checkpoints', then what nothing
    /// claimed - newest registration first within each. Measured on this era's own store (PK5.2 rig):
    /// all 471 artifacts were registered in one sweep with one timestamp, and the newest ten were
    /// another era's files - by time or by registration alone, an answer made of noise.</para></summary>
    /// <param name="stageIds">The plan's stage ids; an artifact is this plan's when its stage is one.</param>
    public static string EvidenceText(string planName, string checkpointId, EvidenceRegistry registry,
        IReadOnlyCollection<string>? stageIds = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var id = (checkpointId ?? "").Trim();
        var stages = new HashSet<string>(stageIds ?? [], StringComparer.OrdinalIgnoreCase);
        int Tier(EvidenceArtifact a) =>
            a.CheckpointId is not { Length: > 0 } cp ? 0
            : stages.Contains(a.StageId ?? "") || stages.Any(s => cp.StartsWith(s + ".", StringComparison.OrdinalIgnoreCase)) ? 2
            : 1;

        var rows = id.Length == 0
            ? [.. registry.Artifacts
                .Select((a, order) => (a, order))
                .OrderByDescending(x => Tier(x.a))
                .ThenByDescending(x => x.order)
                .Take(EvidenceListMax)
                .Select(x => x.a)]
            : registry.ForCheckpoint(id).Reverse().ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"<b>{Escape(planName)} — evidence{(id.Length == 0 ? "" : " for " + Escape(id))}</b>");
        sb.AppendLine();
        if (rows.Count == 0)
        {
            sb.Append(id.Length == 0
                ? "No evidence is registered on this run yet."
                : $"Nothing is registered against {Escape(id)} on this run. <code>/evidence</code> lists what is.");
            return sb.ToString();
        }

        foreach (var a in rows)
            sb.AppendLine($"<code>{Escape(a.CheckpointId ?? "-")}</code> {Escape(a.Path)} "
                + Escape($"· {a.Kind} · {Size(a.Bytes)} · sha {Short(a.Sha256)} · {a.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}Z"));

        if (id.Length == 0 && registry.Count > rows.Count)
        {
            var loose = registry.Artifacts.Count(a => Tier(a) == 0);
            var elsewhere = registry.Artifacts.Count(a => Tier(a) == 1);
            sb.AppendLine(Escape($"…and {registry.Count - rows.Count} more registered ({elsewhere} for other plans' checkpoints, "
                + $"{loose} not tied to a checkpoint). "
                + "/evidence <checkpoint> lists one checkpoint's."));
        }
        return sb.ToString();
    }

    private static IEnumerable<string> PlanFiles(string repo)
    {
        if (!Directory.Exists(repo)) yield break;
        foreach (var file in Directory.EnumerateFiles(repo, "*.plan.json").Order(StringComparer.OrdinalIgnoreCase))
            yield return file;

        var plans = Path.Combine(repo, "plans");
        if (!Directory.Exists(plans)) yield break;
        foreach (var file in Directory.EnumerateFiles(plans, "*.plan.json").Order(StringComparer.OrdinalIgnoreCase))
            yield return file;
        foreach (var dir in Directory.EnumerateDirectories(plans).Order(StringComparer.OrdinalIgnoreCase))
            foreach (var file in Directory.EnumerateFiles(dir, "*.plan.json").Order(StringComparer.OrdinalIgnoreCase))
                yield return file;
    }

    private static string Escape(string text) => MessageComposer.EscapeHtml(text);

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        _ => (bytes / (1024d * 1024d)).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
    };
}
