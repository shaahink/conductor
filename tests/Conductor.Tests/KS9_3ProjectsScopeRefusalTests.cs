using System.Net;
using Conductor.Core;
using Conductor.Core.Integrations;
using Conductor.Core.Integrations.Github;
using Conductor.Core.Store;
using Conductor.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Conductor.Tests;

/// <summary>
/// KS9.3 — the Projects v2 gate, which is the whole of the project half: no GraphQL mutation path
/// was merged, because the scope needed to exercise one has not been granted and this stage's
/// contract makes half-done worse than skipped.
///
/// <para><b>These are text tests on purpose.</b> The acceptance is not "it stops" — anything stops.
/// It is that the refusal names four things: the scopes OBSERVED, the scope REQUIRED, WHERE the
/// token came from, and the exact one-time command the owner runs. A refusal missing any one of
/// them sends an owner to the wrong place, which is the failure this checkpoint is really about, so
/// the sentence itself is the thing under test.</para>
///
/// <para><b>And they count requests.</b> "Zero mutations without the scope" is checked by recording
/// every request the gate makes and asserting they are all GETs — a scope check that learned its
/// answer by attempting a write would already have written on the tokens that DO carry the scope.</para>
/// </summary>
public sealed class KS9_3ProjectsScopeRefusalTests
{
    /// <summary>Verbatim from <c>gh auth status</c> on the owner's machine, 2026-08-15 — the live
    /// refusal branch. Hard-coded so a future reader can see exactly which set produced it.</summary>
    private const string MachineScopes = "delete_repo, gist, read:org, repo, user, workflow";

    private static GithubConfig ProjectBoard(int number = 7) => new()
    {
        Enabled = true,
        Board = GithubConfig.BoardIssuesAndProject,
        ProjectNumber = number,
    };

    // ── the refusal branch, which is the live one ────────────────────────────────────────────────

