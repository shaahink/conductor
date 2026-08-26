using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Conductor.Core.Integrations.Github;
using Conductor.Core.Store;
using Xunit;

namespace Conductor.Tests;

/// <summary>
/// DV6.4 — the bug ledger as GitHub code-scanning alerts.
///
/// <para>The bar this file holds is narrower than "it produces SARIF". Three things have to be true
/// or the feature is a liability: a citation is resolved or REFUSED and never guessed; the document
/// is stable enough to golden and carries its own analysis category so it cannot close another
/// tool's alerts; and a 202 is treated as a receipt, not as an ingestion, because GitHub validates
/// afterwards and reports a rejected document on the status call alone.</para>
/// </summary>
public sealed class DV6_4SarifTests
{
    private static readonly string[] Tracked =
    [
        "src/Conductor.Core/Store/StateHome.cs",
        "src/Conductor.Core/Engine/VerdictEngine.cs",
        "src/Conductor/Models/AgentConfig.cs",
        "src/Conductor/Commands/BugCommand.cs",
        "tools/w3/window-close.ps1",
        "face-go/internal/tui/widgets/editor.go",
        "face-go/internal/tui/agent.go",
        "face-go/internal/tui/testdata/golden/agent.go",
    ];

    private static Func<string, string?> Resolver() => SarifBugLocations.Resolver(Tracked);

    private static BugRow Bug(
        long id, string title, string? detail = null, string severity = "medium", string status = "open") =>
        new(id, "run-divan", title, detail, severity, status, "DV6", 19, null, "2026-08-26 09:00:00", "2026-08-26 09:00:00");

    private static CarriedBugRow Carried(BugRow bug, string plan = "Divan") => new(bug, plan);

    // ────────────────────────────── the citation, resolved or refused ──────────────────────────────

    [Fact]
    public void A_full_path_with_a_line_is_a_location()
    {
        var found = SarifBugLocations.Find(
            "AgentConfig.Merge silently drops Env — src/Conductor/Models/AgentConfig.cs:36-48 builds it",
            Resolver());

        var one = Assert.Single(found);
        Assert.Equal("src/Conductor/Models/AgentConfig.cs", one.Path);
        Assert.Equal(36, one.StartLine);
        Assert.Equal(48, one.EndLine);
    }

    /// <summary>Most sessions write the file name alone, because that is what a reader needs. It
    /// becomes an alert only when exactly one tracked file bears the name.</summary>
    [Fact]
    public void A_bare_file_name_resolves_when_exactly_one_tracked_file_bears_it()
    {
        var one = Assert.Single(SarifBugLocations.Find("VerdictEngine.cs:370 parses the wrong glyph", Resolver()));

        Assert.Equal("src/Conductor.Core/Engine/VerdictEngine.cs", one.Path);
        Assert.Equal(370, one.StartLine);
        Assert.Null(one.EndLine);
    }

    /// <summary>Two tracked files named <c>agent.go</c>. An alert on the wrong one is worse than no
    /// alert, so the ambiguity is refused rather than resolved to the first match.</summary>
    [Fact]
    public void An_ambiguous_bare_name_is_refused_not_guessed()
    {
        Assert.Empty(SarifBugLocations.Find("agent.go:36 mis-renders the pane", Resolver()));

        // The same bug, written with enough path to be unambiguous, resolves.
        var one = Assert.Single(SarifBugLocations.Find("tui/agent.go:36 mis-renders the pane", Resolver()));
        Assert.Equal("face-go/internal/tui/agent.go", one.Path);
    }

    [Fact]
    public void A_path_no_tracked_file_matches_is_refused()
    {
        Assert.Empty(SarifBugLocations.Find("src/Conductor.Core/Gone/Removed.cs:12 no longer exists", Resolver()));
        Assert.Empty(SarifBugLocations.Find("Removed.cs:12 no longer exists", Resolver()));
    }

    /// <summary>SARIF would happily take a region starting at line 1. That would hang every bug that
    /// merely MENTIONS a file at the top of it, which reads as a fact and is not one.</summary>
    [Fact]
    public void A_citation_with_no_line_is_not_a_location()
    {
        Assert.Empty(SarifBugLocations.Find(
            "the fix is in src/Conductor.Core/Store/StateHome.cs and nowhere else", Resolver()));
    }

