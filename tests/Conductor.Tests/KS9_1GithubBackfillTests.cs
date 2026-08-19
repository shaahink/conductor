using System.Net;
using System.Text.Json;
using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Integrations.Github;

namespace Conductor.Tests;

/// <summary>
/// KS9.1 — the backfill, asserted against the REQUEST BODIES it puts on the wire.
///
/// <para><b>Why a recording handler and not a mock of the client.</b> Every acceptance clause here is
/// a statement about what GitHub receives: the title shape, the label set, the <c>confirmed</c>
/// distinction, the marker that carries identity, one comment per <c>SessionFinished</c>, and — the
/// one that matters most — that a SECOND identical pass issues zero <c>POST /issues</c>. A mocked
/// client would let all six of those be true of the mock and false of the wire.</para>
///
/// <para><b>The second pass is fed the first pass's own output.</b> The fake serves back the issues
/// it was asked to create, which is what GitHub would do, so the idempotence assertion is a real
/// round trip rather than a hand-written fixture that happens to match.</para>
/// </summary>
public class KS9_1GithubBackfillTests
{
    /// <summary>The default status is spelled the way the ARCHIVE spells it — <c>Completed</c>, the
    /// RunStatus enum's own casing — not the way the task graph spells its lower-case statuses. The
    /// first live backfill left a finished run's diary issue OPEN because an ordinal comparison here
    /// was written in the wrong half's vocabulary, and the fixture agreed with the bug.</summary>
    private static ArchivedRun Run(string status = "Completed") => new(
        RunId: "run-abc123456789", PlanName: "karvansara", Repo: "C:/code/conductor", Branch: "feat/karvansara",
        EngineVersion: "0.4.1", Status: status, StartedUtc: "2026-08-01T00:00:00Z", EndedUtc: "2026-08-02T00:00:00Z",
        LastActivityUtc: null, Sessions: 2, CostUsd: 12.5m, Tokens: 1000);

    /// <summary>Two checkpoints in one stage: one confirmed-done, one still open — the two states the
    /// mirror must not flatten.</summary>
    private static List<ConductorEvent> Log() =>
    [
        new TaskAdded { TaskId = "KS9.1", CheckpointId = "KS9.1", Title = "the client", Source = "plan", Kind = "checkpoint", StageId = "KS9" },
        new TaskAdded { TaskId = "KS9.2", CheckpointId = "KS9.2", Title = "the live mirror", Source = "plan", Kind = "checkpoint", StageId = "KS9" },
        new TaskStatusChanged { TaskId = "KS9.1", Status = "in_progress", Source = "agent" },
        new TaskStatusChanged { TaskId = "KS9.1", Status = "done", Source = "agent", Commit = "abcdef1234567890", Evidence = ".conductor/evidence/KS9/ks9-1.md" },
        new CheckpointConfirmed { CheckpointId = "KS9.1", StageId = "KS9" },
        new SessionFinished { Number = 1, StageId = "KS9", Outcome = "Delivered", NewlyDone = ["KS9.1"], NewCommits = ["abcdef1234567890"], CostUsd = 6.25m, TokensInput = 100, TokensCacheRead = 900 },
        new SessionFinished { Number = 2, StageId = "KS9", Outcome = "NoProgress" },
    ];