    [Fact]
    public async Task TheRefusalNamesTheScopesObservedTheScopeRequiredTheSourceAndTheCommand()
    {
        using var github = new Recorder(MachineScopes);
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), github, disposeHandler: false);

        var refusal = await GithubProjects
            .PreflightAsync(client, ProjectBoard(), GithubIdentity.TokenEnvVar).ConfigureAwait(true);
        var text = string.Join("\n", refusal);

        // (a) the scopes observed — every one of them, so an owner can see what they DO have.
        foreach (var scope in MachineScopes.Split(',', StringSplitOptions.TrimEntries))
            Assert.Contains(scope, text, StringComparison.Ordinal);
        // (b) the scope required, and why REST cannot substitute for it.
        Assert.Contains("'project' scope", text, StringComparison.Ordinal);
        Assert.Contains("GraphQL-only", text, StringComparison.Ordinal);
        // (c) where the token came from.
        Assert.Contains(GithubIdentity.TokenEnvVar, text, StringComparison.Ordinal);
        // (d) the exact one-time command, and that conductor will not run it.
        Assert.Contains("gh auth refresh -s project", text, StringComparison.Ordinal);
        Assert.Contains("conductor will not run that", text, StringComparison.Ordinal);
        // and the way forward that works today.
        Assert.Contains("github.board to 'issues'", text, StringComparison.Ordinal);
    }

    /// <summary>The secrets file is the other source, and a refusal that named the environment
    /// variable when the token came from a file would send the owner to edit the wrong thing.</summary>
    [Fact]
    public async Task TheRefusalNamesTheSecretsFileWhenTheTokenCameFromIt()
    {
        var path = Path.Combine(Path.GetTempPath(), "ks9-3", "secrets.local.json");
        using var github = new Recorder(MachineScopes);
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), github, disposeHandler: false);

        var refusal = await GithubProjects.PreflightAsync(client, ProjectBoard(), path).ConfigureAwait(true);

        Assert.Contains(path, string.Join("\n", refusal), StringComparison.Ordinal);
        Assert.DoesNotContain(GithubIdentity.TokenEnvVar, string.Join("\n", refusal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheScopeCheckPerformsZeroMutations()
    {
        using var github = new Recorder(MachineScopes);
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), github, disposeHandler: false);

        await GithubProjects.PreflightAsync(client, ProjectBoard(), "src").ConfigureAwait(true);

        Assert.All(github.Seen, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.Equal(["GET /user"], github.Seen.Select(r => $"{r.Method} {r.Path}"));
    }

    /// <summary>The one that keeps a later reader honest. Grant the scope and this STILL refuses,
    /// and says the mutation path does not exist — a gate that fell through to silence would read,
    /// from the outside, exactly like a board being mirrored.</summary>
    [Fact]
    public async Task WithTheScopeGrantedItStillRefusesAndSaysTheBoardIsNotImplemented()
    {
        using var github = new Recorder(MachineScopes + ", project");
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), github, disposeHandler: false);

        var refusal = await GithubProjects.PreflightAsync(client, ProjectBoard(), "src").ConfigureAwait(true);
        var text = string.Join("\n", refusal);

        Assert.NotEmpty(refusal);
        Assert.Contains(GithubProjects.NotImplementedLine, text, StringComparison.Ordinal);
        Assert.Contains("the gate is not what stopped this", text, StringComparison.Ordinal);
        Assert.Contains("SKIPPED rather than half-built", text, StringComparison.Ordinal);
        Assert.All(github.Seen, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    // ── the scope question itself ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(MachineScopes, false)]
    [InlineData("repo, project", true)]
    [InlineData("project", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    // read:project grants a READ of a board. This integration writes, so it is not enough, and
    // accepting it would produce a 403 at the first mutation instead of a refusal at the gate.
    [InlineData("repo, read:project", false)]
    public void TheScopeIsReadFromTheHeaderAndReadProjectIsNotEnough(string? header, bool expected) =>
        Assert.Equal(expected, GithubProjects.HasScope(header));

    /// <summary>A fine-grained PAT gets no <c>X-OAuth-Scopes</c> header at all. Telling its owner
    /// "scopes observed: none" and pointing them at <c>gh auth refresh</c> would be advice that
    /// cannot work — a fine-grained token has no classic scopes to add.</summary>
    [Fact]
    public void ATokenWithNoScopeHeaderIsDescribedAsUnreportedRatherThanEmpty()
    {
        var text = string.Join("\n", GithubProjects.ScopeRefusal(null, GithubIdentity.TokenEnvVar));

        Assert.Contains("none reported", text, StringComparison.Ordinal);
        Assert.Contains("fine-grained PAT", text, StringComparison.Ordinal);
    }

    /// <summary>"Could not ask" is a third answer. Reporting an unreachable API as a missing scope
    /// would send an owner to grant a scope they may already have.</summary>
    [Fact]
    public async Task AProbeThatFailsIsReportedAsSuchAndNotAsAMissingScope()
    {
        using var dead = new Recorder(null, dead: true);
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), dead, disposeHandler: false);

        var refusal = await GithubProjects.PreflightAsync(client, ProjectBoard(), "src").ConfigureAwait(true);
        var text = string.Join("\n", refusal);

        Assert.Contains("could not read this token's scopes", text, StringComparison.Ordinal);
        Assert.Contains("connection refused", text, StringComparison.Ordinal);
        Assert.DoesNotContain(GithubProjects.GrantCommand, text, StringComparison.Ordinal);
    }

    // ── the config gate, which costs nothing and runs first ──────────────────────────────────────

    /// <summary>docs/plan-config.md has promised this refusal since KS9.1 while <c>projectNumber</c>
    /// had no reader anywhere in src — the config-that-nothing-consumes anti-pattern
    /// NEXT-FEATURES.md names. It refuses by name now, and nothing is contacted to find out.</summary>
    [Fact]
    public async Task AMissingProjectNumberRefusesByNameWithoutContactingAnything()
    {
        var config = ProjectBoard(number: 0);
        using var github = new Recorder(MachineScopes);
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), github, disposeHandler: false);

        var refusal = await GithubProjects.PreflightAsync(client, config, "src").ConfigureAwait(true);
        var text = string.Join("\n", refusal);

        Assert.Contains("github.projectNumber is 0", text, StringComparison.Ordinal);
        Assert.Contains("nothing was contacted", text, StringComparison.Ordinal);
        Assert.Empty(github.Seen);
        Assert.Equal(0, client.RequestCount);
    }

    [Fact]
    public void ANegativeProjectNumberIsRefusedToo() =>
        Assert.Contains("github.projectNumber is -1",
            ProjectBoard(number: -1).BoardRefusal() ?? "", StringComparison.Ordinal);

    /// <summary>A misspelt board must not be read as the default. Silently downgrading
    /// <c>issues+projekt</c> to issues-only is indistinguishable, from the outside, from a project
    /// mirror that ran and did nothing.</summary>
    [Fact]
    public void AnUnknownBoardValueIsRefusedByNameInsteadOfDefaultingToIssues()
    {
        var config = new GithubConfig { Board = "issues+projekt", ProjectNumber = 3 };

        Assert.False(config.WantsProjectBoard);
        Assert.Contains("'issues+projekt' is not a board", config.BoardRefusal() ?? "", StringComparison.Ordinal);
    }

    /// <summary>The regression that matters more than the feature: a plan that never asked for any
    /// of this must behave exactly as it did before the field had a reader.</summary>
    [Theory]
    [InlineData("issues")]
    [InlineData("ISSUES")]
    [InlineData("")]
    [InlineData(null)]
    public void TheDefaultBoardIsNeitherARefusalNorAProjectRequest(string? board)
    {
        var config = new GithubConfig { Board = board! };

        Assert.Null(config.BoardRefusal());
        Assert.False(config.WantsProjectBoard);
    }

    [Fact]
    public void TheProjectBoardIsRecognisedWhateverItsCasingAndSpacing()
    {
        var config = new GithubConfig { Board = "  Issues+Project ", ProjectNumber = 2 };

        Assert.True(config.WantsProjectBoard);
        Assert.Null(config.BoardRefusal());
    }

    /// <summary>A token in the plan's OWN secrets file, never in the environment variable. Both are
    /// real sources, but $CONDUCTOR_GITHUB_TOKEN is process-global and KS9_1GithubTokenTests clears
    /// it while asserting the no-token refusal — xUnit runs the two classes in parallel, so a mirror
    /// here vanished mid-test. A per-temp-dir file cannot race.</summary>
    private static void Token(PlanConfig plan)
    {
        Directory.CreateDirectory(plan.StateDir);
        SecretsStore.WriteGithubToken(plan.StateDir, "t");
    }

    /// <summary>A real store, because TryCreate takes one and a fake IRunStore here would be a
    /// second implementation of an interface this file has no opinion about.</summary>
    private static SqliteRunStore Store(string dir)
    {
        var store = new SqliteRunStore(Path.Combine(dir, "run.db"), NullLogger<SqliteRunStore>.Instance);
        store.SetRunId("run-ks930000000");
        store.InitializeRun("run-ks930000000", "ks9-3", "C:/code/conductor", "feat/karvansara",
            new EngineStamp("0.4.1", "abc123", false));
        return store;
    }

    // ── the run's own boundary ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A plan that asks a RUN for a project board is told, once, at mirror creation — and then keeps
    /// its issue mirror. That asymmetry with the CLI is deliberate and is the KS9.2 posture: a run
    /// must never lose a working issue board over an extra it cannot have. What it must not get is
    /// silence, which is what the field's first ten weeks of having no reader looked like.
    /// </summary>
    [Theory]
    [InlineData(GithubConfig.BoardIssuesAndProject, 3, "not implemented")]
    [InlineData(GithubConfig.BoardIssuesAndProject, 0, "github.projectNumber is 0")]
    [InlineData("issues+projekt", 3, "is not a board")]
    public void ARunAskedForAProjectBoardIsToldOnceAndKeepsItsIssueMirror(string board, int number, string expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ks93m-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var plan = new PlanConfig
            {
                Name = "p",
                Repo = dir,
                Github = new GithubConfig
                {
                    Enabled = true, Repo = "owner/scratch", Board = board, ProjectNumber = number,
                },
            };
            var lines = new List<string>();
            Token(plan);

            using var store = Store(dir);
            using var mirror = GithubMirror.TryCreate(plan, store, "run-ks930000000", lines.Add);

            Assert.NotNull(mirror);   // the issue mirror lives
            var told = Assert.Single(lines, l => l.StartsWith("github project board off:", StringComparison.Ordinal));
            Assert.Contains(expected, told, StringComparison.Ordinal);
            Assert.Contains("the issue board is unaffected", told, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* not the assertion */ }
        }
    }

    /// <summary>And the default board says nothing at all — a line here would be noise on every run
    /// that never asked for a board.</summary>
    [Fact]
    public void ARunOnTheDefaultBoardIsToldNothingAboutProjects()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ks93n-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var plan = new PlanConfig
            {
                Name = "p", Repo = dir,
                Github = new GithubConfig { Enabled = true, Repo = "owner/scratch" },
            };
            var lines = new List<string>();
            Token(plan);

            using var store = Store(dir);
            using var mirror = GithubMirror.TryCreate(plan, store, "run-ks930000000", lines.Add);

            Assert.NotNull(mirror);
            Assert.DoesNotContain(lines, l => l.Contains("project board", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* not the assertion */ }
        }
    }

    /// <summary>Records what the gate did, and answers /user with the scopes header a classic token
    /// gets. Anything other than /user is a bug in the gate, so it 500s rather than being helpful.</summary>
    private sealed class Recorder(string? scopes, bool dead = false) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            Seen.Add((request.Method, request.RequestUri?.AbsolutePath ?? ""));
            if (dead) throw new HttpRequestException("connection refused");

            var resp = new HttpResponseMessage(
                request.RequestUri?.AbsolutePath == "/user" ? HttpStatusCode.OK : HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"login\":\"shaahink\"}"),
            };
            if (scopes is not null) resp.Headers.TryAddWithoutValidation("X-OAuth-Scopes", scopes);
            return Task.FromResult(resp);
        }
    }
}