    [Fact]
    public void Prose_that_merely_looks_like_a_path_is_not_one()
    {
        Assert.Empty(SarifBugLocations.Find(
            "shipped as 0.4.1 against api.github.com; see #79 and the 12:30 boundary", Resolver()));
    }

    [Fact]
    public void A_backwards_span_keeps_the_start_and_drops_the_end()
    {
        var one = Assert.Single(SarifBugLocations.Find("tools/w3/window-close.ps1:40-12 is wrong", Resolver()));

        Assert.Equal(40, one.StartLine);
        Assert.Null(one.EndLine);
    }

    [Fact]
    public void A_windows_separator_is_normalised_to_the_uri_form()
    {
        var one = Assert.Single(SarifBugLocations.Find(@"src\Conductor\Commands\BugCommand.cs:109 writes it", Resolver()));

        Assert.Equal("src/Conductor/Commands/BugCommand.cs", one.Path);
    }

    [Fact]
    public void The_same_place_cited_twice_is_one_location()
    {
        var found = SarifBugLocations.Find(
            "VerdictEngine.cs:370 and again VerdictEngine.cs:370, plus BugCommand.cs:109", Resolver());

        Assert.Equal(2, found.Count);
    }

    // ────────────────────────────── which bugs become alerts ──────────────────────────────

    [Fact]
    public void Only_open_bugs_that_name_a_place_become_findings()
    {
        var findings = SarifDocument.Findings(
        [
            Carried(Bug(1, "open and located — VerdictEngine.cs:370")),
            Carried(Bug(2, "open, no place named")),
            Carried(Bug(3, "fixed and located — BugCommand.cs:109", status: "fixed")),
        ], Resolver());

        var one = Assert.Single(findings);
        Assert.Equal(1, one.Bug.Id);
    }

