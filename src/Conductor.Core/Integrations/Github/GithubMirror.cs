using System.Globalization;
using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Store;
using Conductor.Models;

namespace Conductor.Core.Integrations.Github;

/// <summary>
/// KS9.2 — the live mirror. A RECONCILER over <see cref="IRunStore.ReadEventsAfter"/> on the
/// <c>WatchLoop</c> cursor idiom, not a hot <c>IEventSink</c>.
///
/// <para><b>Why not a sink.</b> A sink runs on the writer's thread for every event the engine emits,
/// so a slow or dead GitHub becomes back-pressure on the run loop, and a dropped event is gone. A
/// reconciler asks the store what has happened since a persisted mark, pushes the board that the fold
/// implies, and moves the mark only if the push landed. An outage costs a repeated pass; it cannot
/// cost a lost event and it cannot cost a stalled run. <c>ArchitectureBoundaryTests</c> pins the
/// absence: no type under <c>Integrations/Github</c> implements <see cref="IEventSink"/>.</para>
///
/// <para><b>The delta decides IF, the fold decides WHAT.</b> <see cref="IRunStore.ReadEventsAfter"/>
/// answers "is there anything new" — and when the answer is no, the pass issues ZERO requests, which
/// is what keeps a boundary that changed nothing free. When the answer is yes, the desired board is
/// computed from the WHOLE log, because a board IS a fold: a tail-only fold would describe a run that
/// began at the cursor, and after a process restart that is not the run.</para>
///
/// <para><b>A failure holds the mark and never reaches the caller.</b> The <c>Notify</c> idiom next
/// door is <c>_ = SomethingAsync(...)</c>, which swallows faults silently; this deliberately does not
/// copy that. Every pass returns a verdict, a failed pass logs exactly one line and records itself
/// against the cursor row without advancing it, and the next pass pushes the same batch again —
/// idempotent by KS9.1's marker identity, so convergence costs nothing but a request.</para>
///
/// <para><b>Still nothing inbound.</b> Observed issues answer one question — which issue is ours —
/// and never influence run state. D-7 / A16 / ADR 0005.</para>
/// </summary>
public sealed class GithubMirror : IDisposable
{
    private readonly IRunStore _store;
    private readonly GithubClient _client;
    private readonly GithubBoardSync _sync;
    private readonly Action<string> _log;
    private readonly bool _includeDiary;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;
    private int _coalesced;

    public string RunId { get; }
    public string Repo { get; }

    /// <summary>Requests issued across every pass this mirror has run — the number the batching bar
    /// is asserted against.</summary>
    public int RequestCount => _client.RequestCount;

    /// <summary>Passes that ended in an error. Non-zero is not a run failure; it is a mirror that is
    /// behind, which is the designed posture during an outage.</summary>
    public int FailedPasses { get; private set; }

    public GithubMirror(
        IRunStore store, string runId, string repo, string token, string labelPrefix,
        bool includeDiary, Action<string> log, HttpMessageHandler? handler = null)
    {
        _store = store;
        RunId = runId;
        Repo = repo;
        _log = log;
        _includeDiary = includeDiary;
        _client = new GithubClient(token, TimeSpan.FromSeconds(30), handler, disposeHandler: handler is null);

        // The local map is loaded ONCE and written through on every create. GitHub's issue list is a
        // read replica — measured live: four issues created, invisible to a list two seconds later,
        // four more created — so "have I already made this" is answered from here, not from there.
        _map = new GithubMap((key, kind, number) => store.WriteGithubMapEntry(RunId, Repo, key, kind, number));
        foreach (var row in store.ReadGithubMap(runId, repo)) _map.Seed(row.Key, row.Kind, row.IssueNumber);
        _sync = new GithubBoardSync(_client, repo, labelPrefix, _map);
    }

    private readonly GithubMap _map;

    /// <summary>The mirror for this run, or null when the plan has not asked for one. Off by default
    /// means absent: a null <c>github</c> block, <c>enabled: false</c>, <c>liveMirror: false</c>, no
    /// token or no store each yield null, and a null mirror is never called.</summary>
    public static GithubMirror? TryCreate(
        PlanConfig plan, IRunStore? store, string runId, Action<string> log,
        HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (store is null) return null;
        if (plan.Github is not { Enabled: true, LiveMirror: true } cfg) return null;

        var repo = GithubIdentity.Resolve(plan);
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/', StringComparison.Ordinal))
        {
            log($"github mirror off: github.enabled is set but no owner/name destination could be resolved");
            return null;
        }

        var (token, source) = GithubIdentity.ResolveToken(plan);
        if (token is null)
        {
            // Loud, once, at creation. A mirror that silently did nothing because a token was missing
            // is the failure mode that makes an operator trust a board that was never written.
            log("github mirror off: enabled in the plan but no token — " + GithubIdentity.MissingTokenRefusal(plan)[0]);
            return null;
        }

        log($"github mirror on → {repo} (token from {source})");
        return new GithubMirror(store, runId, repo, token, cfg.LabelPrefix, cfg.RunHistoryIssue, log, handler);
    }

