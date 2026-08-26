using Conductor.Core.Events;
using Conductor.Core.Http;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.History;

/// <summary>
/// KS2.2 — one finished run, projected into the control plane's own wire contracts so the Face can be
/// pointed at a run that no engine is serving.
///
/// <para><b>Why this exists at all.</b> Every read surface of the Face speaks one language: the DTOs in
/// <c>Conductor.Core.Http</c>. A finished run holds all the same facts — sessions, money, a timeline, a
/// report — in its <c>run.db</c>, and until now the only way to see them was <c>conductor history</c>'s
/// printed page. This type is the translation, and the ONLY translation: the archive plane
/// (<c>Conductor.Http.ArchiveControlPlane</c>) is a socket around it and holds no projection of its own,
/// so "what the Face sees for a past run" is decided in one file that tests can call with no HTTP.</para>
///
/// <para><b>Read-only is enforced by the connection, not by discipline.</b> Everything here goes through
/// <see cref="RunArchive"/> (<c>Mode=ReadOnly;Cache=Private</c>). <see cref="SqliteRunStore"/> is never
/// constructed: its constructor creates the directory, sets <c>journal_mode=WAL</c> and runs the
/// migrations — three writes before the first read, which would rewrite a July run's schema just to
/// look at it.</para>
///
/// <para><b>What an archive cannot say, it does not say.</b> There is no live session, so there is no
/// burn rate, no in-flight estimate and no gate battery; those fields carry their empty values rather
/// than a last-known number dressed up as current. The run's status is reconciled through KS1.3's
/// <see cref="RunLiveness"/> exactly as the history listing reconciles it, so a run whose row still
/// says <c>running</c> because its engine was killed does not become <c>running</c> again by being
/// opened here.</para>
/// </summary>
public sealed partial class ArchiveView
{
    private readonly RunArchive _archive;
    private readonly Lazy<List<ConductorEvent>> _events;
    private readonly Lazy<IReadOnlyList<ArchivedCost>> _costs;

    private ArchiveView(RunArchive archive, ArchivedRun run, string repo, bool storeLooksLive)
    {
        _archive = archive;
        Run = run;
        Repo = string.IsNullOrWhiteSpace(run.Repo) ? repo : run.Repo;
        StoreLooksLive = storeLooksLive;
        _events = new Lazy<List<ConductorEvent>>(() => archive.EventsOf(run.RunId));
        _costs = new Lazy<IReadOnlyList<ArchivedCost>>(() => archive.Costs(run.RunId));
    }

    /// <summary>The run row this view serves.</summary>
    public ArchivedRun Run { get; }

    /// <summary>The repo the run recorded, falling back to the catalogue's when the row has none.</summary>
    public string Repo { get; }

    /// <summary>The database being read. Never written — see the type remarks.</summary>
    public string RunDbPath => _archive.DbPath;

    /// <summary>The directory holding <see cref="RunDbPath"/> — the run's own folder in the state home,
    /// which is what a client that wants to open files beside the database needs. Deliberately NOT the
    /// repo's <c>.conductor</c>: a run imported from another machine has no such directory here.</summary>
    public string StateDir => Path.GetDirectoryName(Path.GetFullPath(_archive.DbPath)) ?? "";

    /// <summary>KS1.3: whether an engine is holding this store. False is the normal answer for an
    /// archive and the reason the served status can be trusted; true means someone attached the archive
    /// plane to a run that is still going, and the status word says so instead of lying either way.</summary>
    public bool StoreLooksLive { get; }

    /// <summary>The word this run should be PRINTED with — the stored status reconciled against
    /// liveness, the same rule <see cref="RunHistoryRow.Status"/> obeys.</summary>
    public string Status => RunLiveness.Reconcile(Run.Status, StoreLooksLive);