    /// <summary>The closing mechanism, stated as a test. Code scanning resolves an alert whose result
    /// stops appearing in a later analysis of the same category, so <c>conductor bug fix</c> closes
    /// the alert with no second call — provided a fixed bug really does leave the document.</summary>
    [Fact]
    public void A_fixed_bug_leaves_the_document_so_its_alert_closes_itself()
    {
        var open = Bug(7, "StateHome.cs:27 resolves by repo first");
        var before = SarifDocument.Payload([Carried(open)], Resolver(), "0.4.1");
        var after = SarifDocument.Payload([Carried(open with { Status = "fixed" })], Resolver(), "0.4.1");

        Assert.Contains("conductor/bug/7", before.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("conductor/bug/7", after.Json, StringComparison.Ordinal);
        Assert.Empty(after.Findings);
    }

    [Fact]
    public void Open_bugs_with_no_place_are_counted_never_hidden()
    {
        var payload = SarifDocument.Payload(
        [
            Carried(Bug(1, "located — VerdictEngine.cs:370")),
            Carried(Bug(2, "no place named")),
            Carried(Bug(3, "no place named either")),
            Carried(Bug(4, "fixed, and not counted as missing", status: "fixed")),
        ], Resolver(), "0.4.1");

        Assert.Single(payload.Findings);
        Assert.Equal(2, payload.WithoutLocation);
    }

    [Theory]
    [InlineData("high", "error")]
    [InlineData("medium", "warning")]
    [InlineData("low", "note")]
    [InlineData("catastrophic", "warning")]
    public void Severity_maps_to_a_sarif_level(string severity, string expected) =>
        Assert.Equal(expected, SarifDocument.Level(severity));

    // ────────────────────────────── the document itself ──────────────────────────────

    private static IReadOnlyList<CarriedBugRow> Ledger() =>
    [
        Carried(Bug(61, "CONDUCTOR_RUN_DB does not redirect the measuring verbs",
            "StateHome.cs:27-29 is documented as overriding the resolved database FILE.", "medium")),
        Carried(Bug(70, "AgentConfig.Merge silently drops Env",
            "src/Conductor/Models/AgentConfig.cs:36-48 — the merged instance keeps the stage's env only.",
            "high")),
        Carried(Bug(72, "face: bubbles/textarea cannot replace widgets.TextArea",
            "widgets/editor.go:88 takes a tea.KeyMsg.", "low")),
    ];

    private static JsonElement Run(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("runs")[0];

    [Fact]
    public void The_document_declares_its_own_analysis_category()
    {
        // Without this, GitHub reads the upload as the repository's ONLY analysis and closes every
        // alert any other tool raised.
        var run = Run(SarifDocument.Payload(Ledger(), Resolver(), "0.4.1").Json);

        Assert.Equal("conductor-bugs/", run.GetProperty("automationDetails").GetProperty("id").GetString());
        Assert.Equal("Conductor", run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());
        Assert.Equal("0.4.1", run.GetProperty("tool").GetProperty("driver").GetProperty("semanticVersion").GetString());
    }

    /// <summary>The bug's row id is the fingerprint, so the same bug re-uploaded from a later commit
    /// UPDATES its alert instead of raising a second one — the duplicate-on-second-pass failure bug
    /// #79 records for the issue mirror.</summary>
    [Fact]
    public void Every_result_is_fingerprinted_by_its_bug_id()
    {
        var run = Run(SarifDocument.Payload(Ledger(), Resolver(), "0.4.1").Json);
        var results = run.GetProperty("results");

        Assert.Equal(3, results.GetArrayLength());
        for (var i = 0; i < results.GetArrayLength(); i++)
        {
            var result = results[i];
            var id = result.GetProperty("partialFingerprints").GetProperty("conductorBugId").GetString();
            Assert.Equal(SarifDocument.RuleId(long.Parse(id!, CultureInfo.InvariantCulture)),
                result.GetProperty("ruleId").GetString());
            Assert.Equal(i, result.GetProperty("ruleIndex").GetInt32());
            var region = result.GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("region");
            Assert.True(region.GetProperty("startLine").GetInt32() > 0);
        }
    }

    [Fact]
    public void The_rule_index_points_at_the_rule_that_describes_the_result()
    {
        var run = Run(SarifDocument.Payload(Ledger(), Resolver(), "0.4.1").Json);
        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules");
        var results = run.GetProperty("results");

        for (var i = 0; i < results.GetArrayLength(); i++)
        {
            Assert.Equal(results[i].GetProperty("ruleId").GetString(),
                rules[results[i].GetProperty("ruleIndex").GetInt32()].GetProperty("id").GetString());
        }
    }

    /// <summary>Nothing in the document is stamped with "now". Two renders of an unchanged ledger
    /// are byte-identical, which is what makes the golden below a bar rather than a diary.</summary>
    [Fact]
    public void Two_renders_of_an_unchanged_ledger_are_byte_identical()
    {
        var first = SarifDocument.Payload(Ledger(), Resolver(), "0.4.1").Json;
        var second = SarifDocument.Payload(Ledger().Reverse().ToList(), Resolver(), "0.4.1").Json;

        Assert.Equal(first, second, StringComparer.Ordinal);
    }

    [Fact]
    public void The_alert_body_says_how_to_close_it()
    {
        var run = Run(SarifDocument.Payload(Ledger(), Resolver(), "0.4.1").Json);
        var help = run.GetProperty("tool").GetProperty("driver").GetProperty("rules")[1]
            .GetProperty("help").GetProperty("text").GetString();

        Assert.Contains("conductor bug fix 70", help, StringComparison.Ordinal);
        Assert.Contains("Filed by: Divan, stage DV6, session 19", help, StringComparison.Ordinal);
    }

    /// <summary>The whole document, byte for byte. A missing golden FAILS rather than writing
    /// itself — DV6.3's rule, and KS11.1's before it.</summary>
    [Fact]
    public void Golden_the_whole_document()
    {
        var json = SarifDocument.Payload(Ledger(), Resolver(), "0.4.1").Json;
        var path = Path.Combine(RepoRoot(), "tests", "Conductor.Tests", "testdata", "dv6-4", "bugs.sarif");
        var normalised = json.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

        if (string.Equals(Environment.GetEnvironmentVariable("CONDUCTOR_GOLDEN_REBASELINE"), "1", StringComparison.Ordinal))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, normalised);
            return;
        }

        Assert.True(File.Exists(path),
            "golden bugs.sarif is missing — regenerate with CONDUCTOR_GOLDEN_REBASELINE=1 and READ the diff");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), normalised, StringComparer.Ordinal);
    }

    // ────────────────────────────── the wire ──────────────────────────────

    private static async Task<GithubSarifPass> PushAsync(
        FakeCodeScanning fake, bool dryRun = false, IReadOnlyList<CarriedBugRow>? ledger = null)
    {
        using var client = new GithubClient("t", TimeSpan.FromSeconds(5), fake, disposeHandler: false);
        var payload = SarifDocument.Payload(ledger ?? Ledger(), Resolver(), "0.4.1");
        return await new GithubSarifSync(client, "shaahink/scratch").PushAsync(
            payload, "0123456789abcdef0123456789abcdef01234567", "refs/heads/main", "env",
            dryRun, statusAttempts: 3, statusDelay: TimeSpan.Zero).ConfigureAwait(true);
    }

    /// <summary>GitHub takes gzip-then-base64, not JSON. This asserts the round trip against the
    /// bytes the server actually received, not against the intent of the code that sent them.</summary>
    [Fact]
    public async Task The_upload_is_gzip_base64_and_the_server_reads_back_the_document()
    {
        using var fake = new FakeCodeScanning();

        var pass = await PushAsync(fake);

        Assert.True(pass.Ok, string.Join(" | ", pass.Errors));
        Assert.Equal(3, pass.Reported);
        Assert.Equal("complete", pass.ProcessingStatus);
        var body = JsonDocument.Parse(Assert.Single(fake.Uploads)).RootElement;
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", body.GetProperty("commit_sha").GetString());
        Assert.Equal("refs/heads/main", body.GetProperty("ref").GetString());
        Assert.True(body.GetProperty("validate").GetBoolean());
        var received = await GithubClient.UngzipBase64Async(body.GetProperty("sarif").GetString()!);
        Assert.Equal(SarifDocument.Payload(Ledger(), Resolver(), "0.4.1").Json, received, StringComparer.Ordinal);
        Assert.Equal("POST", Assert.Single(fake.Seen, s => s.Path.EndsWith("/sarifs", StringComparison.Ordinal)).Method);
    }

    /// <summary>A 202 is a receipt. GitHub validates afterwards, and a rejected document fails on the
    /// status call and NOWHERE else — a pass that stopped at the 202 would report it as a success.
    /// </summary>
    [Fact]
    public async Task A_document_github_rejects_after_the_202_is_a_failure()
    {
        using var fake = new FakeCodeScanning { Processing = "failed", ProcessingErrors = ["rejected: missing region"] };

        var pass = await PushAsync(fake);

        Assert.False(pass.Ok);
        Assert.Contains("rejected: missing region", string.Join(" ", pass.Errors), StringComparison.Ordinal);
        Assert.Equal("failed", pass.ProcessingStatus);
    }

    [Fact]
    public async Task A_pending_upload_is_asked_again_until_it_settles()
    {
        using var fake = new FakeCodeScanning { PendingReplies = 2 };

        var pass = await PushAsync(fake);

        Assert.True(pass.Ok, string.Join(" | ", pass.Errors));
        Assert.Equal("complete", pass.ProcessingStatus);
        Assert.Equal(3, fake.Seen.Count(s => s.Method == "GET" && s.Path.Contains("/sarifs/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_public_repository_is_told_it_is_free()
    {
        using var fake = new FakeCodeScanning();

        var pass = await PushAsync(fake);

        Assert.Contains(pass.Notes, n => n.Contains("is public", StringComparison.Ordinal)
            && n.Contains("free", StringComparison.Ordinal));
    }

    /// <summary>The KS9.3 shape: a scope that is certainly missing is a refusal with the ONE command
    /// that grants it, and nothing is sent.</summary>
    [Fact]
    public async Task A_private_repository_without_the_scope_is_refused_by_name()
    {
        using var fake = new FakeCodeScanning { Private = true, Scopes = "repo,workflow,gist,read:org,user,delete_repo" };

        var pass = await PushAsync(fake);

        Assert.False(pass.Ok);
        var refusal = string.Join(" ", pass.Errors);
        Assert.Contains("security_events", refusal, StringComparison.Ordinal);
        Assert.Contains("gh auth refresh -s security_events", refusal, StringComparison.Ordinal);
        Assert.Empty(fake.Uploads);
        Assert.Contains(pass.Notes, n => n.Contains("Advanced Security", StringComparison.Ordinal));
    }

    /// <summary>Visibility alone is NOT a refusal: an organisation with Advanced Security is exactly
    /// the case where a private upload succeeds, and guessing otherwise denies a paying owner the
    /// feature they bought.</summary>
    [Fact]
    public async Task A_private_repository_with_the_scope_is_attempted_and_only_github_may_refuse_it()
    {
        using var fake = new FakeCodeScanning { Private = true, Scopes = "repo,security_events" };

        var pass = await PushAsync(fake);

        Assert.True(pass.Ok, string.Join(" | ", pass.Errors));
        Assert.Single(fake.Uploads);
    }

    /// <summary>The measured caveat: GitHub's own 403 on a private repository without Advanced
    /// Security, translated into the sentence that names the cause instead of a status code.</summary>
    [Fact]
    public async Task A_403_is_translated_into_the_advanced_security_sentence()
    {
        using var fake = new FakeCodeScanning
        {
            Private = true,
            Scopes = "repo,security_events",
            UploadStatus = HttpStatusCode.Forbidden,
            UploadBody = "{\"message\":\"Advanced Security must be enabled for this repository to use code scanning.\"}",
        };

        var pass = await PushAsync(fake);

        Assert.False(pass.Ok);
        var refusal = string.Join(" ", pass.Errors);
        Assert.Contains("403", refusal, StringComparison.Ordinal);
        Assert.Contains("Advanced Security", refusal, StringComparison.Ordinal);
        Assert.Contains("free on PUBLIC repositories", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_renders_and_sends_nothing()
    {
        using var fake = new FakeCodeScanning();

        var pass = await PushAsync(fake, dryRun: true);

        Assert.Equal(3, pass.Reported);
        Assert.True(pass.PayloadBytes > 0);
        Assert.Empty(fake.Seen);
        Assert.Contains(pass.Notes, n => n.Contains("dry run", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ledger_with_no_located_bug_contacts_nothing()
    {
        using var fake = new FakeCodeScanning();

        var pass = await PushAsync(fake, ledger: [Carried(Bug(1, "nothing here names a place"))]);

        Assert.Equal(0, pass.Reported);
        Assert.Empty(fake.Seen);
        Assert.Contains(pass.Notes, n => n.Contains("nothing to upload", StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Conductor.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>The code-scanning half of api.github.com, with the knobs the two real refusals need:
    /// a private repository, a token's scope header, and a 403 body.</summary>
    private sealed class FakeCodeScanning : HttpMessageHandler
    {
        public List<(string Method, string Path)> Seen { get; } = [];
        public List<string> Uploads { get; } = [];
        public bool Private { get; init; }
        public string? Scopes { get; init; }
        public HttpStatusCode UploadStatus { get; init; } = HttpStatusCode.Accepted;
        public string? UploadBody { get; init; }
        public string Processing { get; init; } = "complete";
        public List<string>? ProcessingErrors { get; init; }
        public int PendingReplies { get; init; }

        private int _statusCalls;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Seen.Add((request.Method.Method, path));

            if (path.EndsWith("/code-scanning/sarifs", StringComparison.Ordinal))
            {
                Uploads.Add(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                return Reply(UploadStatus,
                    UploadBody ?? "{\"id\":\"sarif-1\",\"url\":\"https://api.github.com/sarifs/sarif-1\"}");
            }

            if (path.Contains("/code-scanning/sarifs/", StringComparison.Ordinal))
            {
                var pending = _statusCalls++ < PendingReplies;
                var errors = ProcessingErrors is null
                    ? ""
                    : ",\"errors\":[" + string.Join(",", ProcessingErrors.Select(e => "\"" + e + "\"")) + "]";
                return Reply(HttpStatusCode.OK,
                    $"{{\"processing_status\":\"{(pending ? "pending" : Processing)}\"{errors}}}");
            }

            if (path == "/user")
                return Reply(HttpStatusCode.OK, "{\"login\":\"shaahink\"}");

            return Reply(HttpStatusCode.OK,
                $"{{\"full_name\":\"shaahink/scratch\",\"private\":{(Private ? "true" : "false")}," +
                $"\"visibility\":\"{(Private ? "private" : "public")}\",\"default_branch\":\"main\"}}");
        }

        private HttpResponseMessage Reply(HttpStatusCode code, string json)
        {
            var resp = new HttpResponseMessage(code)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            if (Scopes is not null) resp.Headers.TryAddWithoutValidation("X-OAuth-Scopes", Scopes);
            return resp;
        }
    }
}
