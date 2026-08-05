using System.Collections.Concurrent;
using System.Data;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Conductor.Core.Store;

public sealed partial class SqliteRunStore
{
    private long _seq;
    private string _runId = "";

    // Channel + drain for async event writes (same pattern as EventLog).
    private readonly ConcurrentQueue<ConductorEvent> _eventQueue = new();
    private readonly CancellationTokenSource _drainCts = new();
    private Task? _drainTask;

    internal void SetRunId(string runId)
    {
        _runId = runId;
        try
        {
            var rows = Query("SELECT COALESCE(MAX(seq), 0) FROM events WHERE run_id = @runId",
                ("@runId", runId));
            _seq = rows.Count > 0 ? Convert.ToInt64(rows[0].Values.First()) : 0;
        }
        catch { _seq = 0; }
    }

    // ---------------------------------------------------------------- IEventSink.Emit

    public void Emit(ConductorEvent evt)
    {
        var stamped = evt with
        {
            Seq = Interlocked.Increment(ref _seq),
            Ts = DateTimeOffset.UtcNow,
            RunId = _runId,
        };
        _eventQueue.Enqueue(stamped);
        StartDrainIfNeeded();
    }

    // ---------------------------------------------------------------- event writes (internal)

    private void StartDrainIfNeeded()
    {
        if (_drainTask != null) return;
        lock (_eventQueue)
        {
            if (_drainTask != null) return;
#pragma warning disable MA0040 // Deliberately do NOT flow the token into Task.Run: the drain loop's final
            // flush must still run after _drainCts is cancelled at dispose. Task.Run(_, token) would skip the
            // delegate entirely when cancellation races task scheduling, silently dropping buffered events.
            _drainTask = Task.Run(() => DrainLoopAsync(_drainCts.Token));
#pragma warning restore MA0040
        }
    }

#pragma warning disable MA0045 // Drain loop sync DB writes — batch-persist inside a transaction; async overhead per-INSERT is not warranted.
    private async Task DrainLoopAsync(CancellationToken ct)
    {
        var batch = new List<ConductorEvent>();
        while (!ct.IsCancellationRequested)
        {
            batch.Clear();
            while (_eventQueue.TryDequeue(out var evt))
                batch.Add(evt);

            if (batch.Count > 0)
            {
                try { PersistBatch(batch); }
                catch (Exception ex) when (ex is SqliteException or ObjectDisposedException)
                {
                    _logger.LogError(ex, "Failed to persist {Count} events to run.db", batch.Count);
                }
            }

            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        // Final drain
        batch.Clear();
        while (_eventQueue.TryDequeue(out var evt))
            batch.Add(evt);
        if (batch.Count > 0)
        {
            try { PersistBatch(batch); }
            catch (Exception ex) when (ex is SqliteException or ObjectDisposedException)
            {
                _logger.LogError(ex, "Failed to persist final {Count} events on shutdown", batch.Count);
            }
        }
    }

    /// <summary>Drains and persists everything queued right now, on the caller's thread. Lets a
    /// control-plane write respond only once its event is durably readable, instead of racing the
    /// 200ms drain cadence. Safe alongside the drain loop: both paths dequeue from the same queue
    /// (each event lands exactly once) and <see cref="PersistBatch"/> serialises on one gate.</summary>
    public void FlushEvents()
    {
        var batch = new List<ConductorEvent>();
        while (_eventQueue.TryDequeue(out var evt))
            batch.Add(evt);
        if (batch.Count == 0) return;
        try { PersistBatch(batch); }
        catch (Exception ex) when (ex is SqliteException or ObjectDisposedException)
        {
            _logger.LogError(ex, "Failed to flush {Count} events to run.db", batch.Count);
        }
    }

    /// <summary>Serialises ALL use of the single SqliteConnection — transactions (PersistBatch),
    /// ad-hoc writes (TryExecute) and reads (Query). Microsoft.Data.Sqlite connections are not
    /// thread-safe, and the control plane's HTTP threads query concurrently with the engine's
    /// writes: without this gate that race corrupts the connection's internal command list
    /// (SqliteConnection.RemoveCommand index crash — /tasks 500s in the 2026-07-16 dogfood).</summary>
    private readonly Lock _persistGate = new();

    private void PersistBatch(List<ConductorEvent> batch)
    {
        if (batch.Count == 0) return;
        lock (_persistGate) PersistBatchLocked(batch);
    }

    private void PersistBatchLocked(List<ConductorEvent> batch)
    {
        using var tx = _conn.BeginTransaction();
        // W1.1: seq is (re)assigned HERE, from the database, inside the transaction — the Emit-time
        // stamp is only a provisional queue ordinal. Two processes share run.db (the engine and the
        // `conductor task` claim path), each with its own in-memory counter, and the events PK is
        // (seq, run_id): persist-time allocation is the only stamp that cannot collide.
        var nextSeq = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var evt in batch)
            {
                var runId = evt.RunId ?? "";
                if (!nextSeq.TryGetValue(runId, out var seq))
                {
                    using var maxCmd = _conn.CreateCommand();
                    maxCmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM events WHERE run_id = @runId";
                    maxCmd.Parameters.AddWithValue("@runId", runId);
                    seq = Convert.ToInt64(maxCmd.ExecuteScalar()!);
                }
                nextSeq[runId] = ++seq;
                var stamped = evt with { Seq = seq };

                var payload = JsonSerializer.Serialize(stamped, PlanConfig.JsonOpts);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO events (seq, ts, run_id, session_id, type, payload) " +
                    "VALUES (@seq, @ts, @runId, @sessionId, @type, @payload)";
                cmd.Parameters.AddWithValue("@seq", stamped.Seq);
                cmd.Parameters.AddWithValue("@ts", stamped.Ts.ToString("O"));
                cmd.Parameters.AddWithValue("@runId", runId);
                cmd.Parameters.AddWithValue("@sessionId", (object?)stamped.SessionId ?? DBNull.Value);
                var typeName = stamped.GetType().Name;
                cmd.Parameters.AddWithValue("@type", typeName);
                cmd.Parameters.AddWithValue("@payload", payload);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        // Keep the provisional counter at or above the durable one, so queue ordinals stay monotone
        // even after another process advanced the log underneath us.
        if (nextSeq.TryGetValue(_runId, out var latest))
        {
            long observed;
            while ((observed = Interlocked.Read(ref _seq)) < latest
                   && Interlocked.CompareExchange(ref _seq, latest, observed) != observed)
            {
            }
        }
    }
#pragma warning restore MA0045

    internal void DisposeEventsDrain()
    {
#pragma warning disable MA0040, MA0045 // Dispose path — sync cancel + wait on teardown is intentional
        _drainCts.Cancel();
        try { _drainTask?.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _drainCts.Dispose();
#pragma warning restore MA0040, MA0045
    }

    // ---------------------------------------------------------------- event reads

    void IRunStore.AppendEvent(ConductorEvent evt) => Emit(evt);

    public IReadOnlyList<ConductorEvent> ReadAllEvents(string runId)
    {
        var rows = Query(
            "SELECT type, payload FROM events WHERE run_id = @runId ORDER BY seq",
            ("@runId", runId));
        return DeserializeEvents(rows);
    }

    public IReadOnlyList<ConductorEvent> ReadEventsAfter(string runId, long afterSeq)
    {
        var rows = Query(
            "SELECT type, payload FROM events WHERE run_id = @runId AND seq > @after ORDER BY seq",
            ("@runId", runId), ("@after", afterSeq));
        return DeserializeEvents(rows);
    }

    public RunStateProjection.InterruptedSessionInfo? FindInterruptedSession(string runId)
    {
        var events = ReadAllEvents(runId);
        return RunStateProjection.FindInterruptedSession(events);
    }

    private static IReadOnlyList<ConductorEvent> DeserializeEvents(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var events = new List<ConductorEvent>(rows.Count);
        foreach (var row in rows)
        {
            try
            {
                var json = (string)row["payload"]!;
                var evt = JsonSerializer.Deserialize<ConductorEvent>(json, PlanConfig.JsonOpts);
                if (evt != null) events.Add(evt);
            }
            catch (JsonException)
            {
                // Skip torn/corrupt event — same tolerance as EventLog.ReadAll
            }
        }
        return events;
    }
}