    /// <summary>One pass against a fake the CALLER owns, so the same fake can be driven twice — the
    /// second pass is the whole point, and it must see the first pass's issues.</summary>
    private static async Task<GithubSyncResult> BackfillAsync(
        FakeGithub fake, List<ConductorEvent>? log = null, bool diary = true, bool dryRun = false)
    {
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);
        var sync = new GithubBoardSync(client, "owner/scratch", "conductor");
        return await sync.BackfillAsync(log ?? Log(), Run(), "0.4.1+abc", diary, dryRun).ConfigureAwait(true);
    }

    [Fact]
    public async Task OneIssuePerCheckpointCarriesTitleLabelsMilestoneAndMarker()
    {
        using var fake = new FakeGithub();
        var result = await BackfillAsync(fake).ConfigureAwait(true);

        Assert.Empty(result.Errors);
        var created = fake.Posted("/repos/owner/scratch/issues");
        // Two checkpoints plus the run diary issue.
        Assert.Equal(3, created.Count);

        var done = created.Single(b => Title(b).StartsWith("KS9.1", StringComparison.Ordinal));
        Assert.Equal("KS9.1 — the client", Title(done));
        Assert.Contains("<!-- conductor:task KS9.1 -->", Body(done), StringComparison.Ordinal);
        Assert.Contains("conductor:status:done", Labels(done));
        Assert.Contains("conductor:source:plan", Labels(done));
        Assert.Contains("conductor:confirmed", Labels(done));
        Assert.Equal(fake.MilestoneNumberFor("KS9"), Milestone(done));

        // The DONE ✓ distinction must survive the mirror: KS9.2 is not confirmed and must not wear
        // the label. A board that showed both as the same colour would be the exact lie W1.1 exists
        // to prevent — a claim is not a confirmation.
        var open = created.Single(b => Title(b).StartsWith("KS9.2", StringComparison.Ordinal));
        Assert.Contains("conductor:status:todo", Labels(open));
        Assert.DoesNotContain("conductor:confirmed", Labels(open));
    }

    [Fact]
    public async Task ADoneCheckpointIsClosedAndAnOpenOneIsNot()
    {
        using var fake = new FakeGithub();
        await BackfillAsync(fake).ConfigureAwait(true);

        var closed = fake.Requests
            .Where(r => r.Method == "PATCH" && r.Body.Contains("\"state\":\"closed\"", StringComparison.Ordinal))
            .ToList();
        // The done checkpoint, and the run diary issue for a completed run. Nothing else.
        Assert.Equal(2, closed.Count);
        Assert.Contains(closed, r => r.Path == "/repos/owner/scratch/issues/" + fake.NumberOfTask("KS9.1"));
    }

    [Fact]
    public async Task TheDiaryIsOneIssueWithOneCommentPerFinishedSession()
    {
        using var fake = new FakeGithub();
        await BackfillAsync(fake).ConfigureAwait(true);

        var runIssue = fake.Posted("/repos/owner/scratch/issues")
            .Single(b => Body(b).Contains("<!-- conductor:run run-abc123456789 -->", StringComparison.Ordinal));
        Assert.Contains("**Plan** karvansara", Body(runIssue), StringComparison.Ordinal);
        Assert.Contains("**Branch** feat/karvansara", Body(runIssue), StringComparison.Ordinal);
        Assert.Contains("**Engine** 0.4.1+abc", Body(runIssue), StringComparison.Ordinal);

        var comments = fake.Requests
            .Where(r => r.Method == "POST" && r.Path.EndsWith("/comments", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, comments.Count);
        Assert.Contains(comments, c => c.Body.Contains("conductor:session run-abc123456789#1", StringComparison.Ordinal));
        Assert.Contains(comments, c => c.Body.Contains("conductor:session run-abc123456789#2", StringComparison.Ordinal));
        Assert.Contains(comments, c => c.Body.Contains("newly done: KS9.1", StringComparison.Ordinal));
    }

    /// <summary>The bar the whole checkpoint turns on. A second identical backfill must create
    /// nothing and comment nothing — and it must be true because the mirror LOOKED, not because a
    /// local cache happened to survive: this run's map is never written at all.</summary>
    [Fact]
    public async Task ASecondIdenticalBackfillCreatesNothing()
    {
        using var fake = new FakeGithub();
        var first = await BackfillAsync(fake).ConfigureAwait(true);
        Assert.Equal(3, first.Created.Count);
        fake.Requests.Clear();

        var second = await BackfillAsync(fake).ConfigureAwait(true);

        Assert.Empty(second.Created);
        Assert.Empty(second.Comments);
        Assert.Empty(second.Errors);
        Assert.Equal(3, second.Unchanged.Count);
        Assert.DoesNotContain(fake.Requests, r => r.Method == "POST");
        Assert.DoesNotContain(fake.Requests, r => r.Method == "PATCH");
    }

    /// <summary>A checkpoint that left the plan is closed, labelled and COMMENTED — never deleted.
    /// The history on the issue is the reason the mirror exists.</summary>
    [Fact]
    public async Task AnItemWhoseDeclarationDisappearedIsRetiredNotDeleted()
    {
        using var fake = new FakeGithub();
        await BackfillAsync(fake).ConfigureAwait(true);
        fake.Requests.Clear();

        var shrunk = Log().Where(e => e is not TaskAdded { TaskId: "KS9.2" }).ToList();
        var result = await BackfillAsync(fake, shrunk).ConfigureAwait(true);

        Assert.Contains("KS9.2", result.Retired);
        Assert.DoesNotContain(fake.Requests, r => r.Method == "DELETE");
        var patch = fake.Requests.Single(r => r.Method == "PATCH"
            && r.Path == "/repos/owner/scratch/issues/" + fake.NumberOfTask("KS9.2"));
        Assert.Contains("conductor:retired", patch.Body, StringComparison.Ordinal);
        Assert.Contains("\"state\":\"closed\"", patch.Body, StringComparison.Ordinal);
        Assert.Contains(fake.Requests, r => r.Method == "POST"
            && r.Path.EndsWith("/comments", StringComparison.Ordinal)
            && r.Body.Contains("Retired by conductor", StringComparison.Ordinal));
    }

    /// <summary>A human's own labels are not conductor's to remove. An upsert that resent only its
    /// own label set would strip a repo's triage on every status change.</summary>
    [Fact]
    public async Task AForeignLabelSurvivesAStatusChange()
    {
        using var fake = new FakeGithub();
        await BackfillAsync(fake).ConfigureAwait(true);
        fake.AddLabel(fake.NumberOfTask("KS9.2"), "needs-discussion");
        fake.Requests.Clear();

        var advanced = Log();
        advanced.Add(new TaskStatusChanged { TaskId = "KS9.2", Status = "in_progress", Source = "agent" });
        await BackfillAsync(fake, advanced).ConfigureAwait(true);

        var patch = fake.Requests.Single(r => r.Method == "PATCH"
            && r.Path == "/repos/owner/scratch/issues/" + fake.NumberOfTask("KS9.2"));
        Assert.Contains("needs-discussion", patch.Body, StringComparison.Ordinal);
        Assert.Contains("conductor:status:in_progress", patch.Body, StringComparison.Ordinal);
    }

    /// <summary>A finished run's diary issue is CLOSED, whatever case the archive stored the status
    /// in. Both spellings are asserted because the two halves of this codebase genuinely use two
    /// vocabularies, and the mirror sits across the seam.</summary>
    [Theory]
    [InlineData("Completed", true)]
    [InlineData("completed", true)]
    [InlineData("Aborted", true)]
    [InlineData("running", false)]
    public void TheDiaryIssueClosesWithTheRun(string status, bool shouldClose)
    {
        var run = Run(status);
        var diary = GithubBoardPlan.Diary(Log(), run, "0.4.1+abc");
        Assert.Equal(shouldClose, diary.Closed);
    }

    /// <summary>A dry run reconciles and reports, and puts nothing on the wire that writes.</summary>
    [Fact]
    public async Task ADryRunWritesNothing()
    {
        using var fake = new FakeGithub();
        var result = await BackfillAsync(fake, dryRun: true).ConfigureAwait(true);

        Assert.Equal(3, result.Created.Count);
        Assert.All(fake.Requests, r => Assert.Equal("GET", r.Method));
    }

    /// <summary>A transport failure is an ANSWER, not an exception. The mirror's whole failure
    /// posture — "the run is unharmed, the board converges later" — starts here.</summary>
    [Fact]
    public async Task ANetworkFailureIsReturnedNotThrown()
    {
        using var dead = new DeadHandler();
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), dead, disposeHandler: false);
        var result = await new GithubBoardSync(client, "owner/scratch", "conductor")
            .BackfillAsync(Log(), Run(), "0.4.1+abc", includeDiary: true, dryRun: false).ConfigureAwait(true);

        Assert.False(result.Ok);
        Assert.Single(result.Errors);
        Assert.Empty(result.Created);
    }

    // ── reading the recorded bodies ──────────────────────────────────────────────────────────────

    private static string Title(string body) => Field(body).GetProperty("title").GetString() ?? "";
    private static string Body(string body) => Field(body).GetProperty("body").GetString() ?? "";
    private static int? Milestone(string body) =>
        Field(body).TryGetProperty("milestone", out var m) ? m.GetInt32() : null;
    private static List<string> Labels(string body) =>
        Field(body).TryGetProperty("labels", out var l)
            ? [.. l.EnumerateArray().Select(e => e.GetString() ?? "")]
            : [];
    private static JsonElement Field(string body) => JsonDocument.Parse(body).RootElement;

    private sealed class DeadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }

    private sealed class StatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    /// <summary>A 4xx must not be read as "nothing there" — it must be reported with the status and
    /// the URL, because a 403 without a User-Agent is the single most mis-diagnosed condition in
    /// this integration.</summary>
    [Fact]
    public async Task AnHttpErrorCarriesTheStatusAndTheUrl()
    {
        using var forbidden = new StatusHandler(HttpStatusCode.Forbidden, "{\"message\":\"Bad credentials\"}");
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), forbidden, disposeHandler: false);
        var (issues, error) = await client.ListIssuesAsync("owner/scratch").ConfigureAwait(true);

        Assert.Null(issues);
        Assert.NotNull(error);
        Assert.Contains("403", error, StringComparison.Ordinal);
        Assert.Contains("/repos/owner/scratch/issues", error, StringComparison.Ordinal);
        Assert.Contains("Bad credentials", error, StringComparison.Ordinal);
    }

    /// <summary>GitHub answers 403 to a request with no User-Agent. It is set once, in the client;
    /// this is the assertion that keeps it there.</summary>
    [Fact]
    public async Task EveryRequestCarriesAUserAgentAndTheJsonAcceptHeader()
    {
        using var fake = new FakeGithub();
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);
        await client.ListIssuesAsync("owner/scratch").ConfigureAwait(true);

        var request = fake.Requests[0];
        Assert.Contains("conductor/", request.UserAgent, StringComparison.Ordinal);
        Assert.Contains("application/vnd.github+json", request.Accept, StringComparison.Ordinal);
        Assert.Equal("Bearer t", request.Authorization);
    }
}