    // ── the pass ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>One reconcile pass. Awaitable and deterministic — this is what the tests and the
    /// manual catch-up drive. The engine's boundaries call <see cref="Fire"/>, which wraps this.
    /// It never throws: the verdict is the return value.</summary>
    public async Task<GithubMirrorPass> ReconcileAsync(
        string reason, string? runStatusOverride = null, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return GithubMirrorPass.Idle(reason, "mirror disposed");

        // A boundary that fires while a pass is in flight is COALESCED into one follow-up rather than
        // queued or dropped. Dropping it was the first design and the live rig refuted it: a run-start
        // pass against the real API takes seconds, the session that follows takes one, and its
        // session-end boundary was thrown away — so that session's events waited for the NEXT process.
        // Queuing every boundary would instead let a slow network build a backlog of identical diffs.
        // One flag: however many boundaries fire during a pass, exactly one more pass follows it.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _coalesced, 1);
            return GithubMirrorPass.Idle(reason, "a pass is already running — coalesced into the next");
        }

        var before = _client.RequestCount;
        try
        {
            var cursor = _store.ReadGithubCursor(RunId, Repo);
            var delta = _store.ReadEventsAfter(RunId, cursor.Seq);
            if (delta.Count == 0)
                return GithubMirrorPass.Idle(reason, "nothing new since seq " + cursor.Seq.ToString(CultureInfo.InvariantCulture));

            var head = delta.Max(e => e.Seq);
            var all = _store.ReadAllEvents(RunId);
            var run = Describe(runStatusOverride);

            var result = await _sync.BackfillAsync(
                all, run, run.EngineVersion ?? BuildInfo.Current.Full, _includeDiary, dryRun: false, ct)
                .ConfigureAwait(false);
            var requests = _client.RequestCount - before;

            if (result.Errors.Count > 0)
            {
                // HOLD. The mark stays where it was, so the very next pass re-pushes this same batch;
                // KS9.1's marker identity makes that a no-op for everything that did land.
                FailedPasses++;
                var first = result.Errors[0];
                _store.RecordGithubSyncError(RunId, Repo, first);
                _log($"github mirror behind ({reason}): {first} — cursor held at {cursor.Seq}, {requests} requests");
                return GithubMirrorPass.Failed(reason, cursor.Seq, requests, first, result);
            }

            _store.WriteGithubCursor(RunId, Repo, head, null);
            _log($"github mirror {reason}: {result.Summary()} — cursor {cursor.Seq}→{head}, {requests} requests");
            return GithubMirrorPass.Pushed(reason, head, requests, result);
        }
        catch (OperationCanceledException)
        {
            // A cancelled pass is the run shutting down, not a mirror failure. The mark is not moved;
            // the next process start picks the batch up.
            return GithubMirrorPass.Idle(reason, "cancelled");
        }
        // The client is already total against transport faults — every HTTP call it makes returns a
        // (value, error) pair rather than throwing (GithubClient.GetAsync / SendJsonAsync catch on
        // IsTransport). What is left is the STORE side of the pass: a read that lost a race with the
        // engine's writer, or a torn payload. Named types rather than a bare `catch (Exception)`,
        // which the analyzer ratchet would need a suppression for and which would also hide a genuine
        // bug in the fold behind a log line. Same list as WatchLoop.Drain.
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException
                                      or IOException or System.Text.Json.JsonException)
        {
            FailedPasses++;
            _store.RecordGithubSyncError(RunId, Repo, ex.Message);
            _log($"github mirror failed ({reason}): {ex.GetType().Name}: {ex.Message} — cursor held");
            return GithubMirrorPass.Failed(reason, _store.ReadGithubCursor(RunId, Repo).Seq,
                _client.RequestCount - before, ex.Message, null);
        }
        finally
        {
            _gate.Release();
            // The coalesced follow-up starts only after the gate is free, and is TRACKED — a drain
            // that returned while this one was still starting would truncate exactly the pass the
            // coalescing was invented to save.
            if (Interlocked.Exchange(ref _coalesced, 0) == 1 && Volatile.Read(ref _disposed) == 0)
                _ = Track(Task.Run(() => ReconcileAsync(reason + " +coalesced", runStatusOverride, CancellationToken.None)));
        }
    }

    /// <summary>The engine's boundary call: start a pass and forget it, but never SILENTLY. Returns
    /// the task so a shutdown path can wait for it; callers on the hot path ignore it, and because
    /// <see cref="ReconcileAsync"/> cannot throw there is no fault to observe.</summary>
    public Task<GithubMirrorPass> Fire(string reason, string? runStatusOverride = null) =>
        Track(Task.Run(() => ReconcileAsync(reason, runStatusOverride, CancellationToken.None)));

    // Every pass ever fired that has not finished. MEASURED, live: a run in once-mode returns from
    // the loop the instant its session ends, and a teardown that did not wait disposed the HttpClient
    // out from under a pass halfway through creating a board — one issue on GitHub out of three, no
    // diary, and a cancellation where a real error belonged. Tracking only the LAST fire was the
    // second version of this bug and the rig refuted that too: the last fire is usually the one that
    // was coalesced and returned instantly, while the pass that mattered was still running.
    private readonly List<Task> _fired = [];

    private Task<GithubMirrorPass> Track(Task<GithubMirrorPass> task)
    {
        lock (_fired)
        {
            _fired.RemoveAll(t => t.IsCompleted);
            _fired.Add(task);
        }
        return task;
    }

    /// <summary>Wait for every pass in flight, so a process may exit without truncating one. Bounded:
    /// the budget expiring is not an error, because the cursor did not move and the next process's
    /// run-start pass pushes the same batch. The loop re-checks after each wait, because a coalesced
    /// follow-up is born inside the pass being waited for.</summary>
    public async Task DrainAsync(TimeSpan budget)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Task[] pending;
            lock (_fired)
            {
                _fired.RemoveAll(t => t.IsCompleted);
                pending = [.. _fired];
            }
            if (pending.Length == 0) return;

            var left = budget - clock.Elapsed;
            if (left <= TimeSpan.Zero) break;
            var all = Task.WhenAll(pending);
            if (!ReferenceEquals(await Task.WhenAny(all, Task.Delay(left)).ConfigureAwait(false), all)) break;
        }
        _log($"github mirror: a pass was still running after {budget.TotalSeconds:0}s at shutdown — " +
             "the cursor did not move, so the next run pushes the same batch");
    }

    /// <summary>The run's identity as the diary header wants it. Read from the store, so a resumed
    /// run's header says what the row says rather than what this process happens to remember.</summary>
    private ArchivedRun Describe(string? statusOverride)
    {
        var row = _store.QueryRun(RunId);
        return new ArchivedRun(
            RunId: RunId,
            PlanName: row?.PlanName ?? "",
            Repo: row?.Repo ?? "",
            Branch: row?.Branch,
            EngineVersion: row?.DriverVersion,
            // The completion boundary fires while the loop is still deciding; the runs row may not
            // carry the terminal status yet, and the diary issue would then stay open on a run that
            // had finished. The caller that KNOWS says so. (KS9.1 paid for the other half of this:
            // the archive spells the status `Completed`, the task graph lower-cases its own.)
            Status: statusOverride ?? row?.Status ?? "running",
            StartedUtc: row?.StartedUtc,
            EndedUtc: row?.EndedUtc,
            LastActivityUtc: null,
            Sessions: 0, CostUsd: 0m, Tokens: 0L);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _client.Dispose();
        _gate.Dispose();
    }
}

/// <summary>What one pass did. A verdict rather than a void, so "the mirror is behind" is a fact a
/// caller can assert on instead of a line in a log file.</summary>
public sealed record GithubMirrorPass(
    string Reason, bool Ran, bool Ok, long Cursor, int Requests, string? Error, GithubSyncResult? Result)
{
    public static GithubMirrorPass Idle(string reason, string why) =>
        new(reason, Ran: false, Ok: true, Cursor: -1, Requests: 0, Error: why, Result: null);

    public static GithubMirrorPass Pushed(string reason, long cursor, int requests, GithubSyncResult result) =>
        new(reason, Ran: true, Ok: true, cursor, requests, Error: null, result);

    public static GithubMirrorPass Failed(string reason, long cursor, int requests, string error, GithubSyncResult? result) =>
        new(reason, Ran: true, Ok: false, cursor, requests, error, result);
}
