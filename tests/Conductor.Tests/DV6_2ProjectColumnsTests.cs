using Conductor.Core.Events;
using Conductor.Core.History;
using Conductor.Core.Integrations.Github;

namespace Conductor.Tests;

/// <summary>
/// DV6.2 — the columns. KS9.3 refused to write a Projects v2 mutation path it could not exercise
/// even once; this is that path, exercised.
///
/// <para><b>Why the proof is a stub and what makes it honest.</b> The machine's token still lacks the
/// classic <c>project</c> scope (measured 2026-08-26: delete_repo, gist, read:org, repo, user,
/// workflow), so no live board could be written without an interactive owner act. What WAS done live,
/// read-only, is the half that a stub cannot vouch for: each of the four GraphQL documents below was
/// sent to api.github.com and came back <c>INSUFFICIENT_SCOPES</c> — GitHub validating the document
/// against its schema and THEN refusing the token — and both mutation input shapes were confirmed by
/// introspection, which needs no scope at all. The transcript is the checkpoint's evidence. So the
/// stub proves the LOGIC (columns, idempotence, refusals) and the live probe proves the WIRE.</para>
///
/// <para><b>The claim that carries the most weight is the second pass.</b> Bug #79 is this same
/// integration duplicating a whole board because it answered "have I already written this" from a
/// read replica. The project half is built so that it cannot: <c>addProjectV2ItemById</c> is
/// idempotent by GitHub's contract, so a stale listing costs redundant writes and never a second
/// item. Both halves of that are asserted here.</para>
/// </summary>
public sealed class DV6_2ProjectColumnsTests
{
    private const string Repo = "owner/scratch";
    private const int ProjectNumber = 7;

    /// <summary>Verbatim from api.github.com on 2026-08-26, with this repo's own token. It is the
    /// sentence an owner will actually meet, and it names both the scope and the page to grant it on
    /// — which is why the client passes GraphQL error messages through untouched.</summary>
    private const string LiveScopeError =
        "Your token has not been granted the required scopes to execute this query. " +
        "The 'id' field requires one of the following scopes: ['read:project'], but your token has " +
        "only been granted the: ['delete_repo', 'gist', 'read:org', 'repo', 'user', 'workflow'] " +
        "scopes. Please modify your token's scopes at: https://github.com/settings/tokens.";

    // ── the columns themselves, end to end through the reconciler ─────────────────────────────────

    /// <summary>The feature, in one assertion per column: a checkpoint's STATUS decides which column
    /// its issue lands in, and the board's own state says so afterwards.</summary>
    [Fact]
    public async Task EachCheckpointLandsInTheColumnItsStatusNames()
    {
        using var fake = new FakeGithub();
        var result = await SyncAsync(fake, Board()).ConfigureAwait(true);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Project);
        Assert.Empty(result.Project!.Errors);
        Assert.Equal("Divan", result.Project.ProjectTitle);