    /// <summary>
    /// Opens one run out of a state home by the selector a person types: a run id, a run-id prefix, a
    /// catalogue slug, or a repo leaf name — <see cref="RunHistory.Find"/>'s vocabulary.
    /// <para>Null with a precise <paramref name="refusal"/> rather than an exception, because every
    /// caller here is a listing or an attach: KS0.3's handoff records that the catalogue holds rows
    /// whose stores cannot be opened at all, and one of those must refuse by name, not by stack.</para>
    /// </summary>
    public static ArchiveView? Open(string root, string selector, out string refusal)
    {
        refusal = "";
        if (string.IsNullOrWhiteSpace(selector))
        {
            refusal = "no run named — pass a run id, an id prefix, a catalogue slug, a repo name, or a run.db path.";
            return null;
        }

        // A path to a database is taken at its word, ahead of the catalogue. The catalogue is an index
        // and an index can be stale or absent — a copied store, a database handed over by someone else,
        // or a run this machine has never catalogued is still a run, and refusing to open a file that
        // is plainly there because no index mentions it would be the index pretending to be the truth.
        // A path is answered as a path either way: "nothing in this machine's history matches
        // C:\...\run.db" is a true sentence that sends the reader to the wrong question.
        if (LooksLikeAPath(selector))
            return OpenDb(selector, null, out refusal);

        IReadOnlyList<RunHistoryRow> rows;
        try
        {
            rows = RunHistory.List(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            refusal = $"this machine's run catalogue under {root} could not be read: {e.Message}";
            return null;
        }

        var found = RunHistory.Find(root, selector, out var ambiguous);
        if (found is { Run: { } run })
        {
            var archive = RunArchive.TryOpen(found.RunDbPath, out var problem);
            if (archive is null)
            {
                refusal = Describe(found.RunDbPath, problem);
                return null;
            }
            return new ArchiveView(archive, run, found.Repo, found.StoreLooksLive);
        }

        if (ambiguous.Count > 1)
        {
            refusal = $"'{selector}' matches {ambiguous.Count} runs — name one of: "
                + string.Join(", ", ambiguous.Take(5).Select(r => r.Run?.ShortRunId ?? r.Slug));
            return null;
        }

        // KS1.3/KS0.3: the selector may well name a row that IS in the catalogue and whose store this
        // engine cannot open. Find() only considers readable rows, so without this the answer would be
        // "no such run" for a run that is plainly listed — the exact conflation RunDbProblem exists to
        // end. A blank-id row still matches on its slug, which is all such a row has.
        var broken = rows.FirstOrDefault(r => !r.Readable && MatchesBroken(r, selector.Trim()));
        refusal = broken is null
            ? $"nothing in this machine's history matches '{selector}'. Try `conductor history`."
            : Describe(broken.RunDbPath, broken.Problem);
        return null;
    }

    /// <summary>Opens a database directly, for a caller that already knows the path (a copied store, a
    /// test rig). <paramref name="runId"/> null takes the newest run in the file.</summary>
    public static ArchiveView? OpenDb(string dbPath, string? runId, out string refusal)
    {
        refusal = "";
        var archive = RunArchive.TryOpen(dbPath, out var problem);
        if (archive is null)
        {
            refusal = Describe(dbPath, problem);
            return null;
        }
        var runs = archive.Runs();
        var run = string.IsNullOrWhiteSpace(runId)
            ? runs.FirstOrDefault()
            : runs.FirstOrDefault(r => r.RunId.StartsWith(runId, StringComparison.OrdinalIgnoreCase));
        if (run is null)
        {
            refusal = string.IsNullOrWhiteSpace(runId)
                ? $"{dbPath} is a run database with no runs in it."
                : $"{dbPath} holds no run matching '{runId}'.";
            return null;
        }
        return new ArchiveView(archive, run, run.Repo, RunLiveness.StoreLooksLive(dbPath, run.Repo));
    }

    /// <summary>One sentence per way a catalogued store fails to open. The distinction is the whole
    /// point of <see cref="RunDbProblem"/>: "that run's file has been deleted" and "that path is not a
    /// run database" send an operator to two different places.
    /// <para>Public because the LISTINGS say it too — <c>Conductor.Core.Fleet.FacePastRuns</c> labels an
    /// unreadable row with this exact sentence, so what the picker shows and what the attach refuses
    /// with cannot drift into two accounts of the same broken file.</para></summary>
    public static string Describe(string dbPath, RunDbProblem problem) => problem switch
    {
        RunDbProblem.Missing => $"that run's database is gone — nothing at {dbPath}.",
        RunDbProblem.NotARunDatabase => $"{dbPath} is not a conductor run database this engine can read.",
        _ => $"{dbPath} could not be opened.",
    };

    /// <summary>Only a selector that could not be a run id, slug or repo leaf: it has a separator or a
    /// <c>.db</c> tail. A bare word is never treated as a path, so a slug that happens to name a file
    /// in the working directory cannot hijack the lookup.</summary>
    private static bool LooksLikeAPath(string selector) =>
        selector.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || selector.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
        || selector.EndsWith(".db", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesBroken(RunHistoryRow row, string selector) =>
        string.Equals(row.Slug, selector, StringComparison.OrdinalIgnoreCase)
        || string.Equals(row.Key, selector, StringComparison.OrdinalIgnoreCase)
        || string.Equals(RunHistory.RepoLabel(row.Repo), selector, StringComparison.OrdinalIgnoreCase);

    // ── the pieces every projection below shares ─────────────────────────────────────────────────

    private List<ConductorEvent> Events => _events.Value;

    /// <summary>The archived event log, oldest first — what the raw-stream pane replays. Public because
    /// the archive plane serves it verbatim over SSE; nothing here can mutate it.</summary>
    public IReadOnlyList<ConductorEvent> Log() => Events;

    /// <summary>DV6.1 — this database's whole bug ledger, read-only. Behind the view rather than on
    /// the archive directly for the same reason <see cref="Log"/> is: a caller holds a view, and a
    /// second handle on the same file is a second thing to keep honest.</summary>
    public IReadOnlyList<Store.CarriedBugRow> Bugs()
    {
        var plans = _archive.Runs().GroupBy(r => r.RunId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().PlanName, StringComparer.Ordinal);
        return [.. _archive.BugLedger().Select(b =>
            new Store.CarriedBugRow(b, plans.GetValueOrDefault(b.RunId, "")))];
    }

    private IReadOnlyList<ArchivedCost> CostRows => _costs.Value;

    /// <summary>The task graph the run ended with, folded from the log the way the live plane folds it.
    /// Archived items are dropped here for the same reason <c>GET /tasks</c> drops them: they left the
    /// declared plan, and history keeps them in the log rather than on the board.</summary>
    private List<TaskItem> LiveTasks()
    {
        var graph = new TaskGraph();
        graph.Fold(Events);
        return graph.Tasks.Where(t => !string.Equals(t.Status, "archived", StringComparison.Ordinal)).ToList();
    }
}
