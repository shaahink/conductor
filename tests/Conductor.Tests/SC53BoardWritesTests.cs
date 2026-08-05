using Conductor.Http;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Conductor.Commands;
using Conductor.Core;
using Conductor.Core.Events;
using Conductor.Core.Http;
using Conductor.Core.Integrations;
using Conductor.Core.Orchestration;
using Conductor.Core.Planning;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// SC5.3 — the board is two-way and honest.
/// <para>Two defects, one root: the CLI owned a private write path. It could only push a card
/// forward (undoing a mis-drag needed a hand-rolled HTTP POST — round-four #2), and it printed
/// success for a transition it had silently refused (round-four #1). Both are measured here against
/// a REAL run.db and, for the ingress-parity claim, a real HttpListener — because "the two ingresses
/// share a path" is exactly the kind of statement a doc comment has lied about in this repo before.</para>
/// </summary>
public sealed class SC53BoardWritesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"conductor-sc53-{Guid.NewGuid():N}");
    private readonly SqliteRunStore _store;
    private readonly ConcurrentQueue<ControlCommand> _inbox = new();
    private readonly HttpClient _http = new();
    private const string RunId = "run-sc53";

    public SC53BoardWritesTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "TRACKER.md"), "# T\n");
        _store = new SqliteRunStore(Path.Combine(_dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        _store.InitializeRun(RunId, "sc53", _dir, "feat/sarban", "1.0");
        _store.SeedCheckpoints(RunId,
        [
            ("SC5.1", "SC5", "wait", "DONE", "abc1234", "evidence.md"),
            ("SC5.3", "SC5", "two-way board", "TODO", "-", "-"),
            ("SC5.4", "SC5", "bg mapping", "TODO", "-", "-"),
        ]);
    }

    public void Dispose()
    {
        _http.Dispose();
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string StatusOf(string id) =>
        _store.GetCheckpoints(RunId).Single(c => c.Id == id).Status;

    // ---------------------------------------------------------------- the three missing moves

    /// <summary>The moves the CLI never had. BLOCKED is the interesting one: the fold has accepted it
    /// since W1.1 and the tracker renders the label, but no write ingress would speak the word.</summary>
    [Theory]
    [InlineData("todo", "TODO")]
    [InlineData("blocked", "BLOCKED")]
    [InlineData("skipped", "SKIPPED")]
    public void EachNewMoveLandsAndReportsTheCardsRealStatus(string status, string label)
    {
        var result = TaskBoard.Move(_store, RunId, "SC5.3", status);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(status, result.Actual);
        Assert.Equal(label, StatusOf("SC5.3"));
    }

    /// <summary>Round-four #2, the reason this checkpoint exists: a card dragged to the wrong column
    /// can be put back from the shell, with no HTTP POST hand-rolled against the control plane.</summary>
    [Fact]
    public void AMisDraggedCardCanBePutBackFromTheCli()
    {
        Assert.True(TaskBoard.Move(_store, RunId, "SC5.4", "done", "sha1234", "wrong card").Ok);
        Assert.Equal("DONE", StatusOf("SC5.4"));

        var back = TaskBoard.Move(_store, RunId, "SC5.4", "todo");

        Assert.True(back.Ok, back.Message);
        Assert.Equal("TODO", StatusOf("SC5.4"));
    }

    // ---------------------------------------------------------------- post-fold truth

    /// <summary>Round-four #1. The transition is refused by the fold; what must NOT happen is the
    /// green line the CLI used to print over it. The message names the card's real status, and the
    /// exit code follows the message — a script reading $? is one of the surfaces being lied to.</summary>
    [Fact]
    public void InProgressOnAClaimedCheckpointReportsDoneAndFails()
    {
        var result = TaskBoard.Start(_store, RunId, "SC5.1");

        Assert.False(result.Ok);
        Assert.Equal("done", result.Actual);
        Assert.Contains("SC5.1 is DONE and stayed DONE", result.Message, StringComparison.Ordinal);
        Assert.Contains("--todo SC5.1", result.Message, StringComparison.Ordinal);   // the verb that does mean it
        Assert.Equal("DONE", StatusOf("SC5.1"));                                     // and the claim is intact
    }

    /// <summary>The same honesty on the CLAIM verb. skipped → done is not a legal fold transition, so
    /// `task --done` on a skipped card used to be a silent no-op under a green line — on the one verb
    /// the whole program treats as the definition of progress.</summary>
    [Fact]
    public void DoneOnASkippedCardIsRefusedInsteadOfSilentlyPrintingSuccess()
    {
        Assert.True(TaskBoard.Move(_store, RunId, "SC5.4", "skipped").Ok);

        var claim = TaskBoard.Move(_store, RunId, "SC5.4", "done", "sha9999", "evidence.md");

        Assert.False(claim.Ok);
        Assert.Equal("skipped", claim.Actual);
        Assert.Contains("SKIPPED → DONE is not a legal move", claim.Message, StringComparison.Ordinal);
        Assert.Equal("SKIPPED", StatusOf("SC5.4"));
    }

    [Fact]
    public void AMoveAgainstAnUnknownCardIsAnError()
    {
        var result = TaskBoard.Move(_store, RunId, "SC9.9", "todo");

        Assert.False(result.Ok);
        Assert.Contains("task not found: SC9.9", result.Message, StringComparison.Ordinal);
    }

    /// <summary>A legal move still carries its attribution: the claim's commit and evidence ride the
    /// SHARED event now, which is what let `task --done` give up its private write path.</summary>
    [Fact]
    public void AClaimStillCarriesItsCommitAndEvidence()
    {
        Assert.True(TaskBoard.Move(_store, RunId, "SC5.3", "done", "deadbee", "evidence/SC5.3.md").Ok);

        var row = _store.GetCheckpoints(RunId).Single(c => c.Id == "SC5.3");
        Assert.Equal("deadbee", row.Commit);
        Assert.Equal("evidence/SC5.3.md", row.Evidence);
    }

    // ---------------------------------------------------------------- one path, both ingresses

    /// <summary>The parity claim, measured rather than asserted in prose: the CLI's move and
    /// <c>POST /tasks/update</c> reach the same run.db through the same validator, and the HTTP
    /// ingress now accepts the status the CLI just wrote. Before SC5.3 this test could not exist —
    /// "blocked" was rejected at both ingresses while the fold would have accepted it.</summary>
    [Fact]
    public async Task TheCliMoveAndTheControlPlaneAgreeOnBlocked()
    {
        Assert.Contains("blocked", TaskWrites.ValidStatuses);

        var plan = new PlanConfig
        {
            Name = "sc53",
            Repo = _dir.Replace("\\", "/", StringComparison.Ordinal),
            Tracker = "TRACKER.md",
            Agent = new AgentConfig { Command = "echo", Args = ["{prompt}"] },
            Stages = [new StageConfig { Id = "SC5", Title = "board", Sessions = 1 }],
        };
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var probe = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        var server = new ControlPlaneServer(plan, new RunState { RunId = RunId }, _store, _inbox,
            new NoOpTelegramService(), NullLogger.Instance, probe);
        Assert.True(server.Start(), "control plane failed to bind");
        try
        {
            _http.DefaultRequestHeaders.Add("X-Conductor-Token", server.Token);

            // CLI ingress
            Assert.True(TaskBoard.Move(_store, RunId, "SC5.3", "blocked").Ok);
            Assert.Equal("BLOCKED", StatusOf("SC5.3"));

            // HTTP ingress, same store, same card back out of the block
            using var content = new StringContent("""{"taskId":"SC5.3","status":"in_progress"}""",
                Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(
                new Uri($"http://127.0.0.1:{server.Port}/tasks/update"), content);
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal("in_progress", doc.RootElement.GetProperty("status").GetString());
            Assert.Equal("IN PROGRESS", StatusOf("SC5.3"));
        }
        finally { server.Dispose(); }
    }

    // ---------------------------------------------------------------- amendments

    /// <summary>An acceptance correction is a record, not a replacement: the second amendment must not
    /// erase the first, and neither may erase owner context the card already carried.</summary>
    [Fact]
    public void AmendmentsAccumulateAndNeverClobberExistingContext()
    {
        ((IRunStore)_store).AppendEvent(new TaskDetailEdited { RunId = RunId, TaskId = "SC5.3", Context = "owner: start in TaskWrites" });
        _store.FlushEvents();

        var first = TaskBoard.Amend(_store, RunId, "SC5.3", "acceptance says every ingress; MCP has no --amend tool yet");
        var second = TaskBoard.Amend(_store, RunId, "SC5.3", "delivering the CLI half only, said so in the handoff");

        Assert.True(first.Ok, first.Message);
        Assert.True(second.Ok, second.Message);
        Assert.Contains("owner: start in TaskWrites", second.Actual, StringComparison.Ordinal);
        Assert.Contains("MCP has no --amend tool yet", second.Actual, StringComparison.Ordinal);
        Assert.Contains("delivering the CLI half only", second.Actual, StringComparison.Ordinal);
        Assert.Equal(2, second.Actual.Split("AMENDED ").Length - 1);
    }

    /// <summary>The correction has to reach the session that would otherwise re-encode the false
    /// premise — so it is measured where it matters: in the composed prompt, through the same
    /// <see cref="SessionRunner.BuildTaskContextSection"/> the run loop calls.</summary>
    [Fact]
    public void AnAmendmentReachesTheNextSessionsComposedPrompt()
    {
        Assert.True(TaskBoard.Amend(_store, RunId, "SC5.3", "criterion 2 assumes a bg row per session; it is per PID").Ok);

        var graph = new TaskGraph();
        graph.Fold(_store.ReadAllEvents(RunId));
        var plan = new PlanConfig
        {
            Name = "sc53", Repo = ".", Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "SC5", Title = "board", Sessions = 1 }],
        };

        var section = SessionRunner.BuildTaskContextSection(plan, graph, ["SC5.3"]);

        Assert.Contains("criterion 2 assumes a bg row per session; it is per PID", section, StringComparison.Ordinal);
        Assert.Contains("AMENDED ", section, StringComparison.Ordinal);
    }

    /// <summary>An amendment is knowledge as well as card metadata — the ledger is where the next
    /// session reads what the last one learned, the pairing `--blocked-until` already makes.</summary>
    [Fact]
    public void AnAmendmentIsAlsoWrittenToTheKnowledgeLedger()
    {
        Assert.True(TaskBoard.Amend(_store, RunId, "SC5.3", "the premise is wrong", stageId: "SC5").Ok);

        var rows = _store.QueryLedger(RunId, kind: "amendment");
        var row = Assert.Single(rows);
        Assert.Contains("SC5.3: the premise is wrong", row.Content, StringComparison.Ordinal);
        Assert.Equal("SC5", row.StageId);
    }

    // ---------------------------------------------------------------- a skipped card means something

    /// <summary>A verb that moves a card on the board and changes nothing else is a new lie, not a
    /// feature. Three surfaces had to learn the word: the generated tracker rendered SKIPPED as
    /// <b>TODO</b>, the row regex would not match the word at all (so the checkpoint silently left
    /// the parsed snapshot — and WorkGraphSync archives what the tracker stops declaring), and the
    /// scheduler asked only "is it done?", so a skipped card stayed ready work forever.</summary>
    [Fact]
    public void ASkippedCardRendersAsSkipped_ParsesBack_AndIsNotReadyWork()
    {
        var plan = new PlanConfig
        {
            Name = "sc53", Repo = _dir.Replace("\\", "/", StringComparison.Ordinal), Tracker = "TRACKER.md",
            Stages = [new StageConfig { Id = "SC5", Title = "board", Sessions = 1 }],
        };
        Assert.True(TaskBoard.Move(_store, RunId, "SC5.4", "skipped").Ok);

        TrackerGenerator.Write(plan, _store, RunId);
        var rendered = File.ReadAllText(plan.TrackerPath);
        Assert.Contains("| SC5.4 |", rendered, StringComparison.Ordinal);
        Assert.Matches(@"\|\s*SC5\.4\s*\|[^|]*\|\s*SKIPPED\s*\|", rendered);

        // …and the view still round-trips: the row parses, keeps its stage, and reads as skipped.
        var reparsed = TrackerParser.Parse(rendered);
        var row = Assert.Single(reparsed.Checkpoints, c => c.Id == "SC5.4");
        Assert.True(row.IsSkipped);
        Assert.False(row.IsOpen);
        Assert.Equal("SC5", row.StageId);

        // …so the scheduler no longer offers it as work, while the genuinely open cards remain.
        var ready = reparsed.ForStage("SC5").Where(c => c.IsOpen).Select(c => c.Id).ToList();
        Assert.DoesNotContain("SC5.4", ready);
        Assert.Contains("SC5.3", ready);
    }

    /// <summary>The other half of "settled": a stage whose remaining card was deliberately skipped
    /// must be able to COMPLETE. Without this, one `task --skipped` would hold a stage — and the
    /// plan — open forever, which is a worse failure than the one the verb removes.</summary>
    [Fact]
    public void ASkippedCardDoesNotHoldItsStageOpen()
    {
        Assert.True(TaskBoard.Move(_store, RunId, "SC5.3", "done", "sha", "e").Ok);
        Assert.True(TaskBoard.Move(_store, RunId, "SC5.4", "skipped").Ok);

        var snapshot = WorkSnapshot.Read(_store, RunId, () => new TrackerSnapshot());

        Assert.True(snapshot.StageDone("SC5"));
        Assert.True(snapshot.AllDone);
    }

    /// <summary>BLOCKED is NOT settled — blocked work is still owed, and a card parked with
    /// `--blocked` must keep its stage open and stay schedulable.</summary>
    [Fact]
    public void ABlockedCardStillHoldsItsStageOpen()
    {
        Assert.True(TaskBoard.Move(_store, RunId, "SC5.3", "done", "sha", "e").Ok);
        Assert.True(TaskBoard.Move(_store, RunId, "SC5.4", "blocked").Ok);

        var snapshot = WorkSnapshot.Read(_store, RunId, () => new TrackerSnapshot());

        Assert.False(snapshot.StageDone("SC5"));
        Assert.Contains("SC5.4", snapshot.ForStage("SC5").Where(c => c.IsOpen).Select(c => c.Id));
    }

    [Fact]
    public void AnAmendmentWithNoNoteIsRefused()
    {
        var result = TaskBoard.Amend(_store, RunId, "SC5.3", "   ");

        Assert.False(result.Ok);
        Assert.Contains("needs a note", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAmendmentAgainstAnUnknownCardIsRefused()
    {
        var result = TaskBoard.Amend(_store, RunId, "SC9.9", "no such card");

        Assert.False(result.Ok);
        Assert.Contains("task not found: SC9.9", result.Message, StringComparison.Ordinal);
    }
}