        Assert.Equal("Todo", fake.ColumnOf(fake.NumberOfTask("DV6.4")));
        Assert.Equal("In Progress", fake.ColumnOf(fake.NumberOfTask("DV6.3")));
        Assert.Equal("Done", fake.ColumnOf(fake.NumberOfTask("DV6.1")));
        Assert.Equal(3, fake.BoardItemCount);
        Assert.Equal(3, result.Project.Added.Count);
        Assert.Equal(3, result.Project.Moved.Count);
    }

    /// <summary>The idempotence bar, stated as a number about WRITES. A pass that looked and left
    /// must be distinguishable from one that wrote the whole board again, and a request count cannot
    /// tell them apart.</summary>
    [Fact]
    public async Task ASecondPassMovesNothingAndIssuesZeroMutations()
    {
        using var fake = new FakeGithub();
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);
        var sync = Sync(client);

        await BackfillAsync(sync, Board()).ConfigureAwait(true);
        var mutationsAfterFirst = client.MutationCount;
        Assert.True(mutationsAfterFirst > 0, "the first pass must actually write the board");

        var second = await BackfillAsync(sync, Board()).ConfigureAwait(true);

        Assert.Equal(mutationsAfterFirst, client.MutationCount);
        Assert.Equal(3, second.Project!.Unchanged.Count);
        Assert.Empty(second.Project.Added);
        Assert.Empty(second.Project.Moved);
    }

    /// <summary>
    /// Bug #79's lesson, applied to the half that was built after it. The board's item listing is a
    /// read replica; with it stale, this pass believes nothing is on the board and adds everything
    /// again. The bar is not "it makes no request" — it is that the BOARD is unharmed, because
    /// <c>addProjectV2ItemById</c> answers with the item that is already there.
    /// </summary>
    [Fact]
    public async Task AStaleBoardListingCostsRedundantWritesAndCannotMintASecondItem()
    {
        using var fake = new FakeGithub();
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);
        var sync = Sync(client);

        await BackfillAsync(sync, Board()).ConfigureAwait(true);
        Assert.Equal(3, fake.BoardItemCount);

        fake.ProjectItemsAreStale = true;
        var second = await BackfillAsync(sync, Board()).ConfigureAwait(true);

        Assert.Equal(3, fake.BoardItemCount);
        Assert.Equal("Done", fake.ColumnOf(fake.NumberOfTask("DV6.1")));
        Assert.Empty(second.Project!.Errors);
        // It DID write — three adds and three field sets — and that is the honest cost of a stale
        // replica. What it did not do is duplicate the board.
        Assert.Equal(3, second.Project.Added.Count);
    }

    /// <summary>A card that moves gets moved: the fold changes, and the next pass carries the issue
    /// across a column. This is the whole point of a Kanban mirror and it is one assertion.</summary>
    [Fact]
    public async Task AStatusChangeBetweenPassesMovesTheCardAcrossColumns()
    {
        using var fake = new FakeGithub();
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);
        var sync = Sync(client);

        await BackfillAsync(sync, Board()).ConfigureAwait(true);
        Assert.Equal("Todo", fake.ColumnOf(fake.NumberOfTask("DV6.4")));

        var moved = Board();
        moved.Add(new TaskStatusChanged { TaskId = "DV6.4", Status = "done", Source = "engine" });
        var second = await BackfillAsync(sync, moved).ConfigureAwait(true);

        Assert.Equal("Done", fake.ColumnOf(fake.NumberOfTask("DV6.4")));
        Assert.Contains("DV6.4", second.Project!.Moved, StringComparer.Ordinal);
        Assert.Empty(second.Project.Added);
    }

    /// <summary>The ledger rides along. DV6.1 made bugs and followups their own issue class; a board
    /// that showed the checkpoints and not the backlog would be half a board.</summary>
    [Fact]
    public async Task LedgerIssuesGetColumnsToo()
    {
        using var fake = new FakeGithub();
        var ledger = new List<GithubLedgerCard>
        {
            new("bug:79", GithubIdentity.BugMarker(79), "the duplicate",
                GithubIdentity.BugMarker(79) + " open", ["conductor:bug"], false, true),
            new("bug:12", GithubIdentity.BugMarker(12), "long fixed",
                GithubIdentity.BugMarker(12) + " fixed", ["conductor:bug"], true, true),
        };
        var result = await SyncAsync(fake, Board(), ledger).ConfigureAwait(true);

        Assert.Equal("Todo", fake.ColumnOf(fake.NumberOfMarker(GithubIdentity.BugMarker(79))));
        Assert.Equal("Done", fake.ColumnOf(fake.NumberOfMarker(GithubIdentity.BugMarker(12))));
        Assert.Empty(result.Project!.Errors);
    }

    // ── the columns a board does not have ─────────────────────────────────────────────────────────

    /// <summary>GitHub's default template has no word for "blocked". Leaving those cards off the
    /// board would hide the one card an operator opens a board to find, so they fall back — and the
    /// fallback is SAID, once, naming both the missing column and the one used instead.</summary>
    [Fact]
    public async Task ABlockedCardFallsBackToInProgressAndTheFallbackIsSaidOutLoud()
    {
        using var fake = new FakeGithub();
        var log = Board();
        log.Add(new TaskStatusChanged { TaskId = "DV6.4", Status = "blocked", Source = "engine" });
        var result = await SyncAsync(fake, log).ConfigureAwait(true);

        Assert.Equal("In Progress", fake.ColumnOf(fake.NumberOfTask("DV6.4")));
        var note = Assert.Single(result.Project!.Notes);
        Assert.Contains("no 'Blocked' option on this board", note, StringComparison.Ordinal);
        Assert.Contains("'In Progress'", note, StringComparison.Ordinal);
    }

    /// <summary>A board whose columns are Now / Next / Later matches nothing conductor knows. The
    /// cards are still ON the board — visible, in its default column — and the answer names the
    /// status AND what the board offered, which is enough to act on without opening GitHub.</summary>
    [Fact]
    public async Task AStatusWithNoColumnAtAllIsUnplacedAndNamedRatherThanGuessedAt()
    {
        using var fake = new FakeGithub
        {
            ProjectOptions = [("o1", "Now"), ("o2", "Next"), ("o3", "Later")],
        };
        var result = await SyncAsync(fake, Board()).ConfigureAwait(true);

        Assert.Equal(3, fake.BoardItemCount);
        Assert.Null(fake.ColumnOf(fake.NumberOfTask("DV6.1")));
        Assert.Equal(3, result.Project!.Unplaced.Count);
        Assert.Contains(result.Project.Notes, n => n.Contains("Now, Next, Later", StringComparison.Ordinal));
        Assert.Contains(result.Project.Notes, n => n.Contains("no option on this board", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABoardWithNoStatusFieldIsNamedRatherThanSearchedForByShape()
    {
        using var fake = new FakeGithub { NoStatusField = true };
        var result = await SyncAsync(fake, Board()).ConfigureAwait(true);

        var error = Assert.Single(result.Project!.Errors);
        Assert.Contains("no single-select field named 'Status'", error, StringComparison.Ordinal);
        Assert.Equal(0, fake.BoardItemCount);
    }

    [Fact]
    public async Task AProjectNumberThatDoesNotExistSaysSoAndSaysWhereTheNumberComesFrom()
    {
        using var fake = new FakeGithub { NoSuchProject = true };
        var result = await SyncAsync(fake, Board()).ConfigureAwait(true);

        var error = Assert.Single(result.Project!.Errors);
        Assert.Contains("there is no project #7 on 'owner'", error, StringComparison.Ordinal);
        Assert.Contains("the number is the one in the project url", error, StringComparison.Ordinal);
    }

    // ── the refusal, where it moved to ────────────────────────────────────────────────────────────

    /// <summary>
    /// A GraphQL failure arrives as HTTP <b>200</b> with an <c>errors</c> array. A client that checked
    /// the status code would read "insufficient scopes" as a board that mirrored fine — which is the
    /// exact failure mode this whole plan was started over. The message is passed through verbatim
    /// because GitHub's own sentence names the scope and the page to grant it on.
    /// </summary>
    [Fact]
    public async Task TheScopeErrorGitHubActuallySendsIsSurfacedVerbatimDespiteTheTwoHundred()
    {
        using var fake = new FakeGithub { ProjectError = LiveScopeError };
        var result = await SyncAsync(fake, Board()).ConfigureAwait(true);

        var error = Assert.Single(result.Project!.Errors);
        Assert.Contains("INSUFFICIENT_SCOPES", error, StringComparison.Ordinal);
        Assert.Contains("read:project", error, StringComparison.Ordinal);
        Assert.Contains("https://github.com/settings/tokens", error, StringComparison.Ordinal);
        Assert.False(result.Ok);
    }

    /// <summary>KS9.2's posture, unchanged by this checkpoint: a run must never lose a working issue
    /// board over an extra it cannot have. The project half fails completely and every issue is still
    /// on the repository, in the right state.</summary>
    [Fact]
    public async Task AProjectHalfThatFailsCompletelyLeavesTheIssueBoardWhole()
    {
        using var fake = new FakeGithub { ProjectError = LiveScopeError };
        var result = await SyncAsync(fake, Board()).ConfigureAwait(true);

        Assert.Equal(3, result.Created.Count);
        Assert.Empty(result.Errors);
        Assert.False(fake.IsOpen(fake.NumberOfTask("DV6.1")));
        Assert.True(fake.IsOpen(fake.NumberOfTask("DV6.4")));
    }

    /// <summary>The sentence KS9.3 left behind is gone, and the one that replaced it is a statement
    /// about a SCOPE rather than about a missing feature. Asserted on the constant because three
    /// surfaces print it and none of them probe.</summary>
    [Fact]
    public void TheStandingSentenceIsAboutTheScopeAndNoLongerAboutAnUnbuiltFeature()
    {
        Assert.Contains("is attempted at each boundary", GithubProjects.NeedsScopeLine, StringComparison.Ordinal);
        Assert.Contains("'project' scope", GithubProjects.NeedsScopeLine, StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented", GithubProjects.NeedsScopeLine, StringComparison.OrdinalIgnoreCase);
    }

    // ── the documents, pinned to what the live schema confirmed ───────────────────────────────────

    /// <summary>
    /// The four GraphQL documents, pinned field by field to what api.github.com confirmed on
    /// 2026-08-26. A stub will happily answer a document the real API would reject, so this is the
    /// test that keeps the stubbed proof honest: change any of these names and the live run breaks
    /// while every other test in this file still passes.
    /// </summary>
    [Fact]
    public void TheFourDocumentsCarryTheNamesTheLiveSchemaConfirmed()
    {
        // Reads — each returned INSUFFICIENT_SCOPES, which is validation passing and authorisation
        // failing, in that order.
        Assert.Contains("repositoryOwner(login:$owner)", GithubProjectSync.ResolveQuery, StringComparison.Ordinal);
        Assert.Contains("... on ProjectV2Owner", GithubProjectSync.ResolveQuery, StringComparison.Ordinal);
        Assert.Contains("projectV2(number:$number)", GithubProjectSync.ResolveQuery, StringComparison.Ordinal);
        Assert.Contains("... on ProjectV2SingleSelectField", GithubProjectSync.ResolveQuery, StringComparison.Ordinal);
        Assert.Contains("fieldValueByName(name:\"Status\")", GithubProjectSync.ItemsQuery, StringComparison.Ordinal);
        Assert.Contains("ProjectV2ItemFieldSingleSelectValue", GithubProjectSync.ItemsQuery, StringComparison.Ordinal);

        // Writes — input shapes confirmed by introspection, which needs no scope, so nothing was
        // written to learn them.
        Assert.Contains("addProjectV2ItemById(input:{projectId:$project,contentId:$content})",
            GithubProjectSync.AddItemMutation, StringComparison.Ordinal);
        Assert.Contains("updateProjectV2ItemFieldValue(input:{projectId:$project,itemId:$item,fieldId:$field,",
            GithubProjectSync.SetStatusMutation, StringComparison.Ordinal);
        Assert.Contains("value:{singleSelectOptionId:$option}", GithubProjectSync.SetStatusMutation, StringComparison.Ordinal);
    }

    /// <summary>Mutations are counted from the text actually sent, so the idempotence number cannot
    /// drift from what went on the wire.</summary>
    [Fact]
    public async Task OnlyDocumentsThatAreMutationsCountAsMutations()
    {
        using var fake = new FakeGithub();
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);

        await client.GraphQlAsync(GithubProjectSync.ResolveQuery,
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["owner"] = "owner", ["number"] = 7 })
            .ConfigureAwait(true);
        Assert.Equal(0, client.MutationCount);

        await client.GraphQlAsync(GithubProjectSync.AddItemMutation,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            { ["project"] = FakeGithub.ProjectNodeId, ["content"] = FakeGithub.NodeId(101) })
            .ConfigureAwait(true);
        Assert.Equal(1, client.MutationCount);
    }

    /// <summary>
    /// MEASURED on the live rig, and the reason this dedupe exists: <c>.conductor/followups.md</c>
    /// carries 91 rows for 55 distinct ids, so the ledger plan hands the board the same issue more
    /// than once — and rows for one id can disagree about whether it is closed. Without this, eleven
    /// cards were reported "moved" on every pass, oscillating between two columns forever. One
    /// placement per ISSUE, and the number dropped is said rather than folded away.
    /// </summary>
    [Fact]
    public async Task TwoCardsNamingOneIssueArePlacedOnceAndTheDuplicateIsSaidOutLoud()
    {
        using var fake = new FakeGithub();
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);
        var project = new GithubProjectSync(client, "owner", ProjectNumber);

        var pass = await project.PlaceAsync(
            [
                new GithubProjectPlacement("followup:FU-1", 100, FakeGithub.NodeId(100), "todo"),
                new GithubProjectPlacement("followup:FU-1", 100, FakeGithub.NodeId(100), "done"),
            ], dryRun: false).ConfigureAwait(true);

        Assert.Single(pass.Added);
        Assert.Equal(1, fake.BoardItemCount);
        Assert.Equal("Todo", fake.ColumnOf(100));
        Assert.Contains(pass.Notes, n => n.Contains("1 of 2 cards named an issue", StringComparison.Ordinal));
    }

    // ── the column table, which is pure ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("todo", "Todo")]
    [InlineData("in_progress", "In Progress")]
    [InlineData("done", "Done")]
    // Case and spacing are the board owner's business, not a reason to miss a column.
    [InlineData("in_progress", "in progress")]
    [InlineData("done", "DONE")]
    public void AStatusMatchesItsColumnWhateverTheBoardSpellsIt(string status, string offered)
    {
        var (name, fallback) = GithubProjectColumns.Resolve(status, [offered]);

        Assert.Equal(offered, name);
        Assert.False(fallback);
    }

    [Fact]
    public void AFallbackIsReportedAsOneAndTheFirstChoiceIsNotAFallback()
    {
        Assert.False(GithubProjectColumns.Resolve("blocked", ["Blocked", "In Progress"]).Fallback);
        Assert.True(GithubProjectColumns.Resolve("blocked", ["In Progress"]).Fallback);
        Assert.True(GithubProjectColumns.Resolve("skipped", ["Todo", "Done"]).Fallback);
        Assert.Null(GithubProjectColumns.Resolve("blocked", ["Now", "Later"]).Name);
        Assert.Null(GithubProjectColumns.Resolve("something_new", ["Todo", "Done"]).Name);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Three checkpoints, one per column of GitHub's default template.</summary>
    private static List<ConductorEvent> Board() =>
    [
        new TaskAdded { TaskId = "DV6.1", CheckpointId = "DV6.1", Title = "the issue class", Source = "plan", Kind = "checkpoint", StageId = "DV6" },
        new TaskAdded { TaskId = "DV6.3", CheckpointId = "DV6.3", Title = "the page", Source = "plan", Kind = "checkpoint", StageId = "DV6" },
        new TaskAdded { TaskId = "DV6.4", CheckpointId = "DV6.4", Title = "sarif", Source = "plan", Kind = "checkpoint", StageId = "DV6" },
        new TaskStatusChanged { TaskId = "DV6.1", Status = "done", Source = "engine" },
        new TaskStatusChanged { TaskId = "DV6.3", Status = "in_progress", Source = "engine" },
    ];

    private static GithubBoardSync Sync(GithubClient client) =>
        new(client, Repo, "conductor", map: null,
            new GithubProjectSync(client, Repo.Split('/', 2)[0], ProjectNumber));

    private static async Task<GithubSyncResult> SyncAsync(
        FakeGithub fake, List<ConductorEvent> log, IReadOnlyList<GithubLedgerCard>? ledger = null)
    {
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);
        return await BackfillAsync(Sync(client), log, ledger).ConfigureAwait(true);
    }

    private static Task<GithubSyncResult> BackfillAsync(
        GithubBoardSync sync, List<ConductorEvent> log, IReadOnlyList<GithubLedgerCard>? ledger = null) =>
        sync.BackfillAsync(
            log,
            new ArchivedRun(
                RunId: "run-dv62000000", PlanName: "divan", Repo: "C:/code/conductor", Branch: "feat/divan",
                EngineVersion: "0.4.1", Status: "Running", StartedUtc: null, EndedUtc: null,
                LastActivityUtc: null, Sessions: 1, CostUsd: 0m, Tokens: 0),
            "0.4.1", includeDiary: false, dryRun: false, ledger);
}
